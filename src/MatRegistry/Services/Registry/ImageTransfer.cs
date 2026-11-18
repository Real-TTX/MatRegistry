using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>The progress of a copy, reported after each blob.</summary>
public sealed record TransferProgress(int BlobsTotal, int BlobsCopied, int BlobsSkipped, long BytesCopied);

/// <summary>
/// Copies an image between two registries, each hosted (the database and the blob store) or remote (the client): the manifest, for an index the manifest of every
/// platform first, and every blob the target does not have yet. A blob from a remote registry is staged in a file and checked against its digest before it goes on.
/// Used by the copy jobs (<see cref="CopyWorker"/>).
/// </summary>
public static class ImageTransfer
{
    public static async Task<string> RunAsync(RegistryContext c, Data.Registry source, string sourceRepository, string reference, Data.Registry target,
        string targetRepository, string? targetTag, long? userId, Func<TransferProgress, Task> report, CancellationToken ct)
    {
        var from = await Side.ForAsync(c, source, sourceRepository, create: false, userId, ct);
        var to = await Side.ForAsync(c, target, targetRepository, create: true, userId, ct);
        var top = await from.GetManifestAsync(c, reference, ct) ?? throw new UpstreamException($"The source has no manifest “{reference}”.");
        var (doc, error) = ManifestDocument.Parse(top.Content, top.MediaType);
        if (doc is null)
        {
            throw new UpstreamException(error ?? "The manifest of the source is not valid.");
        }

        // the images: the top one, or the one of every platform of an index
        var images = new List<UpstreamManifest>();
        if (doc.Kind == ManifestKind.Index)
        {
            foreach (var child in doc.Manifests.DistinctBy(m => m.Digest))
            {
                var manifest = await from.GetManifestAsync(c, child.Digest, ct) ?? throw new UpstreamException($"The source does not have the manifest {RegistryNames.Short(child.Digest)} of its index.");
                images.Add(manifest);
            }
        }
        else
        {
            images.Add(top);
        }

        var blobs = images.Select(i => ManifestDocument.Parse(i.Content, i.MediaType).Manifest).Where(d => d is { Kind: ManifestKind.Image })
            .SelectMany(d => d!.Children).Where(b => !MediaTypes.IsForeign(b.MediaType)).DistinctBy(b => b.Digest).ToList();
        var progress = new TransferProgress(blobs.Count, 0, 0, 0);
        await report(progress);

        foreach (var blob in blobs)
        {
            ct.ThrowIfCancellationRequested();
            if (await to.HasBlobAsync(c, blob.Digest, ct))
            {
                progress = progress with { BlobsSkipped = progress.BlobsSkipped + 1 };
            }
            else
            {
                await CopyBlobAsync(c, from, to, blob.Digest, userId, ct);
                progress = progress with { BlobsCopied = progress.BlobsCopied + 1, BytesCopied = progress.BytesCopied + blob.Size };
            }

            await report(progress);
        }

        if (doc.Kind == ManifestKind.Index)
        {
            foreach (var image in images)
            {
                await to.PutManifestAsync(c, image, image.Digest, userId, ct);
            }
        }

        await to.PutManifestAsync(c, top, string.IsNullOrEmpty(targetTag) ? top.Digest : targetTag, userId, ct);
        return top.Digest;
    }

    private static async Task CopyBlobAsync(RegistryContext c, Side from, Side to, string digest, long? userId, CancellationToken ct)
    {
        // a blob of the store needs no copy into the store: it is only linked
        if (from.Local && to.Local)
        {
            await RegistryEngine.LinkBlobAsync(c.Db, to.RepositoryId, digest, c.Store.SizeOf(digest), userId, ct);
            return;
        }

        string path;
        var staged = false;
        if (from.Local)
        {
            if (!c.Store.Exists(digest))
            {
                throw new UpstreamException($"The store does not have the blob {RegistryNames.Short(digest)} any more.");
            }

            path = c.Store.PathOf(digest);
        }
        else
        {
            path = c.Store.NewTempPath();
            staged = true;
            using var response = await c.Upstream.GetBlobAsync(from.Target!, from.Name, digest, ct) ?? throw new UpstreamException($"The source does not have the blob {RegistryNames.Short(digest)}.");
            await using (var file = File.Create(path))
            {
                await (await response.Content.ReadAsStreamAsync(ct)).CopyToAsync(file, ct);
            }

            if (await BlobStore.DigestOfFileAsync(path, ct) != digest)
            {
                File.Delete(path);
                throw new UpstreamException($"The blob {RegistryNames.Short(digest)} of the source has another digest.");
            }
        }

        try
        {
            if (to.Local)
            {
                var size = await c.Store.CommitAsync(path, digest, ct);
                staged = false;
                await RegistryEngine.LinkBlobAsync(c.Db, to.RepositoryId, digest, size, userId, ct);
            }
            else
            {
                await c.Upstream.UploadBlobAsync(to.Target!, to.Name, digest, path, ct);
            }
        }
        finally
        {
            if (staged && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>One side of a copy: a repository of the store, or one of a remote registry (by its name there).</summary>
    private sealed class Side
    {
        public required bool Local { get; init; }

        public required string Name { get; init; }

        public UpstreamTarget? Target { get; init; }

        public long RepositoryId { get; private init; }

        /// <summary>The side of a registry: a hosted one by its repository (made for the target, it must exist for the source), a remote one by its address.</summary>
        public static async Task<Side> ForAsync(RegistryContext c, Data.Registry registry, string repository, bool create, long? userId, CancellationToken ct)
        {
            if (registry.Kind == RegistryKind.Hosted)
            {
                var id = create
                    ? (await RegistryEngine.EnsureRepositoryAsync(c.Db, registry.Id, repository, userId, ct)).Id
                    : await c.Db.Repositories.AsNoTracking().Where(r => r.RegistryId == registry.Id && r.Name == repository).Select(r => r.Id).FirstOrDefaultAsync(ct);
                return id == 0
                    ? throw new UpstreamException($"The repository {registry.Name}/{repository} does not exist.")
                    : new Side { Local = true, Name = repository, RepositoryId = id };
            }

            var target = c.Secrets.TargetOf(registry) ?? throw new UpstreamException($"The address of {registry.Name} is not valid.");
            return new Side { Local = false, Name = target.RepositoryOf(repository), Target = target };
        }

        public async Task<UpstreamManifest?> GetManifestAsync(RegistryContext c, string reference, CancellationToken ct)
        {
            if (!Local)
            {
                return await c.Upstream.GetManifestAsync(Target!, Name, reference, ct);
            }

            var manifest = await RegistryEngine.FindManifestAsync(c.Db, RepositoryId, reference, ct);
            return manifest is null ? null : new UpstreamManifest(manifest.Content, manifest.MediaType, manifest.Digest);
        }

        public async Task<bool> HasBlobAsync(RegistryContext c, string digest, CancellationToken ct)
            => Local
                ? await RegistryEngine.LinkedSizeAsync(c.Db, RepositoryId, digest, ct) is not null && c.Store.Exists(digest)
                : await c.Upstream.HeadBlobAsync(Target!, Name, digest, forPush: true, ct) is not null;

        public async Task PutManifestAsync(RegistryContext c, UpstreamManifest manifest, string reference, long? userId, CancellationToken ct)
        {
            if (!Local)
            {
                await c.Upstream.PutManifestAsync(Target!, Name, reference, manifest.Content, manifest.MediaType, ct);
                return;
            }

            await RegistryEngine.StoreManifestAsync(c, RepositoryId, manifest.Content, manifest.MediaType, RegistryNames.IsDigest(reference) ? null : reference,
                requireReferences: true, userId, null, ct);
            var now = DateTime.UtcNow;
            await c.Db.Repositories.Where(r => r.Id == RepositoryId).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastPushDate, now).SetProperty(r => r.UpdateDate, now), ct);
        }
    }
}
