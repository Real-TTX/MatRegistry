using MatRegistry.Data;
using MatRegistry.Localization;

namespace MatRegistry.Services.Registry;

/// <summary>
/// The suggestions of the search in the header bar (MatUi <c>data-search-url</c>, SHELL-20): the repositories and registries of the person that match what was typed,
/// as <c>{ groups: [ { name, items: [ { title, text, href, icon } ] } ] }</c>. Only what the person may see (the business layer checks it).
/// </summary>
public static class SuggestEndpoints
{
    public static IEndpointRouteBuilder MapSuggest(this IEndpointRouteBuilder app)
    {
        app.MapGet("/live/suggest", async (HttpContext http, AppDbContext db, CurrentUser me, string? q, int? limit) =>
        {
            var text = (q ?? string.Empty).Trim();
            var take = Math.Clamp(limit ?? 8, 1, 20);
            if (text.Length == 0)
            {
                return Results.Json(new { groups = Array.Empty<object>() });
            }

            var actor = RegistryActor.ForUser(me);
            var repositories = await RepositoryService.ListAsync(db, actor, new RepositoryCriteria(text, null, null, "name", false, 1, take), http.RequestAborted);
            var registries = await RegistryAdmin.ListAsync(db, actor, new RegistryCriteria(text, null, "name", false, 1, Math.Min(take, 5)), http.RequestAborted);
            var groups = new List<object>();
            if (repositories.Items.Count > 0)
            {
                groups.Add(new
                {
                    name = Text.T("Repositories.Title"),
                    items = repositories.Items.Select(r => new
                    {
                        title = r.FullName,
                        text = Text.T("Search.Tags", r.Tags),
                        href = $"/Repositories/View?id={r.Id}",
                        icon = r.Kind == RegistryKind.Hosted ? "box" : "cache",
                    }),
                });
            }

            if (registries.Items.Count > 0)
            {
                groups.Add(new
                {
                    name = Text.T("Registries.Title"),
                    items = registries.Items.Select(r => new
                    {
                        title = r.Name,
                        text = r.Kind == RegistryKind.Hosted ? Text.T("Registries.Kind.Hosted") : r.Url ?? string.Empty,
                        href = $"/Registries/View?id={r.Id}",
                        icon = r.Kind == RegistryKind.Hosted ? "registry" : "link",
                    }),
                });
            }

            return Results.Json(new { groups });
        });
        return app;
    }
}
