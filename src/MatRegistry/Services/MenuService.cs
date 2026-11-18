using MatRegistry.Data;
using MatRegistry.Services.Registry;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services;

/// <summary>What the menu shows: the registries of the person (hosted and bound), the copies that run.</summary>
public sealed class MenuModel
{
    public IReadOnlyList<(long Id, string Name, RegistryKind Kind)> Registries { get; init; } = [];

    /// <summary>Copies of the person that wait or run.</summary>
    public int CopiesRunning { get; init; }

    /// <summary>The person may copy into at least one registry.</summary>
    public bool CanCopy { get; init; }
}

/// <summary>One menu of the switcher at the foot of the menu.</summary>
public sealed record MenuArea(string Id, string Title, string Icon);

public sealed class MenuService(AppDbContext db, CurrentUser me)
{
    private MenuModel? _model;

    /// <summary>The menus of the switcher: the registry, the settings of the person and, for an administrator, the administration.</summary>
    public static IReadOnlyList<MenuArea> Areas(bool admin)
    {
        var list = new List<MenuArea>
        {
            new("registry", Localization.Text.T("Shell.Area.Registry"), "box"),
            new("settings", Localization.Text.T("Settings.Title"), "settings"),
        };
        if (admin)
        {
            list.Add(new MenuArea("admin", Localization.Text.T("Admin.Title"), "shield"));
        }

        return list;
    }

    /// <summary>The menu a page belongs to, by its address.</summary>
    public static string AreaOf(string? path)
    {
        path ??= "/";
        bool Under(string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        if (Under("/Admin"))
        {
            return "admin";
        }

        return Under("/Account") ? "settings" : "registry";
    }

    public async Task<MenuModel> GetAsync()
    {
        if (_model is not null)
        {
            return _model;
        }

        var actor = RegistryActor.ForUser(me);
        var roles = await RegistryPermissions.RolesAsync(db, actor);
        var ids = roles.Keys.ToList();
        var registries = await db.Registries.AsNoTracking().Where(r => ids.Contains(r.Id) && r.IsActive).OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.Kind }).ToListAsync();
        _model = new MenuModel
        {
            Registries = [.. registries.Select(r => (r.Id, r.Name, r.Kind))],
            CopiesRunning = await db.CopyJobs.CountAsync(j => j.UserId == me.RequiredId && (j.State == CopyState.Waiting || j.State == CopyState.Running)),
            CanCopy = roles.Values.Any(r => r >= AccessRole.Editor),
        };
        return _model;
    }
}
