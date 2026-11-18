using MatRegistry.Data;
using MatRegistry.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

public sealed record RegistryRow(long Id, string Name, RegistryKind Kind, string? Description, string? Url, RegistryVisibility Visibility, bool ProxyEnabled, bool IsActive,
    int Repositories, int Tags, long Size, bool? IsReachable, DateTime? LastCheck, string? LastCheckResult, AccessRole Role);

public sealed record RegistryCriteria(string? Search, string? Kind, string Sort, bool Descending, int Page, int PageSize);

public sealed record RegistryList(List<RegistryRow> Items, int Total, int Matching);

/// <summary>What the form of a registry sends. <see cref="Password"/> empty keeps the stored one; <see cref="ClearPassword"/> removes it.</summary>
public sealed record RegistryInput(
    string Name,
    RegistryKind Kind,
    string? Description,
    RegistryVisibility Visibility,
    bool IsActive,
    string? Url,
    string? Username,
    string? Password,
    bool ClearPassword,
    bool ProxyEnabled,
    int CacheMinutes,
    bool SkipTlsVerify);

/// <summary>The form of a registry as it is stored (never with its secret: only whether one is set).</summary>
public sealed record RegistryForm(long Id, string Name, RegistryKind Kind, string? Description, RegistryVisibility Visibility, bool IsActive, string? Url, string? Username,
    bool HasPassword, bool ProxyEnabled, int CacheMinutes, bool SkipTlsVerify, bool? IsReachable, DateTime? LastCheck, string? LastCheckResult, int Repositories);

public sealed record AccessRow(long UserId, string DisplayName, string Email, AccessRole Role, bool IsAdmin);

/// <summary>
/// The registries (the pages Registries): the ones MatRegistry serves and the ones it is bound to. Everybody sees the registries they have a role in (and the public
/// ones); only administrators create, change, bind and delete them and give people their roles. The secret of a remote registry is protected and never leaves here.
/// </summary>
public static class RegistryAdmin
{
    public static async Task<RegistryList> ListAsync(AppDbContext db, RegistryActor actor, RegistryCriteria criteria, CancellationToken ct)
    {
        var roles = await RegistryPermissions.RolesAsync(db, actor, ct);
        var ids = roles.Keys.ToList();
        var query = db.Registries.AsNoTracking().Where(r => actor.IsAdmin || (ids.Contains(r.Id) && r.IsActive));
        var total = await query.CountAsync(ct);
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var s = criteria.Search.Trim().ToLowerInvariant();
            query = query.Where(r => r.Name.Contains(s) || (r.Description != null && r.Description.ToLower().Contains(s)) || (r.Url != null && r.Url.ToLower().Contains(s)));
        }

        if (Enum.TryParse<RegistryKind>(criteria.Kind, true, out var kind))
        {
            query = query.Where(r => r.Kind == kind);
        }

        var matching = await query.CountAsync(ct);
        var rows = await query.Select(r => new
        {
            r.Id,
            r.Name,
            r.Kind,
            r.Description,
            r.Url,
            r.Visibility,
            r.ProxyEnabled,
            r.IsActive,
            Repositories = db.Repositories.Count(x => x.RegistryId == r.Id),
            Tags = db.Tags.Count(t => t.Repository!.RegistryId == r.Id),
            Size = db.Blobs.Where(b => db.RepositoryBlobs.Any(l => l.BlobId == b.Id && l.Repository!.RegistryId == r.Id)).Sum(b => (long?)b.Size) ?? 0,
            r.IsReachable,
            r.LastCheckDate,
            r.LastCheckResult,
        }).ToListAsync(ct);
        var items = rows.Select(r => new RegistryRow(r.Id, r.Name, r.Kind, r.Description, r.Url, r.Visibility, r.ProxyEnabled, r.IsActive, r.Repositories, r.Tags, r.Size,
            r.IsReachable, r.LastCheckDate, r.LastCheckResult, roles.GetValueOrDefault(r.Id)));
        items = criteria.Sort switch
        {
            "kind" => criteria.Descending ? items.OrderByDescending(r => r.Kind).ThenBy(r => r.Name) : items.OrderBy(r => r.Kind).ThenBy(r => r.Name),
            "repositories" => criteria.Descending ? items.OrderByDescending(r => r.Repositories) : items.OrderBy(r => r.Repositories),
            "size" => criteria.Descending ? items.OrderByDescending(r => r.Size) : items.OrderBy(r => r.Size),
            _ => criteria.Descending ? items.OrderByDescending(r => r.Name, StringComparer.Ordinal) : items.OrderBy(r => r.Name, StringComparer.Ordinal),
        };
        return new RegistryList([.. items.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize)], total, matching);
    }

    /// <summary>The registries the actor may copy from (Viewer) or into (Editor), for the pickers of a copy.</summary>
    public static async Task<List<(long Id, string Name, RegistryKind Kind)>> ChoicesAsync(AppDbContext db, RegistryActor actor, AccessRole atLeast, CancellationToken ct)
    {
        var roles = await RegistryPermissions.RolesAsync(db, actor, ct);
        var ids = roles.Where(r => r.Value >= atLeast).Select(r => r.Key).ToList();
        var rows = await db.Registries.AsNoTracking().Where(r => ids.Contains(r.Id) && r.IsActive).OrderBy(r => r.Name).Select(r => new { r.Id, r.Name, r.Kind }).ToListAsync(ct);
        return [.. rows.Select(r => (r.Id, r.Name, r.Kind))];
    }

    /// <summary>A registry for its page: null when it is not there or the actor may not see it.</summary>
    public static async Task<(RegistryForm Form, AccessRole Role)?> GetAsync(AppDbContext db, RegistryActor actor, long id, CancellationToken ct)
    {
        var r = await db.Registries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
        {
            return null;
        }

        var role = await RegistryPermissions.RoleAsync(db, actor, r, ct);
        if (role == AccessRole.None || (!r.IsActive && !actor.IsAdmin))
        {
            return null;
        }

        var repositories = await db.Repositories.CountAsync(x => x.RegistryId == id, ct);
        return (new RegistryForm(r.Id, r.Name, r.Kind, r.Description, r.Visibility, r.IsActive, r.Url, r.Username, !string.IsNullOrEmpty(r.SecretProtected), r.ProxyEnabled,
            r.CacheMinutes, r.SkipTlsVerify, r.IsReachable, r.LastCheckDate, r.LastCheckResult, repositories), role);
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes a registry; administrators only. Answers the id, or the messages of the fields that are wrong.</summary>
    public static async Task<(long? Id, List<FieldError> Errors, bool Denied)> SaveAsync(AppDbContext db, RegistrySecrets secrets, RegistryActor actor, long? id,
        RegistryInput input, CancellationToken ct)
    {
        if (!actor.IsAdmin)
        {
            return (null, [], true);
        }

        var errors = new List<FieldError>();
        var name = (input.Name ?? string.Empty).Trim().ToLowerInvariant();
        if (!RegistryNames.IsRegistryName(name))
        {
            errors.Add(new FieldError("Name", Text.T("Registries.Name.Invalid")));
        }
        else if (await db.Registries.AnyAsync(r => r.Name == name && r.Id != (id ?? 0), ct))
        {
            errors.Add(new FieldError("Name", Text.T("Registries.Name.Taken")));
        }

        if (!Enum.IsDefined(input.Kind))
        {
            errors.Add(new FieldError("Kind", Text.T("Registries.Kind.Invalid")));
        }

        var url = input.Kind == RegistryKind.Remote ? UpstreamClient.NormalizeUrl(input.Url) : null;
        if (input.Kind == RegistryKind.Remote && url is null)
        {
            errors.Add(new FieldError("Address", Text.T("Registries.Url.Invalid")));
        }

        if (input.CacheMinutes is < 1 or > 10080)
        {
            errors.Add(new FieldError("CacheMinutes", Text.T("System.Range", 1, 10080)));
        }

        if ((input.Description?.Length ?? 0) > 1000 || (input.Username?.Length ?? 0) > 255 || (input.Password?.Length ?? 0) > 2000)
        {
            errors.Add(new FieldError("Description", Text.T("Common.TooLong")));
        }

        Data.Registry? registry = null;
        if (id is { } existing)
        {
            registry = await db.Registries.FirstOrDefaultAsync(r => r.Id == existing, ct);
            if (registry is null)
            {
                return (null, [], true);
            }

            if (registry.Kind != input.Kind && await db.Repositories.AnyAsync(r => r.RegistryId == existing, ct))
            {
                errors.Add(new FieldError("Kind", Text.T("Registries.Kind.Locked")));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        var isNew = registry is null;
        registry ??= new Data.Registry();
        registry.Name = name;
        registry.Kind = input.Kind;
        registry.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        registry.Visibility = input.Visibility;
        registry.IsActive = input.IsActive;
        if (input.Kind == RegistryKind.Remote)
        {
            var changedAddress = registry.Url != url!.ToString().TrimEnd('/') || registry.Username != Clean(input.Username);
            registry.Url = url.ToString().TrimEnd('/');
            registry.Username = Clean(input.Username);
            if (input.ClearPassword)
            {
                registry.SecretProtected = null;
            }
            else if (!string.IsNullOrEmpty(input.Password))
            {
                registry.SecretProtected = secrets.Protect(input.Password);
            }

            registry.ProxyEnabled = input.ProxyEnabled;
            registry.CacheMinutes = input.CacheMinutes;
            registry.SkipTlsVerify = input.SkipTlsVerify;
            if (changedAddress || input.ClearPassword || !string.IsNullOrEmpty(input.Password))
            {
                registry.IsReachable = null;
                registry.LastCheckDate = null;
                registry.LastCheckResult = null;
            }
        }
        else
        {
            registry.Url = null;
            registry.Username = null;
            registry.SecretProtected = null;
            registry.ProxyEnabled = false;
            registry.SkipTlsVerify = false;
        }

        if (isNew)
        {
            db.Registries.Add(registry);
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "registry", registry.Name, isNew ? "created" : "changed", registry.Id, null, ct);
        return (registry.Id, errors, false);
    }

    /// <summary>Deletes a registry with its repositories (the files go with the next garbage collection); administrators only.</summary>
    public static async Task<bool> DeleteAsync(AppDbContext db, RegistryActor actor, long id, CancellationToken ct)
    {
        if (!actor.IsAdmin)
        {
            return false;
        }

        var registry = await db.Registries.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (registry is null)
        {
            return false;
        }

        await db.Repositories.Where(r => r.RegistryId == id).ExecuteDeleteAsync(ct);
        await db.RegistryAccess.Where(a => a.RegistryId == id).ExecuteDeleteAsync(ct);
        db.Registries.Remove(registry);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "registry", registry.Name, "deleted", id, null, ct);
        return true;
    }

    /// <summary>Asks a remote registry whether it answers and takes the credentials, and keeps the answer; null for a hosted registry or one the actor may not see.</summary>
    public static async Task<(bool Ok, string Message)?> CheckAsync(RegistryContext c, RegistryActor actor, long id, CancellationToken ct)
    {
        var registry = await c.Db.Registries.FirstOrDefaultAsync(r => r.Id == id && r.Kind == RegistryKind.Remote, ct);
        if (registry is null || await RegistryPermissions.RoleAsync(c.Db, actor, registry, ct) == AccessRole.None)
        {
            return null;
        }

        var target = c.Secrets.TargetOf(registry);
        var result = target is null ? (false, Text.T("Registries.Url.Invalid")) : await c.Upstream.PingAsync(target, ct);
        registry.IsReachable = result.Item1;
        registry.LastCheckDate = DateTime.UtcNow;
        registry.LastCheckResult = result.Item2.Length > 1000 ? result.Item2[..1000] : result.Item2;
        await c.Db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Checks every active remote registry (the action of the platform and the housekeeping).</summary>
    public static async Task<(int Reachable, int Total)> CheckAllAsync(RegistryContext c, RegistryActor actor, CancellationToken ct)
    {
        var ids = await c.Db.Registries.Where(r => r.Kind == RegistryKind.Remote && r.IsActive).Select(r => r.Id).ToListAsync(ct);
        var reachable = 0;
        foreach (var id in ids)
        {
            if (await CheckAsync(c, actor, id, ct) is { Ok: true })
            {
                reachable++;
            }
        }

        return (reachable, ids.Count);
    }

    /// <summary>The people and their role in a registry (administrators have every role anyway); administrators only.</summary>
    public static async Task<List<AccessRow>?> AccessAsync(AppDbContext db, RegistryActor actor, long id, CancellationToken ct)
    {
        if (!actor.IsAdmin || !await db.Registries.AnyAsync(r => r.Id == id, ct))
        {
            return null;
        }

        var grants = await db.RegistryAccess.AsNoTracking().Where(a => a.RegistryId == id).ToDictionaryAsync(a => a.UserId, a => a.Role, ct);
        var people = await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.Role }).ToListAsync(ct);
        return [.. people.Select(p => new AccessRow(p.Id, p.DisplayName, p.Email, p.Role == UserRole.Admin ? AccessRole.Editor : grants.GetValueOrDefault(p.Id), p.Role == UserRole.Admin))];
    }

    /// <summary>Sets the roles of the people in a registry (None removes one); administrators only.</summary>
    public static async Task<bool> SaveAccessAsync(AppDbContext db, RegistryActor actor, long id, IReadOnlyDictionary<long, AccessRole> roles, CancellationToken ct)
    {
        var registry = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (!actor.IsAdmin || registry is null)
        {
            return false;
        }

        var existing = await db.RegistryAccess.Where(a => a.RegistryId == id).ToListAsync(ct);
        var people = await db.Users.Where(u => u.IsActive && u.Role != UserRole.Admin).Select(u => u.Id).ToListAsync(ct);
        foreach (var userId in people)
        {
            var wanted = roles.GetValueOrDefault(userId);
            var entry = existing.FirstOrDefault(a => a.UserId == userId);
            if (wanted == AccessRole.None)
            {
                if (entry is not null)
                {
                    db.RegistryAccess.Remove(entry);
                }
            }
            else if (entry is null)
            {
                db.RegistryAccess.Add(new RegistryAccessEntry { RegistryId = id, UserId = userId, Role = wanted });
            }
            else if (entry.Role != wanted)
            {
                entry.Role = wanted;
            }
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "access", registry.Name, null, id, null, ct);
        return true;
    }

    /// <summary>Sets the role of one person in a registry (None removes it); administrators only, and not for an administrator (who has every role anyway).</summary>
    public static async Task<bool> SetRoleAsync(AppDbContext db, RegistryActor actor, long id, long userId, AccessRole role, CancellationToken ct)
    {
        var registry = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        var person = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (!actor.IsAdmin || registry is null || person is null || person.Role == UserRole.Admin || !Enum.IsDefined(role))
        {
            return false;
        }

        var entry = await db.RegistryAccess.FirstOrDefaultAsync(a => a.RegistryId == id && a.UserId == userId, ct);
        if (role == AccessRole.None)
        {
            if (entry is not null)
            {
                db.RegistryAccess.Remove(entry);
            }
        }
        else if (entry is null)
        {
            db.RegistryAccess.Add(new RegistryAccessEntry { RegistryId = id, UserId = userId, Role = role });
        }
        else
        {
            entry.Role = role;
        }

        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, actor, "access", registry.Name, $"{person.Email}: {role}", id, null, ct);
        return true;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
