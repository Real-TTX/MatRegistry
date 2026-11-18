using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Registries;

/// <summary>A repository of a remote registry: its tags as the registry has them now, searched and sorted here, each to look at or to copy.</summary>
public sealed class BrowseModel(AppDbContext db, BlobStore store, UpstreamClient upstream, RegistrySecrets secrets) : AppPageModel
{
    private const int PageSize = 50;

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty(SupportsGet = true)] public string Repository { get; set; } = string.Empty;

    public Data.Registry Registry { get; private set; } = null!;

    public List<string> Tags { get; private set; } = [];

    public string? Error { get; private set; }

    public bool CanCopy { get; private set; }

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var c = new RegistryContext(db, store, upstream, secrets);
        Repository = Repository.Trim().Trim('/');
        if (await RemoteBrowser.AccessAsync(c, RegistryActor.ForUser(Me), Id, ct) is not { } access)
        {
            return NotFound();
        }

        Registry = access.Registry;
        CanCopy = (await RegistryAdmin.ChoicesAsync(db, RegistryActor.ForUser(Me), AccessRole.Editor, ct)).Count > 0;
        var all = new List<string>();
        if (!RegistryNames.IsName(Repository))
        {
            Error = Text.T("Repositories.Name.Invalid");
        }
        else
        {
            try
            {
                all = await RemoteBrowser.TagsAsync(c, access, Repository, ct);
            }
            catch (UpstreamException ex)
            {
                Error = ex.NotFound ? Text.T("Registries.Browse.Unknown", Repository) : ex.Message;
            }
        }

        var search = ListQuery.Text(Request, "q");
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        IEnumerable<string> matching = string.IsNullOrEmpty(search) ? all : all.Where(t => t.Contains(search, StringComparison.OrdinalIgnoreCase));
        matching = desc ? matching.OrderByDescending(t => t, StringComparer.Ordinal) : matching.OrderBy(t => t, StringComparer.Ordinal);
        var list = matching.ToList();
        Tags = [.. ListQuery.Slice(list, page, PageSize)];
        Toolbar = new ToolbarModel
        {
            Id = "remote-tags",
            Action = "/Registries/Browse",
            Label = Text.T("Tags.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Tags.Search"),
            SortOptions = [new("name", Text.T("Common.Name"))],
            Sort = "name",
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = Text.T("Tags.Many"),
            NounOne = Text.T("Tags.One"),
            Keep = new Dictionary<string, string?> { ["id"] = Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["repository"] = Repository },
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = list.Count, Url = Toolbar.UrlWithPage };
        return Page();
    }
}
