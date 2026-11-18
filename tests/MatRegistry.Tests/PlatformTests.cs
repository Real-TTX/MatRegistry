using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatRegistry.Data;
using MatRegistry.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatRegistry.Tests;

/// <summary>The description of MatRegistry for the platform (contract 2): valid, what it consumes, its launcher, quick actions and widgets, the public address fixed by MAT_PUBLIC_URL.</summary>
public sealed class PlatformTests
{
    private static readonly string Web = Path.Combine(Root(), "src", "MatRegistry", "wwwroot");

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid_with_the_icons_of_the_MatUi_sprite(string language)
    {
        var sprite = MatPlatformValidator.ReadSprite(Path.Combine(Web, "matui", "matui-icons.svg"));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatRegistryPlatform.Describe, language), sprite));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(o => MatRegistryPlatform.Describe(o, "https://registry.example.com"), language), sprite));
    }

    [Fact]
    public void MatRegistry_provides_keys_to_pull_consumes_sign_in_and_mail_and_contributes_a_launcher_and_widgets()
    {
        var definition = MatPlatformBuilder.Build(MatRegistryPlatform.Describe, "en");
        Assert.Equal(MatApiDefinition.ContractVersion, definition.Contract);
        Assert.Equal([Services.Registry.RegistryAuth.Capability], definition.Provides.Select(p => p.Capability));
        Assert.Equal([MatCapabilities.IdentityOidc, MatCapabilities.MailSmtp], definition.Consumes.Select(c => c.Capability));
        Assert.All(definition.Consumes, c => Assert.True(c.Optional));

        var contributes = definition.Contributes!;
        var launcher = Assert.Single(contributes.Launchers);
        Assert.Equal(("registry", "/Overview"), (launcher.Id, launcher.Url));
        Assert.True(File.Exists(Path.Combine(Web, launcher.Icon.TrimStart('/'))), launcher.Icon);
        Assert.Equal(["/Repositories", "/Registries", "/Copies/New", "/Account/Tokens"], contributes.QuickActions.Select(q => q.Url));
        Assert.All(contributes.QuickActions, q => Assert.Equal("registry", q.Launcher));

        // widgets only from existing values, only for administrators
        Assert.Equal(["store", "users"], contributes.Widgets.Select(w => w.Id));
        Assert.All(contributes.Widgets, w => Assert.Equal(MatAudience.Admin, w.Audience));
        Assert.Contains(definition.Actions, a => a.Id == "openConnections" && a.Url == "/Admin/Connections");
        Assert.Contains(definition.Actions, a => a.Id == "collectGarbage");
    }

    [Fact]
    public void MAT_PUBLIC_URL_fixes_the_public_address()
    {
        Assert.Null(MatPlatformBuilder.Build(MatRegistryPlatform.Describe, "en").Configure.Single(f => f.Key == "publicUrl").Fixed);
        var field = MatPlatformBuilder.Build(o => MatRegistryPlatform.Describe(o, "https://registry.example.com"), "en").Configure.Single(f => f.Key == "publicUrl");
        Assert.True(field.Fixed);
        Assert.Contains("MAT_PUBLIC_URL", field.Hint, StringComparison.Ordinal);
    }

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatRegistry.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatRegistry.slnx not found.");
    }
}

/// <summary>MatRegistry in memory with the variables of a broker: the platform endpoints, the fixed public address, the health.</summary>
public sealed class PlatformAppTests : IAsyncLifetime
{
    private const string ApiKey = "test-api-key-0123456789";
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "matregistry-tests", Guid.NewGuid().ToString("N")[..12]);

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
            web.UseSetting("MAT_API_KEY", ApiKey);
            web.UseSetting("MAT_PUBLIC_URL", "https://registry.example.com/");
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

    private HttpClient Api()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    [DbFact]
    public async Task The_variables_of_a_broker_fix_the_public_address()
    {
        using var api = Api();
        var definition = await api.GetFromJsonAsync<JsonElement>("/api");
        Assert.Equal(2, definition.GetProperty("contract").GetInt32());
        Assert.Equal("registry", definition.GetProperty("contributes").GetProperty("launchers")[0].GetProperty("id").GetString());
        var publicUrl = definition.GetProperty("configure").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "publicUrl");
        Assert.True(publicUrl.GetProperty("fixed").GetBoolean());

        var values = (await api.GetFromJsonAsync<JsonElement>("/api/configure")).GetProperty("values");
        Assert.Equal("https://registry.example.com", values.GetProperty("publicUrl").GetString());
        var refused = await api.PutAsync("/api/configure", new StringContent("""{"values":{"publicUrl":"https://other.example.com"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var changed = await api.PutAsync("/api/configure", new StringContent("""{"values":{"tokenMinutes":30}}""", Encoding.UTF8, "application/json"));
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        Assert.Equal(30, (await api.GetFromJsonAsync<JsonElement>("/api/configure")).GetProperty("values").GetProperty("tokenMinutes").GetInt32());
    }

    [DbFact]
    public async Task The_health_and_the_state_answer()
    {
        using var api = Api();
        var health = await api.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Contains(health.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "remotes");
        var state = await api.GetFromJsonAsync<JsonElement>("/api/state");
        Assert.Equal(JsonValueKind.Object, state.ValueKind);
    }
}
