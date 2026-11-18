using MatRegistry.Data;
using MatRegistry.Localization;
using MatRegistry.Services.Registry;
using Microsoft.AspNetCore.Mvc;

namespace MatRegistry.Pages.Copies;

/// <summary>One copy: from where to where, how far it is (the page follows it while it runs), what came out of it. It can be removed from the list once it ended.</summary>
public sealed class ViewModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public Guid Token { get; set; }

    public CopyRow Job { get; private set; } = null!;

    public bool Running => Job.State is CopyState.Waiting or CopyState.Running;

    /// <summary>How far it is in per cent (by blobs), for the meter.</summary>
    public int Percent => Job.BlobsTotal == 0 ? (Job.State == CopyState.Done ? 100 : 0) : (int)Math.Round(100.0 * (Job.BlobsCopied + Job.BlobsSkipped) / Job.BlobsTotal);

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await CopyService.GetAsync(db, RegistryActor.ForUser(Me), Token, HttpContext.RequestAborted) is not { } job)
        {
            return NotFound();
        }

        Job = job;
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await CopyService.DeleteAsync(db, RegistryActor.ForUser(Me), Token, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Flash(Text.T("Copies.Deleted"));
        return LocalRedirect("/Copies");
    }
}
