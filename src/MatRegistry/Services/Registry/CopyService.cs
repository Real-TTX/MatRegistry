using System.Threading.Channels;
using MatRegistry.Data;
using MatRegistry.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>What a person asks to copy.</summary>
public sealed record CopyInput(long SourceRegistryId, string SourceRepository, string SourceReference, long TargetRegistryId, string TargetRepository, string TargetTag);

public sealed record CopyRow(Guid Token, string Source, string Target, CopyState State, int BlobsTotal, int BlobsCopied, int BlobsSkipped, long BytesCopied,
    string? Digest, string? Message, DateTime Created, DateTime? Finished, string Owner);

public sealed record CopyCriteria(string? Search, string? State, string Sort, bool Descending, int Page, int PageSize);

public sealed record CopyList(List<CopyRow> Items, int Total, int Matching);

/// <summary>
/// The copies of images between registries (the page Copies): a person may copy from a registry where they are Viewer into one where they are Editor, hosted or
/// remote. A copy is a job that <see cref="CopyWorker"/> runs in the background; a person sees their own, an administrator every one.
/// </summary>
public static class CopyService
{
    public static async Task<CopyList> ListAsync(AppDbContext db, RegistryActor actor, CopyCriteria criteria, CancellationToken ct)
    {
        var query = db.CopyJobs.AsNoTracking().IgnoreQueryFilters();
        if (!actor.IsAdmin)
        {
            query = query.Where(j => j.UserId == actor.UserId);
        }

        var total = await query.CountAsync(ct);
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var s = criteria.Search.Trim().ToLowerInvariant();
            query = query.Where(j => (j.SourceRegistry!.Name + "/" + j.SourceRepository).Contains(s) || (j.TargetRegistry!.Name + "/" + j.TargetRepository).Contains(s)
                                     || j.SourceReference.ToLower().Contains(s) || j.TargetTag.ToLower().Contains(s));
        }

        if (Enum.TryParse<CopyState>(criteria.State, true, out var state))
        {
            query = query.Where(j => j.State == state);
        }

        var matching = await query.CountAsync(ct);
        query = criteria.Sort switch
        {
            "source" => criteria.Descending ? query.OrderByDescending(j => j.SourceRepository) : query.OrderBy(j => j.SourceRepository),
            "state" => criteria.Descending ? query.OrderByDescending(j => j.State) : query.OrderBy(j => j.State),
            _ => criteria.Descending ? query.OrderBy(j => j.CreateDate) : query.OrderByDescending(j => j.CreateDate),
        };
        return new CopyList(await RowsAsync(db, query.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize), ct), total, matching);
    }

    public static async Task<CopyRow?> GetAsync(AppDbContext db, RegistryActor actor, Guid token, CancellationToken ct)
        => (await RowsAsync(db, db.CopyJobs.AsNoTracking().IgnoreQueryFilters().Where(j => j.Token == token && (actor.IsAdmin || j.UserId == actor.UserId)), ct))
            .FirstOrDefault();

    private static async Task<List<CopyRow>> RowsAsync(AppDbContext db, IQueryable<CopyJob> query, CancellationToken ct)
    {
        var rows = await query.Select(j => new
        {
            j.Token,
            Source = j.SourceRegistry!.Name + "/" + j.SourceRepository + (j.SourceReference.StartsWith("sha256:") ? "@" : ":") + j.SourceReference,
            Target = j.TargetRegistry!.Name + "/" + j.TargetRepository + (j.TargetTag == string.Empty ? string.Empty : ":" + j.TargetTag),
            j.State,
            j.BlobsTotal,
            j.BlobsCopied,
            j.BlobsSkipped,
            j.BytesCopied,
            j.Digest,
            j.Message,
            j.CreateDate,
            j.FinishedDate,
            j.UserId,
        }).ToListAsync(ct);
        var owners = await OwnersAsync(db, rows.Select(r => r.UserId), ct);
        return [.. rows.Select(r => new CopyRow(r.Token, r.Source, r.Target, r.State, r.BlobsTotal, r.BlobsCopied, r.BlobsSkipped, r.BytesCopied, r.Digest, r.Message,
            r.CreateDate, r.FinishedDate, owners.GetValueOrDefault(r.UserId, string.Empty)))];
    }

    /// <summary>Checks a copy against the roles of the person and starts it; answers the job, or the messages of the fields that are wrong.</summary>
    public static async Task<(CopyJob? Job, List<FieldError> Errors)> CreateAsync(AppDbContext db, RegistryActor actor, CopyInput input, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        var sourceRepository = (input.SourceRepository ?? string.Empty).Trim().Trim('/');
        var reference = (input.SourceReference ?? string.Empty).Trim();
        var targetRepository = (input.TargetRepository ?? string.Empty).Trim().Trim('/').ToLowerInvariant();
        var targetTag = (input.TargetTag ?? string.Empty).Trim();

        var source = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == input.SourceRegistryId && r.IsActive, ct);
        if (source is null || await RegistryPermissions.RoleAsync(db, actor, source, ct) < AccessRole.Viewer)
        {
            errors.Add(new FieldError("SourceRegistryId", Text.T("Copies.Source.Invalid")));
        }

        var target = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == input.TargetRegistryId && r.IsActive, ct);
        if (target is null || await RegistryPermissions.RoleAsync(db, actor, target, ct) < AccessRole.Editor)
        {
            errors.Add(new FieldError("TargetRegistryId", Text.T("Copies.Target.Invalid")));
        }

        if (!RegistryNames.IsName(sourceRepository.ToLowerInvariant()) && !RegistryNames.IsName(sourceRepository))
        {
            errors.Add(new FieldError("SourceRepository", Text.T("Repositories.Name.Invalid")));
        }
        else if (source is { Kind: RegistryKind.Hosted } && !await db.Repositories.AnyAsync(r => r.RegistryId == source.Id && r.Name == sourceRepository, ct))
        {
            errors.Add(new FieldError("SourceRepository", Text.T("Copies.Source.Unknown")));
        }

        if (!RegistryNames.IsTag(reference) && !RegistryNames.IsDigest(reference))
        {
            errors.Add(new FieldError("SourceReference", Text.T("Copies.Reference.Invalid")));
        }

        if (!RegistryNames.IsName(targetRepository))
        {
            errors.Add(new FieldError("TargetRepository", Text.T("Repositories.Name.Invalid")));
        }

        if (targetTag.Length > 0 && !RegistryNames.IsTag(targetTag))
        {
            errors.Add(new FieldError("TargetTag", Text.T("Copies.Tag.Invalid")));
        }

        if (errors.Count == 0 && source!.Id == target!.Id && sourceRepository == targetRepository && (targetTag.Length == 0 || targetTag == reference))
        {
            errors.Add(new FieldError("TargetTag", Text.T("Copies.Same")));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var job = new CopyJob
        {
            UserId = actor.UserId ?? 0,
            SourceRegistryId = source!.Id,
            SourceRepository = sourceRepository,
            SourceReference = reference,
            TargetRegistryId = target!.Id,
            TargetRepository = targetRepository,
            TargetTag = targetTag,
            State = CopyState.Waiting,
        };
        db.CopyJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return (job, errors);
    }

    /// <summary>Removes a copy from the list (not one that runs); false when it is not there or not the person's.</summary>
    public static async Task<bool> DeleteAsync(AppDbContext db, RegistryActor actor, Guid token, CancellationToken ct)
        => await db.CopyJobs.Where(j => j.Token == token && j.State != CopyState.Running && (actor.IsAdmin || j.UserId == actor.UserId)).ExecuteDeleteAsync(ct) > 0;

    private static async Task<Dictionary<long, string>> OwnersAsync(AppDbContext db, IEnumerable<long> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}

/// <summary>The queue of the copies: a page puts the id of a new job in, the worker takes it.</summary>
public sealed class CopyQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();

    public void Enqueue(long jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<long> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Runs the copies one after the other. After a restart the jobs that waited or ran start again (a copy skips what the target has).</summary>
public sealed class CopyWorker(IServiceScopeFactory scopes, CopyQueue queue, ILogger<CopyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            using (var scope = scopes.CreateScope())
            {
                // a copy that ran when the app stopped starts again
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.CopyJobs.Where(j => j.State == CopyState.Running).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, CopyState.Waiting), stoppingToken);
                var open = await db.CopyJobs.Where(j => j.State == CopyState.Waiting).OrderBy(j => j.Id).Select(j => j.Id).ToListAsync(stoppingToken);
                open.ForEach(queue.Enqueue);
            }

            await foreach (var id in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await RunAsync(id, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the database was not there, say: the job stays waiting and runs after the next start
                    logger.LogWarning(ex, "Copy {Id} could not run.", id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    public async Task RunAsync(long id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var c = new RegistryContext(db, services.GetRequiredService<BlobStore>(), services.GetRequiredService<UpstreamClient>(), services.GetRequiredService<RegistrySecrets>());
        // take the job (one statement: a job is run once even when it is in the queue twice)
        var started = DateTime.UtcNow;
        var taken = await db.CopyJobs.Where(j => j.Id == id && j.State == CopyState.Waiting).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, CopyState.Running)
            .SetProperty(j => j.StartedDate, started).SetProperty(j => j.Message, (string?)null).SetProperty(j => j.UpdateDate, started), ct);
        if (taken == 0)
        {
            return;
        }

        var job = await db.CopyJobs.AsNoTracking().IgnoreQueryFilters().FirstAsync(j => j.Id == id, ct);
        var source = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.SourceRegistryId && r.IsActive, ct);
        var target = await db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == job.TargetRegistryId && r.IsActive, ct);
        var actor = new RegistryActor(job.UserId, false, null, false, string.Empty);
        try
        {
            if (source is null || target is null)
            {
                throw new UpstreamException("A registry of the copy is gone or switched off.");
            }

            var digest = await ImageTransfer.RunAsync(c, source, job.SourceRepository, job.SourceReference, target, job.TargetRepository,
                string.IsNullOrEmpty(job.TargetTag) ? null : job.TargetTag, job.UserId, p => db.CopyJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.BlobsTotal, p.BlobsTotal).SetProperty(j => j.BlobsCopied, p.BlobsCopied).SetProperty(j => j.BlobsSkipped, p.BlobsSkipped)
                    .SetProperty(j => j.BytesCopied, p.BytesCopied), ct), ct);
            var done = DateTime.UtcNow;
            await db.CopyJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, CopyState.Done).SetProperty(j => j.Digest, digest)
                .SetProperty(j => j.FinishedDate, done).SetProperty(j => j.UpdateDate, done), ct);
            await ActivityLog.AddAsync(db, actor, "copy", $"{source.Name}/{job.SourceRepository} → {target.Name}/{job.TargetRepository}", RegistryNames.Short(digest),
                target.Id, null, ct);
            logger.LogInformation("Copy {Id} done: {Digest}", id, digest);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // every failure ends the job with its message; the worker goes on with the next one
            var failed = DateTime.UtcNow;
            var message = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            await db.CopyJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, CopyState.Failed).SetProperty(j => j.Message, message)
                .SetProperty(j => j.FinishedDate, failed).SetProperty(j => j.UpdateDate, failed), CancellationToken.None);
            logger.LogWarning("Copy {Id} failed: {Message}", id, ex.Message);
        }
    }
}
