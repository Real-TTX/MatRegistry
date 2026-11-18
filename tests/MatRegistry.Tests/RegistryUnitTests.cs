using System.Text;
using MatRegistry.Data;
using MatRegistry.Services.Registry;

namespace MatRegistry.Tests;

/// <summary>What the registry reads without a database: names, digests, manifests, image configs, the addresses of remote registries, the blob store.</summary>
public sealed class RegistryUnitTests
{
    [Theory]
    [InlineData("team/app", true)]
    [InlineData("team/web/api", true)]
    [InlineData("a.b-c__d/x", true)]
    [InlineData("Team/app", false)]
    [InlineData("team//app", false)]
    [InlineData("team/app/", false)]
    [InlineData("-team/app", false)]
    [InlineData("team/app-", false)]
    public void Repository_names_follow_the_spec(string name, bool valid) => Assert.Equal(valid, RegistryNames.IsName(name));

    [Theory]
    [InlineData("latest", true)]
    [InlineData("1.27.3-alpine", true)]
    [InlineData("_private", true)]
    [InlineData(".hidden", false)]
    [InlineData("-x", false)]
    [InlineData("a:b", false)]
    public void Tags_follow_the_spec(string tag, bool valid) => Assert.Equal(valid, RegistryNames.IsTag(tag));

    [Fact]
    public void A_name_is_split_into_registry_and_repository()
    {
        Assert.Equal(("team", "web/api"), RegistryNames.Split("team/web/api"));
        Assert.Null(RegistryNames.Split("nginx"));
        Assert.False(RegistryNames.IsRegistryName("v2"));
        Assert.True(RegistryNames.IsRegistryName("dockerhub"));
    }

    [Fact]
    public void A_digest_is_sha256_of_the_bytes()
    {
        var digest = RegistryNames.DigestOf(Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest);
        Assert.True(RegistryNames.IsDigest(digest));
        Assert.False(RegistryNames.IsDigest("sha512:abc"));
        Assert.Equal("sha256:2cf24dba5fb0", RegistryNames.Short(digest));
    }

    [Fact]
    public void An_image_manifest_is_read_with_its_config_and_layers()
    {
        var json = """
            {"schemaVersion":2,"mediaType":"application/vnd.docker.distribution.manifest.v2+json",
             "config":{"mediaType":"application/vnd.docker.container.image.v1+json","size":10,"digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111"},
             "layers":[{"mediaType":"application/vnd.docker.image.rootfs.diff.tar.gzip","size":100,"digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222"}]}
            """;
        var (doc, error) = ManifestDocument.Parse(Encoding.UTF8.GetBytes(json), null);
        Assert.Null(error);
        Assert.Equal(ManifestKind.Image, doc!.Kind);
        Assert.Equal(MediaTypes.DockerManifest, doc.MediaType);
        Assert.Equal(110, doc.DeclaredSize);
        Assert.Equal(2, doc.Children.Count());
        Assert.Null(doc.EffectiveArtifactType);
    }

    [Fact]
    public void An_index_is_read_with_the_platforms_of_its_manifests()
    {
        var json = """
            {"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[
              {"mediaType":"application/vnd.oci.image.manifest.v1+json","size":500,"digest":"sha256:3333333333333333333333333333333333333333333333333333333333333333","platform":{"os":"linux","architecture":"arm64","variant":"v8"}},
              {"mediaType":"application/vnd.oci.image.manifest.v1+json","size":501,"digest":"sha256:4444444444444444444444444444444444444444444444444444444444444444","platform":{"os":"linux","architecture":"amd64"}}]}
            """;
        var (doc, _) = ManifestDocument.Parse(Encoding.UTF8.GetBytes(json), "application/vnd.oci.image.index.v1+json; charset=utf-8");
        Assert.Equal(ManifestKind.Index, doc!.Kind);
        Assert.Equal(["linux/arm64/v8", "linux/amd64"], doc.Manifests.Select(m => m.Platform));
        var rows = RegistryEngine.Describe(doc).ToList();
        Assert.All(rows, r => Assert.Equal(ReferenceKind.Manifest, r.Kind));
    }

    [Fact]
    public void An_artifact_and_its_subject_are_read()
    {
        var json = """
            {"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","artifactType":"application/spdx+json",
             "config":{"mediaType":"application/vnd.oci.empty.v1+json","size":2,"digest":"sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"},
             "layers":[],"subject":{"mediaType":"application/vnd.oci.image.manifest.v1+json","size":7,"digest":"sha256:5555555555555555555555555555555555555555555555555555555555555555"},
             "annotations":{"org.opencontainers.image.created":"2026-11-18T10:00:00Z"}}
            """;
        var (doc, _) = ManifestDocument.Parse(Encoding.UTF8.GetBytes(json), null);
        Assert.Equal("application/spdx+json", doc!.EffectiveArtifactType);
        Assert.Equal("sha256:5555555555555555555555555555555555555555555555555555555555555555", doc.Subject!.Digest);
        Assert.Single(doc.Annotations);
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"name":"x","tag":"y","fsLayers":[]}""")]
    [InlineData("""{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","layers":[]}""")]
    [InlineData("""{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"mediaType":"x","size":1,"digest":"md5:abc"}}""")]
    [InlineData("not json")]
    public void A_wrong_manifest_is_refused_with_a_reason(string json)
    {
        var (doc, error) = ManifestDocument.Parse(Encoding.UTF8.GetBytes(json), null);
        Assert.Null(doc);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_content_type_that_differs_from_the_manifest_is_refused()
    {
        var json = """{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}""";
        var (doc, error) = ManifestDocument.Parse(Encoding.UTF8.GetBytes(json), MediaTypes.DockerManifest);
        Assert.Null(doc);
        Assert.NotNull(error);
    }

    [Fact]
    public void The_config_of_an_image_is_read()
    {
        var json = """
            {"architecture":"amd64","os":"linux","created":"2026-11-01T12:00:00Z",
             "config":{"Env":["PATH=/usr/bin"],"Entrypoint":["/app"],"Cmd":["--serve"],"ExposedPorts":{"8080/tcp":{}},"Labels":{"org.opencontainers.image.source":"https://github.com/x/y"},"User":"app","WorkingDir":"/srv"},
             "history":[{"created":"2026-11-01T11:00:00Z","created_by":"COPY . /srv"},{"created_by":"ENV A=1","empty_layer":true}]}
            """;
        var config = ImageConfig.Parse(Encoding.UTF8.GetBytes(json))!;
        Assert.Equal(("linux", "amd64"), (config.Os, config.Architecture));
        Assert.Equal(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), config.Created);
        Assert.Equal(["/app"], config.Entrypoint);
        Assert.Equal(["8080/tcp"], config.ExposedPorts);
        Assert.Equal("app", config.User);
        Assert.Equal(2, config.History.Count);
        Assert.True(config.History[1].EmptyLayer);
    }

    [Theory]
    [InlineData("docker.io", "https://registry-1.docker.io/")]
    [InlineData("https://index.docker.io/v2/", "https://registry-1.docker.io/")]
    [InlineData("ghcr.io", "https://ghcr.io/")]
    [InlineData("http://nas.local:5000/", "http://nas.local:5000/")]
    [InlineData("https://harbor.example.com/v2", "https://harbor.example.com/")]
    public void The_address_of_a_remote_registry_is_normalised(string text, string expected) => Assert.Equal(expected, UpstreamClient.NormalizeUrl(text)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("http://")]
    public void What_is_no_address_is_refused(string text) => Assert.Null(UpstreamClient.NormalizeUrl(text));

    [Fact]
    public void Docker_hub_keeps_official_images_under_library()
    {
        var hub = new UpstreamTarget(1, new Uri("https://registry-1.docker.io"), null, null, false);
        Assert.Equal("library/nginx", hub.RepositoryOf("nginx"));
        Assert.Equal("bitnami/redis", hub.RepositoryOf("bitnami/redis"));
        var ghcr = new UpstreamTarget(2, new Uri("https://ghcr.io"), null, null, false);
        Assert.Equal("nginx", ghcr.RepositoryOf("nginx"));
    }

    [Fact]
    public async Task The_blob_store_takes_a_file_only_with_its_digest()
    {
        var root = Path.Combine(Path.GetTempPath(), "matregistry-store-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new BlobStore(root);
            var bytes = Encoding.UTF8.GetBytes("layer");
            var digest = RegistryNames.DigestOf(bytes);

            var wrong = store.NewTempPath();
            await File.WriteAllBytesAsync(wrong, Encoding.UTF8.GetBytes("other"));
            Assert.Equal(-1, await store.CommitAsync(wrong, digest, CancellationToken.None));
            Assert.False(File.Exists(wrong));
            Assert.False(store.Exists(digest));

            var token = Guid.NewGuid();
            store.StartUpload(token);
            await store.AppendAsync(token, new MemoryStream(bytes[..2]), CancellationToken.None);
            await store.AppendAsync(token, new MemoryStream(bytes[2..]), CancellationToken.None);
            Assert.Equal(bytes.Length, store.UploadSize(token));
            Assert.Equal(bytes.Length, await store.CommitAsync(store.UploadPath(token), digest, CancellationToken.None));
            Assert.True(store.Exists(digest));
            Assert.Equal(bytes, await store.ReadAllAsync(digest, CancellationToken.None));

            // the same blob again: the store keeps the one it has
            Assert.True(await store.PutAsync(bytes, digest, CancellationToken.None));
            Assert.Single(store.Files());
            Assert.Throws<ArgumentException>(() => store.PathOf("../../etc/passwd"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
