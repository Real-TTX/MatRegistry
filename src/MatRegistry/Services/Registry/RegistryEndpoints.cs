using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatRegistry.Data;
using MatPlatform;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;

namespace MatRegistry.Services.Registry;

/// <summary>
/// The registry API under <c>/v2/</c> (OCI Distribution 1.1 and the Docker Registry HTTP API v2) and its token service <c>/v2/token</c>. It turns requests into calls of
/// <see cref="RegistryEngine"/> and its answers (and errors) back into HTTP; it decides nothing itself. The body of a request has no size limit here (layers are
/// gigabytes), the status code pages of the site stay out, and every answer names the API version.
/// </summary>
public static partial class RegistryEndpoints
{
    private const string ApiVersionHeader = "Docker-Distribution-API-Version";

    [GeneratedRegex("^(?<name>.+)/blobs/uploads/(?<id>[^/]*)$")]
    private static partial Regex UploadPath();

    [GeneratedRegex("^(?<name>.+)/(?<kind>manifests|blobs|referrers)/(?<ref>[^/]+)$")]
    private static partial Regex ItemPath();

    [GeneratedRegex("^(?<name>.+)/tags/list$")]
    private static partial Regex TagsPath();

    public static IEndpointRouteBuilder MapRegistry(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/v2/token", ["GET", "POST"], TokenAsync).AllowAnonymous().RequireRateLimiting("registry-token").DisableAntiforgery();
        app.MapMethods("/v2", ["GET", "HEAD"], HandleAsync).AllowAnonymous().DisableAntiforgery();
        app.MapMethods("/v2/{**path}", ["GET", "HEAD", "PUT", "PATCH", "POST", "DELETE"], HandleAsync).AllowAnonymous().DisableAntiforgery();
        return app;
    }

    /// <summary>The token service: Basic credentials (or none, for a public pull) for a token (GET as Docker asks, POST as containerd asks).</summary>
    private static async Task TokenAsync(HttpContext http, AppDbContext db, AccountService accounts, RegistryAuth auth, IMemoryCache cache, MatPlatformSettings platform)
    {
        Prepare(http);
        RegistryActor? actor;
        if (HttpMethods.IsPost(http.Request.Method))
        {
            if (!http.Request.HasFormContentType)
            {
                await ErrorAsync(http, new RegistryException(400, "UNSUPPORTED", "A form is expected."), null);
                return;
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form["grant_type"] != "password")
            {
                await ErrorAsync(http, new RegistryException(400, "UNSUPPORTED", "Only the grant type password is supported."), null);
                return;
            }

            actor = await auth.CheckCredentialsAsync(db, accounts, form["username"].ToString(), form["password"].ToString(), http.RequestAborted);
        }
        else
        {
            var header = http.Request.Headers.Authorization.ToString();
            actor = string.IsNullOrWhiteSpace(header) ? RegistryActor.Anonymous : RegistryAuth.ParseBasic(header) is { } basic
                ? await auth.CheckCredentialsAsync(db, accounts, basic.User, basic.Password, http.RequestAborted)
                : null;
        }

        if (actor is null)
        {
            http.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Ui.AppName}\"";
            await ErrorAsync(http, new RegistryException(401, "UNAUTHORIZED", "The user name or the password is wrong."), null);
            return;
        }

        var (_, minutes) = await SettingsAsync(http, db, cache, platform);
        var lifetime = TimeSpan.FromMinutes(minutes);
        var token = auth.Issue(actor, lifetime);
        await http.Response.WriteAsJsonAsync(new Dictionary<string, object>
        {
            ["token"] = token,
            ["access_token"] = token,
            ["expires_in"] = (int)lifetime.TotalSeconds,
            ["issued_at"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        }, http.RequestAborted);
    }

    private static async Task HandleAsync(HttpContext http, AppDbContext db, AccountService accounts, RegistryAuth auth, BlobStore store, UpstreamClient upstream,
        RegistrySecrets secrets, IMemoryCache cache, MatPlatformSettings platform, ILoggerFactory logs)
    {
        Prepare(http);
        var path = (http.Request.RouteValues["path"] as string ?? string.Empty).TrimEnd('/');
        var method = http.Request.Method;
        var write = !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method);
        var actor = await auth.AuthenticateAsync(db, accounts, http.Request.Headers.Authorization.ToString(), http.RequestAborted);
        string? scopeName = null;
        try
        {
            if (actor is null)
            {
                throw new RegistryException(401, "UNAUTHORIZED", "The credentials are not valid (or the token has expired).");
            }

            var c = new RegistryContext(db, store, upstream, secrets);
            if (path.Length == 0)
            {
                if (actor.IsAnonymous)
                {
                    throw RegistryException.Unauthorized();
                }

                await http.Response.WriteAsJsonAsync(new { }, http.RequestAborted);
                return;
            }

            if (path == "_catalog" && HttpMethods.IsGet(method))
            {
                var (names, more) = await RegistryEngine.CatalogAsync(c, actor, Count(http), http.Request.Query["last"], http.RequestAborted);
                Next(http, "/v2/_catalog", names, more);
                await http.Response.WriteAsJsonAsync(new { repositories = names }, http.RequestAborted);
                return;
            }

            if (TagsPath().Match(path) is { Success: true } tags && HttpMethods.IsGet(method))
            {
                scopeName = tags.Groups["name"].Value;
                var (list, more) = await RegistryEngine.TagsAsync(c, actor, scopeName, Count(http), http.Request.Query["last"], http.RequestAborted);
                Next(http, $"/v2/{scopeName}/tags/list", list, more);
                await http.Response.WriteAsJsonAsync(new { name = scopeName, tags = list }, http.RequestAborted);
                return;
            }

            if (UploadPath().Match(path + (path.EndsWith("/blobs/uploads", StringComparison.Ordinal) ? "/" : string.Empty)) is { Success: true } upload)
            {
                scopeName = upload.Groups["name"].Value;
                await UploadAsync(http, c, actor, scopeName, upload.Groups["id"].Value);
                return;
            }

            if (ItemPath().Match(path) is { Success: true } item)
            {
                scopeName = item.Groups["name"].Value;
                var reference = item.Groups["ref"].Value;
                switch (item.Groups["kind"].Value)
                {
                    case "manifests":
                        await ManifestAsync(http, c, actor, scopeName, reference);
                        return;
                    case "blobs":
                        await BlobAsync(http, c, actor, scopeName, reference);
                        return;
                    case "referrers" when HttpMethods.IsGet(method):
                        var (index, filtered) = await RegistryEngine.ReferrersAsync(c, actor, scopeName, reference, http.Request.Query["artifactType"], http.RequestAborted);
                        if (filtered)
                        {
                            http.Response.Headers["OCI-Filters-Applied"] = "artifactType";
                        }

                        http.Response.ContentType = MediaTypes.OciIndex;
                        await http.Response.Body.WriteAsync(index, http.RequestAborted);
                        return;
                }
            }

            throw new RegistryException(404, "NOT_FOUND", "There is no such address in the registry API.");
        }
        catch (RegistryException ex)
        {
            if (ex.Status == 401)
            {
                var (baseUrl, _) = await SettingsAsync(http, db, cache, platform);
                var scope = scopeName is null ? string.Empty : $",scope=\"repository:{scopeName}:{(write ? "pull,push" : "pull")}\"";
                http.Response.Headers.WWWAuthenticate = $"Bearer realm=\"{baseUrl}/v2/token\",service=\"{RegistryAuth.Service}\"{scope}";
            }

            if (ex.Status >= 500)
            {
                logs.CreateLogger("MatRegistry.Registry").LogWarning("{Method} /v2/{Path}: {Message}", method, path, ex.Message);
            }

            await ErrorAsync(http, ex, ex.Code == "BLOB_UPLOAD_INVALID" && ex.Status == 416 ? ex.Detail : null);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // the client went away
        }
    }

    private static async Task ManifestAsync(HttpContext http, RegistryContext c, RegistryActor actor, string name, string reference)
    {
        var request = http.Request;
        var response = http.Response;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            var head = HttpMethods.IsHead(request.Method);
            var manifest = await RegistryEngine.GetManifestAsync(c, actor, name, reference, head, http.RequestAborted);
            response.ContentType = manifest.MediaType;
            response.ContentLength = manifest.Content.Length;
            response.Headers["Docker-Content-Digest"] = manifest.Digest;
            response.Headers.ETag = $"\"{manifest.Digest}\"";
            if (!head)
            {
                await response.Body.WriteAsync(manifest.Content, http.RequestAborted);
            }

            return;
        }

        if (HttpMethods.IsPut(request.Method))
        {
            var content = await ReadLimitedAsync(request.Body, RegistryEngine.MaxManifestBytes, http.RequestAborted);
            var pushed = await RegistryEngine.PutManifestAsync(c, actor, name, reference, content, request.ContentType, http.RequestAborted);
            response.StatusCode = StatusCodes.Status201Created;
            response.Headers.Location = $"/v2/{name}/manifests/{pushed.Digest}";
            response.Headers["Docker-Content-Digest"] = pushed.Digest;
            if (pushed.Subject is not null)
            {
                response.Headers["OCI-Subject"] = pushed.Subject;
            }

            response.ContentLength = 0;
            return;
        }

        if (HttpMethods.IsDelete(request.Method))
        {
            await RegistryEngine.DeleteManifestAsync(c, actor, name, reference, http.RequestAborted);
            response.StatusCode = StatusCodes.Status202Accepted;
            response.ContentLength = 0;
            return;
        }

        throw RegistryException.Unsupported("This method is not supported for manifests.");
    }

    private static async Task BlobAsync(HttpContext http, RegistryContext c, RegistryActor actor, string name, string digest)
    {
        var request = http.Request;
        var response = http.Response;
        if (HttpMethods.IsDelete(request.Method))
        {
            await RegistryEngine.DeleteBlobAsync(c, actor, name, digest, http.RequestAborted);
            response.StatusCode = StatusCodes.Status202Accepted;
            response.ContentLength = 0;
            return;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            throw RegistryException.Unsupported("This method is not supported for blobs.");
        }

        var head = HttpMethods.IsHead(request.Method);
        var blob = await RegistryEngine.GetBlobAsync(c, actor, name, digest, head, http.RequestAborted);
        response.Headers["Docker-Content-Digest"] = blob.Digest;
        response.Headers.ETag = $"\"{blob.Digest}\"";
        response.Headers.CacheControl = "max-age=31536000";
        if (blob.Path is not null && !head)
        {
            // a file of the store: ranges as the client asks (a pull that resumes)
            await Results.File(blob.Path, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(http);
            return;
        }

        response.ContentType = "application/octet-stream";
        if (blob.Size >= 0)
        {
            response.ContentLength = blob.Size;
        }

        response.Headers.AcceptRanges = "bytes";
        if (!head && blob.Writer is not null)
        {
            await blob.Writer(response.Body, http.RequestAborted);
        }
    }

    private static async Task UploadAsync(HttpContext http, RegistryContext c, RegistryActor actor, string name, string id)
    {
        var request = http.Request;
        var response = http.Response;
        var method = request.Method;
        if (id.Length == 0)
        {
            if (!HttpMethods.IsPost(method))
            {
                throw RegistryException.Unsupported("An upload starts with POST.");
            }

            var started = await RegistryEngine.StartUploadAsync(c, actor, name, request.Query["mount"], request.Query["from"], request.Query["digest"], request.Body,
                http.RequestAborted);
            if (started.Digest is not null)
            {
                Created(response, name, started.Digest);
                return;
            }

            Progress(response, name, started, StatusCodes.Status202Accepted);
            return;
        }

        if (HttpMethods.IsPatch(method))
        {
            var state = await RegistryEngine.PatchUploadAsync(c, actor, name, id, request.Body, request.Headers.ContentRange.ToString(), http.RequestAborted);
            Progress(response, name, state, StatusCodes.Status202Accepted);
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            var digest = await RegistryEngine.CompleteUploadAsync(c, actor, name, id, request.Query["digest"], request.Body, http.RequestAborted);
            Created(response, name, digest);
            return;
        }

        if (HttpMethods.IsGet(method))
        {
            var state = await RegistryEngine.UploadStatusAsync(c, actor, name, id, http.RequestAborted);
            Progress(response, name, state, StatusCodes.Status204NoContent);
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            await RegistryEngine.CancelUploadAsync(c, actor, name, id, http.RequestAborted);
            response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        throw RegistryException.Unsupported("This method is not supported for uploads.");
    }

    private static void Created(HttpResponse response, string name, string digest)
    {
        response.StatusCode = StatusCodes.Status201Created;
        response.Headers.Location = $"/v2/{name}/blobs/{digest}";
        response.Headers["Docker-Content-Digest"] = digest;
        response.ContentLength = 0;
    }

    private static void Progress(HttpResponse response, string name, UploadResult state, int status)
    {
        var id = state.Token!.Value.ToString();
        response.StatusCode = status;
        response.Headers.Location = $"/v2/{name}/blobs/uploads/{id}";
        response.Headers.Range = state.Size > 0 ? string.Create(CultureInfo.InvariantCulture, $"0-{state.Size - 1}") : "0-0";
        response.Headers["Docker-Upload-UUID"] = id;
        response.ContentLength = 0;
    }

    /// <summary>The <c>Link</c> to the next page of a list (<c>n</c> and <c>last</c>).</summary>
    private static void Next(HttpContext http, string path, List<string> items, bool more)
    {
        if (more && items.Count > 0 && Count(http) is { } n)
        {
            http.Response.Headers.Link = $"<{path}?n={n}&last={Uri.EscapeDataString(items[^1])}>; rel=\"next\"";
        }
    }

    private static int? Count(HttpContext http)
        => int.TryParse(http.Request.Query["n"], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 0, 10000) : null;

    /// <summary>What every answer of the API needs: the version header, no limit on the body, no status code page of the site.</summary>
    private static void Prepare(HttpContext http)
    {
        http.Response.Headers[ApiVersionHeader] = "registry/2.0";
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }
    }

    private static async Task ErrorAsync(HttpContext http, RegistryException ex, object? range)
    {
        if (http.Response.HasStarted)
        {
            return;
        }

        http.Response.StatusCode = ex.Status;
        if (range is not null && JsonSerializer.SerializeToElement(range).TryGetProperty("size", out var size) && size.TryGetInt64(out var s))
        {
            http.Response.Headers.Range = s > 0 ? string.Create(CultureInfo.InvariantCulture, $"0-{s - 1}") : "0-0";
        }

        if (HttpMethods.IsHead(http.Request.Method))
        {
            return;
        }

        var error = new Dictionary<string, object?> { ["code"] = ex.Code, ["message"] = ex.Message };
        if (ex.Detail is not null)
        {
            error["detail"] = ex.Detail;
        }

        await http.Response.WriteAsJsonAsync(new { errors = new[] { error } }, http.RequestAborted);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
            {
                throw RegistryException.SizeInvalid("The manifest is larger than 4 MiB.");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The public address (the realm of the challenge) and the life of a token, from the settings (kept for 30 seconds).</summary>
    public static async Task<(string BaseUrl, int TokenMinutes)> SettingsAsync(HttpContext http, AppDbContext db, IMemoryCache cache, MatPlatformSettings platform)
    {
        var settings = await cache.GetOrCreateAsync("registry:settings", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            var row = await SettingsStore.LoadAsync(db);
            return (InstanceService.PublicUrlOf(row, platform), Math.Clamp(row.TokenMinutes, 1, 1440));
        });
        return (settings.Item1 ?? $"{http.Request.Scheme}://{http.Request.Host}", settings.Item2);
    }
}
