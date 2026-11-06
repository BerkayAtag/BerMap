using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BerMap.Tests.Integration;

/// <summary>
/// Simulation on a snapshot basis against PostgreSQL, through the API: the
/// snapshot keeps the topology (and the jsonb properties) it captured while
/// the live graph moves on. See <see cref="ApiSmokeTests"/> for
/// BERMAP_TEST_CONNECTION_STRING.
/// </summary>
public sealed class SnapshotSimulationTests : IAsyncLifetime
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
            builder.UseSetting("BerMap:DiscoveryScheduler:Enabled", "false");
            builder.UseSetting("BerMap:DiscoveryWorker:Enabled", "false");
        });
        await _factory.WaitUntilReadyAsync();
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

    private async Task WithContextAsync(Func<BerMapDbContext, Task> action)
    {
        using var scope = _factory!.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<BerMapDbContext>());
    }

    private static InfrastructureNode Node(string name, NodeType type, string properties = "{}") => new()
    {
        Name = name,
        FQDN = $"{name}.corp.local",
        NodeType = type,
        Status = "healthy",
        DiscoverySource = "VMware",
        PropertiesJson = properties,
    };

    private static InfrastructureRelationship Rel(InfrastructureNode source, InfrastructureNode target, RelationshipType type) => new()
    {
        SourceNodeId = source.Id,
        TargetNodeId = target.Id,
        RelationshipType = type,
        SourceSystem = "VMware",
    };

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, Guid target, Guid? snapshotId = null) =>
        client.PostAsJsonAsync("/api/simulations/run", snapshotId is { } id
            ? new { targetNodeId = target, scenarioType = "host_failure", snapshotId = id }
            : (object)new { targetNodeId = target, scenarioType = "host_failure" });

    private static async Task<JsonElement> RunOkAsync(HttpClient client, Guid target, Guid? snapshotId = null)
    {
        var response = await RunAsync(client, target, snapshotId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Names(JsonElement result, string group) =>
        result.GetProperty(group).EnumerateArray().Select(e => e.GetProperty("name").GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] RecommendationCodes(JsonElement result) =>
        result.GetProperty("recommendationItems").EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray();

    [Fact]
    public async Task Simulation_OnSnapshot_SeesTheCapturedTopology_WhileLiveSeesTheVMotion()
    {
        if (_factory == null)
            return;

        // CL-PROD (HA off) Contains esx-a and esx-b; vm-01 runs on esx-a, app-01 depends on it.
        var cluster = Node("CL-PROD", NodeType.Cluster, """{"HaEnabled": false, "HostCount": 2}""");
        var hostA = Node("esx-a", NodeType.Hypervisor);
        var hostB = Node("esx-b", NodeType.Hypervisor);
        var vm = Node("vm-01", NodeType.VirtualMachine);
        var app = Node("app-01", NodeType.Application);
        var vmOnHostA = Rel(vm, hostA, RelationshipType.HostedOn);
        await WithContextAsync(async context =>
        {
            context.InfrastructureNodes.AddRange(cluster, hostA, hostB, vm, app);
            context.InfrastructureRelationships.AddRange(
                Rel(cluster, hostA, RelationshipType.Contains),
                Rel(cluster, hostB, RelationshipType.Contains),
                vmOnHostA,
                Rel(app, vm, RelationshipType.DependsOn));
            await context.SaveChangesAsync();
        });

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client));

        var created = await client.PostAsJsonAsync("/api/snapshots", new { name = "before-vmotion" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var snapshot = await created.Content.ReadFromJsonAsync<JsonElement>();
        var snapshotId = snapshot.GetProperty("id").GetGuid();
        var capturedAt = snapshot.GetProperty("graphCapturedAt").GetDateTime();

        // After the capture: vm-01 moves to esx-b (the old placement goes
        // stale, as reconciliation leaves it), HA is turned on, and a new host
        // is discovered.
        var newHost = Node("esx-new", NodeType.Hypervisor);
        await WithContextAsync(async context =>
        {
            var old = await context.InfrastructureRelationships.SingleAsync(r => r.Id == vmOnHostA.Id);
            old.IsStale = true;
            old.StaleSince = DateTime.UtcNow;
            context.InfrastructureRelationships.Add(Rel(vm, hostB, RelationshipType.HostedOn));
            var liveCluster = await context.InfrastructureNodes.SingleAsync(n => n.Id == cluster.Id);
            liveCluster.PropertiesJson = """{"HaEnabled": true, "HostCount": 3}""";
            context.InfrastructureNodes.Add(newHost);
            await context.SaveChangesAsync();
        });

        // esx-a: empty on the live graph, hosted vm-01 in the snapshot.
        var liveA = await RunOkAsync(client, hostA.Id);
        Assert.Empty(Names(liveA, "confirmedImpacts"));
        Assert.Equal(JsonValueKind.Null, liveA.GetProperty("snapshotId").ValueKind);
        Assert.Equal(JsonValueKind.Null, liveA.GetProperty("snapshotName").ValueKind);
        Assert.Equal(JsonValueKind.Null, liveA.GetProperty("snapshotCreatedAt").ValueKind);

        var snapshotA = await RunOkAsync(client, hostA.Id, snapshotId);
        Assert.Equal(new[] { "vm-01" }, Names(snapshotA, "confirmedImpacts"));
        Assert.Equal(new[] { "app-01" }, Names(snapshotA, "probableImpacts"));
        Assert.Equal(snapshotId, snapshotA.GetProperty("snapshotId").GetGuid());
        Assert.Equal("before-vmotion", snapshotA.GetProperty("snapshotName").GetString());
        Assert.True(Math.Abs((snapshotA.GetProperty("snapshotCreatedAt").GetDateTime() - capturedAt).TotalMilliseconds) < 1);
        Assert.True(snapshotA.GetProperty("riskScore").GetInt32() > liveA.GetProperty("riskScore").GetInt32());
        // The frozen jsonb properties decide the HA verdict, not today's.
        Assert.Contains("ha_disabled", RecommendationCodes(snapshotA));
        Assert.Equal("CL-PROD", snapshotA.GetProperty("clusterHeadroom").GetProperty("clusterName").GetString());

        // esx-b: the mirror image.
        var liveB = await RunOkAsync(client, hostB.Id);
        Assert.Equal(new[] { "vm-01" }, Names(liveB, "confirmedImpacts"));
        Assert.DoesNotContain("ha_disabled", RecommendationCodes(liveB));
        var snapshotB = await RunOkAsync(client, hostB.Id, snapshotId);
        Assert.Empty(Names(snapshotB, "confirmedImpacts"));

        // A host discovered after the capture is not in the snapshot.
        var notInSnapshot = await RunAsync(client, newHost.Id, snapshotId);
        Assert.Equal(HttpStatusCode.NotFound, notInSnapshot.StatusCode);
        var notInSnapshotBody = await notInSnapshot.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_in_snapshot", notInSnapshotBody.GetProperty("code").GetString());
        Assert.Contains("before-vmotion", notInSnapshotBody.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await RunAsync(client, newHost.Id)).StatusCode);

        // Unknown snapshot and unknown live target.
        var unknownSnapshot = await RunAsync(client, hostA.Id, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknownSnapshot.StatusCode);
        Assert.Equal("snapshot_not_found",
            (await unknownSnapshot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var unknownTarget = await RunAsync(client, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknownTarget.StatusCode);
        Assert.Equal("not_found",
            (await unknownTarget.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var emptySnapshotId = await RunAsync(client, hostA.Id, Guid.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptySnapshotId.StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = AdminPassword });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload.GetProperty("token").GetString()!;
    }
}
