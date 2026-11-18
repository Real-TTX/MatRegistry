using System.Text.Json;
using System.Text.Json.Serialization;
using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Auth;

/// <summary>
/// The data of a person under the GDPR: the export of everything that is about them (Art. 15 and 20, a JSON file), and the erasure of the account (Art. 17).
/// <para>
/// The export holds the account and its settings, the roles in the registries, the access tokens (name, scope, dates: never a secret), the copies, the activity of
/// the person and the sessions. No password hash, no token.
/// </para>
/// <para>
/// The erasure deletes for good: the account, its sessions, roles, access tokens, copies and activity. The images a person pushed stay in their repositories: they
/// belong to the registry, not to the account.
/// </para>
/// </summary>
public sealed class AccountDataService(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The export as JSON (UTF-8).</summary>
    public async Task<byte[]> ExportAsync(long userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var export = new
        {
            Format = "MatRegistry export 1",
            ExportedAt = DateTime.UtcNow,
            Account = new
            {
                user.Email,
                user.DisplayName,
                user.Role,
                user.TimeZone,
                user.Language,
                user.Theme,
                user.Scheme,
                user.CreateDate,
                user.LastLoginDate,
                LinkedWithMatIdentity = user.ExternalSubject != null,
            },
            Roles = await db.RegistryAccess.AsNoTracking().Where(a => a.UserId == userId)
                .Select(a => new { Registry = a.Registry!.Name, a.Role, Since = a.CreateDate }).ToListAsync(ct),
            AccessTokens = await db.AccessTokens.AsNoTracking().Where(t => t.UserId == userId)
                .Select(t => new { t.Name, t.SecretPrefix, t.Scope, t.CreateDate, t.ExpiresDate, t.LastUsedDate }).ToListAsync(ct),
            Copies = await db.CopyJobs.AsNoTracking().IgnoreQueryFilters().Where(j => j.UserId == userId).OrderByDescending(j => j.CreateDate)
                .Select(j => new
                {
                    Source = j.SourceRegistry!.Name + "/" + j.SourceRepository + ":" + j.SourceReference,
                    Target = j.TargetRegistry!.Name + "/" + j.TargetRepository + ":" + j.TargetTag,
                    j.State,
                    j.Digest,
                    j.CreateDate,
                    j.FinishedDate,
                }).ToListAsync(ct),
            Activity = await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredDate).Take(5000)
                .Select(a => new { a.OccurredDate, a.Action, a.Target, a.Detail }).ToListAsync(ct),
            Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new { s.CreateDate, s.LastSeenDate, s.ExpiresDate, s.IsRevoked, s.UserAgent, s.IpAddress }).ToListAsync(ct),
        };

        return JsonSerializer.SerializeToUtf8Bytes(export, Json);
    }

    /// <summary>Erases the account for good (see the class). The caller checks the password and that it is not the last administrator.</summary>
    public async Task EraseAsync(long userId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Activities.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.CopyJobs.IgnoreQueryFilters().Where(j => j.UserId == userId).ExecuteDeleteAsync(ct);
        await db.AccessTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.RegistryAccess.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Is the person the last active administrator (who must not go, or nobody can run the installation)?</summary>
    public async Task<bool> IsLastAdminAsync(long userId)
        => await db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin && u.IsActive)
           && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && u.IsActive);
}
