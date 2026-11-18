using MatRegistry.Data;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

/// <summary>A manifest of a remote registry as the page shows it.</summary>
public sealed record RemoteManifest(string Digest, string MediaType, ManifestKind Kind, long Size, IReadOnlyList<Descriptor> Children, ImageConfig? Config, string Json);

/// <summary>A remote registry the actor may browse: the registry, the role and the client's view of it.</summary>
public sealed record RemoteAccess(Data.Registry Registry, AccessRole Role, UpstreamTarget Target);

/// <summary>
/// Browsing a registry MatRegistry is bound to (the page of a remote registry): its catalog when it offers one (Docker Hub and GHCR do not: a repository is opened by
/// its name), the tags of a repository, a manifest with its platforms and layers, and, for an Editor, deleting a manifest there. Viewer at least; every call goes to
/// the remote registry with the stored credentials, which never reach the page.
/// </summary>
public static class RemoteBrowser
{
    public static async Task<RemoteAccess?> AccessAsync(RegistryContext c, RegistryActor actor, long registryId, CancellationToken ct)
    {
        var registry = await c.Db.Registries.AsNoTracking().FirstOrDefaultAsync(r => r.Id == registryId && r.Kind == RegistryKind.Remote && r.IsActive, ct);
        if (registry is null)
        {
            return null;
        }

        var role = await RegistryPermissions.RoleAsync(c.Db, actor, registry, ct);
        var target = c.Secrets.TargetOf(registry);
        return role == AccessRole.None || target is null ? null : new RemoteAccess(registry, role, target);
    }

    /// <summary>A page of the catalog; null when the registry offers none to these credentials.</summary>
    public static Task<(List<string> Names, string? Next)?> CatalogAsync(RegistryContext c, RemoteAccess access, string? last, int count, CancellationToken ct)
        => c.Upstream.CatalogAsync(access.Target, count, last, ct);

    public static async Task<List<string>> TagsAsync(RegistryContext c, RemoteAccess access, string repository, CancellationToken ct)
    {
        var tags = await c.Upstream.TagsAsync(access.Target, access.Target.RepositoryOf(repository), 5000, ct);
        tags.Sort(StringComparer.Ordinal);
        return tags;
    }

    public static async Task<RemoteManifest?> ManifestAsync(RegistryContext c, RemoteAccess access, string repository, string reference, CancellationToken ct)
    {
        var name = access.Target.RepositoryOf(repository);
        var manifest = await c.Upstream.GetManifestAsync(access.Target, name, reference, ct);
        if (manifest is null)
        {
            return null;
        }

        var (doc, error) = ManifestDocument.Parse(manifest.Content, manifest.MediaType);
        if (doc is null)
        {
            throw new UpstreamException(error ?? "The manifest is not valid.");
        }

        ImageConfig? config = null;
        if (doc is { Kind: ManifestKind.Image, Config: { } descriptor } && MediaTypes.IsImageConfig(descriptor.MediaType) && descriptor.Size < RegistryEngine.MaxManifestBytes)
        {
            using var response = await c.Upstream.GetBlobAsync(access.Target, name, descriptor.Digest, ct);
            if (response is not null)
            {
                config = ImageConfig.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            }
        }

        var children = doc.Kind == ManifestKind.Index ? doc.Manifests : doc.Children.ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(manifest.Content).RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return new RemoteManifest(manifest.Digest, doc.MediaType, doc.Kind, doc.Kind == ManifestKind.Image ? doc.DeclaredSize : doc.Manifests.Sum(m => m.Size), children, config, json);
    }

    /// <summary>Deletes a manifest at the remote registry (Editor); the registry may refuse it, the message says why.</summary>
    public static async Task<bool> DeleteAsync(RegistryContext c, RegistryActor actor, RemoteAccess access, string repository, string digest, CancellationToken ct)
    {
        if (access.Role < AccessRole.Editor || !RegistryNames.IsDigest(digest))
        {
            return false;
        }

        await c.Upstream.DeleteManifestAsync(access.Target, access.Target.RepositoryOf(repository), digest, ct);
        await ActivityLog.AddAsync(c.Db, actor, "delete", $"{access.Registry.Name}/{repository}@{digest}", "remote", access.Registry.Id, null, ct);
        return true;
    }
}
