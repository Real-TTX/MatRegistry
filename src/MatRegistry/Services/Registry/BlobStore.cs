using System.Security.Cryptography;

namespace MatRegistry.Services.Registry;

/// <summary>
/// The files of the images, by their digest: <c>&lt;data&gt;/registry/blobs/sha256/ab/abcdef…</c>, each stored once for every repository. Uploads grow in
/// <c>registry/uploads/&lt;token&gt;</c> and move into the store when their digest is right; a download from another registry is staged in <c>registry/tmp</c>.
/// A file in the store is never changed: a digest names exactly one content. Which repository may read a blob is the database's business (<c>RepositoryBlob</c>).
/// </summary>
public sealed class BlobStore
{
    private const int BufferSize = 1 << 20;

    public BlobStore(IConfiguration configuration)
        : this(Path.Combine(configuration["DataDirectory"] ?? "/data", "registry"))
    {
    }

    public BlobStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(BlobsFolder);
        Directory.CreateDirectory(UploadsFolder);
        Directory.CreateDirectory(TempFolder);
    }

    public string Root { get; }

    private string BlobsFolder => Path.Combine(Root, "blobs", "sha256");

    private string UploadsFolder => Path.Combine(Root, "uploads");

    private string TempFolder => Path.Combine(Root, "tmp");

    /// <summary>The file of a digest (that it is a digest was checked before: only hex digits reach a path).</summary>
    public string PathOf(string digest)
    {
        if (!RegistryNames.IsDigest(digest))
        {
            throw new ArgumentException("Not a digest.", nameof(digest));
        }

        var hex = RegistryNames.Hex(digest);
        return Path.Combine(BlobsFolder, hex[..2], hex);
    }

    public bool Exists(string digest) => RegistryNames.IsDigest(digest) && File.Exists(PathOf(digest));

    public long SizeOf(string digest) => new FileInfo(PathOf(digest)).Length;

    public FileStream OpenRead(string digest)
        => new(PathOf(digest), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task<byte[]> ReadAllAsync(string digest, CancellationToken ct) => await File.ReadAllBytesAsync(PathOf(digest), ct);

    public string UploadPath(Guid token) => Path.Combine(UploadsFolder, token.ToString("n"));

    /// <summary>A new file for a download of another registry or a copy (the caller deletes it, or <see cref="CommitAsync"/> moves it).</summary>
    public string NewTempPath() => Path.Combine(TempFolder, Guid.NewGuid().ToString("n"));

    /// <summary>Starts an empty upload file.</summary>
    public void StartUpload(Guid token) => File.Create(UploadPath(token)).Dispose();

    /// <summary>Appends a request body to an upload; answers the new size.</summary>
    public async Task<long> AppendAsync(Guid token, Stream body, CancellationToken ct)
    {
        await using var file = new FileStream(UploadPath(token), FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        await body.CopyToAsync(file, BufferSize, ct);
        return file.Length;
    }

    public long UploadSize(Guid token)
    {
        var file = new FileInfo(UploadPath(token));
        return file.Exists ? file.Length : -1;
    }

    public void DeleteUpload(Guid token) => TryDelete(UploadPath(token));

    /// <summary>The digest of a file (read once).</summary>
    public static async Task<string> DigestOfFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, ct);
        return RegistryNames.Sha256 + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Moves a finished file into the store when its content has <paramref name="digest"/>; answers its size, or -1 when the content is something else (the file is
    /// deleted then). When the store has the blob already (another upload of the same layer at the same time) the file is dropped.
    /// </summary>
    public async Task<long> CommitAsync(string path, string digest, CancellationToken ct)
    {
        var actual = await DigestOfFileAsync(path, ct);
        if (!string.Equals(actual, digest, StringComparison.Ordinal))
        {
            TryDelete(path);
            return -1;
        }

        var target = PathOf(digest);
        var size = new FileInfo(path).Length;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.Move(path, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            TryDelete(path);
        }

        return size;
    }

    /// <summary>Stores bytes that are known (a manifest's config fetched from another registry); answers false when they are not <paramref name="digest"/>.</summary>
    public async Task<bool> PutAsync(byte[] content, string digest, CancellationToken ct)
    {
        if (RegistryNames.DigestOf(content) != digest)
        {
            return false;
        }

        if (Exists(digest))
        {
            return true;
        }

        var temp = NewTempPath();
        await File.WriteAllBytesAsync(temp, content, ct);
        return await CommitAsync(temp, digest, ct) >= 0;
    }

    /// <summary>Removes a blob from the store (the garbage collection, after its record is gone).</summary>
    public void Delete(string digest) => TryDelete(PathOf(digest));

    /// <summary>Every blob file of the store with its size and age (for the garbage collection: files without a record).</summary>
    public IEnumerable<(string Digest, long Size, DateTime WrittenUtc)> Files()
    {
        if (!Directory.Exists(BlobsFolder))
        {
            yield break;
        }

        foreach (var file in new DirectoryInfo(BlobsFolder).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var digest = RegistryNames.Sha256 + file.Name;
            if (RegistryNames.IsDigest(digest))
            {
                yield return (digest, file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>Files of uploads and downloads that were left (older than <paramref name="age"/>): removed, answers how many.</summary>
    public int CleanLeftovers(TimeSpan age, IReadOnlySet<string> activeUploads)
    {
        var removed = 0;
        var before = DateTime.UtcNow - age;
        foreach (var folder in new[] { UploadsFolder, TempFolder })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in new DirectoryInfo(folder).EnumerateFiles().Where(f => f.LastWriteTimeUtc < before && !activeUploads.Contains(f.Name)))
            {
                if (TryDelete(file.FullName))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    /// <summary>How many bytes the store holds (the admin page; walks the folder).</summary>
    public long TotalBytes() => Files().Sum(f => f.Size);

    /// <summary>The free space of the volume of the store in bytes (-1 when unknown).</summary>
    public long FreeSpace()
    {
        try
        {
            return new DriveInfo(Root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
