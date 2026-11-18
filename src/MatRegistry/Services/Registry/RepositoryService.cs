using System.Text.Json;
using MatRegistry.Data;
using MatRegistry.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

public sealed record RepositoryRow(long Id, long RegistryId, string Registry, RegistryKind Kind, string Name, int Tags, long Size, DateTime? LastPush, DateTime? LastPull,
    long Pulls)
{
    public string FullName => $"{Registry}/{Name}";
}

public sealed record RepositoryCriteria(string? Search, long? RegistryId, string? Kind, string Sort, bool Descending, int Page, int PageSize);

public sealed record RepositoryList(List<RepositoryRow> Items, int Total, int Matching);

public sealed record RepositoryDetail(long Id, long RegistryId, string Registry, RegistryKind Kind, string Name, string? Description, int Tags, int Manifests, long Size,
    DateTime Created, DateTime? LastPush, DateTime? LastPull, long Pulls, AccessRole Role)
{
    public string FullName => $"{Registry}/{Name}";

    public bool CanWrite => Role >= AccessRole.Editor;
}

public sealed record TagRow(string Name, string Digest, ManifestKind Kind, string MediaType, IReadOnlyList<string> Platforms, long Size, DateTime Updated,
    DateTime? ImageCreated, string? ArtifactType);

public sealed record TagCriteria(string? Search, string Sort, bool Descending, int Page, int PageSize);

public sealed record TagList(List<TagRow> Items, int Total, int Matching);

/// <summary>One line of a manifest: a layer, the config, or a manifest of an index (with its platform and whether it is here).</summary>
public sealed record ReferenceRow(ReferenceKind Kind, int Position, string Digest, string MediaType, long Size, string? Platform, bool Present);

public sealed record ReferrerRow(string Digest, string? ArtifactType, long Size, DateTime Created);

public sealed record ManifestDetail(
    string Digest,
    string MediaType,
    ManifestKind Kind,
    long Size,
    long TotalSize,
    string? Platform,
    string? ArtifactType,
    string? Subject,
    DateTime Pushed,
    DateTime? LastPull,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ReferenceRow> References,
    IReadOnlyList<ReferrerRow> Referrers,
    ImageConfig? Config,
    IReadOnlyDictionary<string, string> Annotations,
    string Json);

/// <summary>The numbers of the start page for a person: what they can see.</summary>
public sealed record Overview(int Registries, int Repositories, int Tags, long Size, List<RepositoryRow> Recent);

/// <summary>
/// The repositories for the pages (list, tags, a manifest) and what may be done with them: a person sees the repositories of the registries they have a role in,
/// deletes with the role Editor. Everything is checked here (<see cref="RegistryPermissions"/>); what may not be seen is not there (404).
/// </summary>
public static class RepositoryService
{
    public static async Task<RepositoryList> ListAsync(AppDbContext db, RegistryActor actor, RepositoryCriteria criteria, CancellationToken ct)
    {
        var roles = await RegistryPermissions.RolesAsync(db, actor, ct);
        var ids = roles.Keys.ToList();
        var query = db.Repositories.AsNoTracking().Where(r => ids.Contains(r.RegistryId) && r.Registry!.IsActive);
        var total = await query.CountAsync(ct);
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var s = criteria.Search.Trim().ToLowerInvariant();
            query = query.Where(r => (r.Registry!.Name + "/" + r.Name).Contains(s) || (r.Description != null && r.Description.ToLower().Contains(s)));
        }

        if (criteria.RegistryId is { } registryId)
        {
            query = query.Where(r => r.RegistryId == registryId);
        }

        if (Enum.TryParse<RegistryKind>(criteria.Kind, true, out var kind))
        {
            query = query.Where(r => r.Registry!.Kind == kind);
        }

        var matching = await query.CountAsync(ct);

        // dates and numbers start with the largest; the direction of the toolbar turns it round
        query = criteria.Sort switch
        {
            "pushed" => criteria.Descending ? query.OrderBy(r => r.LastPushDate) : query.OrderByDescending(r => r.LastPushDate),
            "pulls" => criteria.Descending ? query.OrderBy(r => r.PullCount) : query.OrderByDescending(r => r.PullCount),
            "size" => criteria.Descending
                ? query.OrderBy(r => db.Blobs.Where(b => db.RepositoryBlobs.Any(l => l.BlobId == b.Id && l.RepositoryId == r.Id)).Sum(b => (long?)b.Size))
                : query.OrderByDescending(r => db.Blobs.Where(b => db.RepositoryBlobs.Any(l => l.BlobId == b.Id && l.RepositoryId == r.Id)).Sum(b => (long?)b.Size)),
            "tags" => criteria.Descending ? query.OrderBy(r => db.Tags.Count(t => t.RepositoryId == r.Id)) : query.OrderByDescending(r => db.Tags.Count(t => t.RepositoryId == r.Id)),
            _ => criteria.Descending ? query.OrderByDescending(r => r.Registry!.Name).ThenByDescending(r => r.Name) : query.OrderBy(r => r.Registry!.Name).ThenBy(r => r.Name),
        };
        var items = await Project(db, query.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize)).ToListAsync(ct);
        return new RepositoryList(items, total, matching);
    }

    public static async Task<Overview> OverviewAsync(AppDbContext db, RegistryActor actor, CancellationToken ct)
    {
        var roles = await RegistryPermissions.RolesAsync(db, actor, ct);
        var ids = roles.Keys.ToList();
        var repositories = db.Repositories.AsNoTracking().Where(r => ids.Contains(r.RegistryId) && r.Registry!.IsActive);
        var recent = await Project(db, repositories.Where(r => r.LastPushDate != null).OrderByDescending(r => r.LastPushDate).Take(6)).ToListAsync(ct);
        return new Overview(
            await db.Registries.CountAsync(r => ids.Contains(r.Id) && r.IsActive, ct),
            await repositories.CountAsync(ct),
            await db.Tags.CountAsync(t => ids.Contains(t.Repository!.RegistryId) && t.Repository.Registry!.IsActive, ct),
            await db.Blobs.Where(b => db.RepositoryBlobs.Any(l => l.BlobId == b.Id && ids.Contains(l.Repository!.RegistryId))).SumAsync(b => (long?)b.Size, ct) ?? 0,
            recent);
    }

    public static async Task<RepositoryDetail?> GetAsync(AppDbContext db, RegistryActor actor, long id, CancellationToken ct)
    {
        var repository = await db.Repositories.AsNoTracking().Include(r => r.Registry).FirstOrDefaultAsync(r => r.Id == id && r.Registry!.IsActive, ct);
        if (repository?.Registry is null)
        {
            return null;
        }

        var role = await RegistryPermissions.RoleAsync(db, actor, repository.Registry, ct);
        if (role == AccessRole.None)
        {
            return null;
        }

        // a cache is filled by pulls only: nobody changes it by hand except its administrators (they delete what it should fetch again)
        if (repository.Registry.Kind == RegistryKind.Remote && !actor.IsAdmin)
        {
            role = AccessRole.Viewer;
        }

        return new RepositoryDetail(repository.Id, repository.RegistryId, repository.Registry.Name, repository.Registry.Kind, repository.Name, repository.Description,
            await db.Tags.CountAsync(t => t.RepositoryId == id, ct),
            await db.Manifests.CountAsync(m => m.RepositoryId == id, ct),
            await db.Blobs.Where(b => db.RepositoryBlobs.Any(l => l.BlobId == b.Id && l.RepositoryId == id)).SumAsync(b => (long?)b.Size, ct) ?? 0,
            repository.CreateDate, repository.LastPushDate, repository.LastPullDate, repository.PullCount, role);
    }

    public static async Task<TagList?> TagsAsync(AppDbContext db, RegistryActor actor, long repositoryId, TagCriteria criteria, CancellationToken ct)
    {
        if (await GetAsync(db, actor, repositoryId, ct) is null)
        {
            return null;
        }

        var query = db.Tags.AsNoTracking().Where(t => t.RepositoryId == repositoryId);
        var total = await query.CountAsync(ct);
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var s = criteria.Search.Trim().ToLowerInvariant();
            query = query.Where(t => t.Name.ToLower().Contains(s) || t.Manifest!.Digest.Contains(s));
        }

        var matching = await query.CountAsync(ct);
        query = criteria.Sort switch
        {
            "name" => criteria.Descending ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name),
            "size" => criteria.Descending ? query.OrderBy(t => t.Manifest!.TotalSize) : query.OrderByDescending(t => t.Manifest!.TotalSize),
            _ => criteria.Descending ? query.OrderBy(t => t.UpdateDate) : query.OrderByDescending(t => t.UpdateDate),
        };
        var rows = await query.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).Select(t => new
        {
            t.Name,
            t.UpdateDate,
            t.Manifest!.Id,
            t.Manifest.Digest,
            t.Manifest.Kind,
            t.Manifest.MediaType,
            t.Manifest.TotalSize,
            t.Manifest.Os,
            t.Manifest.Architecture,
            t.Manifest.Variant,
            t.Manifest.ImageCreatedDate,
            t.Manifest.ArtifactType,
        }).ToListAsync(ct);
        var indexIds = rows.Where(r => r.Kind == ManifestKind.Index).Select(r => r.Id).ToList();
        var platforms = (await db.ManifestReferences.AsNoTracking().Where(x => indexIds.Contains(x.ManifestId) && x.Platform != null)
                .Select(x => new { x.ManifestId, x.Platform, x.Position }).ToListAsync(ct))
            .Where(x => x.Platform != "unknown/unknown")
            .GroupBy(x => x.ManifestId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.Position).Select(x => x.Platform!).Distinct().ToList());
        var items = rows.Select(r => new TagRow(r.Name, r.Digest, r.Kind, r.MediaType,
            r.Kind == ManifestKind.Index ? platforms.GetValueOrDefault(r.Id) ?? [] : ManifestDocument.PlatformOf(r.Os, r.Architecture, r.Variant) is { } p ? [p] : [],
            r.TotalSize, r.UpdateDate, r.ImageCreatedDate, r.ArtifactType)).ToList();
        return new TagList(items, total, matching);
    }

    /// <summary>A manifest of a repository with everything the page shows: its lines, the config of an image, the tags on it, what refers to it, the JSON.</summary>
    public static async Task<ManifestDetail?> ManifestAsync(RegistryContext c, RegistryActor actor, long repositoryId, string digest, CancellationToken ct)
    {
        if (!RegistryNames.IsDigest(digest) || await GetAsync(c.Db, actor, repositoryId, ct) is null)
        {
            return null;
        }

        var m = await c.Db.Manifests.AsNoTracking().Include(x => x.References).FirstOrDefaultAsync(x => x.RepositoryId == repositoryId && x.Digest == digest, ct);
        if (m is null)
        {
            return null;
        }

        var tags = await c.Db.Tags.AsNoTracking().Where(t => t.ManifestId == m.Id).OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
        var childDigests = m.References.Where(r => r.Kind == ReferenceKind.Manifest).Select(r => r.Digest).ToList();
        var presentManifests = await c.Db.Manifests.AsNoTracking().Where(x => x.RepositoryId == repositoryId && childDigests.Contains(x.Digest)).Select(x => x.Digest).ToListAsync(ct);
        var blobDigests = m.References.Where(r => r.Kind != ReferenceKind.Manifest).Select(r => r.Digest).ToList();
        var presentBlobs = await c.Db.RepositoryBlobs.AsNoTracking().Where(l => l.RepositoryId == repositoryId && blobDigests.Contains(l.Blob!.Digest)).Select(l => l.Blob!.Digest).ToListAsync(ct);
        var present = presentManifests.Concat(presentBlobs).ToHashSet(StringComparer.Ordinal);
        var references = m.References.OrderBy(r => r.Position)
            .Select(r => new ReferenceRow(r.Kind, r.Position, r.Digest, r.MediaType, r.Size, r.Platform, present.Contains(r.Digest))).ToList();
        var referrers = await c.Db.Manifests.AsNoTracking().Where(x => x.RepositoryId == repositoryId && x.SubjectDigest == digest).OrderByDescending(x => x.CreateDate)
            .Select(x => new ReferrerRow(x.Digest, x.ArtifactType, x.Size, x.CreateDate)).ToListAsync(ct);

        ImageConfig? config = null;
        var configRef = m.References.FirstOrDefault(r => r.Kind == ReferenceKind.Config);
        if (configRef is not null && MediaTypes.IsImageConfig(configRef.MediaType) && configRef.Size < RegistryEngine.MaxManifestBytes && present.Contains(configRef.Digest)
            && c.Store.Exists(configRef.Digest))
        {
            config = ImageConfig.Parse(await c.Store.ReadAllAsync(configRef.Digest, ct));
        }

        var (doc, _) = ManifestDocument.Parse(m.Content, m.MediaType);
        return new ManifestDetail(m.Digest, m.MediaType, m.Kind, m.Size, m.TotalSize, ManifestDocument.PlatformOf(m.Os, m.Architecture, m.Variant), m.ArtifactType,
            m.SubjectDigest, m.CreateDate, m.LastPullDate, tags, references, referrers, config, doc?.Annotations ?? new Dictionary<string, string>(), Pretty(m.Content));
    }

    /// <summary>Removes a tag (the manifest stays, by its digest, until the garbage collection takes untagged ones).</summary>
    public static async Task<bool> DeleteTagAsync(AppDbContext db, RegistryActor actor, long repositoryId, string tag, CancellationToken ct)
    {
        var repository = await GetAsync(db, actor, repositoryId, ct);
        if (repository is not { CanWrite: true })
        {
            return false;
        }

        var removed = await db.Tags.Where(t => t.RepositoryId == repositoryId && t.Name == tag).ExecuteDeleteAsync(ct) > 0;
        if (removed)
        {
            await ActivityLog.AddAsync(db, actor, "delete", $"{repository.FullName}:{tag}", null, repository.RegistryId, repositoryId, ct);
        }

        return removed;
    }

    /// <summary>Removes a manifest with every tag on it.</summary>
    public static async Task<bool> DeleteManifestAsync(AppDbContext db, RegistryActor actor, long repositoryId, string digest, CancellationToken ct)
    {
        var repository = await GetAsync(db, actor, repositoryId, ct);
        if (repository is not { CanWrite: true })
        {
            return false;
        }

        var removed = await db.Manifests.Where(m => m.RepositoryId == repositoryId && m.Digest == digest).ExecuteDeleteAsync(ct) > 0;
        if (removed)
        {
            await ActivityLog.AddAsync(db, actor, "delete", $"{repository.FullName}@{digest}", null, repository.RegistryId, repositoryId, ct);
        }

        return removed;
    }

    /// <summary>Removes a repository with its tags and manifests.</summary>
    public static async Task<bool> DeleteAsync(AppDbContext db, RegistryActor actor, long repositoryId, CancellationToken ct)
    {
        var repository = await GetAsync(db, actor, repositoryId, ct);
        if (repository is not { CanWrite: true })
        {
            return false;
        }

        await db.Repositories.Where(r => r.Id == repositoryId).ExecuteDeleteAsync(ct);
        await ActivityLog.AddAsync(db, actor, "delete", repository.FullName, null, repository.RegistryId, null, ct);
        return true;
    }

    public static async Task<string?> SaveDescriptionAsync(AppDbContext db, RegistryActor actor, long repositoryId, string? description, CancellationToken ct)
    {
        var repository = await GetAsync(db, actor, repositoryId, ct);
        if (repository is not { CanWrite: true })
        {
            return Text.T("Common.NotAllowed");
        }

        var text = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (text is { Length: > 2000 })
        {
            return Text.T("Common.TooLong");
        }

        var now = DateTime.UtcNow;
        await db.Repositories.Where(r => r.Id == repositoryId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Description, text).SetProperty(r => r.UpdateDate, now).SetProperty(r => r.UpdateUserId, actor.UserId), ct);
        return null;
    }

    private static IQueryable<RepositoryRow> Project(AppDbContext db, IQueryable<Repository> query)
        => query.Select(r => new RepositoryRow(r.Id, r.RegistryId, r.Registry!.Name, r.Registry.Kind, r.Name,
            db.Tags.Count(t => t.RepositoryId == r.Id),
            db.Blobs.Where(b => db.RepositoryBlobs.Any(l => l.BlobId == b.Id && l.RepositoryId == r.Id)).Sum(b => (long?)b.Size) ?? 0,
            r.LastPushDate, r.LastPullDate, r.PullCount));

    private static string Pretty(byte[] content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return System.Text.Encoding.UTF8.GetString(content);
        }
    }
}
