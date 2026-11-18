using System.Text.RegularExpressions;
using MatRegistry.Data;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry;

/// <summary>
/// Docker <c>HEALTHCHECK</c> without curl: the app asks its own <c>/api/health</c> (platform contract: 200 for ok and degraded, 503 for down, so a degraded app is not
/// restarted) and answers with an exit code.
/// </summary>
public static partial class HealthCheck
{
    [GeneratedRegex(@":(\d+)")]
    private static partial Regex PortRegex();

    public static async Task<int> RunAsync()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var match = PortRegex().Match(urls ?? string.Empty);
        var port = match.Success ? match.Groups[1].Value : "4780";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"http://localhost:{port}/api/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

public static class Database
{
    /// <summary>Creates and updates the tables. The database container may still be starting: try again for a while before giving up.</summary>
    public static async Task MigrateWithRetryAsync(WebApplication app)
    {
        const int attempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                app.Logger.LogInformation("The database is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                app.Logger.LogWarning("The database is not ready yet (attempt {Attempt}/{Attempts}): {Message}", attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}

/// <summary>
/// Headers every response carries: no inline script or style (the toolkit needs none), nothing from another site, no guessing of types.
/// The registry API answers JSON and blobs only; the same headers do no harm there.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    private const string Policy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; " +
        "frame-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = Policy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        return next(context);
    }
}

/// <summary>
/// The housekeeping that runs by itself, every hour: old sessions and old copies go, the garbage collection runs when it is due (<c>GcIntervalHours</c>), and the bound
/// registries are asked whether they answer.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The housekeeping failed; it tries again later.");
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var instance = services.GetRequiredService<InstanceService>();
        var result = await instance.CleanUpAsync(ct);
        logger.LogInformation("Housekeeping: {Sessions} old sessions and {Copies} old copies removed.", result.Sessions, result.Copies);

        var system = new RegistryActor(null, true, null, false, "housekeeping");
        var settings = await instance.SettingsAsync();
        if (settings.GcIntervalHours > 0 && (settings.LastGcDate is null || settings.LastGcDate < DateTime.UtcNow.AddHours(-settings.GcIntervalHours)))
        {
            var gc = await instance.CollectGarbageAsync(system, ct);
            logger.LogInformation("Garbage collection: {Result}", gc?.Describe() ?? "already running");
        }

        var c = new RegistryContext(services.GetRequiredService<AppDbContext>(), services.GetRequiredService<BlobStore>(), services.GetRequiredService<UpstreamClient>(),
            services.GetRequiredService<RegistrySecrets>());
        var (reachable, total) = await RegistryAdmin.CheckAllAsync(c, system, ct);
        if (reachable < total)
        {
            logger.LogWarning("{Unreachable} of {Total} bound registries are not reachable.", total - reachable, total);
        }
    }
}

/// <summary>
/// A text that is posted empty is an empty string, not <c>null</c>: the properties of the page models are not nullable, so the code never meets a null in a text.
/// </summary>
public sealed class EmptyStringBinderProvider : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinderProvider
{
    public Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder? GetBinder(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBinderProviderContext context)
        => context.Metadata.ModelType == typeof(string) ? new EmptyStringBinder() : null;

    private sealed class EmptyStringBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext)
        {
            var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (result == Microsoft.AspNetCore.Mvc.ModelBinding.ValueProviderResult.None)
            {
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);
            bindingContext.Result = Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingResult.Success(result.FirstValue ?? string.Empty);
            return Task.CompletedTask;
        }
    }
}
