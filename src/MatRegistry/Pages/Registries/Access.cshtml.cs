using MatRegistry.Controls;
using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Registries;

/// <summary>
/// Who may do what in a registry (tab Access): every active person with None, Viewer (pull, browse, copy from) or Editor (also push, delete, copy into). Administrators
/// have every role and are listed as such. Administrators only (checked again in <see cref="RegistryAdmin"/>).
/// </summary>
public sealed class AccessModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public long UserIdOf { get; set; }

    [BindProperty] public AccessRole Role { get; set; }

    public RegistryForm Registry { get; private set; } = null!;

    public List<AccessRow> People { get; private set; } = [];

    public List<Choice> RoleChoices { get; } =
    [
        new(nameof(AccessRole.None), Text.T("Roles.None")),
        new(nameof(AccessRole.Viewer), Text.T("Roles.Viewer")),
        new(nameof(AccessRole.Editor), Text.T("Roles.Editor")),
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        var actor = RegistryActor.ForUser(Me);
        if (!Me.IsAdmin || await RegistryAdmin.GetAsync(db, actor, Id, HttpContext.RequestAborted) is not { } found
            || await RegistryAdmin.AccessAsync(db, actor, Id, HttpContext.RequestAborted) is not { } people)
        {
            return NotFound();
        }

        Registry = found.Form;
        People = people;
        return Page();
    }

    public async Task<IActionResult> OnPostRoleAsync()
    {
        if (!await RegistryAdmin.SetRoleAsync(db, RegistryActor.ForUser(Me), Id, UserIdOf, Role, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect($"/Registries/Access?id={Id}");
    }
}
