using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace BerMap.Tests.Integration;

/// <summary>
/// Users, audit, collectors and API validation against PostgreSQL (see
/// <see cref="ApiSmokeTests"/> for BERMAP_TEST_CONNECTION_STRING). The login
/// rate limit is 10 per minute and client, so each test signs in sparingly.
/// </summary>
public sealed class ManagementApiTests : IAsyncLifetime
{
    private const string AdminPassword = "Integration-Test-Pa55";
    private const string EnrollmentToken = "integration-test-token";

    private readonly string? _serverConnectionString =
        Environment.GetEnvironmentVariable("BERMAP_TEST_CONNECTION_STRING");

    private string _databaseName = string.Empty;
    private string _vaultDirectory = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
            return Task.CompletedTask;

        _databaseName = "bermap_it_" + Guid.NewGuid().ToString("N")[..12];
        _vaultDirectory = Path.Combine(Path.GetTempPath(), _databaseName + "-vault");
        var connection = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _databaseName };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connection.ConnectionString);
            builder.UseSetting("BerMap:CredentialVault:Directory", _vaultDirectory);
            builder.UseSetting("BerMap:Bootstrap:AdminPassword", AdminPassword);
            builder.UseSetting("BerMap:Collectors:EnrollmentToken", EnrollmentToken);
            builder.UseSetting("BerMap:DiscoveryScheduler:Enabled", "false");
            builder.UseSetting("BerMap:DiscoveryWorker:Enabled", "false");
        });
        _factory.CreateClient().Dispose();
        return Task.CompletedTask;
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
    public async Task Users_CreateLoginRoleEnforcementAndDeactivation()
    {
        if (_factory == null)
            return;

        using var admin = await ClientAsync("admin", AdminPassword);

        var weak = await admin.PostAsJsonAsync("/api/users", NewUser("weak.user", "ReadOnlyViewer", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var created = await admin.PostAsJsonAsync("/api/users", NewUser("viewer.one", "ReadOnlyViewer", "Viewer-Password-123"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var userId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var duplicate = await admin.PostAsJsonAsync("/api/users", NewUser("VIEWER.one", "ReadOnlyViewer", "Viewer-Password-123"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var viewer = await ClientAsync("viewer.one", "Viewer-Password-123");
        var me = await viewer.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("ReadOnlyViewer", me.GetProperty("role").GetString());

        // A viewer reads but may not change state, manage users or read the audit trail.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/inventory/nodes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/audit/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/snapshots", new { name = "viewer snapshot" })).StatusCode);

        // A role change applies to the next request, without a new sign-in.
        var promoted = await admin.PutAsJsonAsync($"/api/users/{userId}", new { role = "SecurityAuditor" });
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);

        var deactivated = await admin.PutAsJsonAsync($"/api/users/{userId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/inventory/nodes")).StatusCode);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "viewer.one", password = "Viewer-Password-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Users_LastAdminIsProtected_AndPasswordsCanBeChangedAndReset()
    {
        if (_factory == null)
            return;

        using var admin = await ClientAsync("admin", AdminPassword);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var adminId = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin")
            .GetProperty("id").GetString();

        var demote = await admin.PutAsJsonAsync($"/api/users/{adminId}", new { role = "ReadOnlyViewer" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Contains("last_admin", await demote.Content.ReadAsStringAsync());
        var disable = await admin.PutAsJsonAsync($"/api/users/{adminId}", new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);

        var created = await admin.PostAsJsonAsync("/api/users", NewUser("operator.one", "InfrastructureOperator", "Operator-Password-123"));
        var operatorId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        using var operatorClient = await ClientAsync("operator.one", "Operator-Password-123");
        var wrongCurrent = await operatorClient.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "not-the-password", newPassword = "Operator-Password-456" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        var changed = await operatorClient.PostAsJsonAsync("/api/auth/change-password",
            new { currentPassword = "Operator-Password-123", newPassword = "Operator-Password-456" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "operator.one", password = "Operator-Password-123" })).StatusCode);

        var reset = await admin.PostAsJsonAsync($"/api/users/{operatorId}/reset-password", new { password = "Reset-Password-789" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/auth/login",
            new { username = "operator.one", password = "Reset-Password-789" })).StatusCode);
    }

    [Fact]
    public async Task Audit_FiltersPagesAndExportsFormulaSafeCsv()
    {
        if (_factory == null)
            return;

        // One failed sign-in whose attempted username a spreadsheet would run as a formula.
        var anonymous = _factory.CreateClient();
        await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "=HYPERLINK(\"x\")", password = "wrong" });

        using var admin = await ClientAsync("admin", AdminPassword);
        var snapshot = await admin.PostAsJsonAsync("/api/snapshots", new { name = "audit-snapshot" });
        Assert.Equal(HttpStatusCode.Created, snapshot.StatusCode);
        var snapshotId = (await snapshot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var created = await admin.GetFromJsonAsync<JsonElement>("/api/audit?action=Snapshot.Created");
        var entry = Assert.Single(created.GetProperty("items").EnumerateArray());
        Assert.Equal(snapshotId, entry.GetProperty("entityId").GetString());
        Assert.Equal("admin", entry.GetProperty("username").GetString());

        var failed = await admin.GetFromJsonAsync<JsonElement>("/api/audit?success=false");
        Assert.Equal(1, failed.GetProperty("totalCount").GetInt32());
        Assert.Equal("Auth.LoginFailed", failed.GetProperty("items")[0].GetProperty("action").GetString());

        var byUser = await admin.GetFromJsonAsync<JsonElement>("/api/audit?username=ADM&entityType=Snapshot");
        Assert.Equal(1, byUser.GetProperty("totalCount").GetInt32());

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var all = await admin.GetFromJsonAsync<JsonElement>($"/api/audit?from={today}&to={today}");
        Assert.True(all.GetProperty("totalCount").GetInt32() >= 3); // failed login, login, snapshot
        var future = await admin.GetFromJsonAsync<JsonElement>($"/api/audit?from={tomorrow}");
        Assert.Equal(0, future.GetProperty("totalCount").GetInt32());

        var paged = await admin.GetFromJsonAsync<JsonElement>("/api/audit?page=2&pageSize=1");
        Assert.Equal(2, paged.GetProperty("page").GetInt32());
        Assert.Single(paged.GetProperty("items").EnumerateArray());
        Assert.Equal(200, (await admin.GetFromJsonAsync<JsonElement>("/api/audit?pageSize=5000")).GetProperty("pageSize").GetInt32());

        var export = await admin.GetAsync("/api/audit/export?success=false");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("createdAt,username,", lines[0]);
        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\",", lines[1]);
    }

    [Fact]
    public async Task Collectors_ReportMissingMetricsAsNull_AndCanBeDeleted()
    {
        if (_factory == null)
            return;

        using var admin = await ClientAsync("admin", AdminPassword);
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/register")
        {
            Content = JsonContent.Create(new { name = "collector-1", hostname = "col01.corp.local", supportedAdapters = Array.Empty<string>() })
        };
        register.Headers.Add("X-BerMap-Enrollment-Token", EnrollmentToken);
        var registered = await admin.SendAsync(register);
        var collectorId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("collectorId").GetString();

        var collector = Assert.Single((await admin.GetFromJsonAsync<JsonElement>("/api/collectors")).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, collector.GetProperty("metrics").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/collectors/{collectorId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/collectors/{collectorId}")).StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>("/api/collectors")).EnumerateArray());

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/audit?action=Collector.Deleted");
        Assert.Equal(1, audit.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Validation_ReturnsCleanClientErrors()
    {
        if (_factory == null)
            return;

        using var admin = await ClientAsync("admin", AdminPassword);

        var compare = await admin.GetAsync("/api/snapshots/compare");
        Assert.Equal(HttpStatusCode.BadRequest, compare.StatusCode);
        var compareBody = await compare.Content.ReadAsStringAsync();
        Assert.DoesNotContain("(Parameter", compareBody);
        Assert.DoesNotContain("00000000-", compareBody);

        var unnamed = await admin.PostAsJsonAsync("/api/snapshots", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
        Assert.DoesNotContain("(Parameter", await unnamed.Content.ReadAsStringAsync());

        var nodeType = await admin.GetAsync("/api/inventory/nodes?nodeType=NotAType");
        Assert.Equal(HttpStatusCode.BadRequest, nodeType.StatusCode);
        Assert.Contains("invalid_filter", await nodeType.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/inventory/nodes?status=broken")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/inventory/nodes?nodeType=VirtualMachine&status=healthy")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/api/integrations/jobs/{Guid.NewGuid()}/cancel", null)).StatusCode);

        var missingConfig = await admin.PostAsJsonAsync("/api/integrations", new
        {
            name = "AD without server",
            adapterType = "ActiveDirectory",
            configuration = new { },
            secret = "not-a-real-secret"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingConfig.StatusCode);
        Assert.Contains("CONFIG_REQUIRED", await missingConfig.Content.ReadAsStringAsync());

        var collectorManaged = await admin.PostAsJsonAsync("/api/integrations", new
        {
            name = "AD via collector",
            adapterType = "ActiveDirectory",
            collectorManaged = true
        });
        Assert.Equal(HttpStatusCode.Created, collectorManaged.StatusCode);
        var integration = await collectorManaged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(integration.GetProperty("collectorManaged").GetBoolean());
        Assert.Equal("Active", integration.GetProperty("status").GetString());
        var id = integration.GetProperty("id").GetString();

        var test = await admin.PostAsJsonAsync($"/api/integrations/{id}/test", new { });
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        Assert.Contains("COLLECTOR_MANAGED", await test.Content.ReadAsStringAsync());
        var discover = await admin.PostAsJsonAsync($"/api/integrations/{id}/discover", new { });
        Assert.Equal(HttpStatusCode.BadRequest, discover.StatusCode);
        Assert.Contains("COLLECTOR_MANAGED", await discover.Content.ReadAsStringAsync());
    }

    private static object NewUser(string username, string role, string password) =>
        new { username, displayName = username, email = $"{username}@corp.local", role, password };

    private async Task<HttpClient> ClientAsync(string username, string password)
    {
        var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
