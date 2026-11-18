using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;

namespace MatRegistry.Pages.Copies;

/// <summary>The copies of images between registries: the person's own (an administrator sees every one), with their state; search, filter and sort above.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public List<CopyRow> Items { get; private set; } = [];

    public bool Running => Items.Any(i => i.State is CopyState.Waiting or CopyState.Running);

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var state = ListQuery.Text(Request, "state") is { } s && Enum.TryParse<CopyState>(s, true, out _) ? s.ToLowerInvariant() : null;
        var sort = ListQuery.Text(Request, "sort") is "source" or "state" ? ListQuery.Text(Request, "sort")! : "created";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await CopyService.ListAsync(db, RegistryActor.ForUser(Me), new CopyCriteria(search, state, sort, desc, page, PageSize), HttpContext.RequestAborted);
        Items = list.Items;
        Toolbar = new ToolbarModel
        {
            Id = "copies",
            Action = "/Copies",
            Label = Text.T("Copies.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Copies.Search"),
            Filters =
            [
                new FilterField("state", Text.T("Common.Status"), [.. Enum.GetValues<CopyState>().Select(x => new FilterOption(x.ToString().ToLowerInvariant(), Text.T("Copies.State." + x)))], state),
            ],
            SortOptions = [new("created", Text.T("Copies.Col.Started")), new("source", Text.T("Copies.Col.Source")), new("state", Text.T("Common.Status"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "created",
            Total = list.Total,
            Matching = list.Matching,
            Noun = Text.T("Copies.Many"),
            NounOne = Text.T("Copies.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Matching, Url = Toolbar.UrlWithPage };
    }
}
