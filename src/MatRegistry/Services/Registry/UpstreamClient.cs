using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace MatRegistry.Services.Registry;

/// <summary>A remote registry as the client talks to it: its address and the credentials (the secret is already unprotected; it never leaves this process).</summary>
public sealed record UpstreamTarget(long RegistryId, Uri BaseUrl, string? Username, string? Password, bool SkipTlsVerify)
{
    /// <summary>Docker Hub keeps the official images under <c>library/</c>: <c>nginx</c> is <c>library/nginx</c> there.</summary>
    public bool IsDockerHub => BaseUrl.Host is "registry-1.docker.io" or "docker.io" or "index.docker.io";

    public string RepositoryOf(string name) => IsDockerHub && !name.Contains('/', StringComparison.Ordinal) ? "library/" + name : name;

    public bool HasCredentials => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);
}

/// <summary>An error of the remote registry, worded for a person (the status it answered, if it answered).</summary>
public sealed class UpstreamException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    public bool NotFound => Status == HttpStatusCode.NotFound;
}

/// <summary>A manifest fetched from a remote registry.</summary>
public sealed record UpstreamManifest(byte[] Content, string MediaType, string Digest);

/// <summary>
/// The HTTP client of the registries MatRegistry is bound to (OCI Distribution): it signs in the way the registry asks (the token service of Docker Hub, GHCR,
/// Harbor, another MatRegistry, or Basic) and keeps the tokens for their life. Only the business layer uses it (browsing, the pull-through cache, copies).
/// </summary>
public sealed partial class UpstreamClient(IHttpClientFactory factory, IMemoryCache cache)
{
    public const string ClientName = "upstream";
    public const string InsecureClientName = "upstream-insecure";

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex ChallengeParameter();

    /// <summary>Turns what a person typed (<c>docker.io</c>, <c>ghcr.io</c>, <c>http://nas:5000/v2/</c>) into the base address; null when it is no address.</summary>
    public static Uri? NormalizeUrl(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = uri.Host is "docker.io" or "index.docker.io" or "hub.docker.com" ? "registry-1.docker.io" : uri.Host;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v2", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        var builder = new UriBuilder(uri.Scheme, host, uri.IsDefaultPort ? -1 : uri.Port, path);
        return new Uri(builder.Uri.ToString().TrimEnd('/'));
    }

    /// <summary>Asks <c>/v2/</c>: reachable and the credentials accepted? Answers false with the reason in words.</summary>
    public async Task<(bool Ok, string Message)> PingAsync(UpstreamTarget target, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(target, HttpMethod.Get, "/v2/", string.Empty, null, null, HttpCompletionOption.ResponseContentRead, ct);
            return response.IsSuccessStatusCode
                ? (true, $"HTTP {(int)response.StatusCode}")
                : (false, response.StatusCode == HttpStatusCode.Unauthorized ? "The registry did not accept the credentials." : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (UpstreamException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>One page of the catalog; null when the registry does not offer one (Docker Hub, GHCR) or does not let these credentials read it.</summary>
    public async Task<(List<string> Names, string? Next)?> CatalogAsync(UpstreamTarget target, int count, string? last, CancellationToken ct)
    {
        var path = $"/v2/_catalog?n={count}" + (string.IsNullOrEmpty(last) ? string.Empty : "&last=" + Uri.EscapeDataString(last));
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(target, HttpMethod.Get, path, "registry:catalog:*", null, null, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (UpstreamException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // the token service gives no token for the catalog (Docker Hub answers 400): the registry offers none
            return null;
        }

        using var _ = response;
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
            return null;
        }

        await EnsureAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var names = json.RootElement.TryGetProperty("repositories", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        var next = NextLast(response);
        return (names, next ?? (names.Count >= count ? names[^1] : null));
    }

    /// <summary>The tags of a repository (every page, at most <paramref name="max"/>).</summary>
    public async Task<List<string>> TagsAsync(UpstreamTarget target, string repository, int max, CancellationToken ct)
    {
        var tags = new List<string>();
        string? last = null;
        while (tags.Count < max)
        {
            var path = $"/v2/{repository}/tags/list?n=1000" + (last is null ? string.Empty : "&last=" + Uri.EscapeDataString(last));
            using var response = await SendAsync(target, HttpMethod.Get, path, Scope(repository, false), null, null, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureAsync(response, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            var page = json.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            tags.AddRange(page);
            last = NextLast(response);
            if (last is null || page.Count == 0)
            {
                break;
            }
        }

        return tags.Count > max ? tags[..max] : tags;
    }

    /// <summary>A manifest by tag or digest; null when the registry does not know it. A digest is checked against the bytes.</summary>
    public async Task<UpstreamManifest?> GetManifestAsync(UpstreamTarget target, string repository, string reference, CancellationToken ct)
    {
        using var response = await SendAsync(target, HttpMethod.Get, $"/v2/{repository}/manifests/{reference}", Scope(repository, false), null, MediaTypes.Accept,
            HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(response, ct);
        var content = await response.Content.ReadAsByteArrayAsync(ct);
        if (content.Length > RegistryEngine.MaxManifestBytes)
        {
            throw new UpstreamException("The manifest of the remote registry is too large.");
        }

        var digest = RegistryNames.DigestOf(content);
        if (RegistryNames.IsDigest(reference) && reference != digest)
        {
            throw new UpstreamException("The remote registry answered a manifest with another digest.");
        }

        var mediaType = MediaTypes.Clean(response.Content.Headers.ContentType?.ToString()) ?? string.Empty;
        if (!MediaTypes.IsManifest(mediaType))
        {
            var (parsed, _) = ManifestDocument.Parse(content, null);
            mediaType = parsed?.MediaType ?? mediaType;
        }

        return new UpstreamManifest(content, mediaType, digest);
    }

    /// <summary>The digest a tag points at (HEAD, which Docker Hub does not count as a pull); null when unknown.</summary>
    public async Task<string?> HeadManifestAsync(UpstreamTarget target, string repository, string reference, CancellationToken ct)
    {
        using var response = await SendAsync(target, HttpMethod.Head, $"/v2/{repository}/manifests/{reference}", Scope(repository, false), null, MediaTypes.Accept,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(response, ct);
        return response.Headers.TryGetValues("Docker-Content-Digest", out var values) && values.FirstOrDefault() is { } d && RegistryNames.IsDigest(d) ? d : null;
    }

    /// <summary>The size of a blob, or null when the registry does not have it.</summary>
    public async Task<long?> HeadBlobAsync(UpstreamTarget target, string repository, string digest, bool forPush, CancellationToken ct)
    {
        using var response = await SendAsync(target, HttpMethod.Head, $"/v2/{repository}/blobs/{digest}", Scope(repository, forPush), null, null,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureAsync(response, ct);
        return response.Content.Headers.ContentLength ?? 0;
    }

    /// <summary>A blob as a stream (the caller disposes the answer); null when the registry does not have it.</summary>
    public async Task<HttpResponseMessage?> GetBlobAsync(UpstreamTarget target, string repository, string digest, CancellationToken ct)
    {
        var response = await SendAsync(target, HttpMethod.Get, $"/v2/{repository}/blobs/{digest}", Scope(repository, false), null, null,
            HttpCompletionOption.ResponseHeadersRead, ct, streaming: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        try
        {
            await EnsureAsync(response, ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }

    /// <summary>Uploads a file as a blob: POST for an upload, then one PUT with the bytes (a monolithic upload, which every registry takes).</summary>
    public async Task UploadBlobAsync(UpstreamTarget target, string repository, string digest, string path, CancellationToken ct)
    {
        Uri location;
        using (var start = await SendAsync(target, HttpMethod.Post, $"/v2/{repository}/blobs/uploads/", Scope(repository, true), () => new ByteArrayContent([]), null,
                   HttpCompletionOption.ResponseContentRead, ct))
        {
            await EnsureAsync(start, ct);
            location = start.Headers.Location ?? throw new UpstreamException("The remote registry gave no address for the upload.");
        }

        var url = location.IsAbsoluteUri ? location.ToString() : new Uri(target.BaseUrl, location).ToString();
        url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "digest=" + Uri.EscapeDataString(digest);
        using var put = await SendAsync(target, HttpMethod.Put, url, Scope(repository, true), () =>
        {
            var content = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return content;
        }, null, HttpCompletionOption.ResponseContentRead, ct, streaming: true);
        await EnsureAsync(put, ct);
    }

    public async Task PutManifestAsync(UpstreamTarget target, string repository, string reference, byte[] content, string mediaType, CancellationToken ct)
    {
        using var response = await SendAsync(target, HttpMethod.Put, $"/v2/{repository}/manifests/{reference}", Scope(repository, true), () =>
        {
            var body = new ByteArrayContent(content);
            body.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return body;
        }, null, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Deletes a manifest (and with it every tag on it) at the remote registry; many registries refuse that (the message says so).</summary>
    public async Task DeleteManifestAsync(UpstreamTarget target, string repository, string digest, CancellationToken ct)
    {
        using var response = await SendAsync(target, HttpMethod.Delete, $"/v2/{repository}/manifests/{digest}", Scope(repository, true, delete: true), null, null,
            HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(response, ct);
    }

    private static string Scope(string repository, bool push, bool delete = false)
        => $"repository:{repository}:" + (delete ? "pull,push,delete" : push ? "pull,push" : "pull");

    /// <summary>
    /// Sends a request with the authorization the registry wants: a token of its token service for <paramref name="scope"/> (asked for and kept on the first 401), or
    /// Basic. A request with a body is made again by <paramref name="content"/> when it has to be sent twice.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(UpstreamTarget target, HttpMethod method, string pathOrUrl, string scope, Func<HttpContent>? content,
        string[]? accept, HttpCompletionOption completion, CancellationToken ct, bool streaming = false)
    {
        var client = factory.CreateClient(target.SkipTlsVerify ? InsecureClientName : ClientName);
        using var timeout = streaming ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout?.CancelAfter(MetadataTimeout);
        var token = timeout?.Token ?? ct;
        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(pathOrUrl) : new Uri(target.BaseUrl.ToString().TrimEnd('/') + pathOrUrl);

        HttpRequestMessage Build(AuthenticationHeaderValue? auth)
        {
            var request = new HttpRequestMessage(method, url) { Content = content?.Invoke() };
            request.Headers.UserAgent.ParseAdd("MatRegistry/" + AppInfo.Version.Replace('+', '-'));
            if (accept is not null)
            {
                foreach (var a in accept)
                {
                    request.Headers.Accept.ParseAdd(a);
                }
            }

            request.Headers.Authorization = auth;
            return request;
        }

        try
        {
            var auth = cache.Get<AuthenticationHeaderValue>(TokenKey(target, scope)) ?? (cache.Get<string>(ChallengeKey(target)) == "basic" ? Basic(target) : null);
            var response = await client.SendAsync(Build(auth), completion, token);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
            if (challenge is null)
            {
                return response;
            }

            var retry = await AuthorizeAsync(target, challenge, scope, token);
            if (retry is null)
            {
                return response;
            }

            response.Dispose();
            return await client.SendAsync(Build(retry), completion, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpstreamException("The remote registry did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new UpstreamException("The remote registry cannot be reached: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>Answers the challenge of a 401: a token of the token service (kept until shortly before it ends) or Basic; null when there is nothing to answer with.</summary>
    private async Task<AuthenticationHeaderValue?> AuthorizeAsync(UpstreamTarget target, AuthenticationHeaderValue challenge, string scope, CancellationToken ct)
    {
        if (string.Equals(challenge.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            cache.Set(ChallengeKey(target), "basic", TimeSpan.FromHours(1));
            return target.HasCredentials ? Basic(target) : null;
        }

        if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parameters = ChallengeParameter().Matches(challenge.Parameter ?? string.Empty).ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
        if (!parameters.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri))
        {
            return null;
        }

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Add("service=" + Uri.EscapeDataString(service));
        }

        if (scope.Length > 0)
        {
            query.Add("scope=" + Uri.EscapeDataString(scope));
        }

        var tokenUrl = realmUri + (realmUri.Query.Length > 0 ? "&" : "?") + string.Join('&', query);
        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        if (target.HasCredentials)
        {
            request.Headers.Authorization = Basic(target);
        }

        var client = factory.CreateClient(target.SkipTlsVerify ? InsecureClientName : ClientName);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpstreamException(response.StatusCode == HttpStatusCode.Unauthorized
                ? "The registry did not accept the credentials."
                : $"The token service of the registry answered HTTP {(int)response.StatusCode}.", response.StatusCode);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var root = json.RootElement;
        var value = ManifestDocument.Str(root, "token") ?? ManifestDocument.Str(root, "access_token");
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 60;
        var header = new AuthenticationHeaderValue("Bearer", value);
        cache.Set(TokenKey(target, scope), header, TimeSpan.FromSeconds(Math.Max(10, seconds - 15)));
        return header;
    }

    private static AuthenticationHeaderValue? Basic(UpstreamTarget target)
        => target.HasCredentials ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.Username}:{target.Password}"))) : null;

    private static string TokenKey(UpstreamTarget target, string scope) => $"upstream:token:{target.RegistryId}:{target.BaseUrl}:{target.Username}:{scope}";

    private static string ChallengeKey(UpstreamTarget target) => $"upstream:challenge:{target.RegistryId}:{target.BaseUrl}";

    /// <summary>The <c>last</c> of the next page from a <c>Link</c> header (<c>&lt;/v2/_catalog?last=x&amp;n=100&gt;; rel="next"</c>).</summary>
    private static string? NextLast(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var link in links)
        {
            var start = link.IndexOf('<', StringComparison.Ordinal);
            var end = link.IndexOf('>', StringComparison.Ordinal);
            if (start < 0 || end <= start || !link.Contains("next", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = link[(start + 1)..end];
            var q = target.IndexOf('?', StringComparison.Ordinal);
            if (q < 0)
            {
                continue;
            }

            foreach (var pair in target[(q + 1)..].Split('&'))
            {
                if (pair.StartsWith("last=", StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair[5..]);
                }
            }
        }

        return null;
    }

    /// <summary>Throws the error of the answer in words (the first message of the registry's own error list when it sent one).</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length is > 0 and < 20000)
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    message = ManifestDocument.Str(errors[0], "message") ?? ManifestDocument.Str(errors[0], "code");
                }
            }
        }
        catch (JsonException)
        {
            // not the error format of the spec: the status says enough
        }

        var status = response.StatusCode;
        var text = status switch
        {
            HttpStatusCode.Unauthorized => "The registry did not accept the credentials.",
            HttpStatusCode.Forbidden => "The registry does not allow this.",
            HttpStatusCode.NotFound => "The registry does not know this.",
            HttpStatusCode.TooManyRequests => "The registry limits the requests (too many pulls); try again later.",
            HttpStatusCode.MethodNotAllowed => "The registry does not support this.",
            _ => $"The registry answered HTTP {(int)status}.",
        };
        throw new UpstreamException(message is null ? text : $"{text} ({message})", status);
    }
}
