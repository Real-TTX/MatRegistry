using MatRegistry.Data;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Registries;

/// <summary>
/// A registry. Hosted: how to log in, push and pull, and its repositories. Remote: what it is, how to pull through its cache, and the way into it: its catalog when it
/// offers one, else a repository opened by its name; the repositories its cache holds.
/// </summary>
public sealed class ViewModel(AppDbContext db, BlobStore store, UpstreamClient upstream, RegistrySecrets secrets, InstanceService instance) : AppPageModel
{
    private const int CatalogPage = 50;

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    /// <summary>The last repository of the page before (the catalog of the remote registry is paged by name).</summary>
    [BindProperty(SupportsGet = true)] public string? After { get; set; }

    public RegistryForm Registry { get; private set; } = null!;

    public AccessRole Role { get; private set; }

    public List<RepositoryRow> Repositories { get; private set; } = [];

    public int RepositoryCount { get; private set; }

    /// <summary>The catalog of a remote registry (null: it offers none); <see cref="CatalogNext"/> the start of the next page.</summary>
    public List<string>? Catalog { get; private set; }

    public string? CatalogNext { get; private set; }

    /// <summary>What the remote registry answered when it failed.</summary>
    public string? RemoteError { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public bool IsAdmin => Me.IsAdmin;

    public string UserName => Me.Snapshot?.Email ?? string.Empty;

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var actor = RegistryActor.ForUser(Me);
        if (await RegistryAdmin.GetAsync(db, actor, Id, ct) is not { } found)
        {
            return NotFound();
        }

        Registry = found.Form;
        Role = found.Role;
        Host = InstanceService.RegistryHost((await instance.SettingsAsync()).PublicUrl, Request);
        var list = await RepositoryService.ListAsync(db, actor, new RepositoryCriteria(null, Id, null, "pushed", false, 1, 10), ct);
        Repositories = list.Items;
        RepositoryCount = list.Matching;

        if (Registry.Kind == RegistryKind.Remote && Registry.IsActive)
        {
            var c = new RegistryContext(db, store, upstream, secrets);
            if (await RemoteBrowser.AccessAsync(c, actor, Id, ct) is { } access)
            {
                try
                {
                    var page = await RemoteBrowser.CatalogAsync(c, access, After, CatalogPage, ct);
                    Catalog = page?.Names;
                    CatalogNext = page?.Next;
                }
                catch (UpstreamException ex)
                {
                    RemoteError = ex.Message;
                }
            }
        }

        return Page();
    }
}
