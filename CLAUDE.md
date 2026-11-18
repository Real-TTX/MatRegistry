# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatRegistry is

A Docker registry of its own with a web interface. A **registry** is either **hosted** (created here: MatRegistry keeps the images; its name is the first part of
every image name, `<host>/<registry>/<repository>:<tag>`) or **remote** (bound: Docker Hub, GHCR, Harbor, a second MatRegistry …, with its address and stored
credentials). A remote registry is browsed (catalog, tags, manifests), copied from and to, and – with `ProxyEnabled` – served under its name as a **pull-through
cache** (`<host>/dockerhub/nginx` → `library/nginx` at Docker Hub; a tag is trusted `CacheMinutes`, then checked with a HEAD; when the remote is away the cache
answers). **Copies** move an image with every platform between any two registries in the background. People have the role Viewer (pull, browse, copy from) or
Editor (also push, delete, copy into) per registry; administrators may do everything; a public registry lets anybody pull.

Part of the Mat family on **MatPlatform 2.4** (MatDesign `docs/PLATFORM.md`): MatRegistry **provides** `registry.docker` (a key capability: a key per app, used as
`docker login -u <app> -p <key>`, pulls from every registry; MatPlatform keeps the keys), **consumes** `identity.oidc` (MatIdentity) and `mail.smtp` (MatSMTP), both
optional, and **contributes** the launcher Registry (quick actions Repositories, Registries, Copy an image, Access tokens) and two admin widgets. The capability
`registry.docker` is not written into MatDesign's PLATFORM.md table yet (MatDesign was on a feature branch at the time): add the row there with the next change.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages**, EF Core + **PostgreSQL**, the **MatUi** toolkit of MatDesign (`wwwroot/matui`, release **0.12.0**, bundled
from `origin/main`: never edit by hand), plain JavaScript without a build step. Docker-first. Web port **4780** (pgAdmin 4781, local Postgres 4782). UI languages:
English and German (catalog in `Resources/Texts/*.json`, German with "du"). Accent: olive `#4d7c0f` (white text 5.0:1; not in MatDesign's `AppRegistry` yet).

## Build / run / test

- **Build:** `dotnet build MatRegistry.slnx` (SDK 10, `global.json`). Expected: 0 warnings, 0 errors.
- **Tests:** `dotnet test`. The API and database tests run when `MATREGISTRY_TEST_DB` is set (an administrator connection string, e.g.
  `Host=localhost;Port=4782;Username=matregistry;Password=matregistry;Database=postgres` while the local stack runs); each test creates, migrates and drops a database.
  `RegistryApiTests` drive the registry API in memory the way Docker does (challenge, token service, chunked/monolithic/mounted uploads, manifests, ranges, tags,
  catalog, roles, pull-only tokens, public registries, bound apps, indexes, referrers, deleting, garbage collection, a copy). `RegistryUnitTests`: names, digests,
  manifests, image configs, remote addresses, the blob store. `TextCatalogTests` check every text key of the code and the scripts in English and German.
- **Run / test the app: always rebuild the container** (`./scripts/redeploy.ps1`, `-Clean` for empty volumes). CSS, JS and cshtml are baked into the image.
- **Real tests with Docker** against the local stack: `docker login localhost:4780 -u admin@example.com` (password: an access token), push, pull, a pull through the
  cache of a bound Docker Hub, `docker buildx imagetools create -t localhost:4780/demo/x:1 <multi-arch image>` for an index with attestations.
- **Test account (always):** `admin@example.com` / `Start12345` – the first account of a fresh stack (it becomes the administrator).
- **Real UI tests are end to end in a browser** (Playwright with the installed Edge, `channel: 'msedge'`): every page, the console, a phone (390 px), dark mode.
- **Migrations:** `dotnet ef migrations add <Name> -p src/MatRegistry -o Data/Migrations` (local tool in `dotnet-tools.json`; a design-time factory exists). Applied at start-up.
- **Updating MatUi:** take the release of MatDesign (`origin/main`): the CSS is the parts of `src/MatUi/wwwroot/css` joined in the order of the first line of `matui.css`;
  `matui.js`, `matui.boot.js`, `matui-icons.svg` as they are. **MatPlatform:** `node tools/matplatform-copy.js ../MatRegistry/src/MatRegistry` in MatDesign.
- **Logo:** `node scripts/logo.js` writes the SVGs (mark, favicon, maskable, mono, logo, logo-dark), `scripts/icons.ps1` the PNGs with Edge headless. The glyph of
  the menu is `mat-i-matregistry` in `wwwroot/img/app-icons.svg`.
- Write files with the editor tools, not with shell heredocs that contain apostrophes or backslashes (Git Bash also rewrites arguments that start with `/`).

## Map

```
src/MatRegistry
  Program.cs                 composition root: services, cookie auth (+ MatIdentity), rate limits (sign-in, token service), no body limit, static files, endpoints
  Infrastructure.cs          HealthCheck (--healthcheck), Database.MigrateWithRetry, SecurityHeaders (CSP), MaintenanceService (hourly: clean-up, GC when due, remote checks)
  Data/                      Entities.cs (User, UserSession), RegistryEntities.cs (Registry(+RegistryAccess), Repository, Manifest(+ManifestReference), Tag, Blob,
                             RepositoryBlob, BlobUpload, AccessToken, CopyJob, Activity, SystemSetting), AppDbContext.cs, Migrations/
  Services/Registry/         THE business layer of the registry, static classes taking a RegistryContext (db, BlobStore, UpstreamClient, RegistrySecrets) and the actor:
                             RegistryPermissions (THE check: RoleAsync/RolesAsync of a RegistryActor), RegistryEngine (the API: manifests, blobs, uploads, tags, catalog,
                             referrers; StoreManifestAsync, LinkBlobAsync, EnsureRepositoryAsync), ProxyCache (pull-through), RegistryAuth (token service, Basic,
                             access tokens, binding keys), RegistryEndpoints (/v2: HTTP only), AccessTokens, RegistryAdmin (registries, roles, checks), RepositoryService
                             (the pages), RemoteBrowser (browse a remote), CopyService + CopyQueue + CopyWorker + ImageTransfer (copies), GarbageCollector, ActivityLog,
                             SuggestEndpoints (/live/suggest); the machinery: BlobStore (files by digest), UpstreamClient (HTTP to remotes: Bearer challenge, Basic,
                             token cache, insecure client for SkipTlsVerify), ManifestDocument + ImageConfig + MediaTypes (parsing), RegistryNames (names, tags, digests)
  Services/Accounts|Auth/    profiles, user administration, sessions in the DB, MatIdentity sign-in, GDPR export/erase
  Services/                  InstanceService (numbers, settings, housekeeping, GC, RegistryHost), MenuService, Ui (icons, logo, bytes), ListModels (toolbar, pager), Dates
  MatPlatform/               copy of the shared package (MatDesign, 2.4.0). Never edit here.
  Platform/                  MatRegistryPlatform (provides/consumes, tiles, settings, actions collectGarbage/checkRegistries/cleanUp, launcher, widgets, health), MatRegistryIdentityUsers
  Controls/                  ONE code base per control: <md-field>, <md-buttons>, <md-table>, <md-tabs>/<md-tab>
  Pages/                     Overview, Repositories (Index, View with tabs Tags/General, Manifest), Registries (Index, View, Edit + Access as tabs, Browse, Remote),
                             Copies (Index, New, View), Account (+ Tokens: Index, Edit), Admin (Users, Connections, Activity, System with tabs), About;
                             Shared/_Commands (commands to copy), _ImageConfig, _Toolbar, _Pager, _Empty
  wwwroot/                   css/app.css (accent + md-*), js (offline, pwa, util, zone), sw.js (shell cache, the read pages network-first with a copy), manifest, llms.txt
tests/MatRegistry.Tests      unit + API/database tests
```

## Conventions

**Registry API**: names are `<registry>/<repository>`; errors are the spec's (`NAME_UNKNOWN`, `MANIFEST_BLOB_UNKNOWN`, `DENIED` …) as `RegistryException` thrown by
the engine. 401 for nobody (with the Bearer challenge naming `/v2/token` and the scope), 403 for somebody without the role. A blob is served only in a repository it
belongs to (`RepositoryBlob`), so nobody reads a private layer by naming its digest elsewhere; a manifest push needs its blobs (and the manifests of an index) in the
repository. Manifests are stored as pushed (bytes in the DB, max 4 MiB); blobs in `/data/registry/blobs/sha256/xx/<hex>`, never changed. Inserts that race (blob,
link, repository, tag) are one SQL statement with `ON CONFLICT`. The GC keeps everything younger than an hour and every repository with an upload in progress.

**Database (PostgreSQL)**: tables PascalCase and singular, key always `Id` BIGINT, every record has `CreateDate/CreateUserId/UpdateDate/UpdateUserId` (filled by
`AppDbContext.SaveChanges`; the raw inserts fill them too), `UpdateState` (0 deleted, 1 created, 2 updated) for soft delete (`User`, `Registry`). A `Token` (uuid)
where a key is a security feature or an address (`UserSession`, `BlobUpload`, `AccessToken`, `CopyJob`).

**Security**: every page and API call goes through `RegistryPermissions`; what may not be seen answers 404. Secrets of remote registries are protected with
DataProtection (keys in the volume) and never leave `RegistryAdmin`/`RegistrySecrets`; access tokens are kept as SHA-256 only and shown once. Registry
administration pages check `IsAdmin` in the page and again in the business layer.

**UI (MatDesign rules)**: CRUD = list page (toolbar above, table, pager under it, actions left below) + separate edit page (Save, Back, other buttons, space, Delete);
a registry has **tabs** (General, Access), a repository (Tags, General), System (General, Registry, Storage). Commands to copy are `mat-input-group` with
`data-copy` (`_Commands`). Icons via `Ui.Icon` (toolkit sprite + `wwwroot/img/app-icons.svg`). Every `td` has `data-label`. The version at the foot of the menu links
to `/About`. `pattern` attributes must be valid with the `v` flag (escape `-` and `/` in classes).

**Code**: Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. The business layer is static classes taking the context and the actor; no database
queries in page models or views beyond calling the business layer. English identifiers and comments, UI texts only in the catalog.

**Compose files**: no environment variables to set, no placeholders; a stack (`name:`) per file; `docker-compose.yml` (release), `.dev.yml` (nightly + pgAdmin),
`.local.yml` (build + pgAdmin, subnet 10.253.78.0/24).

**Git**: commit under the user's name; never add a Claude/Anthropic co-author trailer or a "generated with" line (global rule of the user; it overrides any default).
Branches: `main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development `nightly-<build>-<date>`, local `local-<date>`
(`Directory.Build.props`; `.github/workflows/build.yml` builds, tests and pushes the image `ghcr.io/real-ttx/matregistry`).
