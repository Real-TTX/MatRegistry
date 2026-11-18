using System.Globalization;
using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>What a garbage collection removed.</summary>
public sealed record GcResult(int Manifests, int Blobs, long Bytes, int Uploads, int Files)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Manifests} manifests, {Blobs} blobs ({Ui.Bytes(Bytes)}), {Uploads} uploads, {Files} files");
}

/// <summary>
/// The garbage collection: what no tag and no manifest needs any more leaves the store. In order: uploads left for a day; with <c>GcUntagged</c> the manifests that no
/// tag, no index and no subject keeps (until nothing more goes); the blobs a repository holds but none of its manifests needs; the blobs no repository holds (their
/// files too); files without a record. Everything younger than an hour stays, and a repository with an upload in progress keeps its blobs, so a push that is
/// running is never cut. One collection at a time.
/// </summary>
public static class GarbageCollector
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<GcResult?> RunAsync(AppDbContext db, BlobStore store, bool untagged, RegistryActor actor, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            return null;
        }

        try
        {
            var now = DateTime.UtcNow;
            var grace = now.AddHours(-1);

            var oldUploads = await db.BlobUploads.Where(u => u.UpdateDate < now.AddDays(-1)).Select(u => new { u.Id, u.Token }).ToListAsync(ct);
            foreach (var upload in oldUploads)
            {
                store.DeleteUpload(upload.Token);
            }

            var oldIds = oldUploads.Select(u => u.Id).ToList();
            await db.BlobUploads.Where(u => oldIds.Contains(u.Id)).ExecuteDeleteAsync(ct);

            var manifests = 0;
            if (untagged)
            {
                for (var round = 0; round < 10; round++)
                {
                    var removed = await db.Manifests.Where(m => m.CreateDate < grace
                            && !db.Tags.Any(t => t.ManifestId == m.Id)
                            && !db.ManifestReferences.Any(r => r.Kind == ReferenceKind.Manifest && r.Digest == m.Digest && r.Manifest!.RepositoryId == m.RepositoryId)
                            && (m.SubjectDigest == null || !db.Manifests.Any(s => s.RepositoryId == m.RepositoryId && s.Digest == m.SubjectDigest)))
                        .ExecuteDeleteAsync(ct);
                    manifests += removed;
                    if (removed == 0)
                    {
                        break;
                    }
                }
            }

            await db.RepositoryBlobs.Where(l => l.CreateDate < grace
                    && !db.ManifestReferences.Any(r => r.Kind != ReferenceKind.Manifest && r.Digest == l.Blob!.Digest && r.Manifest!.RepositoryId == l.RepositoryId)
                    && !db.BlobUploads.Any(u => u.RepositoryId == l.RepositoryId))
                .ExecuteDeleteAsync(ct);

            var unused = await db.Blobs.Where(b => b.CreateDate < grace && !db.RepositoryBlobs.Any(l => l.BlobId == b.Id))
                .Select(b => new { b.Id, b.Digest, b.Size }).ToListAsync(ct);
            foreach (var chunk in unused.Chunk(500))
            {
                var ids = chunk.Select(b => b.Id).ToList();
                await db.Blobs.Where(b => ids.Contains(b.Id)).ExecuteDeleteAsync(ct);
                foreach (var blob in chunk)
                {
                    store.Delete(blob.Digest);
                }
            }

            var known = (await db.Blobs.AsNoTracking().Select(b => b.Digest).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            var files = 0;
            long orphanBytes = 0;
            foreach (var file in store.Files().Where(f => f.WrittenUtc < grace && !known.Contains(f.Digest)).ToList())
            {
                store.Delete(file.Digest);
                orphanBytes += file.Size;
                files++;
            }

            var active = (await db.BlobUploads.AsNoTracking().Select(u => u.Token).ToListAsync(ct)).Select(t => t.ToString("n")).ToHashSet(StringComparer.Ordinal);
            files += store.CleanLeftovers(TimeSpan.FromDays(1), active);

            var result = new GcResult(manifests, unused.Count, unused.Sum(b => b.Size) + orphanBytes, oldUploads.Count, files);
            var settings = await SettingsStore.LoadAsync(db, ct);
            settings.LastGcDate = now;
            settings.LastGcResult = result.Describe();
            await db.SaveChangesAsync(ct);
            await ActivityLog.AddAsync(db, actor, "gc", null, result.Describe(), null, null, ct);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }
}
