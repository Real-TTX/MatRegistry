using System.Globalization;
using MatRegistry.Controls;
using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Copies;

/// <summary>
/// A new copy of an image: from a registry the person may pull from (hosted or remote) into one they may push to (hosted or remote), under a repository and a tag.
/// The pages of a repository, a tag and a remote image open it filled in (<c>?source=&amp;repository=&amp;reference=</c>).
/// </summary>
public sealed class NewModel(AppDbContext db, CopyQueue queue) : AppPageModel
{
    [BindProperty] public long SourceRegistryId { get; set; }

    [BindProperty] public string SourceRepository { get; set; } = string.Empty;

    [BindProperty] public string SourceReference { get; set; } = "latest";

    [BindProperty] public long TargetRegistryId { get; set; }

    [BindProperty] public string TargetRepository { get; set; } = string.Empty;

    [BindProperty] public string TargetTag { get; set; } = string.Empty;

    public List<Choice> Sources { get; private set; } = [];

    public List<Choice> Targets { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long? source, string? repository, string? reference)
    {
        await LoadAsync();
        if (Targets.Count == 0)
        {
            FlashError(Text.T("Copies.NoTarget"));
            return LocalRedirect("/Copies");
        }

        SourceRegistryId = source ?? 0;
        SourceRepository = repository ?? string.Empty;
        SourceReference = string.IsNullOrEmpty(reference) ? "latest" : reference;

        // the target is a hosted registry that is not the source, by default with the same name and tag (Docker Hub's library/ left out)
        var target = Targets.FirstOrDefault(t => t.Value != SourceRegistryId.ToString(CultureInfo.InvariantCulture)) ?? Targets[0];
        TargetRegistryId = long.Parse(target.Value, CultureInfo.InvariantCulture);
        TargetRepository = SourceRepository.StartsWith("library/", StringComparison.Ordinal) ? SourceRepository[8..] : SourceRepository;
        TargetTag = RegistryNames.IsTag(SourceReference) ? SourceReference : string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var input = new CopyInput(SourceRegistryId, SourceRepository, SourceReference, TargetRegistryId, TargetRepository, TargetTag);
        var (job, errors) = await CopyService.CreateAsync(db, RegistryActor.ForUser(Me), input, HttpContext.RequestAborted);
        if (job is null)
        {
            errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
            await LoadAsync();
            return Page();
        }

        queue.Enqueue(job.Id);
        Flash(Text.T("Copies.Started"));
        return LocalRedirect($"/Copies/View?token={job.Token}");
    }

    private async Task LoadAsync()
    {
        var actor = RegistryActor.ForUser(Me);
        string Hint(RegistryKind kind) => Text.T(kind == RegistryKind.Hosted ? "Registries.Kind.Hosted" : "Registries.Kind.Remote");
        Sources = [.. (await RegistryAdmin.ChoicesAsync(db, actor, AccessRole.Viewer, HttpContext.RequestAborted))
            .Select(r => new Choice(r.Id.ToString(CultureInfo.InvariantCulture), r.Name, Hint(r.Kind)))];
        Targets = [.. (await RegistryAdmin.ChoicesAsync(db, actor, AccessRole.Editor, HttpContext.RequestAborted))
            .OrderBy(r => r.Kind).ThenBy(r => r.Name)
            .Select(r => new Choice(r.Id.ToString(CultureInfo.InvariantCulture), r.Name, Hint(r.Kind)))];
    }
}
