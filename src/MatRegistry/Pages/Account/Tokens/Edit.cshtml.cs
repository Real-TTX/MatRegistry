using MatRegistry.Controls;
using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Account.Tokens;

/// <summary>
/// An access token: a new one (name, what it may, when it ends; its secret is shown once, right in the answer of the form, and kept nowhere but as a hash), or one
/// that exists (what it is, when it was used; Delete ends it at once).
/// </summary>
public sealed class EditModel(AppDbContext db, InstanceService instance) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? Token { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public TokenScope Scope { get; set; } = TokenScope.Pull;

    [BindProperty] public string Expiry { get; set; } = "90";

    public AccessTokenRow? Existing { get; private set; }

    /// <summary>The token that was just made (its secret is shown once).</summary>
    public NewAccessToken? Created { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public string UserName => Me.Snapshot?.Email ?? string.Empty;

    public string Zone => Me.TimeZone;

    public List<Choice> Scopes { get; } =
    [
        new(nameof(TokenScope.Pull), Text.T("Tokens.Scope.Pull"), Text.T("Tokens.Scope.Pull.Hint")),
        new(nameof(TokenScope.PullPush), Text.T("Tokens.Scope.PullPush"), Text.T("Tokens.Scope.PullPush.Hint")),
    ];

    public List<Choice> Expiries { get; } =
    [
        new("30", Text.T("Tokens.Days", 30)),
        new("90", Text.T("Tokens.Days", 90)),
        new("365", Text.T("Tokens.Days", 365)),
        new("never", Text.T("Tokens.NoExpiry"), Text.T("Tokens.NoExpiry.Hint")),
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadHostAsync();
        if (Token is { } token)
        {
            Existing = (await AccessTokens.ListAsync(db, UserId, HttpContext.RequestAborted)).FirstOrDefault(t => t.Token == token);
            if (Existing is null)
            {
                return NotFound();
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadHostAsync();
        int? days = int.TryParse(Expiry, out var d) ? d : null;
        var (created, error) = await AccessTokens.CreateAsync(db, UserId, Name, Scope, days, HttpContext.RequestAborted);
        if (created is null)
        {
            ModelState.AddModelError(error!.Field, error.Message);
            return Page();
        }

        Created = created;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Token is not { } token || !await AccessTokens.DeleteAsync(db, UserId, token, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Tokens.Deleted"));
        return LocalRedirect("/Account/Tokens");
    }

    private async Task LoadHostAsync() => Host = InstanceService.RegistryHost((await instance.SettingsAsync()).PublicUrl, Request);
}
