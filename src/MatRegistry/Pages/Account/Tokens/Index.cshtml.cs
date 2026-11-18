using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;

namespace MatRegistry.Pages.Account.Tokens;

/// <summary>The access tokens of the person (for <c>docker login</c>): name, what it may, when it ends and was last used; search and sort above.</summary>
public sealed class IndexModel(AppDbContext db, InstanceService instance) : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public List<AccessTokenRow> Items { get; private set; } = [];

    public string Host { get; private set; } = string.Empty;

    public string UserName => Me.Snapshot?.Email ?? string.Empty;

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var sort = ListQuery.Text(Request, "sort") is "name" or "used" ? ListQuery.Text(Request, "sort")! : "created";
        var desc = ListQuery.Descending(Request);
        var all = await AccessTokens.ListAsync(db, UserId, HttpContext.RequestAborted);
        IEnumerable<AccessTokenRow> rows = string.IsNullOrEmpty(search) ? all : all.Where(t => t.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        rows = sort switch
        {
            "name" => desc ? rows.OrderByDescending(t => t.Name) : rows.OrderBy(t => t.Name),
            "used" => desc ? rows.OrderBy(t => t.LastUsed) : rows.OrderByDescending(t => t.LastUsed),
            _ => desc ? rows.OrderBy(t => t.Created) : rows.OrderByDescending(t => t.Created),
        };
        Items = [.. rows];
        Host = InstanceService.RegistryHost((await instance.SettingsAsync()).PublicUrl, Request);
        Toolbar = new ToolbarModel
        {
            Id = "tokens",
            Action = "/Account/Tokens",
            Label = Text.T("Tokens.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Tokens.Search"),
            SortOptions = [new("created", Text.T("Tokens.Created")), new("name", Text.T("Common.Name")), new("used", Text.T("Tokens.LastUsed"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "created",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Tokens.Many"),
            NounOne = Text.T("Tokens.One"),
        };
    }
}
