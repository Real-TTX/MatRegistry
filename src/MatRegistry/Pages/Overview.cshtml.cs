using MatRegistry.Data;
using MatRegistry.Services;
using MatRegistry.Services.Registry;

namespace MatRegistry.Pages;

/// <summary>
/// The start page of a signed-in person: what they can reach (registries, repositories, tags, space), what was pushed last, and how to start with Docker
/// (the login with an access token, a push into a registry they may push to, a pull through a cache).
/// </summary>
public sealed class OverviewModel(AppDbContext db, InstanceService instance) : AppPageModel
{
    public Overview Numbers { get; private set; } = null!;

    public string Host { get; private set; } = string.Empty;

    /// <summary>A hosted registry the person may push to (for the example), else null.</summary>
    public string? PushRegistry { get; private set; }

    /// <summary>A cache of another registry the person may pull through (for the example), else null.</summary>
    public string? CacheRegistry { get; private set; }

    public int Tokens { get; private set; }

    public bool IsAdmin => Me.IsAdmin;

    public string Zone => Me.TimeZone;

    public string UserName => Me.Snapshot?.Email ?? string.Empty;

    public async Task OnGetAsync()
    {
        var actor = RegistryActor.ForUser(Me);
        var ct = HttpContext.RequestAborted;
        Numbers = await RepositoryService.OverviewAsync(db, actor, ct);
        var settings = await instance.SettingsAsync();
        Host = InstanceService.RegistryHost(settings.PublicUrl, Request);
        var editable = await RegistryAdmin.ChoicesAsync(db, actor, AccessRole.Editor, ct);
        PushRegistry = editable.FirstOrDefault(r => r.Kind == RegistryKind.Hosted).Name;
        var readable = await RegistryAdmin.ChoicesAsync(db, actor, AccessRole.Viewer, ct);
        CacheRegistry = await CacheOfAsync(readable.Where(r => r.Kind == RegistryKind.Remote).Select(r => r.Id).ToList());
        Tokens = (await AccessTokens.ListAsync(db, UserId, ct)).Count;
    }

    private async Task<string?> CacheOfAsync(List<long> ids)
    {
        foreach (var id in ids)
        {
            if (await RegistryAdmin.GetAsync(db, RegistryActor.ForUser(Me), id, HttpContext.RequestAborted) is { Form.ProxyEnabled: true } r)
            {
                return r.Form.Name;
            }
        }

        return null;
    }
}
