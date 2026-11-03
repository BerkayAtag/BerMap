using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace BerMap.Tests.Integration;

/// <summary>
/// End-to-end checks against a real PostgreSQL server. Set
/// BERMAP_TEST_CONNECTION_STRING to a server where the test user may create
/// databases; each run uses its own throw-away database. Without it the
/// tests pass trivially so the suite stays green on machines without one.
/// </summary>
public sealed class ApiSmokeTests : IAsyncLifetime
{
    private const string AdminPassword = "Integration-Test-Pa55";

    private readonly string? _serverConnectionString =
        Environment.GetEnvironmentVariable("BERMAP_TEST_CONNECTION_STRING");

    private string _databaseName = string.Empty;
    private string _vaultDirectory = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
            return;

        _databaseName = "bermap_it_" + Guid.NewGuid().ToString("N")[..12];
        _vaultDirectory = Path.Combine(Path.GetTempPath(), _databaseName + "-vault");

        var connection = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _databaseName };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connection.ConnectionString);
            builder.UseSetting("BerMap:CredentialVault:Directory", _vaultDirectory);
            builder.UseSetting("BerMap:Bootstrap:AdminPassword", AdminPassword);
            builder.UseSetting("BerMap:Collectors:EnrollmentToken", "integration-test-token");
        });

        // Creating the client starts the host, which migrates the empty database.
        _factory.CreateClient().Dispose();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory == null)
            return;

        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(_serverConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();

        if (Directory.Exists(_vaultDirectory))
            Directory.Delete(_vaultDirectory, recursive: true);
    }

    [Fact]
    public async Task FreshDatabase_IsMigrated_AndApiRequiresAuthentication()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/does-not-exist")).StatusCode);

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/inventory/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/graph/topology")).StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("admin", me.GetProperty("username").GetString());
        Assert.Equal("PlatformAdministrator", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task CollectorProtocol_RequiresEnrollmentToken()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();
        var body = new { name = "collector-1", hostname = "col01.corp.local", supportedAdapters = new[] { "ActiveDirectory" } };

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/collectors/register", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/collectors/heartbeat", new { collectorId = Guid.NewGuid() })).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/register")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-BerMap-Enrollment-Token", "integration-test-token");
        var registered = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var firstId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("collectorId").GetString();

        // Re-enrolling with the same name and host reuses the collector row.
        using var again = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/register") { Content = JsonContent.Create(body) };
        again.Headers.Add("X-BerMap-Enrollment-Token", "integration-test-token");
        var second = await client.SendAsync(again);
        Assert.Equal(firstId, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("collectorId").GetString());
    }

    [Fact]
    public async Task AuditActor_ComesFromToken_NotFromHeaders()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));
        client.DefaultRequestHeaders.Add("X-BerMap-Actor", "someone-else");

        var created = await client.PostAsJsonAsync("/api/snapshots", new { name = "it-snapshot", createdBy = "someone-else" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var snapshot = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", snapshot.GetProperty("createdBy").GetString());
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = AdminPassword });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("token").GetString()!;
    }
}
