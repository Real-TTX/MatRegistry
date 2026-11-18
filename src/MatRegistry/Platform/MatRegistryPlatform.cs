using System.Text.Json;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using MatPlatform;

namespace MatRegistry.Platform;

/// <summary>
/// MatRegistry as an app of the platform contract (MatDesign docs/PLATFORM.md, contract 2) on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how a consumer such as Mattiverse shows MatRegistry (<see cref="Describe"/>: tiles, the few settings, buttons), what it provides (<c>registry.docker</c>: an app such as
/// MatDock gets a key to pull images with), what it consumes (the sign-in and the mail that their variables set), what it adds to a shell (the launcher Registry, two
/// widgets for administrators), its values and what its buttons do. The settings are those of the admin page System, kept in the same place (<see cref="InstanceService"/>).
/// </summary>
public sealed class MatRegistryPlatform(InstanceService instance, MatPlatformSettings settings, IServiceScopeFactory scopes, ILogger<MatRegistryPlatform> log)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatRegistry in the platform (and the default client id at MatIdentity).</summary>
    public const string AppId = "matregistry";

    /// <summary>The accent of MatRegistry (an olive, white text 5.0:1; see <c>wwwroot/css/app.css</c>).</summary>
    public const string Accent = Ui.Accent;

    /// <summary>The MatApiDefinition of MatRegistry (<c>GET /api</c>) without <c>MAT_PUBLIC_URL</c>.</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, null);

    /// <summary>
    /// The MatApiDefinition of MatRegistry (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are set by MatPlatform. <paramref name="publicUrl"/> is the value of
    /// <c>MAT_PUBLIC_URL</c>: while it is set, the field <c>publicUrl</c> is fixed.
    /// </summary>
    public static void Describe(MatPlatformBuilder o, string? publicUrl)
    {
        var T = o.T;

        o.App(AppId, "MatRegistry", T("Docker-Registry: eigene Registries, gebundene Registries (Docker Hub, GHCR …) als Cache, Kopien zwischen ihnen",
            "Docker registry: hosted registries, bound registries (Docker Hub, GHCR …) as a cache, copies between them"), AppInfo.Version, "/img/logo/matregistry-mark.svg", Accent,
            "/Overview");

        // What MatRegistry does for other apps: a key to pull images with (docker login <host> -u <app> -p <key>). A key capability: MatPlatform keeps the keys.
        o.Provides(RegistryAuth.Capability, label: T("Stellt Images bereit für", "Serves images to"));

        // What MatRegistry uses from other apps: the sign-in and the mail, set by their variables (MatRegistry works without both).
        o.Consumes(MatCapabilities.IdentityOidc, optional: true);
        o.Consumes(MatCapabilities.MailSmtp, optional: true);

        // What MatRegistry adds to the home screen of a shell (Mattiverse): the registry with the views of its menu, and two widgets for administrators.
        o.Launcher("registry", "Registry", "/img/logo/matregistry-mark.svg", "/Overview");
        o.QuickAction("registry", T("Repositories", "Repositories"), "box", url: "/Repositories");
        o.QuickAction("registry", T("Registries", "Registries"), "registry", url: "/Registries");
        o.QuickAction("registry", T("Image kopieren", "Copy an image"), "copy", url: "/Copies/New");
        o.QuickAction("registry", T("Zugriffstokens", "Access tokens"), "key", url: "/Account/Tokens");
        o.Widget(new MatWidget("store", T("Speicher", "Store"), MatWidgetKinds.Tiles, "medium")
        {
            Items = ["repositories", "tags", "size"],
            Icon = "box",
            Url = "/Admin/System",
            Audience = MatAudience.Admin,
        });
        o.Widget(new MatWidget("users", T("Aktive Benutzer", "Active users"), MatWidgetKinds.Stat)
        {
            Items = ["users"],
            Icon = "users",
            Url = "/Admin/Users",
            Audience = MatAudience.Admin,
        });

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("users", "registries", "remote", "unreachable", "repositories", "tags", "size", "copies", "copiesFailed", "lastPush", "lastGc", "registration", "lastActivity"),
            MatSection.Actions("openApp", "openRegistries", "openUsers", "openConnections", "openActivity", "openSystem", "collectGarbage", "checkRegistries", "cleanUp"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "registrationOpen", "publicUrl"),
            MatSection.Form(T("Registry", "Registry"), "tokenMinutes", "gcIntervalHours", "gcUntagged"));

        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("registries", T("Registries", "Registries"), icon: "registry");
        o.Tile("remote", T("Gebundene Registries", "Bound registries"), icon: "link");
        o.Tile("unreachable", T("Nicht erreichbar", "Not reachable"), icon: "alert");
        o.Tile("repositories", T("Repositories", "Repositories"), icon: "box");
        o.Tile("tags", T("Tags", "Tags"), icon: "tag");
        o.Tile("size", T("Belegter Speicher", "Space used"), "text", icon: "hard-drive");
        o.Tile("copies", T("Laufende Kopien", "Copies running"), icon: "copy");
        o.Tile("copiesFailed", T("Fehlgeschlagene Kopien (7 Tage)", "Failed copies (7 days)"), icon: "x-circle");
        o.Tile("lastPush", T("Letzter Push", "Last push"), "time", icon: "upload");
        o.Tile("lastGc", T("Letzte Speicherbereinigung", "Last garbage collection"), "time", icon: "broom");
        o.Tile("registration", T("Registrierung", "Sign-up"), "status", icon: "user");
        o.Tile("lastActivity", T("Letzte Aktivität", "Last activity"), "time", icon: "activity");

        o.Field(new MatField("registrationOpen", T("Registrierung offen", "Open sign-up"), "bool")
        {
            Hint = T("Aus: nur Administratoren legen Konten an.", "Off: only administrators create accounts."),
        });
        var address = new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Max = 300,
            Placeholder = "https://registry.example.com",
            Hint = T("Für die Pull-Befehle und den Token-Dienst; leer: die Adresse des Aufrufs.", "For the pull commands and the token service; empty: the address of the request."),
        };
        o.Field(publicUrl is null ? address : address with
        {
            Fixed = true,
            Hint = T("Festgelegt durch MAT_PUBLIC_URL (Mattiverse); hier lässt sie sich nicht ändern.", "Set by MAT_PUBLIC_URL (Mattiverse); it cannot be changed here."),
        });
        o.Field(new MatField("tokenMinutes", T("Gültigkeit eines Tokens (Minuten)", "Life of a token (minutes)"), "number") { Min = 1, Max = 1440 });
        o.Field(new MatField("gcIntervalHours", T("Speicherbereinigung alle (Stunden)", "Garbage collection every (hours)"), "number")
        {
            Min = 0,
            Max = 720,
            Hint = T("0 = nur von Hand.", "0 = only by hand."),
        });
        o.Field(new MatField("gcUntagged", T("Manifeste ohne Tag entfernen", "Remove manifests without a tag"), "bool"));

        o.LinkAction("openApp", T("Öffnen", "Open"), "external", "/Overview");
        o.LinkAction("openRegistries", T("Registries", "Registries"), "registry", "/Registries");
        o.LinkAction("openUsers", T("Benutzer", "Users"), "users", "/Admin/Users");
        o.LinkAction("openConnections", T("Verbindungen", "Connections"), "plug", "/Admin/Connections");
        o.LinkAction("openActivity", T("Aktivität", "Activity"), "activity", "/Admin/Activity");
        o.LinkAction("openSystem", T("System", "System"), "settings", "/Admin/System");
        o.PostAction(
            "collectGarbage",
            T("Speicher bereinigen", "Collect garbage"),
            "broom",
            T("Blobs, die kein Manifest mehr braucht, jetzt löschen?", "Delete the blobs no manifest needs any more now?"));
        o.PostAction("checkRegistries", T("Gebundene Registries prüfen", "Check bound registries"), "refresh");
        o.PostAction(
            "cleanUp",
            T("Aufräumen", "Clean up"),
            "broom",
            T("Beendete Sitzungen und alte Kopien jetzt entfernen?", "Remove ended sessions and old copies now?"));

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await instance.CanConnectAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        checks.Add(instance.CanWriteFiles()
            ? new MatHealthCheck("files", "ok")
            : new MatHealthCheck("files", "down", call.T("Der Datenordner ist nicht beschreibbar.", "The data folder cannot be written.")));
        var free = instance.FreeSpace();
        if (free is >= 0 and < 1024L * 1024 * 1024)
        {
            checks.Add(new MatHealthCheck("space", "degraded", call.T($"Nur noch {Ui.Bytes(free)} frei.", $"Only {Ui.Bytes(free)} left.")));
        }

        var snapshot = await instance.SnapshotAsync(ct);
        if (await instance.AnyUserAsync() && snapshot.Admins == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein aktiver Administrator.", "No active administrator.")));
        }

        checks.Add(snapshot.Unreachable == 0
            ? new MatHealthCheck("remotes", "ok")
            : new MatHealthCheck("remotes", "degraded", call.T($"{snapshot.Unreachable} gebundene Registries nicht erreichbar.", $"{snapshot.Unreachable} bound registries not reachable.")));
        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await instance.SnapshotAsync(ct);
        var st = await instance.SettingsAsync();
        static MatStateValue Time(DateTime? utc) => new(utc is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null);
        return new Dictionary<string, MatStateValue>
        {
            ["users"] = new(s.Users),
            ["registries"] = new(s.Registries),
            ["remote"] = new(s.RemoteRegistries),
            ["unreachable"] = s.Unreachable > 0 ? new MatStateValue(s.Unreachable, "warn") : new MatStateValue(s.Unreachable),
            ["repositories"] = new(s.Repositories),
            ["tags"] = new(s.Tags),
            ["size"] = new(Ui.Bytes(s.StoredBytes)),
            ["copies"] = s.CopiesRunning > 0 ? new MatStateValue(s.CopiesRunning, "info") : new MatStateValue(s.CopiesRunning),
            ["copiesFailed"] = s.CopiesFailed7Days > 0 ? new MatStateValue(s.CopiesFailed7Days, "warn") : new MatStateValue(s.CopiesFailed7Days),
            ["lastPush"] = Time(s.LastPushUtc),
            ["lastGc"] = Time(s.LastGcUtc),
            ["registration"] = st.RegistrationOpen
                ? new MatStateValue("info", Text: call.T("Offen", "Open"))
                : new MatStateValue("ok", Text: call.T("Geschlossen", "Closed")),
            ["lastActivity"] = Time(s.LastActivityUtc),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var st = await instance.SettingsAsync();
        return new Dictionary<string, object?>
        {
            ["registrationOpen"] = st.RegistrationOpen,
            ["publicUrl"] = st.PublicUrl ?? string.Empty,
            ["tokenMinutes"] = st.TokenMinutes,
            ["gcIntervalHours"] = st.GcIntervalHours,
            ["gcUntagged"] = st.GcUntagged,
        };
    }

    /// <summary>The values are checked against the definition already (MatPlatform, <c>CheckValues</c>); they are checked once more like on the admin page.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var change = new SettingsChange();
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "registrationOpen" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    change = change with { RegistrationOpen = value.GetBoolean() };
                    break;
                case "publicUrl" when settings.AppPublicUrl is not null:
                    errors[key] = call.T("Festgelegt durch MAT_PUBLIC_URL.", "Set by MAT_PUBLIC_URL.");
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.String:
                    change = change with { PublicUrl = value.GetString()! };
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.Null:
                    change = change with { PublicUrl = string.Empty };
                    break;
                case "tokenMinutes" when value.TryGetInt32(out var minutes) && minutes is >= 1 and <= 1440:
                    change = change with { TokenMinutes = minutes };
                    break;
                case "gcIntervalHours" when value.TryGetInt32(out var hours) && hours is >= 0 and <= 720:
                    change = change with { GcIntervalHours = hours };
                    break;
                case "gcUntagged" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    change = change with { GcUntagged = value.GetBoolean() };
                    break;
                default:
                    errors[key] = call.T("Dieser Wert passt nicht.", "This value does not fit.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        if (await instance.SaveSettingsAsync(change) is { } error)
        {
            return new Dictionary<string, string> { [char.ToLowerInvariant(error.Field[0]) + error.Field[1..]] = error.Message };
        }

        log.LogInformation("Settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        var actor = new RegistryActor(null, true, null, false, call.Caller);
        switch (id)
        {
            case "collectGarbage":
                var gc = await instance.CollectGarbageAsync(actor, ct);
                log.LogInformation("Garbage collection through the platform API by {Caller}: {Result}", call.Caller, gc?.Describe() ?? "already running");
                return gc is null
                    ? new MatActionResult(false, call.T("Eine Speicherbereinigung läuft schon.", "A garbage collection is running already."))
                    : new MatActionResult(true, call.T($"Bereinigt: {Ui.Bytes(gc.Bytes)} frei, {gc.Blobs} Blobs, {gc.Manifests} Manifeste.",
                        $"Collected: {Ui.Bytes(gc.Bytes)} freed, {gc.Blobs} blobs, {gc.Manifests} manifests."));
            case "checkRegistries":
                using (var scope = scopes.CreateScope())
                {
                    var services = scope.ServiceProvider;
                    var c = new RegistryContext(services.GetRequiredService<Data.AppDbContext>(), services.GetRequiredService<BlobStore>(),
                        services.GetRequiredService<UpstreamClient>(), services.GetRequiredService<RegistrySecrets>());
                    var (reachable, total) = await RegistryAdmin.CheckAllAsync(c, actor, ct);
                    log.LogInformation("Bound registries checked through the platform API by {Caller}: {Reachable} of {Total}", call.Caller, reachable, total);
                    return new MatActionResult(reachable == total, call.T($"{reachable} von {total} gebundenen Registries erreichbar.", $"{reachable} of {total} bound registries reachable."));
                }

            case "cleanUp":
                var result = await instance.CleanUpAsync(ct);
                log.LogInformation("Clean-up through the platform API by {Caller}: {Sessions} sessions, {Copies} copies", call.Caller, result.Sessions, result.Copies);
                return new MatActionResult(true, call.T($"Aufgeräumt: {result.Sessions} Sitzungen, {result.Copies} Kopien.", $"Cleaned up: {result.Sessions} sessions, {result.Copies} copies."));
            default:
                return null;
        }
    }
}
