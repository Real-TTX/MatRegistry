using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatRegistry.Data;
using MatRegistry.Services;
using MatRegistry.Services.Registry;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatRegistry.Tests;

/// <summary>
/// The registry API in memory against a real PostgreSQL, the way Docker talks to it: the challenge, the token service, chunked and monolithic uploads, manifests,
/// tags, the catalog, ranges, the roles (pull-only tokens, people without a role, anonymous, public registries, bound apps), indexes and referrers, deleting,
/// the garbage collection and a copy between two hosted registries.
/// </summary>
public sealed class RegistryApiTests : IAsyncLifetime
{
    private const string Password = "Start-12345-registry";
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "matregistry-tests", Guid.NewGuid().ToString("N")[..12]);

    private long _adminId;
    private long _teamId;
    private string _token = string.Empty;

    public async Task InitializeAsync()
    {
        if (TestDatabase.Admin is null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        Directory.CreateDirectory(_data);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:Default", _database.Db.Database.GetConnectionString());
            web.UseSetting("DataDirectory", _data);
            web.UseSetting("Logging:LogLevel:Default", "Warning");
        });

        await WithAsync(async (db, services) =>
        {
            var (admin, error) = await services.GetRequiredService<AccountService>().RegisterAsync("admin@example.com", "Admin", Password);
            Assert.Null(error);
            _adminId = admin!.Id;
            _teamId = await CreateRegistryAsync(db, services, "team", RegistryVisibility.Private);
            var (token, _) = await AccessTokens.CreateAsync(db, _adminId, "ci", TokenScope.PullPush, null, CancellationToken.None);
            _token = token!.Secret;
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [DbFact]
    public async Task An_image_is_pushed_and_pulled_the_way_docker_does_it()
    {
        using var anonymous = Client();

        // the ping: a challenge that names the token service
        var ping = await anonymous.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        Assert.Equal("registry/2.0", ping.Headers.GetValues("Docker-Distribution-API-Version").Single());
        var challenge = ping.Headers.WwwAuthenticate.Single();
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("/v2/token", challenge.Parameter, StringComparison.Ordinal);

        // wrong credentials get no token; the access token gets one
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenResponseAsync("admin@example.com", "wrong")).StatusCode);
        using var client = await SignedInAsync("admin@example.com", _token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v2/")).StatusCode);

        var image = await PushImageAsync(client, "team/app", "1.0");

        var manifest = await client.GetAsync("/v2/team/app/manifests/1.0");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal(image.Manifest, await manifest.Content.ReadAsByteArrayAsync());
        Assert.Equal(image.Digest, manifest.Headers.GetValues("Docker-Content-Digest").Single());
        Assert.Equal(MediaTypes.DockerManifest, manifest.Content.Headers.ContentType!.MediaType);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/team/app/blobs/{image.Layer}"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(image.LayerBytes.Length, head.Content.Headers.ContentLength);

        var range = new HttpRequestMessage(HttpMethod.Get, $"/v2/team/app/blobs/{image.Layer}");
        range.Headers.Range = new RangeHeaderValue(2, 5);
        var part = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(image.LayerBytes[2..6], await part.Content.ReadAsByteArrayAsync());

        var tags = await client.GetFromJsonAsync<JsonElement>("/v2/team/app/tags/list");
        Assert.Equal("team/app", tags.GetProperty("name").GetString());
        Assert.Equal(["1.0"], tags.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        var catalog = await client.GetFromJsonAsync<JsonElement>("/v2/_catalog");
        Assert.Equal(["team/app"], catalog.GetProperty("repositories").EnumerateArray().Select(t => t.GetString()));

        // the same image by its digest, and a tag that does not exist
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v2/team/app/manifests/{image.Digest}")).StatusCode);
        var unknown = await client.GetAsync("/v2/team/app/manifests/2.0");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("MANIFEST_UNKNOWN", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")[0].GetProperty("code").GetString());

        // a name without a registry, and a registry that does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v2/nginx/manifests/latest")).StatusCode);
        var nowhere = await client.PostAsync("/v2/other/app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
        Assert.Equal("NAME_UNKNOWN", (await nowhere.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [DbFact]
    public async Task A_manifest_needs_its_blobs_in_the_repository_and_a_digest_must_fit()
    {
        using var client = await SignedInAsync("admin@example.com", _token);
        var image = await PushImageAsync(client, "team/app", "1.0");

        // the same manifest in another repository, where the blobs are not: refused (a layer of a private repository is never read by naming it elsewhere)
        var foreign = new HttpRequestMessage(HttpMethod.Put, "/v2/team/other/manifests/1.0") { Content = new ByteArrayContent(image.Manifest) };
        foreign.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypes.DockerManifest);
        var refused = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("MANIFEST_BLOB_UNKNOWN", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v2/team/other/blobs/{image.Layer}")).StatusCode);

        // a mount from a repository the actor may read links the blob
        var mount = await client.PostAsync($"/v2/team/other/blobs/uploads/?mount={image.Layer}&from=team/app", null);
        Assert.Equal(HttpStatusCode.Created, mount.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v2/team/other/blobs/{image.Layer}")).StatusCode);

        // an upload whose content is not the digest
        var start = await client.PostAsync("/v2/team/app/blobs/uploads/", null);
        var put = await client.PutAsync(Next(start) + "&digest=" + RegistryNames.DigestOf(Encoding.UTF8.GetBytes("something else")), new ByteArrayContent(Encoding.UTF8.GetBytes("data")));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // a manifest by a digest that is not its own
        var wrong = new HttpRequestMessage(HttpMethod.Put, $"/v2/team/app/manifests/{image.Layer}") { Content = new ByteArrayContent(image.Manifest) };
        wrong.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypes.DockerManifest);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(wrong)).StatusCode);
    }

    [DbFact]
    public async Task Roles_tokens_visibility_and_bound_apps_decide_who_pulls_and_pushes()
    {
        using var admin = await SignedInAsync("admin@example.com", _token);
        var image = await PushImageAsync(admin, "team/app", "1.0");

        string pullOnly = string.Empty, nobodysToken = string.Empty, key = string.Empty;
        long publicId = 0;
        await WithAsync(async (db, services) =>
        {
            pullOnly = (await AccessTokens.CreateAsync(db, _adminId, "deploy", TokenScope.Pull, 30, CancellationToken.None)).Token!.Secret;
            var (other, _) = await services.GetRequiredService<AccountService>().RegisterAsync("other@example.com", "Other", Password);
            nobodysToken = (await AccessTokens.CreateAsync(db, other!.Id, "x", TokenScope.PullPush, null, CancellationToken.None)).Token!.Secret;
            publicId = await CreateRegistryAsync(db, services, "public", RegistryVisibility.Public);
            var binding = await services.GetRequiredService<MatBindingStore>().UpsertProvidedAsync(RegistryAuth.Capability, new MatBindingParty("matdock", "MatDock"), "manual", false);
            key = binding.Key!;
        });

        // a token that only pulls: pulls, does not push
        using var deploy = await SignedInAsync("anything", pullOnly);
        Assert.Equal(HttpStatusCode.OK, (await deploy.GetAsync("/v2/team/app/manifests/1.0")).StatusCode);
        var denied = await deploy.PostAsync("/v2/team/app/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("DENIED", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors")[0].GetProperty("code").GetString());

        // a person without a role sees nothing of the private registry; nobody gets the challenge
        using var nobody = await SignedInAsync("other@example.com", nobodysToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/v2/team/app/manifests/1.0")).StatusCode);
        Assert.Empty((await nobody.GetFromJsonAsync<JsonElement>("/v2/_catalog")).GetProperty("repositories").EnumerateArray());
        using var anonymous = Client();
        var challenged = await anonymous.GetAsync("/v2/team/app/manifests/1.0");
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        Assert.Contains("repository:team/app:pull", challenged.Headers.WwwAuthenticate.Single().Parameter, StringComparison.Ordinal);

        // a public registry: anybody pulls (with the anonymous token, as Docker does), only an Editor pushes
        await PushImageAsync(admin, "public/tool", "latest");
        var anonymousToken = (await (await anonymous.GetAsync("/v2/token?service=matregistry&scope=repository:public/tool:pull")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", anonymousToken);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/v2/public/tool/manifests/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v2/public/tool/blobs/uploads/", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync("/v2/public/tool/blobs/uploads/", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync("/v2/public/tool/manifests/latest")).StatusCode);

        // the key of a bound app (registry.docker) pulls from every registry, with Basic straight away too, and never pushes
        using var app = Client();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("matdock:" + key)));
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync($"/v2/team/app/blobs/{image.Layer}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsync("/v2/team/app/blobs/uploads/", null)).StatusCode);

        // a role given in the registry opens it
        await WithAsync(async (db, _) =>
        {
            var other = await db.Users.SingleAsync(u => u.Email == "other@example.com");
            Assert.True(await RegistryAdmin.SetRoleAsync(db, RegistryActor.ForPerson(_adminId, true, "admin"), _teamId, other.Id, AccessRole.Viewer, CancellationToken.None));
        });
        Assert.Equal(HttpStatusCode.OK, (await nobody.GetAsync("/v2/team/app/manifests/1.0")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync("/v2/team/app/blobs/uploads/", null)).StatusCode);
        Assert.NotEqual(0, publicId);
    }

    [DbFact]
    public async Task An_index_needs_its_manifests_and_the_referrers_list_what_refers_to_an_image()
    {
        using var client = await SignedInAsync("admin@example.com", _token);
        var amd = await PushImageAsync(client, "team/multi", null, "amd64");
        var arm = await PushImageAsync(client, "team/multi", null, "arm64");

        var missing = RegistryNames.DigestOf(Encoding.UTF8.GetBytes("nothing"));
        var refused = await PutManifestAsync(client, "team/multi", "1.0", Index((amd.Digest, amd.Manifest.Length, "amd64"), (missing, 10, "arm64")), MediaTypes.OciIndex);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var index = Index((amd.Digest, amd.Manifest.Length, "amd64"), (arm.Digest, arm.Manifest.Length, "arm64"));
        var pushed = await PutManifestAsync(client, "team/multi", "1.0", index, MediaTypes.OciIndex);
        Assert.Equal(HttpStatusCode.Created, pushed.StatusCode);
        var got = await client.GetAsync("/v2/team/multi/manifests/1.0");
        Assert.Equal(MediaTypes.OciIndex, got.Content.Headers.ContentType!.MediaType);

        // a signature that refers to the amd64 image
        var empty = await UploadAsync(client, "team/multi", Encoding.UTF8.GetBytes("{}"));
        var signature = Encoding.UTF8.GetBytes(
            $$$"""{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","artifactType":"application/vnd.dev.cosign.artifact.sig.v1+json","config":{"mediaType":"application/vnd.oci.empty.v1+json","size":2,"digest":"{{{empty}}}"},"layers":[],"subject":{"mediaType":"application/vnd.docker.distribution.manifest.v2+json","size":{{{amd.Manifest.Length}}},"digest":"{{{amd.Digest}}}"}}""");
        var signed = await PutManifestAsync(client, "team/multi", RegistryNames.DigestOf(signature), signature, MediaTypes.OciManifest);
        Assert.Equal(HttpStatusCode.Created, signed.StatusCode);
        Assert.Equal(amd.Digest, signed.Headers.GetValues("OCI-Subject").Single());

        var referrers = await client.GetFromJsonAsync<JsonElement>($"/v2/team/multi/referrers/{amd.Digest}");
        var entry = Assert.Single(referrers.GetProperty("manifests").EnumerateArray());
        Assert.Equal("application/vnd.dev.cosign.artifact.sig.v1+json", entry.GetProperty("artifactType").GetString());
        var filtered = await client.GetAsync($"/v2/team/multi/referrers/{amd.Digest}?artifactType=application/spdx%2Bjson");
        Assert.Equal("artifactType", filtered.Headers.GetValues("OCI-Filters-Applied").Single());
        Assert.Empty((await filtered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("manifests").EnumerateArray());

        // the pages see the platforms of the index
        await WithAsync(async (db, services) =>
        {
            var repository = await db.Repositories.SingleAsync(r => r.Name == "multi");
            var tags = await RepositoryService.TagsAsync(db, RegistryActor.ForPerson(_adminId, true, "admin"), repository.Id, new TagCriteria(null, "name", false, 1, 10), CancellationToken.None);
            var tag = Assert.Single(tags!.Items);
            Assert.Equal(["linux/amd64", "linux/arm64"], tag.Platforms);
            Assert.Equal(ManifestKind.Index, tag.Kind);
        });
    }

    [DbFact]
    public async Task Deleting_a_tag_and_the_garbage_collection_free_the_store()
    {
        using var client = await SignedInAsync("admin@example.com", _token);
        var image = await PushImageAsync(client, "team/app", "1.0");
        Assert.Equal(HttpStatusCode.Accepted, (await client.DeleteAsync("/v2/team/app/manifests/1.0")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/v2/team/app/tags/list")).GetProperty("tags").EnumerateArray());

        await WithAsync(async (db, services) =>
        {
            var store = services.GetRequiredService<BlobStore>();
            Assert.True(store.Exists(image.Layer));

            // younger than an hour: nothing goes (a push might still be running)
            var early = await GarbageCollector.RunAsync(db, store, untagged: true, RegistryActor.ForPerson(_adminId, true, "admin"), CancellationToken.None);
            Assert.Equal(0, early!.Blobs);

            var past = DateTime.UtcNow.AddHours(-2);
            await db.Manifests.ExecuteUpdateAsync(s => s.SetProperty(m => m.CreateDate, past));
            await db.RepositoryBlobs.ExecuteUpdateAsync(s => s.SetProperty(l => l.CreateDate, past));
            await db.Blobs.ExecuteUpdateAsync(s => s.SetProperty(b => b.CreateDate, past));
            var result = await GarbageCollector.RunAsync(db, store, untagged: true, RegistryActor.ForPerson(_adminId, true, "admin"), CancellationToken.None);
            Assert.Equal(1, result!.Manifests);
            Assert.Equal(2, result.Blobs);
            Assert.False(store.Exists(image.Layer));
            Assert.Equal(0, await db.Blobs.CountAsync());
        });
    }

    [DbFact]
    public async Task A_copy_between_two_hosted_registries_takes_the_image_with_its_tag()
    {
        using var client = await SignedInAsync("admin@example.com", _token);
        var image = await PushImageAsync(client, "team/app", "1.0");
        long jobId = 0;
        await WithAsync(async (db, services) =>
        {
            var targetId = await CreateRegistryAsync(db, services, "prod", RegistryVisibility.Private);
            var actor = RegistryActor.ForPerson(_adminId, true, "admin");
            var (job, errors) = await CopyService.CreateAsync(db, actor, new CopyInput(_teamId, "app", "1.0", targetId, "app", "stable"), CancellationToken.None);
            Assert.Empty(errors);
            jobId = job!.Id;
            var (same, refused) = await CopyService.CreateAsync(db, actor, new CopyInput(_teamId, "app", "1.0", _teamId, "app", "1.0"), CancellationToken.None);
            Assert.Null(same);
            Assert.Contains(refused, e => e.Field == "TargetTag");
        });

        // the worker of the app may have taken it already: then this returns at once, and the test waits for the end
        await _factory!.Services.GetRequiredService<CopyWorker>().RunAsync(jobId, CancellationToken.None);

        await WithAsync(async (db, _) =>
        {
            var job = await db.CopyJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            for (var i = 0; i < 100 && job.State is CopyState.Waiting or CopyState.Running; i++)
            {
                await Task.Delay(100);
                job = await db.CopyJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            }

            Assert.True(job.State == CopyState.Done, job.Message);
            Assert.Equal(image.Digest, job.Digest);
            Assert.Equal(2, job.BlobsTotal);
        });
        var copied = await client.GetAsync("/v2/prod/app/manifests/stable");
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        Assert.Equal(image.Digest, copied.Headers.GetValues("Docker-Content-Digest").Single());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v2/prod/app/blobs/{image.Layer}")).StatusCode);
    }

    // ---- helpers ----

    private sealed record PushedImage(string Digest, byte[] Manifest, string Layer, byte[] LayerBytes);

    private HttpClient Client() => _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private Task<HttpResponseMessage> TokenResponseAsync(string user, string password)
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return client.GetAsync("/v2/token?service=matregistry");
    }

    private async Task<HttpClient> SignedInAsync(string user, string password)
    {
        var response = await TokenResponseAsync(user, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Pushes a small image: a config (monolithic upload) and a layer (chunked upload, with and without Content-Range), then the manifest.</summary>
    private async Task<PushedImage> PushImageAsync(HttpClient client, string name, string? tag, string architecture = "amd64")
    {
        var config = Encoding.UTF8.GetBytes($$$"""{"architecture":"{{{architecture}}}","os":"linux","created":"2026-11-18T10:00:00Z","config":{"Cmd":["/app"]},"rootfs":{"type":"layers","diff_ids":[]}}""");
        var configDigest = RegistryNames.DigestOf(config);
        var monolithic = new ByteArrayContent(config);
        var created = await client.PostAsync($"/v2/{name}/blobs/uploads/?digest={configDigest}", monolithic);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var layer = Encoding.UTF8.GetBytes("layer of " + name + architecture + Guid.NewGuid());
        var layerDigest = await UploadAsync(client, name, layer);

        var manifest = Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":2,"mediaType":"application/vnd.docker.distribution.manifest.v2+json","config":{"mediaType":"application/vnd.docker.container.image.v1+json","size":{{config.Length}},"digest":"{{configDigest}}"},"layers":[{"mediaType":"application/vnd.docker.image.rootfs.diff.tar.gzip","size":{{layer.Length}},"digest":"{{layerDigest}}"}]}""");
        var digest = RegistryNames.DigestOf(manifest);
        var put = await PutManifestAsync(client, name, tag ?? digest, manifest, MediaTypes.DockerManifest);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal(digest, put.Headers.GetValues("Docker-Content-Digest").Single());
        return new PushedImage(digest, manifest, layerDigest, layer);
    }

    private static async Task<string> UploadAsync(HttpClient client, string name, byte[] bytes)
    {
        var digest = RegistryNames.DigestOf(bytes);
        var start = await client.PostAsync($"/v2/{name}/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        Assert.Equal("0-0", start.Headers.GetValues("Range").Single());
        var half = bytes.Length / 2;
        var first = await client.PatchAsync(start.Headers.Location!.ToString(), new ByteArrayContent(bytes[..half]));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal($"0-{half - 1}", first.Headers.GetValues("Range").Single());

        // a chunk that does not start where the upload stands
        var gap = new HttpRequestMessage(HttpMethod.Patch, first.Headers.Location) { Content = new ByteArrayContent(bytes[half..]) };
        gap.Content.Headers.TryAddWithoutValidation("Content-Range", $"{half + 1}-{bytes.Length}");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await client.SendAsync(gap)).StatusCode);

        var second = new HttpRequestMessage(HttpMethod.Patch, first.Headers.Location) { Content = new ByteArrayContent(bytes[half..]) };
        second.Content.Headers.TryAddWithoutValidation("Content-Range", $"{half}-{bytes.Length - 1}");
        var patched = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Accepted, patched.StatusCode);
        var done = await client.PutAsync(Next(patched) + "&digest=" + digest, null);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        Assert.Equal(digest, done.Headers.GetValues("Docker-Content-Digest").Single());
        return digest;
    }

    private static string Next(HttpResponseMessage response)
    {
        var location = response.Headers.Location!.ToString();
        return location + (location.Contains('?', StringComparison.Ordinal) ? string.Empty : "?");
    }

    private static Task<HttpResponseMessage> PutManifestAsync(HttpClient client, string name, string reference, byte[] content, string mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/v2/{name}/manifests/{reference}") { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return client.SendAsync(request);
    }

    private static byte[] Index(params (string Digest, int Size, string Architecture)[] manifests)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            mediaType = MediaTypes.OciIndex,
            manifests = manifests.Select(m => new { mediaType = MediaTypes.DockerManifest, size = m.Size, digest = m.Digest, platform = new { os = "linux", architecture = m.Architecture } }),
        }));

    private async Task<long> CreateRegistryAsync(AppDbContext db, IServiceProvider services, string name, RegistryVisibility visibility)
    {
        var (id, errors, _) = await RegistryAdmin.SaveAsync(db, services.GetRequiredService<RegistrySecrets>(), RegistryActor.ForPerson(_adminId, true, "admin"), null,
            new RegistryInput(name, RegistryKind.Hosted, null, visibility, true, null, null, null, false, false, 60, false), CancellationToken.None);
        Assert.Empty(errors);
        return id!.Value;
    }

    private async Task WithAsync(Func<AppDbContext, IServiceProvider, Task> action)
    {
        using var scope = _factory!.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider);
    }
}
