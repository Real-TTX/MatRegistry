using System.Globalization;
using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;

namespace MatRegistry.Pages.Repositories;

/// <summary>The repositories the person may pull from, of every registry (hosted ones and caches), with search, filters and sort above and the pager below.</summary>
public sealed class IndexModel(AppDbContext db, MenuService menus) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public List<RepositoryRow> Items { get; private set; } = [];

    public bool CanCopy { get; private set; }

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var actor = RegistryActor.ForUser(Me);
        var search = ListQuery.Text(Request, "q");
        var registry = ListQuery.Text(Request, "registry");
        var kind = ListQuery.Text(Request, "kind") is "hosted" or "remote" ? ListQuery.Text(Request, "kind") : null;
        var sort = ListQuery.Text(Request, "sort") is { } s && new[] { "name", "pushed", "pulls", "size", "tags" }.Contains(s) ? s : "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        long? registryId = long.TryParse(registry, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        var list = await RepositoryService.ListAsync(db, actor, new RepositoryCriteria(search, registryId, kind, sort, desc, page, PageSize), ct);
        Items = list.Items;
        CanCopy = (await menus.GetAsync()).CanCopy;
        var registries = await RegistryAdmin.ChoicesAsync(db, actor, AccessRole.Viewer, ct);

        Toolbar = new ToolbarModel
        {
            Id = "repositories",
            Action = "/Repositories",
            Label = Text.T("Repositories.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Repositories.Search"),
            Filters =
            [
                new FilterField("registry", Text.T("Registries.One"), [.. registries.Select(r => new FilterOption(r.Id.ToString(CultureInfo.InvariantCulture), r.Name))], registryId?.ToString(CultureInfo.InvariantCulture)),
                new FilterField("kind", Text.T("Registries.Col.Kind"), [new("hosted", Text.T("Registries.Kind.Hosted")), new("remote", Text.T("Registries.Kind.Cache"))], kind),
            ],
            SortOptions =
            [
                new("name", Text.T("Common.Name")), new("pushed", Text.T("Repositories.Col.Pushed")), new("pulls", Text.T("Repositories.Col.Pulls")),
                new("size", Text.T("Repositories.Col.Size")), new("tags", Text.T("Tags.Title")),
            ],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Repositories.Many"),
            NounOne = Text.T("Repositories.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}
