using System.Text.Json;
using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MatRegistry.Services.Registry;

/// <summary>A repository named in a request of the registry API, with the registry it lives in and the role of the actor there.</summary>
public sealed record ResolvedRepository(Data.Registry Registry, Repository? Repository, string Name, string FullName, AccessRole Role)
{
    /// <summary>A remote registry served as a pull-through cache.</summary>
    public bool IsProxy => Registry.Kind == RegistryKind.Remote;
}

public sealed record ManifestResult(byte[] Content, string MediaType, string Digest);

/// <summary>A blob to send: a file of the store (<see cref="Path"/>), or, from the cache of a remote registry, a writer that streams it while it is stored.</summary>
public sealed record BlobResult(string Digest, long Size, string? Path, Func<Stream, CancellationToken, Task>? Writer = null);

/// <summary>The answer of an upload: a blob that is complete (<see cref="Digest"/>) or an upload in progress (<see cref="Token"/> and its size).</summary>
public sealed record UploadResult(Guid? Token, long Size, string? Digest);

public sealed record PushResult(string Digest, string? Subject);

/// <summary>
/// THE business layer of the registry API (OCI Distribution 1.1, the Docker Registry HTTP API v2): manifests, tags, blobs, uploads, the catalog and the referrers.
/// Every call names the actor and is checked here (<see cref="RegistryPermissions"/>); a remote registry with the cache on is served by <see cref="ProxyCache"/>.
/// The endpoints (<c>RegistryEndpoints</c>) only turn HTTP into these calls and the answers back.
/// </summary>
public static class RegistryEngine
{
    /// <summary>The largest manifest that is taken (the spec lets a registry refuse larger ones; 4 MiB is what the others take).</summary>
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>The registry and the repository of a name, checked for the actor. A push creates the repository; a pull of a repository that does not exist is unknown.</summary>
    public static async Task<ResolvedRepository> ResolveAsync(RegistryContext c, RegistryActor actor, string name, bool write, CancellationToken ct)
    {
        if (!RegistryNames.IsName(name))
        {
            throw RegistryException.NameInvalid(name);
        }

        var split = RegistryNames.Split(name);
        if (split is null)
        {
            throw actor.IsAnonymous
                ? RegistryException.Unauthorized()
                : new RegistryException(404, "NAME_UNKNOWN", "A name of MatRegistry starts with its registry: <host>/<registry>/<repository>.", new { name });
        }

        var (registryName, repositoryName) = split.Value;
        var registry = await c.Db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Name == registryName && r.IsActive, ct);
        if (registry is null || (registry.Kind == RegistryKind.Remote && !registry.ProxyEnabled))
        {
            throw actor.IsAnonymous
                ? RegistryException.Unauthorized()
                : new RegistryException(404, "NAME_UNKNOWN", $"There is no registry “{registryName}”; an administrator creates it in MatRegistry.", new { name });
        }

        var role = await RegistryPermissions.RoleAsync(c.Db, actor, registry, ct);
        if (role < (write ? AccessRole.Editor : AccessRole.Viewer))
        {
            throw RegistryException.NotAllowed(actor);
        }

        if (write && registry.Kind == RegistryKind.Remote)
        {
            throw new RegistryException(403, "DENIED", "This registry is a cache of another one and takes no pushes; push to a hosted registry.");
        }

        var repository = await c.Db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.RegistryId == registry.Id && r.Name == repositoryName, ct);
        return new ResolvedRepository(registry, repository, repositoryName, name, role);
    }

    // ---- manifests ----

    public static async Task<ManifestResult> GetManifestAsync(RegistryContext c, RegistryActor actor, string name, string reference, bool head, CancellationToken ct)
    {
        CheckReference(reference);
        var r = await ResolveAsync(c, actor, name, write: false, ct);
        if (r.IsProxy)
        {
            return await ProxyCache.GetManifestAsync(c, r, reference, head, ct);
        }

        if (r.Repository is null)
        {
            throw RegistryException.NameUnknown(name);
        }

        var manifest = await FindManifestAsync(c.Db, r.Repository.Id, reference, ct) ?? throw RegistryException.ManifestUnknown(reference);
        if (!head && !RegistryNames.IsDigest(reference))
        {
            await CountPullAsync(c.Db, r.Repository.Id, manifest.Id, ct);
        }

        return new ManifestResult(manifest.Content, manifest.MediaType, manifest.Digest);
    }

    public static async Task<PushResult> PutManifestAsync(RegistryContext c, RegistryActor actor, string name, string reference, byte[] content, string? contentType,
        CancellationToken ct)
    {
        var byDigest = RegistryNames.IsDigest(reference);
        if (!byDigest && !RegistryNames.IsTag(reference))
        {
            throw RegistryException.TagInvalid(reference);
        }

        if (content.Length > MaxManifestBytes)
        {
            throw RegistryException.SizeInvalid("The manifest is larger than 4 MiB.");
        }

        var r = await ResolveAsync(c, actor, name, write: true, ct);
        var digest = RegistryNames.DigestOf(content);
        if (byDigest && reference != digest)
        {
            throw RegistryException.DigestInvalid("The digest of the address is not the digest of the manifest.");
        }

        var repository = r.Repository ?? await EnsureRepositoryAsync(c.Db, r.Registry.Id, r.Name, actor.UserId, ct);
        var manifest = await StoreManifestAsync(c, repository.Id, content, contentType, byDigest ? null : reference, requireReferences: true, actor.UserId, null, ct);
        var now = DateTime.UtcNow;
        await c.Db.Repositories.Where(x => x.Id == repository.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPushDate, now).SetProperty(x => x.UpdateDate, now), ct);
        if (!byDigest)
        {
            await ActivityLog.AddAsync(c.Db, actor, "push", $"{name}:{reference}", RegistryNames.Short(digest), r.Registry.Id, repository.Id, ct);
        }

        return new PushResult(digest, manifest.SubjectDigest);
    }

    /// <summary>Deletes a manifest (by digest: with every tag on it) or only a tag (by its name).</summary>
    public static async Task DeleteManifestAsync(RegistryContext c, RegistryActor actor, string name, string reference, CancellationToken ct)
    {
        CheckReference(reference);
        var r = await ResolveAsync(c, actor, name, write: true, ct);
        if (r.Repository is null)
        {
            throw RegistryException.NameUnknown(name);
        }

        if (RegistryNames.IsDigest(reference))
        {
            var deleted = await c.Db.Manifests.Where(m => m.RepositoryId == r.Repository.Id && m.Digest == reference).ExecuteDeleteAsync(ct);
            if (deleted == 0)
            {
                throw RegistryException.ManifestUnknown(reference);
            }
        }
        else if (await c.Db.Tags.Where(t => t.RepositoryId == r.Repository.Id && t.Name == reference).ExecuteDeleteAsync(ct) == 0)
        {
            throw RegistryException.ManifestUnknown(reference);
        }

        await ActivityLog.AddAsync(c.Db, actor, "delete", $"{name}{(RegistryNames.IsDigest(reference) ? "@" : ":")}{reference}", null, r.Registry.Id, r.Repository.Id, ct);
    }

    /// <summary>The tags of a repository in lexical order, a page of <paramref name="count"/> after <paramref name="last"/>.</summary>
    public static async Task<(List<string> Tags, bool More)> TagsAsync(RegistryContext c, RegistryActor actor, string name, int? count, string? last, CancellationToken ct)
    {
        var r = await ResolveAsync(c, actor, name, write: false, ct);
        List<string> all;
        if (r.IsProxy)
        {
            all = await ProxyCache.TagsAsync(c, r, ct);
        }
        else
        {
            if (r.Repository is null)
            {
                throw RegistryException.NameUnknown(name);
            }

            all = await c.Db.Tags.AsNoTracking().Where(t => t.RepositoryId == r.Repository.Id).Select(t => t.Name).ToListAsync(ct);
        }

        return Page(all, count, last);
    }

    /// <summary>The repositories the actor may pull from, in lexical order (<c>&lt;registry&gt;/&lt;repository&gt;</c>).</summary>
    public static async Task<(List<string> Names, bool More)> CatalogAsync(RegistryContext c, RegistryActor actor, int? count, string? last, CancellationToken ct)
    {
        var roles = await RegistryPermissions.RolesAsync(c.Db, actor, ct);
        var ids = roles.Keys.ToList();
        var names = await c.Db.Repositories.AsNoTracking()
            .Where(r => ids.Contains(r.RegistryId) && r.Registry!.IsActive && (r.Registry.Kind == RegistryKind.Hosted || r.Registry.ProxyEnabled))
            .Select(r => r.Registry!.Name + "/" + r.Name)
            .ToListAsync(ct);
        return Page(names, count, last);
    }

    /// <summary>The manifests that name <paramref name="digest"/> as their subject (signatures, SBOMs), as an OCI index.</summary>
    public static async Task<(byte[] Index, bool Filtered)> ReferrersAsync(RegistryContext c, RegistryActor actor, string name, string digest, string? artifactType,
        CancellationToken ct)
    {
        if (!RegistryNames.IsDigest(digest))
        {
            throw RegistryException.DigestInvalid("The digest is not valid.");
        }

        var r = await ResolveAsync(c, actor, name, write: false, ct);
        var rows = r.Repository is null
            ? []
            : await c.Db.Manifests.AsNoTracking().Where(m => m.RepositoryId == r.Repository.Id && m.SubjectDigest == digest)
                .Select(m => new { m.Digest, m.MediaType, m.Size, m.ArtifactType, m.Content }).ToListAsync(ct);
        var filtered = !string.IsNullOrEmpty(artifactType);
        var manifests = rows.Where(m => !filtered || m.ArtifactType == artifactType).OrderBy(m => m.Digest, StringComparer.Ordinal).Select(m =>
        {
            var (doc, _) = ManifestDocument.Parse(m.Content, m.MediaType);
            return new Dictionary<string, object?>
            {
                ["mediaType"] = m.MediaType,
                ["digest"] = m.Digest,
                ["size"] = m.Size,
                ["artifactType"] = m.ArtifactType,
                ["annotations"] = doc is { Annotations.Count: > 0 } ? doc.Annotations : null,
            }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value);
        }).ToList();
        var index = new Dictionary<string, object> { ["schemaVersion"] = 2, ["mediaType"] = MediaTypes.OciIndex, ["manifests"] = manifests };
        return (JsonSerializer.SerializeToUtf8Bytes(index), filtered);
    }

    // ---- blobs ----

    public static async Task<BlobResult> GetBlobAsync(RegistryContext c, RegistryActor actor, string name, string digest, bool head, CancellationToken ct)
    {
        if (!RegistryNames.IsDigest(digest))
        {
            throw RegistryException.DigestInvalid("The digest is not valid.");
        }

        var r = await ResolveAsync(c, actor, name, write: false, ct);
        if (r.Repository is not null && await LinkedSizeAsync(c.Db, r.Repository.Id, digest, ct) is { } size && c.Store.Exists(digest))
        {
            return new BlobResult(digest, size, c.Store.PathOf(digest));
        }

        if (r.IsProxy)
        {
            return await ProxyCache.GetBlobAsync(c, r, digest, head, ct);
        }

        throw RegistryException.BlobUnknown(digest);
    }

    /// <summary>Takes a blob out of a repository (the file goes with the garbage collection, when nothing needs it any more).</summary>
    public static async Task DeleteBlobAsync(RegistryContext c, RegistryActor actor, string name, string digest, CancellationToken ct)
    {
        if (!RegistryNames.IsDigest(digest))
        {
            throw RegistryException.DigestInvalid("The digest is not valid.");
        }

        var r = await ResolveAsync(c, actor, name, write: true, ct);
        var removed = r.Repository is null ? 0 : await c.Db.RepositoryBlobs.Where(l => l.RepositoryId == r.Repository.Id && l.Blob!.Digest == digest).ExecuteDeleteAsync(ct);
        if (removed == 0)
        {
            throw RegistryException.BlobUnknown(digest);
        }
    }

    // ---- uploads ----

    /// <summary>
    /// <c>POST /v2/&lt;name&gt;/blobs/uploads/</c>: mounts a blob from another repository the actor may pull from (<paramref name="mount"/> and <paramref name="from"/>),
    /// takes a whole blob at once (<paramref name="digest"/> with the body), or starts an upload.
    /// </summary>
    public static async Task<UploadResult> StartUploadAsync(RegistryContext c, RegistryActor actor, string name, string? mount, string? from, string? digest, Stream body,
        CancellationToken ct)
    {
        var r = await ResolveAsync(c, actor, name, write: true, ct);
        var repository = r.Repository ?? await EnsureRepositoryAsync(c.Db, r.Registry.Id, r.Name, actor.UserId, ct);

        if (RegistryNames.IsDigest(mount) && !string.IsNullOrEmpty(from) && await TryMountAsync(c, actor, repository.Id, mount!, from, ct) is { } mounted)
        {
            return new UploadResult(null, mounted, mount);
        }

        if (!string.IsNullOrEmpty(digest))
        {
            if (!RegistryNames.IsDigest(digest))
            {
                throw RegistryException.DigestInvalid("The digest is not valid (only sha256 is supported).");
            }

            var temp = c.Store.NewTempPath();
            await using (var file = File.Create(temp))
            {
                await body.CopyToAsync(file, ct);
            }

            var size = await c.Store.CommitAsync(temp, digest, ct);
            if (size < 0)
            {
                throw RegistryException.DigestInvalid("The content does not have the digest that was named.");
            }

            await LinkBlobAsync(c.Db, repository.Id, digest, size, actor.UserId, ct);
            return new UploadResult(null, size, digest);
        }

        var upload = new BlobUpload { RepositoryId = repository.Id, UserId = actor.UserId, CreateUserId = actor.UserId };
        c.Store.StartUpload(upload.Token);
        c.Db.BlobUploads.Add(upload);
        await c.Db.SaveChangesAsync(ct);
        return new UploadResult(upload.Token, 0, null);
    }

    /// <summary><c>PATCH</c>: a chunk. With <c>Content-Range</c> it must start where the upload stands (else 416 with the size it has).</summary>
    public static async Task<UploadResult> PatchUploadAsync(RegistryContext c, RegistryActor actor, string name, string token, Stream body, string? contentRange,
        CancellationToken ct)
    {
        var upload = await FindUploadAsync(c, actor, name, token, ct);
        var size = c.Store.UploadSize(upload.Token);
        if (size < 0)
        {
            throw RegistryException.UploadUnknown();
        }

        if (!string.IsNullOrWhiteSpace(contentRange))
        {
            var start = contentRange.Split('-')[0].Trim();
            if (start.StartsWith("bytes", StringComparison.OrdinalIgnoreCase))
            {
                start = start[5..].Trim(' ', '=');
            }

            if (!long.TryParse(start, out var from) || from != size)
            {
                throw new RegistryException(416, "BLOB_UPLOAD_INVALID", "The chunk does not start where the upload stands.", new { size });
            }
        }

        size = await c.Store.AppendAsync(upload.Token, body, ct);
        await c.Db.BlobUploads.Where(u => u.Id == upload.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Size, size).SetProperty(u => u.UpdateDate, DateTime.UtcNow), ct);
        return new UploadResult(upload.Token, size, null);
    }

    /// <summary><c>PUT ?digest=</c>: the last chunk (maybe empty); the blob is stored when its content has the digest.</summary>
    public static async Task<string> CompleteUploadAsync(RegistryContext c, RegistryActor actor, string name, string token, string? digest, Stream body, CancellationToken ct)
    {
        if (!RegistryNames.IsDigest(digest))
        {
            throw RegistryException.DigestInvalid("The digest is missing or not valid (only sha256 is supported).");
        }

        var upload = await FindUploadAsync(c, actor, name, token, ct);
        if (c.Store.UploadSize(upload.Token) < 0)
        {
            throw RegistryException.UploadUnknown();
        }

        await c.Store.AppendAsync(upload.Token, body, ct);
        var size = await c.Store.CommitAsync(c.Store.UploadPath(upload.Token), digest!, ct);
        await c.Db.BlobUploads.Where(u => u.Id == upload.Id).ExecuteDeleteAsync(ct);
        if (size < 0)
        {
            throw RegistryException.DigestInvalid("The content does not have the digest that was named.");
        }

        await LinkBlobAsync(c.Db, upload.RepositoryId, digest!, size, actor.UserId, ct);
        return digest!;
    }

    public static async Task<UploadResult> UploadStatusAsync(RegistryContext c, RegistryActor actor, string name, string token, CancellationToken ct)
    {
        var upload = await FindUploadAsync(c, actor, name, token, ct);
        var size = c.Store.UploadSize(upload.Token);
        return size < 0 ? throw RegistryException.UploadUnknown() : new UploadResult(upload.Token, size, null);
    }

    public static async Task CancelUploadAsync(RegistryContext c, RegistryActor actor, string name, string token, CancellationToken ct)
    {
        var upload = await FindUploadAsync(c, actor, name, token, ct);
        c.Store.DeleteUpload(upload.Token);
        await c.Db.BlobUploads.Where(u => u.Id == upload.Id).ExecuteDeleteAsync(ct);
    }

    // ---- shared by the pull-through cache and the copies ----

    /// <summary>The repository of a registry; made when it is not there yet (two pushes at the same moment end with the same row).</summary>
    public static async Task<Repository> EnsureRepositoryAsync(AppDbContext db, long registryId, string name, long? userId, CancellationToken ct)
    {
        if (!RegistryNames.IsName(name))
        {
            throw RegistryException.NameInvalid(name);
        }

        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Repository\" (\"RegistryId\", \"Name\", \"PullCount\", \"CreateDate\", \"UpdateDate\", \"CreateUserId\", \"UpdateUserId\") " +
            "VALUES (@registry, @name, 0, @now, @now, @user, @user) ON CONFLICT (\"RegistryId\", \"Name\") DO NOTHING",
            [new NpgsqlParameter("registry", registryId), new NpgsqlParameter("name", name), new NpgsqlParameter("now", now), UserParameter(userId)], ct);
        return await db.Repositories.AsNoTracking().FirstAsync(r => r.RegistryId == registryId && r.Name == name, ct);
    }

    /// <summary>Records a blob of the store (once) and that it belongs to the repository.</summary>
    public static async Task LinkBlobAsync(AppDbContext db, long repositoryId, string digest, long size, long? userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Blob\" (\"Digest\", \"Size\", \"CreateDate\", \"UpdateDate\", \"CreateUserId\", \"UpdateUserId\") VALUES (@digest, @size, @now, @now, @user, @user) " +
            "ON CONFLICT (\"Digest\") DO NOTHING",
            [new NpgsqlParameter("digest", digest), new NpgsqlParameter("size", size), new NpgsqlParameter("now", now), UserParameter(userId)], ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"RepositoryBlob\" (\"RepositoryId\", \"BlobId\", \"CreateDate\", \"UpdateDate\", \"CreateUserId\", \"UpdateUserId\") " +
            "SELECT @repository, b.\"Id\", @now, @now, @user, @user FROM \"Blob\" b WHERE b.\"Digest\" = @digest " +
            "ON CONFLICT (\"RepositoryId\", \"BlobId\") DO NOTHING",
            [new NpgsqlParameter("repository", repositoryId), new NpgsqlParameter("digest", digest), new NpgsqlParameter("now", now), UserParameter(userId)], ct);
    }

    /// <summary>The size of a blob of the repository; null when it does not belong to it.</summary>
    public static Task<long?> LinkedSizeAsync(AppDbContext db, long repositoryId, string digest, CancellationToken ct)
        => db.RepositoryBlobs.AsNoTracking().Where(l => l.RepositoryId == repositoryId && l.Blob!.Digest == digest).Select(l => (long?)l.Blob!.Size).FirstOrDefaultAsync(ct);

    /// <summary>A manifest of a repository by tag or digest.</summary>
    public static Task<Manifest?> FindManifestAsync(AppDbContext db, long repositoryId, string reference, CancellationToken ct)
        => RegistryNames.IsDigest(reference)
            ? db.Manifests.AsNoTracking().FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.Digest == reference, ct)
            : db.Tags.AsNoTracking().Where(t => t.RepositoryId == repositoryId && t.Name == reference).Select(t => t.Manifest).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Stores a manifest in a repository (once per digest) with what it refers to, and points <paramref name="tag"/> at it. A push needs every blob and every manifest of
    /// an index in the repository (<paramref name="requireReferences"/>); the cache of a remote registry fetches them later, when a client asks. The platform and the
    /// date of an image come from its config when the store has it.
    /// </summary>
    public static async Task<Manifest> StoreManifestAsync(RegistryContext c, long repositoryId, byte[] content, string? contentType, string? tag, bool requireReferences,
        long? userId, DateTime? checkedDate, CancellationToken ct)
    {
        var (doc, error) = ManifestDocument.Parse(content, contentType);
        if (doc is null)
        {
            throw RegistryException.ManifestInvalid(error ?? "The manifest is not valid.");
        }

        var digest = RegistryNames.DigestOf(content);
        var manifest = await c.Db.Manifests.AsNoTracking().FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.Digest == digest, ct);
        if (manifest is null)
        {
            Dictionary<string, long> children = [];
            if (doc.Kind == ManifestKind.Index)
            {
                var wanted = doc.Manifests.Select(m => m.Digest).Distinct().ToList();
                children = await c.Db.Manifests.AsNoTracking().Where(m => m.RepositoryId == repositoryId && wanted.Contains(m.Digest))
                    .ToDictionaryAsync(m => m.Digest, m => m.TotalSize, ct);
                if (requireReferences && wanted.FirstOrDefault(d => !children.ContainsKey(d)) is { } missing)
                {
                    throw RegistryException.ManifestBlobUnknown(missing);
                }
            }
            else if (requireReferences)
            {
                foreach (var blob in doc.Children.Where(d => !MediaTypes.IsForeign(d.MediaType)))
                {
                    if (await LinkedSizeAsync(c.Db, repositoryId, blob.Digest, ct) is null)
                    {
                        throw RegistryException.ManifestBlobUnknown(blob.Digest);
                    }
                }
            }

            manifest = new Manifest
            {
                RepositoryId = repositoryId,
                Digest = digest,
                MediaType = doc.MediaType,
                Kind = doc.Kind,
                Content = content,
                Size = content.Length,
                TotalSize = doc.Kind == ManifestKind.Index ? children.Values.Sum() : doc.DeclaredSize,
                ArtifactType = doc.EffectiveArtifactType,
                SubjectDigest = doc.Subject?.Digest,
                CreateUserId = userId,
                References = [.. Describe(doc)],
            };
            if (doc is { Kind: ManifestKind.Image, Config: { } config } && MediaTypes.IsImageConfig(config.MediaType) && config.Size < MaxManifestBytes && c.Store.Exists(config.Digest)
                && ImageConfig.Parse(await c.Store.ReadAllAsync(config.Digest, ct)) is { } image)
            {
                manifest.Os = Cut(image.Os, 40);
                manifest.Architecture = Cut(image.Architecture, 40);
                manifest.Variant = Cut(image.Variant, 40);
                manifest.ImageCreatedDate = image.Created;
            }

            c.Db.Manifests.Add(manifest);
            try
            {
                await c.Db.SaveChangesAsync(ct);
                Detach(c.Db, manifest);
            }
            catch (DbUpdateException)
            {
                // the same manifest was pushed at the same moment: take that one (anything else is a real error)
                Detach(c.Db, manifest);
                manifest = await c.Db.Manifests.AsNoTracking().FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.Digest == digest, ct);
                if (manifest is null)
                {
                    throw;
                }
            }
        }

        if (tag is not null)
        {
            await UpsertTagAsync(c.Db, repositoryId, tag, manifest.Id, checkedDate, userId, ct);
        }

        return manifest;
    }

    /// <summary>Points a tag at a manifest (made or moved in one statement).</summary>
    public static Task UpsertTagAsync(AppDbContext db, long repositoryId, string tag, long manifestId, DateTime? checkedDate, long? userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Tag\" (\"RepositoryId\", \"Name\", \"ManifestId\", \"CheckedDate\", \"CreateDate\", \"UpdateDate\", \"CreateUserId\", \"UpdateUserId\") " +
            "VALUES (@repository, @name, @manifest, @checked, @now, @now, @user, @user) " +
            "ON CONFLICT (\"RepositoryId\", \"Name\") DO UPDATE SET \"ManifestId\" = EXCLUDED.\"ManifestId\", \"CheckedDate\" = EXCLUDED.\"CheckedDate\", " +
            "\"UpdateDate\" = CASE WHEN \"Tag\".\"ManifestId\" = EXCLUDED.\"ManifestId\" THEN \"Tag\".\"UpdateDate\" ELSE EXCLUDED.\"UpdateDate\" END, \"UpdateUserId\" = EXCLUDED.\"UpdateUserId\"",
            [
                new NpgsqlParameter("repository", repositoryId), new NpgsqlParameter("name", tag), new NpgsqlParameter("manifest", manifestId),
                new NpgsqlParameter("checked", NpgsqlDbType.TimestampTz) { Value = (object?)checkedDate ?? DBNull.Value },
                new NpgsqlParameter("now", now), UserParameter(userId),
            ], ct);
    }

    public static async Task CountPullAsync(AppDbContext db, long repositoryId, long manifestId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.Repositories.Where(r => r.Id == repositoryId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PullCount, r => r.PullCount + 1).SetProperty(r => r.LastPullDate, now), ct);
        await db.Manifests.Where(m => m.Id == manifestId).ExecuteUpdateAsync(s => s.SetProperty(m => m.LastPullDate, now), ct);
    }

    /// <summary>The descriptors of a manifest as rows (config, layers, or the manifests of an index with their platform).</summary>
    public static IEnumerable<ManifestReference> Describe(ManifestDocument doc)
    {
        var position = 0;
        if (doc.Kind == ManifestKind.Index)
        {
            foreach (var m in doc.Manifests)
            {
                yield return new ManifestReference { Kind = ReferenceKind.Manifest, Position = position++, Digest = m.Digest, MediaType = Cut(m.MediaType, 255)!, Size = m.Size, Platform = Cut(m.Platform, 80) };
            }

            yield break;
        }

        if (doc.Config is { } config)
        {
            yield return new ManifestReference { Kind = ReferenceKind.Config, Position = position++, Digest = config.Digest, MediaType = Cut(config.MediaType, 255)!, Size = config.Size };
        }

        foreach (var l in doc.Layers)
        {
            yield return new ManifestReference { Kind = ReferenceKind.Layer, Position = position++, Digest = l.Digest, MediaType = Cut(l.MediaType, 255)!, Size = l.Size };
        }
    }

    private static async Task<long?> TryMountAsync(RegistryContext c, RegistryActor actor, long repositoryId, string digest, string from, CancellationToken ct)
    {
        try
        {
            var source = await ResolveAsync(c, actor, from, write: false, ct);
            if (source.Repository is null || await LinkedSizeAsync(c.Db, source.Repository.Id, digest, ct) is not { } size || !c.Store.Exists(digest))
            {
                return null;
            }

            await LinkBlobAsync(c.Db, repositoryId, digest, size, actor.UserId, ct);
            return size;
        }
        catch (RegistryException)
        {
            // the source is not readable for the actor: a normal upload instead (the spec says so)
            return null;
        }
    }

    private static async Task<BlobUpload> FindUploadAsync(RegistryContext c, RegistryActor actor, string name, string token, CancellationToken ct)
    {
        var r = await ResolveAsync(c, actor, name, write: true, ct);
        if (!Guid.TryParse(token, out var guid) || r.Repository is null)
        {
            throw RegistryException.UploadUnknown();
        }

        return await c.Db.BlobUploads.AsNoTracking().FirstOrDefaultAsync(u => u.Token == guid && u.RepositoryId == r.Repository.Id, ct) ?? throw RegistryException.UploadUnknown();
    }

    private static void CheckReference(string reference)
    {
        if (!RegistryNames.IsDigest(reference) && !RegistryNames.IsTag(reference))
        {
            throw reference.StartsWith(RegistryNames.Sha256, StringComparison.Ordinal) || reference.Contains(':', StringComparison.Ordinal)
                ? RegistryException.DigestInvalid("The digest is not valid (only sha256 is supported).")
                : RegistryException.TagInvalid(reference);
        }
    }

    private static (List<string> Items, bool More) Page(List<string> all, int? count, string? last)
    {
        all.Sort(StringComparer.Ordinal);
        IEnumerable<string> rest = string.IsNullOrEmpty(last) ? all : all.Where(x => string.CompareOrdinal(x, last) > 0);
        if (count is not { } n || n <= 0)
        {
            return (rest.ToList(), false);
        }

        var page = rest.Take(n + 1).ToList();
        return page.Count > n ? (page[..n], true) : (page, false);
    }

    /// <summary>Lets go of a stored manifest and its rows (the content can be large; nothing else the caller tracks is touched).</summary>
    private static void Detach(AppDbContext db, Manifest manifest)
    {
        foreach (var reference in manifest.References)
        {
            db.Entry(reference).State = EntityState.Detached;
        }

        db.Entry(manifest).State = EntityState.Detached;
    }

    private static NpgsqlParameter UserParameter(long? userId) => new("user", NpgsqlDbType.Bigint) { Value = (object?)userId ?? DBNull.Value };

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}
