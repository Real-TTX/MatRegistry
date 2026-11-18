using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Repositories;

/// <summary>
/// A manifest of a repository: its facts and tags, for an image its config (entrypoint, environment, ports, labels, the steps of the build) and layers, for an index the
/// manifest of every platform, what refers to it (signatures, SBOMs), and its JSON. An Editor deletes it with every tag on it.
/// </summary>
public sealed class ManifestModel(AppDbContext db, BlobStore store, UpstreamClient upstream, RegistrySecrets secrets, InstanceService instance) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty(SupportsGet = true)] public string Digest { get; set; } = string.Empty;

    public RepositoryDetail Repository { get; private set; } = null!;

    public ManifestDetail Manifest { get; private set; } = null!;

    public string Host { get; private set; } = string.Empty;

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var actor = RegistryActor.ForUser(Me);
        if (await RepositoryService.GetAsync(db, actor, Id, ct) is not { } repository
            || await RepositoryService.ManifestAsync(new RegistryContext(db, store, upstream, secrets), actor, Id, Digest, ct) is not { } manifest)
        {
            return NotFound();
        }

        Repository = repository;
        Manifest = manifest;
        Host = InstanceService.RegistryHost((await instance.SettingsAsync()).PublicUrl, Request);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await RepositoryService.DeleteManifestAsync(db, RegistryActor.ForUser(Me), Id, Digest, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Manifest.Deleted", RegistryNames.Short(Digest)));
        return LocalRedirect($"/Repositories/View?id={Id}");
    }
}
