using MatRegistry.Controls;
using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Registries;

/// <summary>
/// Creates (a hosted registry) or binds (a remote one) a registry, or changes it: name, kind, who may pull without signing in; for a remote registry its address, the
/// credentials (the secret is never shown again, only whether one is set), the pull-through cache. Test checks a remote registry; Delete removes it with its
/// repositories. Administrators only (checked again in <see cref="RegistryAdmin"/>).
/// </summary>
public sealed class EditModel(AppDbContext db, RegistrySecrets secrets, BlobStore store, UpstreamClient upstream, InstanceService instance) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)] public RegistryKind Kind { get; set; } = RegistryKind.Hosted;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public RegistryVisibility Visibility { get; set; } = RegistryVisibility.Private;

    [BindProperty] public bool IsActive { get; set; } = true;

    [BindProperty] public string Address { get; set; } = string.Empty;

    [BindProperty] public string Username { get; set; } = string.Empty;

    [BindProperty] public string Password { get; set; } = string.Empty;

    [BindProperty] public bool ClearPassword { get; set; }

    [BindProperty] public bool ProxyEnabled { get; set; } = true;

    [BindProperty] public int CacheMinutes { get; set; } = 60;

    [BindProperty] public bool SkipTlsVerify { get; set; }

    public RegistryForm? Stored { get; private set; }

    public bool IsNew => Id is null;

    public string Host { get; private set; } = string.Empty;

    public string Zone => Me.TimeZone;

    public List<Choice> Kinds { get; } =
    [
        new(nameof(RegistryKind.Hosted), Text.T("Registries.Kind.Hosted"), Text.T("Registries.Kind.Hosted.Hint")),
        new(nameof(RegistryKind.Remote), Text.T("Registries.Kind.Remote"), Text.T("Registries.Kind.Remote.Hint")),
    ];

    public List<Choice> Visibilities { get; } =
    [
        new(nameof(RegistryVisibility.Private), Text.T("Registries.Private"), Text.T("Registries.Private.Hint")),
        new(nameof(RegistryVisibility.Public), Text.T("Registries.Public"), Text.T("Registries.Public.Hint")),
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Me.IsAdmin)
        {
            return NotFound();
        }

        await LoadHostAsync();
        if (Id is not { } id)
        {
            return Page();
        }

        if (await RegistryAdmin.GetAsync(db, RegistryActor.ForUser(Me), id, HttpContext.RequestAborted) is not { } found)
        {
            return NotFound();
        }

        var s = found.Form;
        Stored = s;
        Name = s.Name;
        Kind = s.Kind;
        Description = s.Description ?? string.Empty;
        Visibility = s.Visibility;
        IsActive = s.IsActive;
        Address = s.Url ?? string.Empty;
        Username = s.Username ?? string.Empty;
        ProxyEnabled = s.ProxyEnabled;
        CacheMinutes = s.CacheMinutes;
        SkipTlsVerify = s.SkipTlsVerify;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var input = new RegistryInput(Name, Kind, Description, Visibility, IsActive, Address, Username, Password, ClearPassword, ProxyEnabled, CacheMinutes, SkipTlsVerify);
        var (savedId, errors, denied) = await RegistryAdmin.SaveAsync(db, secrets, RegistryActor.ForUser(Me), Id, input, HttpContext.RequestAborted);
        if (denied)
        {
            return NotFound();
        }

        if (savedId is null)
        {
            errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
            await ReloadAsync();
            return Page();
        }

        // a remote registry is checked right away, so the list says whether it answers
        if (Kind == RegistryKind.Remote)
        {
            await RegistryAdmin.CheckAsync(new RegistryContext(db, store, upstream, secrets), RegistryActor.ForUser(Me), savedId.Value, HttpContext.RequestAborted);
        }

        Flash(Text.T(IsNew ? "Common.CreatedNamed" : "Common.SavedNamed", Name.Trim().ToLowerInvariant()));
        return LocalRedirect(IsNew ? $"/Registries/View?id={savedId}" : "/Registries");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (Id is not { } id || !Me.IsAdmin)
        {
            return NotFound();
        }

        var result = await RegistryAdmin.CheckAsync(new RegistryContext(db, store, upstream, secrets), RegistryActor.ForUser(Me), id, HttpContext.RequestAborted);
        if (result is null)
        {
            return NotFound();
        }

        if (result.Value.Ok)
        {
            Flash(Text.T("Registries.Test.Ok"));
        }
        else
        {
            FlashError(Text.T("Registries.Test.Failed", result.Value.Message));
        }

        return LocalRedirect($"/Registries/Edit?id={id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not { } id || !await RegistryAdmin.DeleteAsync(db, RegistryActor.ForUser(Me), id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", Name));
        return LocalRedirect("/Registries");
    }

    private async Task ReloadAsync()
    {
        await LoadHostAsync();
        if (Id is { } id && await RegistryAdmin.GetAsync(db, RegistryActor.ForUser(Me), id, HttpContext.RequestAborted) is { } found)
        {
            Stored = found.Form;
        }
    }

    private async Task LoadHostAsync() => Host = InstanceService.RegistryHost((await instance.SettingsAsync()).PublicUrl, Request);
}
