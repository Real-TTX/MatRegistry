using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Registries;

/// <summary>
/// An image (a tag or a digest) of a remote registry as the registry has it now: its platforms or layers, its config, its JSON. It can be copied into another registry;
/// an Editor of the remote registry deletes it there (many registries refuse that, and say so).
/// </summary>
public sealed class RemoteModel(AppDbContext db, BlobStore store, UpstreamClient upstream, RegistrySecrets secrets) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty(SupportsGet = true)] public string Repository { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)] public string Reference { get; set; } = string.Empty;

    public RemoteAccess Access { get; private set; } = null!;

    public RemoteManifest? Manifest { get; private set; }

    public string? Error { get; private set; }

    public string BrowseUrl => $"/Registries/Browse?id={Id}&repository={Uri.EscapeDataString(Repository)}";

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var c = new RegistryContext(db, store, upstream, secrets);
        if (await RemoteBrowser.AccessAsync(c, RegistryActor.ForUser(Me), Id, ct) is not { } access)
        {
            return NotFound();
        }

        Access = access;
        if (!RegistryNames.IsName(Repository) || (!RegistryNames.IsTag(Reference) && !RegistryNames.IsDigest(Reference)))
        {
            return NotFound();
        }

        try
        {
            Manifest = await RemoteBrowser.ManifestAsync(c, access, Repository, Reference, ct);
            Error = Manifest is null ? Text.T("Registries.Remote.Unknown", Reference) : null;
        }
        catch (UpstreamException ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string digest)
    {
        var ct = HttpContext.RequestAborted;
        var c = new RegistryContext(db, store, upstream, secrets);
        if (await RemoteBrowser.AccessAsync(c, RegistryActor.ForUser(Me), Id, ct) is not { } access)
        {
            return NotFound();
        }

        try
        {
            if (!await RemoteBrowser.DeleteAsync(c, RegistryActor.ForUser(Me), access, Repository, digest, ct))
            {
                return NotFound();
            }
        }
        catch (UpstreamException ex)
        {
            FlashError(Text.T("Registries.Remote.DeleteFailed", ex.Message));
            return LocalRedirect($"/Registries/Remote?id={Id}&repository={Uri.EscapeDataString(Repository)}&reference={Uri.EscapeDataString(Reference)}");
        }

        Flash(Text.T("Manifest.Deleted", RegistryNames.Short(digest)));
        return LocalRedirect(BrowseUrl);
    }
}
