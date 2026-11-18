using System.Globalization;
using System.Text.Json;
using MatRegistry.Data;

namespace MatRegistry.Services.Registry;

/// <summary>The media types MatRegistry knows (Docker v2 schema 2 and OCI image 1.1).</summary>
public static class MediaTypes
{
    public const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    public const string DockerManifestList = "application/vnd.docker.distribution.manifest.list.v2+json";
    public const string DockerConfig = "application/vnd.docker.container.image.v1+json";
    public const string DockerSchema1 = "application/vnd.docker.distribution.manifest.v1+json";
    public const string DockerSchema1Signed = "application/vnd.docker.distribution.manifest.v1+prettyjws";
    public const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    public const string OciIndex = "application/vnd.oci.image.index.v1+json";
    public const string OciConfig = "application/vnd.oci.image.config.v1+json";
    public const string OciEmpty = "application/vnd.oci.empty.v1+json";

    /// <summary>What a client is told it may receive when MatRegistry asks another registry for a manifest.</summary>
    public static readonly string[] Accept = [OciIndex, DockerManifestList, OciManifest, DockerManifest];

    public static bool IsIndex(string? mediaType) => mediaType is OciIndex or DockerManifestList;

    public static bool IsManifest(string? mediaType) => mediaType is OciIndex or DockerManifestList or OciManifest or DockerManifest;

    public static bool IsImageConfig(string? mediaType) => mediaType is DockerConfig or OciConfig;

    /// <summary>A layer that is not pushed to a registry (Windows base layers): its blob is never asked for.</summary>
    public static bool IsForeign(string? mediaType)
        => mediaType is not null && (mediaType.Contains("foreign", StringComparison.Ordinal) || mediaType.Contains("nondistributable", StringComparison.Ordinal));

    /// <summary>The media type without parameters (<c>; charset=utf-8</c>).</summary>
    public static string? Clean(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
    }
}

/// <summary>A descriptor of a manifest: what it points at.</summary>
public sealed record Descriptor(string MediaType, string Digest, long Size, string? Platform, string? ArtifactType, IReadOnlyList<string> Urls);

/// <summary>
/// A manifest as MatRegistry reads it: an image (config and layers) or an index (manifests), with its subject and artifact type. Made by <see cref="Parse"/>, which also
/// says what is wrong with one (the message of <c>MANIFEST_INVALID</c>).
/// </summary>
public sealed class ManifestDocument
{
    public required string MediaType { get; init; }

    public required ManifestKind Kind { get; init; }

    public Descriptor? Config { get; init; }

    public IReadOnlyList<Descriptor> Layers { get; init; } = [];

    public IReadOnlyList<Descriptor> Manifests { get; init; } = [];

    public Descriptor? Subject { get; init; }

    public string? ArtifactType { get; init; }

    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();

    /// <summary>The config and the layers of an image, or the manifests of an index.</summary>
    public IEnumerable<Descriptor> Children => Kind == ManifestKind.Index ? Manifests : Config is null ? Layers : Layers.Prepend(Config);

    /// <summary>What it adds up to as far as the manifest says: config and layers of an image (an index: 0, its manifests count themselves).</summary>
    public long DeclaredSize => Kind == ManifestKind.Image ? (Config?.Size ?? 0) + Layers.Sum(l => l.Size) : 0;

    /// <summary>The artifact type as the referrers API names it: <c>artifactType</c>, else the type of a config that is not an image config.</summary>
    public string? EffectiveArtifactType
        => ArtifactType ?? (Kind == ManifestKind.Image && Config is { } c && !MediaTypes.IsImageConfig(c.MediaType) && c.MediaType != MediaTypes.OciEmpty ? c.MediaType : null);

    /// <summary>Reads a manifest. <paramref name="contentType"/> is the type the client sent; without one the document's own <c>mediaType</c> counts.</summary>
    public static (ManifestDocument? Manifest, string? Error) Parse(byte[] content, string? contentType)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return (null, "The manifest is no valid JSON.");
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "The manifest is no JSON object.");
            }

            var declared = Str(root, "mediaType");
            var mediaType = MediaTypes.Clean(contentType) ?? declared;
            if (mediaType is MediaTypes.DockerSchema1 or MediaTypes.DockerSchema1Signed || (root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number && schema.GetInt32() == 1))
            {
                return (null, "Image manifests of schema 1 are not supported; push with a current Docker or OCI client.");
            }

            var hasManifests = root.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array;
            var hasLayers = root.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array;
            mediaType ??= hasManifests ? MediaTypes.OciIndex : MediaTypes.OciManifest;
            if (!MediaTypes.IsManifest(mediaType))
            {
                return (null, $"The media type {mediaType} is not a manifest.");
            }

            if (declared is not null && MediaTypes.Clean(declared) != mediaType)
            {
                return (null, "The media type of the request and the one of the manifest differ.");
            }

            var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("annotations", out var ann) && ann.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ann.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                {
                    annotations[p.Name] = p.Value.GetString()!;
                }
            }

            Descriptor? subject = null;
            if (root.TryGetProperty("subject", out var subj) && subj.ValueKind == JsonValueKind.Object)
            {
                subject = Describe(subj, out var error);
                if (subject is null)
                {
                    return (null, error);
                }
            }

            var artifactType = Str(root, "artifactType");
            if (MediaTypes.IsIndex(mediaType))
            {
                if (!hasManifests)
                {
                    return (null, "An index needs a list of manifests.");
                }

                var list = new List<Descriptor>();
                foreach (var m in manifests.EnumerateArray())
                {
                    var d = Describe(m, out var error);
                    if (d is null)
                    {
                        return (null, error);
                    }

                    list.Add(d);
                }

                return (new ManifestDocument { MediaType = mediaType, Kind = ManifestKind.Index, Manifests = list, Subject = subject, ArtifactType = artifactType, Annotations = annotations }, null);
            }

            if (!root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
            {
                return (null, "An image manifest needs a config.");
            }

            var configDescriptor = Describe(config, out var configError);
            if (configDescriptor is null)
            {
                return (null, configError);
            }

            var layerList = new List<Descriptor>();
            if (hasLayers)
            {
                foreach (var l in layers.EnumerateArray())
                {
                    var d = Describe(l, out var error);
                    if (d is null)
                    {
                        return (null, error);
                    }

                    layerList.Add(d);
                }
            }

            return (new ManifestDocument
            {
                MediaType = mediaType,
                Kind = ManifestKind.Image,
                Config = configDescriptor,
                Layers = layerList,
                Subject = subject,
                ArtifactType = artifactType,
                Annotations = annotations,
            }, null);
        }
    }

    private static Descriptor? Describe(JsonElement e, out string? error)
    {
        error = null;
        var digest = Str(e, "digest");
        if (!RegistryNames.IsDigest(digest))
        {
            error = $"The digest {digest} is not valid (only sha256 is supported).";
            return null;
        }

        var size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) && n >= 0 ? n : -1;
        if (size < 0)
        {
            error = $"The size of {digest} is missing.";
            return null;
        }

        string? platform = null;
        if (e.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            platform = PlatformOf(Str(p, "os"), Str(p, "architecture"), Str(p, "variant"));
        }

        var urls = e.TryGetProperty("urls", out var u) && u.ValueKind == JsonValueKind.Array
            ? u.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        return new Descriptor(Str(e, "mediaType") ?? "application/octet-stream", digest!, size, platform, Str(e, "artifactType"), urls);
    }

    /// <summary><c>linux/amd64</c>, <c>linux/arm64/v8</c>; null without os and architecture.</summary>
    public static string? PlatformOf(string? os, string? architecture, string? variant)
        => string.IsNullOrEmpty(os) && string.IsNullOrEmpty(architecture) ? null : string.Join('/', new[] { os, architecture, variant }.Where(x => !string.IsNullOrEmpty(x)));

    internal static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>What the config of an image says (only what the pages show).</summary>
public sealed class ImageConfig
{
    public string? Os { get; init; }

    public string? Architecture { get; init; }

    public string? Variant { get; init; }

    public DateTime? Created { get; init; }

    public string? Author { get; init; }

    public string? User { get; init; }

    public string? WorkingDir { get; init; }

    public IReadOnlyList<string> Entrypoint { get; init; } = [];

    public IReadOnlyList<string> Cmd { get; init; } = [];

    public IReadOnlyList<string> Env { get; init; } = [];

    public IReadOnlyList<string> ExposedPorts { get; init; } = [];

    public IReadOnlyList<string> Volumes { get; init; } = [];

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>The steps of the build (created_by), oldest first, without the empty ones.</summary>
    public IReadOnlyList<(DateTime? Created, string Step, bool EmptyLayer)> History { get; init; } = [];

    /// <summary>Reads the config of an image; null when it is no JSON object.</summary>
    public static ImageConfig? Parse(byte[] content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var config = root.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var history = new List<(DateTime?, string, bool)>();
            if (root.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in h.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                {
                    var by = ManifestDocument.Str(step, "created_by") ?? ManifestDocument.Str(step, "comment") ?? string.Empty;
                    var empty = step.TryGetProperty("empty_layer", out var el) && el.ValueKind == JsonValueKind.True;
                    history.Add((Date(ManifestDocument.Str(step, "created")), by, empty));
                }
            }

            return new ImageConfig
            {
                Os = ManifestDocument.Str(root, "os"),
                Architecture = ManifestDocument.Str(root, "architecture"),
                Variant = ManifestDocument.Str(root, "variant"),
                Created = Date(ManifestDocument.Str(root, "created")),
                Author = ManifestDocument.Str(root, "author"),
                User = config.ValueKind == JsonValueKind.Object ? ManifestDocument.Str(config, "User") : null,
                WorkingDir = config.ValueKind == JsonValueKind.Object ? ManifestDocument.Str(config, "WorkingDir") : null,
                Entrypoint = Strings(config, "Entrypoint"),
                Cmd = Strings(config, "Cmd"),
                Env = Strings(config, "Env"),
                ExposedPorts = Keys(config, "ExposedPorts"),
                Volumes = Keys(config, "Volumes"),
                Labels = Map(config, "Labels"),
                History = history,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime? Date(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1971 ? d.UtcDateTime : null;

    private static List<string> Strings(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    private static List<string> Keys(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? [.. v.EnumerateObject().Select(p => p.Name)] : [];

    private static Dictionary<string, string> Map(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}
