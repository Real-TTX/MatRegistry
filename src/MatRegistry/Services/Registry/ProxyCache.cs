using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>
/// A remote registry served as a pull-through cache: <c>docker pull &lt;host&gt;/dockerhub/nginx:1.27</c> fetches <c>library/nginx:1.27</c> from Docker Hub once and keeps
/// it. A tag is trusted for <c>CacheMinutes</c>, then the remote registry is asked again (HEAD, which does not count as a pull there); when it cannot be reached, what
/// the cache has is served. A blob is streamed to the client while it is stored, and stored only when its digest is right. Pushes are refused (<see cref="RegistryEngine"/>).
/// </summary>
public static class ProxyCache
{
    public static async Task<ManifestResult> GetManifestAsync(RegistryContext c, ResolvedRepository r, string reference, bool head, CancellationToken ct)
    {
        var target = TargetOf(c, r);
        var upstreamName = target.RepositoryOf(r.Name);
        var byDigest = RegistryNames.IsDigest(reference);
        var repository = r.Repository;

        if (repository is not null)
        {
            if (byDigest)
            {
                if (await RegistryEngine.FindManifestAsync(c.Db, repository.Id, reference, ct) is { } known)
                {
                    return new ManifestResult(known.Content, known.MediaType, known.Digest);
                }
            }
            else
            {
                var tag = await c.Db.Tags.AsNoTracking().Where(t => t.RepositoryId == repository.Id && t.Name == reference)
                    .Select(t => new { t.Id, t.CheckedDate, t.Manifest }).FirstOrDefaultAsync(ct);
                if (tag?.Manifest is { } cached)
                {
                    var fresh = tag.CheckedDate is { } checkedAt && checkedAt > DateTime.UtcNow.AddMinutes(-Math.Max(1, r.Registry.CacheMinutes));
                    if (!fresh)
                    {
                        try
                        {
                            var digest = await c.Upstream.HeadManifestAsync(target, upstreamName, reference, ct);
                            fresh = digest is null || digest == cached.Digest;
                        }
                        catch (UpstreamException)
                        {
                            // the remote registry is not there: what the cache has is better than nothing
                            fresh = true;
                        }

                        if (fresh)
                        {
                            var now = DateTime.UtcNow;
                            await c.Db.Tags.Where(t => t.Id == tag.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CheckedDate, now), ct);
                        }
                    }

                    if (fresh)
                    {
                        if (!head)
                        {
                            await RegistryEngine.CountPullAsync(c.Db, repository.Id, cached.Id, ct);
                        }

                        return new ManifestResult(cached.Content, cached.MediaType, cached.Digest);
                    }
                }
            }
        }

        UpstreamManifest fetched;
        try
        {
            fetched = await c.Upstream.GetManifestAsync(target, upstreamName, reference, ct) ?? throw RegistryException.ManifestUnknown(reference);
        }
        catch (UpstreamException ex)
        {
            throw ex.NotFound ? RegistryException.ManifestUnknown(reference) : RegistryException.Upstream(ex.Message);
        }

        repository ??= await RegistryEngine.EnsureRepositoryAsync(c.Db, r.Registry.Id, r.Name, null, ct);
        await FetchConfigAsync(c, target, upstreamName, repository.Id, fetched, ct);
        var stored = await RegistryEngine.StoreManifestAsync(c, repository.Id, fetched.Content, fetched.MediaType, byDigest ? null : reference, requireReferences: false,
            null, DateTime.UtcNow, ct);
        if (!head && !byDigest)
        {
            await RegistryEngine.CountPullAsync(c.Db, repository.Id, stored.Id, ct);
        }

        return new ManifestResult(fetched.Content, stored.MediaType, fetched.Digest);
    }

    public static async Task<BlobResult> GetBlobAsync(RegistryContext c, ResolvedRepository r, string digest, bool head, CancellationToken ct)
    {
        var target = TargetOf(c, r);
        var upstreamName = target.RepositoryOf(r.Name);
        try
        {
            if (c.Store.Exists(digest) || head)
            {
                // the store may have it from another repository: it belongs to this one only when the remote registry has it here too
                var size = await c.Upstream.HeadBlobAsync(target, upstreamName, digest, forPush: false, ct) ?? throw RegistryException.BlobUnknown(digest);
                if (!c.Store.Exists(digest))
                {
                    return new BlobResult(digest, size, null);
                }

                var repository = r.Repository ?? await RegistryEngine.EnsureRepositoryAsync(c.Db, r.Registry.Id, r.Name, null, ct);
                await RegistryEngine.LinkBlobAsync(c.Db, repository.Id, digest, c.Store.SizeOf(digest), null, ct);
                return new BlobResult(digest, c.Store.SizeOf(digest), c.Store.PathOf(digest));
            }

            var response = await c.Upstream.GetBlobAsync(target, upstreamName, digest, ct) ?? throw RegistryException.BlobUnknown(digest);
            var repositoryId = (r.Repository ?? await RegistryEngine.EnsureRepositoryAsync(c.Db, r.Registry.Id, r.Name, null, ct)).Id;
            var length = response.Content.Headers.ContentLength ?? -1;
            return new BlobResult(digest, length, null, (output, token) => StreamAndStoreAsync(c, response, repositoryId, digest, output, token));
        }
        catch (UpstreamException ex)
        {
            throw ex.NotFound ? RegistryException.BlobUnknown(digest) : RegistryException.Upstream(ex.Message);
        }
    }

    /// <summary>The tags as the remote registry has them now; what the cache has when it cannot be reached.</summary>
    public static async Task<List<string>> TagsAsync(RegistryContext c, ResolvedRepository r, CancellationToken ct)
    {
        var target = TargetOf(c, r);
        try
        {
            return await c.Upstream.TagsAsync(target, target.RepositoryOf(r.Name), 10000, ct);
        }
        catch (UpstreamException ex)
        {
            if (r.Repository is not null)
            {
                return await c.Db.Tags.AsNoTracking().Where(t => t.RepositoryId == r.Repository.Id).Select(t => t.Name).ToListAsync(ct);
            }

            throw ex.NotFound ? RegistryException.NameUnknown(r.FullName) : RegistryException.Upstream(ex.Message);
        }
    }

    /// <summary>
    /// The config of an image is fetched with its manifest (it is small, and the client asks for it right after): so the platform and the date of the image are
    /// known in the pages from the start.
    /// </summary>
    private static async Task FetchConfigAsync(RegistryContext c, UpstreamTarget target, string upstreamName, long repositoryId, UpstreamManifest manifest, CancellationToken ct)
    {
        var (doc, _) = ManifestDocument.Parse(manifest.Content, manifest.MediaType);
        if (doc is not { Kind: ManifestKind.Image, Config: { } config } || !MediaTypes.IsImageConfig(config.MediaType) || config.Size > RegistryEngine.MaxManifestBytes)
        {
            return;
        }

        if (!c.Store.Exists(config.Digest))
        {
            using var response = await c.Upstream.GetBlobAsync(target, upstreamName, config.Digest, ct);
            if (response is null || !await c.Store.PutAsync(await response.Content.ReadAsByteArrayAsync(ct), config.Digest, ct))
            {
                return;
            }
        }

        await RegistryEngine.LinkBlobAsync(c.Db, repositoryId, config.Digest, c.Store.SizeOf(config.Digest), null, ct);
    }

    /// <summary>Copies the blob to the client and into a file at once; the file goes into the store when the digest is right.</summary>
    private static async Task StreamAndStoreAsync(RegistryContext c, HttpResponseMessage response, long repositoryId, string digest, Stream output, CancellationToken ct)
    {
        var temp = c.Store.NewTempPath();
        try
        {
            using (response)
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var size = await c.Store.CommitAsync(temp, digest, CancellationToken.None);
            if (size >= 0)
            {
                await RegistryEngine.LinkBlobAsync(c.Db, repositoryId, digest, size, null, CancellationToken.None);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static UpstreamTarget TargetOf(RegistryContext c, ResolvedRepository r)
        => c.Secrets.TargetOf(r.Registry) ?? throw RegistryException.Upstream("The address of the remote registry is not valid.");
}
