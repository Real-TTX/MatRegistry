using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Repositories;

/// <summary>
/// A repository in two tabs: Tags (the list of its tags with their platforms and sizes, search and sort above; an Editor deletes a tag) and General (the description,
/// what it holds; an Editor deletes the whole repository). Above both the pull command of its newest tag.
/// </summary>
public sealed class ViewModel(AppDbContext db, InstanceService instance) : AppPageModel
{
    private const int PageSize = 25;

    public static readonly string[] Tabs = ["tags", "general"];

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "tags";

    [BindProperty] public string Description { get; set; } = string.Empty;

    public RepositoryDetail Repository { get; private set; } = null!;

    public TagList Tags { get; private set; } = new([], 0, 0);

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public string Host { get; private set; } = string.Empty;

    public string Zone => Me.TimeZone;

    /// <summary>The address of an image of this repository: <c>host/registry/repository:tag</c>.</summary>
    public string Image(string reference) => $"{Host}/{Repository.FullName}{(RegistryNames.IsDigest(reference) ? "@" : ":")}{reference}";

    public async Task<IActionResult> OnGetAsync()
    {
        Tab = Tabs.Contains(Tab) ? Tab : "tags";
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Description = Repository.Description ?? string.Empty;
        return Page();
    }

    /// <summary>Saves the description (tab General).</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        var error = await RepositoryService.SaveDescriptionAsync(db, RegistryActor.ForUser(Me), Id, Description, HttpContext.RequestAborted);
        if (error is not null)
        {
            Tab = "general";
            ModelState.AddModelError(nameof(Description), error);
            return await LoadAsync() ? Page() : NotFound();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect($"/Repositories/View?id={Id}&tab=general");
    }

    public async Task<IActionResult> OnPostDeleteTagAsync(string tag)
    {
        if (!await RepositoryService.DeleteTagAsync(db, RegistryActor.ForUser(Me), Id, tag, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Tags.Deleted", tag));
        return LocalRedirect($"/Repositories/View?id={Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var repository = await RepositoryService.GetAsync(db, RegistryActor.ForUser(Me), Id, HttpContext.RequestAborted);
        if (repository is null || !await RepositoryService.DeleteAsync(db, RegistryActor.ForUser(Me), Id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Common.DeletedNamed", repository.FullName));
        return LocalRedirect("/Repositories");
    }

    private async Task<bool> LoadAsync()
    {
        var ct = HttpContext.RequestAborted;
        var actor = RegistryActor.ForUser(Me);
        if (await RepositoryService.GetAsync(db, actor, Id, ct) is not { } repository)
        {
            return false;
        }

        Repository = repository;
        Host = InstanceService.RegistryHost((await instance.SettingsAsync()).PublicUrl, Request);
        var search = ListQuery.Text(Request, "q");
        var sort = ListQuery.Text(Request, "sort") is "name" or "size" ? ListQuery.Text(Request, "sort")! : "pushed";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        Tags = await RepositoryService.TagsAsync(db, actor, Id, new TagCriteria(search, sort, desc, page, PageSize), ct) ?? new TagList([], 0, 0);
        Toolbar = new ToolbarModel
        {
            Id = "tags",
            Action = "/Repositories/View",
            Label = Text.T("Tags.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Tags.Search"),
            SortOptions = [new("pushed", Text.T("Repositories.Col.Pushed")), new("name", Text.T("Common.Name")), new("size", Text.T("Repositories.Col.Size"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "pushed",
            Total = Tags.Total,
            Matching = Tags.Matching,
            Noun = Text.T("Tags.Many"),
            NounOne = Text.T("Tags.One"),
            Keep = new Dictionary<string, string?> { ["id"] = Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = Tags.Matching, Url = Toolbar.UrlWithPage };
        return true;
    }
}
