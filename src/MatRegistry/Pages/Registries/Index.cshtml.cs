using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;

namespace MatRegistry.Pages.Registries;

/// <summary>
/// The registries: the ones MatRegistry serves (hosted) and the ones it is bound to (remote: Docker Hub, GHCR, another MatRegistry …). A person sees the registries they
/// have a role in and the public ones; administrators see all and create and bind new ones.
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public List<RegistryRow> Items { get; private set; } = [];

    public bool IsAdmin => Me.IsAdmin;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Text(Request, "kind") is "hosted" or "remote" ? ListQuery.Text(Request, "kind") : null;
        var sort = ListQuery.Text(Request, "sort") is { } s && new[] { "name", "kind", "repositories", "size" }.Contains(s) ? s : "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await RegistryAdmin.ListAsync(db, RegistryActor.ForUser(Me), new RegistryCriteria(search, kind, sort, desc, page, PageSize), HttpContext.RequestAborted);
        Items = list.Items;
        Toolbar = new ToolbarModel
        {
            Id = "registries",
            Action = "/Registries",
            Label = Text.T("Registries.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Registries.Search"),
            Filters = [new FilterField("kind", Text.T("Registries.Col.Kind"), [new("hosted", Text.T("Registries.Kind.Hosted")), new("remote", Text.T("Registries.Kind.Remote"))], kind)],
            SortOptions = [new("name", Text.T("Common.Name")), new("kind", Text.T("Registries.Col.Kind")), new("repositories", Text.T("Repositories.Title")), new("size", Text.T("Repositories.Col.Size"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Registries.Many"),
            NounOne = Text.T("Registries.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}
