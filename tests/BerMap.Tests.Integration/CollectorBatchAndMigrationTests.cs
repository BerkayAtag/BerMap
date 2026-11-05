using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BerMap.Application.Interfaces;
using BerMap.Contracts;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BerMap.Tests.Integration;

/// <summary>
/// Collector batch admission, the abandoned-job sweep, the impact graph query
/// and the CollectorManaged backfill against PostgreSQL (see
/// <see cref="ApiSmokeTests"/> for BERMAP_TEST_CONNECTION_STRING).
/// </summary>
public sealed class CollectorBatchAndMigrationTests : IAsyncLifetime
{
    private const string EnrollmentToken = "integration-test-token";

    private readonly string? _serverConnectionString =
        Environment.GetEnvironmentVariable("BERMAP_TEST_CONNECTION_STRING");

    private readonly List<string> _databases = new();
    private string _vaultDirectory = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
            return;

        var databaseName = NewDatabaseName();
        _vaultDirectory = Path.Combine(Path.GetTempPath(), databaseName + "-vault");
        var connection = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = databaseName };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connection.ConnectionString);
            builder.UseSetting("BerMap:CredentialVault:Directory", _vaultDirectory);
            builder.UseSetting("BerMap:Bootstrap:AdminPassword", "Integration-Test-Pa55");
            builder.UseSetting("BerMap:Collectors:EnrollmentToken", EnrollmentToken);
            builder.UseSetting("BerMap:DiscoveryScheduler:Enabled", "false");
            builder.UseSetting("BerMap:DiscoveryWorker:Enabled", "false");
        });
        await _factory.WaitUntilReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
        if (_databases.Count == 0)
            return;

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_serverConnectionString);
        await admin.OpenAsync();
        foreach (var database in _databases)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        if (Directory.Exists(_vaultDirectory))
            Directory.Delete(_vaultDirectory, recursive: true);
    }

    private string NewDatabaseName()
    {
        var name = "bermap_it_" + Guid.NewGuid().ToString("N")[..12];
        _databases.Add(name);
        return name;
    }

    private async Task<T> WithContextAsync<T>(Func<BerMapDbContext, Task<T>> action)
    {
        using var scope = _factory!.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BerMapDbContext>());
    }

    private async Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid integrationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/submit-batch?final=true")
        {
            Content = JsonContent.Create(new DiscoveryResult
            {
                Success = true,
                IntegrationId = integrationId,
                Nodes =
                {
                    new DiscoveredNodeContract
                    {
                        ExternalId = "vm-1", NodeType = "VirtualMachine", Name = "vm-1", SourceSystem = "VMware"
                    }
                }
            })
        };
        request.Headers.Add("X-BerMap-Enrollment-Token", EnrollmentToken);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task SubmitBatch_RejectsDisabledAndServerSideIntegrations_AndSweepsAbandonedJobs()
    {
        if (_factory == null)
            return;

        BerMap.Domain.Entities.Integration New(bool enabled, bool collectorManaged) => new()
        {
            Name = "vc-" + Guid.NewGuid().ToString("N")[..6],
            AdapterType = "VMware",
            IsEnabled = enabled,
            CollectorManaged = collectorManaged,
            Status = enabled ? IntegrationStatus.Active : IntegrationStatus.Inactive
        };
        var fed = New(enabled: true, collectorManaged: true);
        var disabled = New(enabled: false, collectorManaged: true);
        var serverSide = New(enabled: true, collectorManaged: false);
        var longAgo = DateTime.UtcNow.AddHours(-2);
        var abandoned = new DiscoveryJob
        {
            IntegrationId = fed.Id, JobType = "Collector", Status = DiscoveryJobStatus.Running,
            StartedAt = longAgo, UpdatedAt = longAgo
        };
        await WithContextAsync(async context =>
        {
            context.Integrations.AddRange(fed, disabled, serverSide);
            context.DiscoveryJobs.Add(abandoned);
            return await context.SaveChangesAsync();
        });

        using var client = _factory.CreateClient();

        var rejectedDisabled = await SubmitAsync(client, disabled.Id);
        Assert.Equal(HttpStatusCode.Conflict, rejectedDisabled.StatusCode);
        Assert.Equal("INTEGRATION_DISABLED",
            (await rejectedDisabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var rejectedServerSide = await SubmitAsync(client, serverSide.Id);
        Assert.Equal(HttpStatusCode.Conflict, rejectedServerSide.StatusCode);
        Assert.Equal("NOT_COLLECTOR_MANAGED",
            (await rejectedServerSide.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var accepted = await SubmitAsync(client, fed.Id);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var newJobId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        var jobs = await WithContextAsync(context => context.DiscoveryJobs.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(jobs, j => j.IntegrationId == disabled.Id || j.IntegrationId == serverSide.Id);
        Assert.Equal(DiscoveryJobStatus.Failed, jobs.Single(j => j.Id == abandoned.Id).Status);
        Assert.Equal(DiscoveryJobStatus.Completed, jobs.Single(j => j.Id == newJobId).Status);
        Assert.Equal(0, await WithContextAsync(context =>
            context.InfrastructureNodes.CountAsync(n => n.LastSeenJobId != newJobId)));
    }

    [Fact]
    public async Task ImpactGraph_TranslatesOnPostgres_AndSkipsStaleAndContainment()
    {
        if (_factory == null)
            return;

        var dc = new InfrastructureNode { Name = "DC-01", NodeType = NodeType.Datacenter };
        var cluster = new InfrastructureNode { Name = "CL-PROD", NodeType = NodeType.Cluster };
        var host = new InfrastructureNode { Name = "esx01", NodeType = NodeType.Hypervisor };
        var oldHost = new InfrastructureNode { Name = "esx00", NodeType = NodeType.Hypervisor };
        var vm = new InfrastructureNode { Name = "app-01", NodeType = NodeType.VirtualMachine };
        var sibling = new InfrastructureNode { Name = "web-01", NodeType = NodeType.VirtualMachine };
        InfrastructureRelationship Rel(InfrastructureNode source, InfrastructureNode target, RelationshipType type, bool stale = false) =>
            new() { SourceNodeId = source.Id, TargetNodeId = target.Id, RelationshipType = type, IsStale = stale };
        await WithContextAsync(async context =>
        {
            context.InfrastructureNodes.AddRange(dc, cluster, host, oldHost, vm, sibling);
            context.InfrastructureRelationships.AddRange(
                Rel(vm, host, RelationshipType.HostedOn),
                Rel(vm, oldHost, RelationshipType.HostedOn, stale: true),
                Rel(cluster, host, RelationshipType.Contains),
                Rel(dc, vm, RelationshipType.Contains),
                Rel(dc, sibling, RelationshipType.Contains));
            return await context.SaveChangesAsync();
        });

        using var scope = _factory.Services.CreateScope();
        var graph = await ((IImpactGraphSource)scope.ServiceProvider.GetRequiredService<IGraphService>())
            .GetImpactGraphAsync(vm.Id, depth: 3);

        Assert.Equal(new[] { "CL-PROD", "DC-01", "app-01", "esx01" }, graph.Nodes.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, graph.Relationships.Count);
        Assert.False(graph.IsTruncated);
    }

    [Fact]
    public async Task Migration_BackfillsCollectorManaged_ForCollectorOnlyIntegrations()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
            return;

        var connection = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = NewDatabaseName() };
        await using var context = new BerMapDbContext(new DbContextOptionsBuilder<BerMapDbContext>()
            .UseNpgsql(connection.ConnectionString, b => b.MigrationsAssembly("BerMap.Infrastructure"))
            .Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101014410_Phase3DiscoveryAndSnapshots");

        var collectorOnly = Guid.NewGuid();
        var mixed = Guid.NewGuid();
        var serverOnly = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        foreach (var id in new[] { collectorOnly, mixed, serverOnly, untouched })
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Integrations" ("Id", "Name", "Description", "AdapterType", "AdapterVersion", "Status",
                    "ConfigurationJson", "ConfigurationVersion", "CredentialReference", "LastError",
                    "DiscoveredEntitiesCount", "ScheduleCron", "TimeoutSeconds", "IsEnabled", "CreatedBy", "CreatedAt")
                VALUES ({0}, {1}, '', 'VMware', '1', 0, '{{}}', 1, '', '', 0, '0 */6 * * *', 60, TRUE, 'admin', now())
                """,
                id, "vc-" + id.ToString("N")[..6]);
        }

        async Task AddJob(Guid integrationId, string jobType) => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "DiscoveryJobs" ("Id", "IntegrationId", "JobType", "Status", "StartedAt", "EntitiesDiscovered",
                "RelationshipsDiscovered", "ProgressPercent", "CurrentPhase", "CurrentMessage", "RequestedBy",
                "ErrorsJson", "WarningsJson", "StatisticsJson", "CreatedAt")
            VALUES ({0}, {1}, {2}, 2, now(), 0, 0, 100, 'Completed', '', 'test', '[]', '[]', '{{}}', now())
            """,
            Guid.NewGuid(), integrationId, jobType);
        await AddJob(collectorOnly, "Collector");
        await AddJob(collectorOnly, "Collector");
        await AddJob(mixed, "Collector");
        await AddJob(mixed, "Full");
        await AddJob(serverOnly, "Full");

        await migrator.MigrateAsync("20261104231317_NodeStatusChangeAndCollectorManaged");

        var managed = await context.Integrations.AsNoTracking()
            .Where(i => i.CollectorManaged)
            .Select(i => i.Id)
            .ToListAsync();
        Assert.Equal(new[] { collectorOnly }, managed);
    }
}
