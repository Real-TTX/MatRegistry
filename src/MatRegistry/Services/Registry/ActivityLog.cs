using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>The log of the administration (Administration → Activity): what was pushed, deleted, copied, changed. Never a secret.</summary>
public static class ActivityLog
{
    public static async Task AddAsync(AppDbContext db, RegistryActor actor, string action, string? target, string? detail, long? registryId, long? repositoryId,
        CancellationToken ct)
    {
        db.Activities.Add(new Activity
        {
            UserId = actor.UserId,
            Consumer = actor.Consumer,
            Action = action,
            Target = Cut(target, 255),
            Detail = Cut(detail, 1000),
            RegistryId = registryId,
            RepositoryId = repositoryId,
            OccurredDate = DateTime.UtcNow,
            CreateUserId = actor.UserId,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The log for administrators (null for anybody else), newest first.</summary>
    public static async Task<(IReadOnlyList<ActivityRow> Rows, int Total)?> AllAsync(AppDbContext db, long adminId, string? search, string? action, int page, int pageSize,
        CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive, ct))
        {
            return null;
        }

        var query = db.Activities.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(a => EF.Functions.ILike(a.Target ?? string.Empty, pattern) || EF.Functions.ILike(a.Detail ?? string.Empty, pattern)
                || EF.Functions.ILike(a.Consumer ?? string.Empty, pattern)
                || db.Users.IgnoreQueryFilters().Any(u => u.Id == a.UserId && EF.Functions.ILike(u.DisplayName, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .Select(a => new ActivityRow(a.Id, a.OccurredDate, a.Action, a.Target, a.Detail,
                db.Users.IgnoreQueryFilters().Where(u => u.Id == a.UserId).Select(u => u.DisplayName).FirstOrDefault() ?? a.Consumer, a.RegistryId, a.RepositoryId))
            .ToListAsync(ct);
        return (rows, total);
    }

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}

public sealed record ActivityRow(long Id, DateTime Date, string Action, string? Target, string? Detail, string? Who, long? RegistryId, long? RepositoryId);
