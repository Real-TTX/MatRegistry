using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services.Registry;
using MatPlatform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatRegistry.Services;

/// <summary>The numbers of the installation (the platform tiles, the admin page System). Only how many and how large, never what is in an image.</summary>
public sealed record InstanceSnapshot(
    int Users,
    int Admins,
    int Registries,
    int RemoteRegistries,
    int Unreachable,
    int Repositories,
    int Tags,
    int Manifests,
    int Blobs,
    long StoredBytes,
    int Tokens,
    int CopiesRunning,
    int CopiesFailed7Days,
    DateTime? LastPushUtc,
    DateTime? LastGcUtc,
    string? LastGcResult,
    DateTime? LastActivityUtc);

/// <summary>
/// The settings of the system (admin page System and the platform contract). <see cref="PublicUrl"/> is the address in force: the value of <c>MAT_PUBLIC_URL</c>
/// while it is set (then <see cref="PublicUrlFixed"/>), else the stored one.
/// </summary>
public sealed record SystemSettings(bool RegistrationOpen, string? PublicUrl, bool PublicUrlFixed, int TokenMinutes, int GcIntervalHours, bool GcUntagged,
    DateTime? LastGcDate, string? LastGcResult);

/// <summary>What the housekeeping removed.</summary>
public sealed record CleanUpResult(int Sessions, int Copies);

/// <summary>A change of the settings of the system: <c>null</c> leaves a value as it is.</summary>
public sealed record SettingsChange(bool? RegistrationOpen = null, string? PublicUrl = null, int? TokenMinutes = null, int? GcIntervalHours = null, bool? GcUntagged = null);

/// <summary>
/// The installation as a whole: its numbers, the settings of the system (registration, public address, the registry API, the garbage collection), the state of the
/// database and the housekeeping. The admin page System, the platform contract (<c>Platform/MatRegistryPlatform</c>) and the background housekeeping go through it.
/// A broker such as Mattiverse may set the public address (<c>MAT_PUBLIC_URL</c>): then it wins and the stored value stays as it is.
/// </summary>
public sealed class InstanceService(AppDbContext db, AccountService accounts, MatPlatformSettings platform, BlobStore store, IMemoryCache cache, IConfiguration configuration)
{
    private readonly string _data = configuration["DataDirectory"] ?? "/data";

    /// <summary>The public address in force: <c>MAT_PUBLIC_URL</c>, else the stored one; null = the address of the request.</summary>
    public static string? PublicUrlOf(SystemSetting s, MatPlatformSettings platform) => platform.AppPublicUrl ?? NormalizePublicUrl(s.PublicUrl);

    /// <summary>The host of the pull commands: <c>registry.example.com</c> or <c>localhost:4780</c> (the public address without its scheme, else the request's).</summary>
    public static string RegistryHost(string? publicUrl, HttpRequest request)
        => publicUrl is not null && Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ? uri.Authority + uri.AbsolutePath.TrimEnd('/') : request.Host.Value ?? "localhost";

    /// <summary>Can the database be reached (the health check)?</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Can the data folder be written (the keys, the blob store, the uploads)?</summary>
    public bool CanWriteFiles()
    {
        try
        {
            Directory.CreateDirectory(_data);
            var probe = Path.Combine(_data, $".probe-{Guid.NewGuid():n}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The free space of the volume of the blob store in bytes (-1 when unknown).</summary>
    public long FreeSpace() => store.FreeSpace();

    public async Task<InstanceSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var settings = await accounts.SettingsAsync();
        return new InstanceSnapshot(
            Users: await db.Users.CountAsync(u => u.IsActive, ct),
            Admins: await db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct),
            Registries: await db.Registries.CountAsync(r => r.IsActive, ct),
            RemoteRegistries: await db.Registries.CountAsync(r => r.IsActive && r.Kind == RegistryKind.Remote, ct),
            Unreachable: await db.Registries.CountAsync(r => r.IsActive && r.Kind == RegistryKind.Remote && r.IsReachable == false, ct),
            Repositories: await db.Repositories.CountAsync(ct),
            Tags: await db.Tags.CountAsync(ct),
            Manifests: await db.Manifests.CountAsync(ct),
            Blobs: await db.Blobs.CountAsync(ct),
            StoredBytes: await db.Blobs.SumAsync(b => (long?)b.Size, ct) ?? 0,
            Tokens: await db.AccessTokens.CountAsync(ct),
            CopiesRunning: await db.CopyJobs.CountAsync(j => j.State == CopyState.Running || j.State == CopyState.Waiting, ct),
            CopiesFailed7Days: await db.CopyJobs.CountAsync(j => j.State == CopyState.Failed && j.FinishedDate >= weekAgo, ct),
            LastPushUtc: await db.Repositories.MaxAsync(r => r.LastPushDate, ct),
            LastGcUtc: settings.LastGcDate,
            LastGcResult: settings.LastGcResult,
            LastActivityUtc: await db.Activities.MaxAsync(a => (DateTime?)a.OccurredDate, ct));
    }

    public Task<bool> AnyUserAsync() => accounts.AnyUserAsync();

    /// <summary>Where the data is: <c>PostgreSQL host:port/database</c> (for the admin page; never in the platform API).</summary>
    public string DatabaseName()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        return $"PostgreSQL {cs.Host}:{cs.Port}/{cs.Database}";
    }

    /// <summary>Where the blob store is (the admin page).</summary>
    public string StorePath => store.Root;

    public async Task<SystemSettings> SettingsAsync()
    {
        var s = await accounts.SettingsAsync();
        return new SystemSettings(s.RegistrationOpen, PublicUrlOf(s, platform), platform.AppPublicUrl is not null, s.TokenMinutes, s.GcIntervalHours, s.GcUntagged, s.LastGcDate,
            s.LastGcResult);
    }

    /// <summary>
    /// Saves the settings of the system; a <c>null</c> leaves a value as it is, an empty address clears it. An address that <c>MAT_PUBLIC_URL</c> sets is left as it is.
    /// Answers the field and the message of the first wrong value (nothing is saved then), else <c>null</c>.
    /// </summary>
    public async Task<(string Field, string Message)?> SaveSettingsAsync(SettingsChange change)
    {
        var publicUrl = platform.AppPublicUrl is null ? change.PublicUrl : null;
        string? url = null;
        if (publicUrl is not null)
        {
            url = NormalizePublicUrl(publicUrl);
            if (url is not null && !IsPublicUrl(url))
            {
                return ("PublicUrl", Text.T("System.PublicUrl.Invalid"));
            }
        }

        if (change.TokenMinutes is < 1 or > 1440)
        {
            return ("TokenMinutes", Text.T("System.Range", 1, 1440));
        }

        if (change.GcIntervalHours is < 0 or > 720)
        {
            return ("GcIntervalHours", Text.T("System.Range", 0, 720));
        }

        var s = await accounts.SettingsAsync();
        if (change.RegistrationOpen is { } open)
        {
            s.RegistrationOpen = open;
        }

        if (publicUrl is not null)
        {
            s.PublicUrl = url;
        }

        s.TokenMinutes = change.TokenMinutes ?? s.TokenMinutes;
        s.GcIntervalHours = change.GcIntervalHours ?? s.GcIntervalHours;
        s.GcUntagged = change.GcUntagged ?? s.GcUntagged;
        await db.SaveChangesAsync();
        cache.Remove("registry:settings");
        return null;
    }

    /// <summary>The address without spaces and the last slash; <c>null</c> when empty.</summary>
    public static string? NormalizePublicUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>The housekeeping: ended sessions go; finished copies older than 30 days go from the list.</summary>
    public async Task<CleanUpResult> CleanUpAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sessions = await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        var monthAgo = now.AddDays(-30);
        var copies = await db.CopyJobs.Where(j => (j.State == CopyState.Done || j.State == CopyState.Failed) && j.FinishedDate < monthAgo).ExecuteDeleteAsync(ct);
        return new CleanUpResult(sessions, copies);
    }

    /// <summary>The garbage collection now (the admin page, the platform action, the schedule); null when one runs already.</summary>
    public async Task<GcResult?> CollectGarbageAsync(RegistryActor actor, CancellationToken ct = default)
    {
        var s = await accounts.SettingsAsync();
        return await GarbageCollector.RunAsync(db, store, s.GcUntagged, actor, ct);
    }
}
