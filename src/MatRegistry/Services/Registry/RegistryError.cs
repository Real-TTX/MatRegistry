namespace MatRegistry.Services.Registry;

/// <summary>
/// An error of the registry API as the distribution spec names it (<c>{"errors": [{"code", "message", "detail"}]}</c>). The engine throws it; the endpoints answer it
/// with its status (and, for 401, the challenge of the token service).
/// </summary>
public sealed class RegistryException(int status, string code, string message, object? detail = null) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public object? Detail { get; } = detail;

    public static RegistryException NameInvalid(string name) => new(400, "NAME_INVALID", "The repository name is not valid.", new { name });

    public static RegistryException NameUnknown(string name) => new(404, "NAME_UNKNOWN", "The repository is not known.", new { name });

    public static RegistryException ManifestUnknown(string reference) => new(404, "MANIFEST_UNKNOWN", "The manifest is not known.", new { reference });

    public static RegistryException ManifestInvalid(string message) => new(400, "MANIFEST_INVALID", message);

    public static RegistryException ManifestBlobUnknown(string digest) => new(404, "MANIFEST_BLOB_UNKNOWN", "A blob the manifest needs is not in the repository.", new { digest });

    public static RegistryException BlobUnknown(string digest) => new(404, "BLOB_UNKNOWN", "The blob is not known in this repository.", new { digest });

    public static RegistryException UploadUnknown() => new(404, "BLOB_UPLOAD_UNKNOWN", "The upload is not known.");

    public static RegistryException UploadInvalid(string message) => new(400, "BLOB_UPLOAD_INVALID", message);

    public static RegistryException DigestInvalid(string message) => new(400, "DIGEST_INVALID", message);

    public static RegistryException SizeInvalid(string message) => new(400, "SIZE_INVALID", message);

    public static RegistryException TagInvalid(string tag) => new(400, "TAG_INVALID", "The tag is not valid.", new { tag });

    public static RegistryException Unauthorized() => new(401, "UNAUTHORIZED", "Authentication is required.");

    public static RegistryException Denied() => new(403, "DENIED", "Access to the resource is denied.");

    public static RegistryException Unsupported(string message) => new(405, "UNSUPPORTED", message);

    public static RegistryException Upstream(string message) => new(502, "UNAVAILABLE", message);

    /// <summary>The answer when the actor may not: 401 for nobody (so a client signs in), 403 for somebody.</summary>
    public static RegistryException NotAllowed(RegistryActor actor) => actor.IsAnonymous ? Unauthorized() : Denied();
}
