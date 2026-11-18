using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>
/// Who acts: a person (by their account, maybe through a personal access token that only pulls), an app bound through <c>registry.docker</c>, or nobody (an anonymous pull).
/// The pages make one from the signed-in person (<see cref="ForUser"/>), the registry API from its credentials (<see cref="RegistryAuth"/>).
/// </summary>
public sealed record RegistryActor(long? UserId, bool IsAdmin, string? Consumer, bool PullOnly, string Name)
{
    public static readonly RegistryActor Anonymous = new(null, false, null, true, "anonymous");

    public bool IsAnonymous => UserId is null && Consumer is null;

    public static RegistryActor ForUser(CurrentUser me)
        => me.Snapshot is { } s ? new RegistryActor(s.Id, s.Role == UserRole.Admin, null, false, s.Email) : Anonymous;

    public static RegistryActor ForPerson(long userId, bool admin, string name, bool pullOnly = false) => new(userId, admin, null, pullOnly, name);

    public static RegistryActor ForConsumer(string consumer) => new(null, false, consumer, true, consumer);
}

/// <summary>
/// THE check of what an actor may do in a registry (every page and the registry API ask here). An administrator may do everything; a person has the role they were
/// given, a public registry gives everybody <see cref="AccessRole.Viewer"/>; an app bound through <c>registry.docker</c> pulls from every registry; a token that only
/// pulls never gets more than <see cref="AccessRole.Viewer"/>. A registry that is switched off serves nobody through the API.
/// </summary>
public static class RegistryPermissions
{
    public static async Task<AccessRole> RoleAsync(AppDbContext db, RegistryActor actor, Data.Registry registry, CancellationToken ct = default)
    {
        var role = AccessRole.None;
        if (actor.IsAdmin)
        {
            role = AccessRole.Editor;
        }
        else if (actor.Consumer is not null)
        {
            role = AccessRole.Viewer;
        }
        else if (actor.UserId is { } userId)
        {
            role = await db.RegistryAccess.Where(a => a.RegistryId == registry.Id && a.UserId == userId).Select(a => a.Role).FirstOrDefaultAsync(ct);
        }

        if (role == AccessRole.None && registry.Visibility == RegistryVisibility.Public)
        {
            role = AccessRole.Viewer;
        }

        return actor.PullOnly && role > AccessRole.Viewer ? AccessRole.Viewer : role;
    }

    /// <summary>The ids of the registries the actor may see (role Viewer or more), with their role.</summary>
    public static async Task<Dictionary<long, AccessRole>> RolesAsync(AppDbContext db, RegistryActor actor, CancellationToken ct = default)
    {
        var registries = await db.Registries.AsNoTracking().Select(r => new { r.Id, r.Visibility }).ToListAsync(ct);
        var grants = actor.UserId is { } userId && !actor.IsAdmin
            ? await db.RegistryAccess.AsNoTracking().Where(a => a.UserId == userId).ToDictionaryAsync(a => a.RegistryId, a => a.Role, ct)
            : [];
        var result = new Dictionary<long, AccessRole>();
        foreach (var r in registries)
        {
            var role = actor.IsAdmin ? AccessRole.Editor : actor.Consumer is not null ? AccessRole.Viewer : grants.GetValueOrDefault(r.Id);
            if (role == AccessRole.None && r.Visibility == RegistryVisibility.Public)
            {
                role = AccessRole.Viewer;
            }

            if (actor.PullOnly && role > AccessRole.Viewer)
            {
                role = AccessRole.Viewer;
            }

            if (role > AccessRole.None)
            {
                result[r.Id] = role;
            }
        }

        return result;
    }
}
