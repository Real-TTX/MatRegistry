using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Mail;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Admin;

/// <summary>
/// The settings of the installation in three tabs: General (registration, public address), Registry (the life of a token of the registry API), Storage (the garbage
/// collection: when it runs by itself, whether it takes untagged manifests, and a run by hand). Beside it what is in the installation. The same values as the platform
/// API; an address <c>MAT_PUBLIC_URL</c> sets is shown read-only.
/// </summary>
public sealed class SystemModel(InstanceService instance, MailService mail, AppDbContext db) : AppPageModel
{
    public static readonly string[] Tabs = ["general", "registry", "storage"];

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "general";

    [BindProperty] public bool RegistrationOpen { get; set; }

    [BindProperty] public string PublicUrl { get; set; } = string.Empty;

    [BindProperty] public int TokenMinutes { get; set; } = 15;

    [BindProperty] public int GcIntervalHours { get; set; } = 24;

    [BindProperty] public bool GcUntagged { get; set; }

    /// <summary><c>MAT_PUBLIC_URL</c> sets the public address.</summary>
    public bool PublicUrlFixed { get; private set; }

    public InstanceSnapshot Counts { get; private set; } = null!;

    public SystemSettings Settings { get; private set; } = null!;

    public string Database { get; private set; } = string.Empty;

    public string StorePath => instance.StorePath;

    public string Zone => Me.TimeZone;

    public long FreeBytes { get; private set; }

    /// <summary>The host of the pull commands, as people type it.</summary>
    public string Host { get; private set; } = string.Empty;

    /// <summary>Where the mail goes (MatSMTP), or null when the installation sends none.</summary>
    public string? MailText => mail.Describe(Languages.Current);

    public async Task OnGetAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";
        var s = await instance.SettingsAsync();
        RegistrationOpen = s.RegistrationOpen;
        PublicUrl = s.PublicUrl ?? string.Empty;
        TokenMinutes = s.TokenMinutes;
        GcIntervalHours = s.GcIntervalHours;
        GcUntagged = s.GcUntagged;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "general";

        // each tab sends its own fields; the others stay as they are
        var change = Tab switch
        {
            "registry" => new SettingsChange(TokenMinutes: TokenMinutes),
            "storage" => new SettingsChange(GcIntervalHours: GcIntervalHours, GcUntagged: GcUntagged),
            _ => new SettingsChange(RegistrationOpen: RegistrationOpen, PublicUrl: PublicUrl),
        };
        if (await instance.SaveSettingsAsync(change) is { } problem)
        {
            ModelState.AddModelError(problem.Field, problem.Message);
            await LoadAsync();
            return Page();
        }

        await ActivityLog.AddAsync(db, RegistryActor.ForUser(Me), "setting", Text.T("System.Tab." + Tab), null, null, null, HttpContext.RequestAborted);
        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/System?tab=" + Tab);
    }

    public async Task<IActionResult> OnPostCollectAsync()
    {
        var result = await instance.CollectGarbageAsync(RegistryActor.ForUser(Me), HttpContext.RequestAborted);
        if (result is null)
        {
            FlashError(Text.T("System.Gc.Running"));
        }
        else
        {
            Flash(Text.T("System.Gc.Done", Ui.Bytes(result.Bytes), result.Blobs, result.Manifests));
        }

        return LocalRedirect("/Admin/System?tab=storage");
    }

    public async Task<IActionResult> OnPostCleanUpAsync()
    {
        var result = await instance.CleanUpAsync(HttpContext.RequestAborted);
        Flash(Text.T("System.CleanedUp", result.Sessions, result.Copies));
        return LocalRedirect("/Admin/System");
    }

    private async Task LoadAsync()
    {
        Counts = await instance.SnapshotAsync();
        Settings = await instance.SettingsAsync();
        Database = instance.DatabaseName();
        FreeBytes = instance.FreeSpace();
        PublicUrlFixed = Settings.PublicUrlFixed;
        Host = InstanceService.RegistryHost(Settings.PublicUrl, Request);
        if (PublicUrlFixed)
        {
            PublicUrl = Settings.PublicUrl ?? string.Empty;
        }
    }
}
