namespace MatRegistry.Data;

public enum UserRole
{
    Admin = 1,
    User = 2,
}

/// <summary>What a person may do with the images of a registry.</summary>
public enum AccessRole
{
    None = 0,

    /// <summary>May see the repositories and pull their images (for a remote registry: browse it and copy from it).</summary>
    Viewer = 1,

    /// <summary>May also push, delete tags and images, and copy into the registry.</summary>
    Editor = 2,
}

/// <summary>A registry is either kept by MatRegistry itself or another registry it is bound to.</summary>
public enum RegistryKind
{
    /// <summary>Served by MatRegistry: <c>&lt;host&gt;/&lt;registry&gt;/&lt;repository&gt;</c>, images are pushed here.</summary>
    Hosted = 1,

    /// <summary>Another registry (Docker Hub, GHCR, a second MatRegistry …): MatRegistry browses it, copies from and to it and, as a cache, pulls through it.</summary>
    Remote = 2,
}

/// <summary>Who may pull without signing in.</summary>
public enum RegistryVisibility
{
    /// <summary>Only people with a role (and the apps bound through <c>registry.docker</c>).</summary>
    Private = 0,

    /// <summary>Anybody may pull, also without an account; pushing still needs the role Editor.</summary>
    Public = 1,
}

/// <summary>
/// A registry: one that MatRegistry serves (<see cref="RegistryKind.Hosted"/>, its name is the first part of every repository in it) or another one it is bound to
/// (<see cref="RegistryKind.Remote"/>). A remote registry with <see cref="ProxyEnabled"/> is also served under its name, as a pull-through cache.
/// </summary>
public sealed class Registry : SoftEntity
{
    /// <summary>The name in the addresses: small letters, digits and <c>. _ -</c> between them (<c>team</c>, <c>dockerhub</c>). Unique among the registries that exist.</summary>
    public string Name { get; set; } = string.Empty;

    public RegistryKind Kind { get; set; } = RegistryKind.Hosted;

    public string? Description { get; set; }

    public RegistryVisibility Visibility { get; set; } = RegistryVisibility.Private;

    public bool IsActive { get; set; } = true;

    /// <summary>The address of a remote registry (<c>https://registry-1.docker.io</c>, <c>https://ghcr.io</c>).</summary>
    public string? Url { get; set; }

    /// <summary>The user at the remote registry; empty = anonymous.</summary>
    public string? Username { get; set; }

    /// <summary>The password or token at the remote registry, protected with the data protection keys of the volume. Never leaves the business layer.</summary>
    public string? SecretProtected { get; set; }

    /// <summary>A remote registry is served as a pull-through cache under <c>&lt;host&gt;/&lt;name&gt;/…</c>.</summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>How long a tag of the cache is trusted before the remote registry is asked again (minutes).</summary>
    public int CacheMinutes { get; set; } = 60;

    /// <summary>Accept a certificate the system does not trust (a remote registry in the own network with a certificate of its own).</summary>
    public bool SkipTlsVerify { get; set; }

    public DateTime? LastCheckDate { get; set; }

    public bool? IsReachable { get; set; }

    public string? LastCheckResult { get; set; }

    public List<RegistryAccessEntry> Access { get; set; } = [];
}

/// <summary>The role of one person in one registry. Administrators need none.</summary>
public sealed class RegistryAccessEntry : Entity
{
    public long RegistryId { get; set; }

    public Registry? Registry { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public AccessRole Role { get; set; } = AccessRole.Viewer;
}

/// <summary>
/// A repository of a registry (<c>team/web</c> in <c>&lt;host&gt;/&lt;registry&gt;/team/web</c>). It is made by the first push (or the first pull through a cache) and holds
/// its manifests and tags. Deleting it removes them; the files of the layers go with the next garbage collection.
/// </summary>
public sealed class Repository : Entity
{
    public long RegistryId { get; set; }

    public Registry? Registry { get; set; }

    /// <summary>The path inside the registry: lower case parts separated by <c>/</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? LastPushDate { get; set; }

    public DateTime? LastPullDate { get; set; }

    public long PullCount { get; set; }

    public List<Tag> Tags { get; set; } = [];

    public List<Manifest> Manifests { get; set; } = [];
}

/// <summary>What kind of manifest it is.</summary>
public enum ManifestKind
{
    /// <summary>An image (or an artifact): a config and layers.</summary>
    Image = 1,

    /// <summary>An index (a multi-platform image): a list of manifests.</summary>
    Index = 2,
}

/// <summary>A manifest of a repository, addressed by its digest. Its bytes are kept as they were pushed (the digest is over them).</summary>
public sealed class Manifest : Entity
{
    public long RepositoryId { get; set; }

    public Repository? Repository { get; set; }

    /// <summary><c>sha256:…</c></summary>
    public string Digest { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public ManifestKind Kind { get; set; }

    /// <summary>The bytes as they were pushed.</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>The size of the manifest itself.</summary>
    public long Size { get; set; }

    /// <summary>An image: the config and every layer; an index: what its manifests add up to (as far as they are here).</summary>
    public long TotalSize { get; set; }

    /// <summary>The type of an artifact (<c>artifactType</c>, else the media type of its config when that is no image config).</summary>
    public string? ArtifactType { get; set; }

    /// <summary>The manifest this one refers to (<c>subject</c>: a signature, an SBOM): the referrers API lists it.</summary>
    public string? SubjectDigest { get; set; }

    /// <summary>Of an image: from its config.</summary>
    public string? Os { get; set; }

    public string? Architecture { get; set; }

    public string? Variant { get; set; }

    /// <summary>When the image was built (its config says so); null for an index and an artifact.</summary>
    public DateTime? ImageCreatedDate { get; set; }

    public DateTime? LastPullDate { get; set; }

    public List<ManifestReference> References { get; set; } = [];
}

/// <summary>What a manifest refers to: a blob (its config, a layer) or, of an index, another manifest.</summary>
public enum ReferenceKind
{
    Config = 0,
    Layer = 1,
    Manifest = 2,
}

/// <summary>One descriptor of a manifest: what it needs, in order. The garbage collection keeps every blob some manifest refers to.</summary>
public sealed class ManifestReference : Entity
{
    public long ManifestId { get; set; }

    public Manifest? Manifest { get; set; }

    public ReferenceKind Kind { get; set; }

    public int Position { get; set; }

    public string Digest { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Of a manifest in an index: <c>linux/amd64</c>, <c>linux/arm64/v8</c>.</summary>
    public string? Platform { get; set; }
}

/// <summary>A tag: a name of a repository that points at a manifest.</summary>
public sealed class Tag : Entity
{
    public long RepositoryId { get; set; }

    public Repository? Repository { get; set; }

    public string Name { get; set; } = string.Empty;

    public long ManifestId { get; set; }

    public Manifest? Manifest { get; set; }

    /// <summary>A tag of a cache: when the remote registry was last asked about it.</summary>
    public DateTime? CheckedDate { get; set; }
}

/// <summary>A file of the blob store (<c>/data/registry/blobs/sha256/ab/abcdef…</c>), stored once for every repository that has it.</summary>
public sealed class Blob : Entity
{
    public string Digest { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime? LastPullDate { get; set; }
}

/// <summary>
/// A blob belongs to a repository: it was pushed or mounted there, or a manifest of it needs it. A blob is only served in a repository it belongs to, so nobody reads
/// the layers of a private repository by naming their digest somewhere else.
/// </summary>
public sealed class RepositoryBlob : Entity
{
    public long RepositoryId { get; set; }

    public Repository? Repository { get; set; }

    public long BlobId { get; set; }

    public Blob? Blob { get; set; }
}

/// <summary>An upload in progress. The address of the upload names the <see cref="Token"/> (a secret of the uploader); the bytes are in <c>/data/registry/uploads</c>.</summary>
public sealed class BlobUpload : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long RepositoryId { get; set; }

    public Repository? Repository { get; set; }

    /// <summary>How many bytes have arrived.</summary>
    public long Size { get; set; }

    /// <summary>Who started it (null: an app bound through <c>registry.docker</c>).</summary>
    public long? UserId { get; set; }
}

/// <summary>What a personal access token may do (never more than the person may).</summary>
public enum TokenScope
{
    Pull = 1,
    PullPush = 2,
}

/// <summary>
/// A personal access token for <c>docker login</c> (people who sign in through MatIdentity have no password here). Only its hash is kept; it is shown once.
/// <see cref="Token"/> is its public id in the addresses of the pages.
/// </summary>
public sealed class AccessToken : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the secret (hex).</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The first characters of the secret, to tell tokens apart.</summary>
    public string SecretPrefix { get; set; } = string.Empty;

    public TokenScope Scope { get; set; } = TokenScope.Pull;

    public DateTime? ExpiresDate { get; set; }

    public DateTime? LastUsedDate { get; set; }
}

public enum CopyState
{
    Waiting = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
}

/// <summary>
/// A copy of an image from one registry to another (both may be hosted or remote): the manifest, the manifests of every platform and every blob. The copy worker
/// runs it in the background; <see cref="Token"/> addresses it.
/// </summary>
public sealed class CopyJob : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public long SourceRegistryId { get; set; }

    public Registry? SourceRegistry { get; set; }

    public string SourceRepository { get; set; } = string.Empty;

    /// <summary>A tag or a digest.</summary>
    public string SourceReference { get; set; } = string.Empty;

    public long TargetRegistryId { get; set; }

    public Registry? TargetRegistry { get; set; }

    public string TargetRepository { get; set; } = string.Empty;

    /// <summary>The tag at the target; empty = only by digest.</summary>
    public string TargetTag { get; set; } = string.Empty;

    public CopyState State { get; set; }

    public string? Digest { get; set; }

    public int BlobsTotal { get; set; }

    public int BlobsCopied { get; set; }

    public int BlobsSkipped { get; set; }

    public long BytesCopied { get; set; }

    public string? Message { get; set; }

    public DateTime? StartedDate { get; set; }

    public DateTime? FinishedDate { get; set; }
}

/// <summary>What happened: a push, a deleted tag, a registry changed, a copy, a garbage collection … (the log of the administration).</summary>
public sealed class Activity : Entity
{
    public long? UserId { get; set; }

    /// <summary>An app bound through <c>registry.docker</c> that did it.</summary>
    public string? Consumer { get; set; }

    public long? RegistryId { get; set; }

    public long? RepositoryId { get; set; }

    /// <summary>push, delete, registry, access, copy, gc, token, setting …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The name of the item at the time (it may be gone later).</summary>
    public string? Target { get; set; }

    public string? Detail { get; set; }

    public DateTime OccurredDate { get; set; }
}

/// <summary>The settings of the whole installation (one row, Id 1).</summary>
public sealed class SystemSetting : Entity
{
    /// <summary>Whether anyone may create an account (the first account of an installation can always sign up and becomes the administrator).</summary>
    public bool RegistrationOpen { get; set; }

    /// <summary>The address MatRegistry is reached at from outside (pull commands, the realm of the token, links in mails); empty = the address of the request.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>How long a token of the registry API lives (minutes).</summary>
    public int TokenMinutes { get; set; } = 15;

    /// <summary>Run the garbage collection by itself every this many hours (0 = only by hand).</summary>
    public int GcIntervalHours { get; set; } = 24;

    /// <summary>The garbage collection also removes manifests that no tag and no index points at.</summary>
    public bool GcUntagged { get; set; }

    public DateTime? LastGcDate { get; set; }

    public string? LastGcResult { get; set; }
}
