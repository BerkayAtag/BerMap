using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BerMap.Application.Interfaces;
using BerMap.Contracts;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BerMap.Tests.Integration;

/// <summary>
/// Collector batches against PostgreSQL, whose unique (source, target, type)
/// relationship index the in-memory provider does not enforce: multi-batch
/// snapshots over edges another integration owns, adoption of orphaned edges,
/// relationship-only chunks, ingestion failures and oversized bodies (see
/// <see cref="ApiSmokeTests"/> for BERMAP_TEST_CONNECTION_STRING).
/// </summary>
public sealed class CollectorIngestionRegressionTests : IAsyncLifetime
{
    private const string AdminPassword = "Integration-Test-Pa55";
    private const string EnrollmentToken = "integration-test-token";
    private const int MaxRequestBodyBytes = 256 * 1024;

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
            builder.UseSetting("BerMap:Collectors:EnrollmentToken", EnrollmentToken);
            builder.UseSetting("BerMap:Collectors:HeartbeatTimeoutSeconds", "300");
            builder.UseSetting("BerMap:DiscoveryScheduler:Enabled", "false");
            builder.UseSetting("BerMap:DiscoveryWorker:Enabled", "false");
        });

        // Real Kestrel (not TestServer), so its request body limit applies.
        _factory.UseKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
        _factory.StartServer();
        await _factory.WaitUntilReadyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
        if (string.IsNullOrEmpty(_databaseName))
            return;

        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(_serverConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();

        if (Directory.Exists(_vaultDirectory))
            Directory.Delete(_vaultDirectory, recursive: true);
    }

    private async Task<T> WithContextAsync<T>(Func<BerMapDbContext, Task<T>> action)
    {
        using var scope = _factory!.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BerMapDbContext>());
    }

    private async Task<Guid> AddCollectorIntegrationAsync(string adapterType = "VMware")
    {
        var integration = new BerMap.Domain.Entities.Integration
        {
            Name = "vc-" + Guid.NewGuid().ToString("N")[..6],
            AdapterType = adapterType,
            IsEnabled = true,
            CollectorManaged = true,
            Status = IntegrationStatus.Active
        };
        await WithContextAsync(async context =>
        {
            context.Integrations.Add(integration);
            return await context.SaveChangesAsync();
        });
        return integration.Id;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, DiscoveryResult batch, bool final, Guid? jobId = null)
    {
        var query = $"?final={(final ? "true" : "false")}" + (jobId.HasValue ? $"&jobId={jobId.Value:D}" : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/submit-batch" + query)
        {
            Content = JsonContent.Create(batch)
        };
        request.Headers.Add("X-BerMap-Enrollment-Token", EnrollmentToken);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> JobIdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

    private static DiscoveredNodeContract Host(string prefix) => new()
    {
        ExternalId = prefix + "host-1", NodeType = "Hypervisor", Name = "esx01", FQDN = "esx01.test.local",
        BiosUuid = "4c4c4544-0000-1000-8000-00000000e501", SourceSystem = "VMware"
    };

    private static DiscoveredNodeContract Vm(string prefix, int number) => new()
    {
        ExternalId = $"{prefix}vm-{number}", NodeType = "VirtualMachine", Name = $"app{number:00}",
        FQDN = $"app{number:00}.test.local", BiosUuid = $"4c4c4544-0000-1000-8000-0000000000{number:00}",
        SourceSystem = "VMware"
    };

    private static DiscoveredRelationshipContract HostedOn(string prefix, int vm) => new()
    {
        SourceExternalId = $"{prefix}vm-{vm}",
        TargetExternalId = prefix + "host-1",
        RelationshipType = "HostedOn",
        ConfidenceLevel = "Verified",
        Evidence = "runtime.host",
        SourceSystem = "VMware"
    };

    private static DiscoveryResult Batch(Guid integrationId, IEnumerable<DiscoveredNodeContract> nodes, params DiscoveredRelationshipContract[] relationships) => new()
    {
        Success = true,
        Status = "Completed",
        IntegrationId = integrationId,
        Nodes = nodes.ToList(),
        Relationships = relationships.ToList()
    };

    [Fact]
    public async Task MultiBatchSnapshot_OverAForeignEdgeBetweenEarlierBatchNodes_CompletesInsteadOfDuplicateKey()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();
        var other = await AddCollectorIntegrationAsync();
        var collector = await AddCollectorIntegrationAsync();

        var seeded = await SubmitAsync(client, Batch(other, new[] { Host("o:"), Vm("o:", 1) }, HostedOn("o:", 1)), final: true);
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        // Batch 1 carries host and vm-1 (merged by BIOS UUID), the final batch
        // vm-2 and every relationship, including the foreign vm-1 -> host.
        var first = await SubmitAsync(client, Batch(collector, new[] { Host("c:"), Vm("c:", 1) }), final: false);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var jobId = await JobIdAsync(first);

        var final = await SubmitAsync(client,
            Batch(collector, new[] { Vm("c:", 2) }, HostedOn("c:", 1), HostedOn("c:", 2)), final: true, jobId);
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);

        var job = await WithContextAsync(context => context.DiscoveryJobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
        Assert.Equal(DiscoveryJobStatus.Completed, job.Status);
        Assert.Equal(2, await WithContextAsync(context => context.InfrastructureRelationships.CountAsync()));
        Assert.Equal(other, (await WithContextAsync(context => context.InfrastructureRelationships.AsNoTracking()
            .SingleAsync(r => r.TargetNode!.Name == "esx01" && r.SourceNode!.Name == "app01"))).IntegrationId);
    }

    [Fact]
    public async Task EdgesOfADeletedIntegration_AreAdoptedByTheRecreatedOne_AndCountForImpact()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();
        var deleted = await AddCollectorIntegrationAsync();
        Assert.Equal(HttpStatusCode.OK,
            (await SubmitAsync(client, Batch(deleted, new[] { Host("a:"), Vm("a:", 1) }, HostedOn("a:", 1)), final: true)).StatusCode);

        // What deleting the integration leaves behind.
        await WithContextAsync(async context =>
        {
            var integration = await context.Integrations.SingleAsync(i => i.Id == deleted);
            integration.DeletedAt = DateTime.UtcNow;
            integration.IsEnabled = false;
            foreach (var relationship in context.InfrastructureRelationships)
            {
                relationship.IsStale = true;
                relationship.StaleSince = DateTime.UtcNow;
            }
            return await context.SaveChangesAsync();
        });

        var recreated = await AddCollectorIntegrationAsync();
        for (var run = 0; run < 2; run++)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await SubmitAsync(client, Batch(recreated, new[] { Host("b:"), Vm("b:", 1) }, HostedOn("b:", 1)), final: true)).StatusCode);
        }

        var edge = await WithContextAsync(context => context.InfrastructureRelationships.AsNoTracking().SingleAsync());
        Assert.False(edge.IsStale);
        Assert.Equal(recreated, edge.IntegrationId);

        using var admin = await AdminClientAsync();
        var hostId = await WithContextAsync(context => context.InfrastructureNodes.Where(n => n.Name == "esx01").Select(n => n.Id).SingleAsync());
        var simulation = await admin.PostAsJsonAsync("/api/simulations/run", new { scenarioType = "host_failure", targetNodeId = hostId });
        Assert.Equal(HttpStatusCode.OK, simulation.StatusCode);
        var impact = await simulation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "app01" }, impact.GetProperty("confirmedImpacts").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToArray());
    }

    [Fact]
    public async Task RelationshipOnlyBatches_AreAccepted_AndTheFinalOneCompletesTheSnapshot()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();
        var integration = await AddCollectorIntegrationAsync();

        var nodes = await SubmitAsync(client, Batch(integration, new[] { Host("r:"), Vm("r:", 1), Vm("r:", 2) }), final: false);
        Assert.Equal(HttpStatusCode.OK, nodes.StatusCode);
        var jobId = await JobIdAsync(nodes);

        var chunk = await SubmitAsync(client, Batch(integration, Array.Empty<DiscoveredNodeContract>(), HostedOn("r:", 1)), final: false, jobId);
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        var last = await SubmitAsync(client, Batch(integration, Array.Empty<DiscoveredNodeContract>(), HostedOn("r:", 2)), final: true, jobId);
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);

        var job = await WithContextAsync(context => context.DiscoveryJobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
        Assert.Equal(DiscoveryJobStatus.Completed, job.Status);
        Assert.Equal(3, job.EntitiesDiscovered);
        Assert.Equal(2, job.RelationshipsDiscovered);
        Assert.Equal(2, await WithContextAsync(context => context.InfrastructureRelationships.CountAsync(r => !r.IsStale)));
        Assert.Equal(3, await WithContextAsync(context => context.InfrastructureNodes.CountAsync(n => !n.IsStale)));

        // A batch with neither nodes nor relationships is still rejected.
        var empty = await SubmitAsync(client, Batch(integration, Array.Empty<DiscoveredNodeContract>()), final: false);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task IngestionFailure_FailsTheJob_AndTheIntegrationHealth()
    {
        if (_factory == null)
            return;

        var integration = await AddCollectorIntegrationAsync();
        using var failing = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IDiscoveryIngestionService, FailingIngestionService>()));
        using var client = failing.CreateClient();

        var response = await SubmitAsync(client, Batch(integration, new[] { Host("f:") }), final: true);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var scope = failing.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BerMapDbContext>();
        var job = await context.DiscoveryJobs.AsNoTracking().SingleAsync(j => j.IntegrationId == integration);
        Assert.Equal(DiscoveryJobStatus.Failed, job.Status);
        Assert.Contains("INGESTION_FAILED", job.ErrorsJson);

        var stored = await context.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration);
        Assert.Equal(IntegrationStatus.Error, stored.Status);
        Assert.StartsWith("INGESTION_FAILED: simulated ingestion failure", stored.LastError);
        Assert.NotNull(stored.LastErrorAt);
        Assert.Equal(job.Id, stored.LastJobId);
    }

    [Fact]
    public async Task OversizedBatch_Is413ProblemJson_NotARetryable500()
    {
        if (_factory == null)
            return;

        using var client = _factory.CreateClient();
        var integration = await AddCollectorIntegrationAsync();
        var batch = Batch(integration, new[] { Vm("big:", 1) });
        batch.Warnings.Add(new string('x', MaxRequestBodyBytes * 2));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/submit-batch?final=true")
        {
            Content = new StringContent(JsonSerializer.Serialize(batch), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-BerMap-Enrollment-Token", EnrollmentToken);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("request_body_too_large", problem.GetProperty("code").GetString());
        Assert.Equal(413, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task CollectorStatus_HonoursTheConfiguredHeartbeatTimeout()
    {
        if (_factory == null)
            return;

        using (var anonymous = _factory.CreateClient())
        {
            var unauthorized = await anonymous.GetAsync("/api/collectors");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.Equal("application/problem+json", unauthorized.Content.Headers.ContentType?.MediaType);
        }

        using var admin = await AdminClientAsync();
        foreach (var (name, secondsAgo) in new[] { ("quiet-wan", 200), ("gone", 400) })
        {
            using var register = new HttpRequestMessage(HttpMethod.Post, "/api/collectors/register")
            {
                Content = JsonContent.Create(new { name, hostname = name + ".test.local", supportedAdapters = Array.Empty<string>() })
            };
            register.Headers.Add("X-BerMap-Enrollment-Token", EnrollmentToken);
            Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(register)).StatusCode);

            await WithContextAsync(async context =>
            {
                var collector = await context.Collectors.SingleAsync(c => c.Name == name);
                collector.LastHeartbeatAt = DateTime.UtcNow.AddSeconds(-secondsAgo);
                return await context.SaveChangesAsync();
            });
        }

        // BerMap:Collectors:HeartbeatTimeoutSeconds=300: 200 s of silence is
        // still Online (the old hard-coded 90 s said Offline), 400 s is not.
        var collectors = (await admin.GetFromJsonAsync<JsonElement>("/api/collectors")).EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        Assert.Equal("Online", collectors["quiet-wan"]);
        Assert.Equal("Offline", collectors["gone"]);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        using var anonymous = _factory!.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = AdminPassword });
        response.EnsureSuccessStatusCode();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        return client;
    }

    private sealed class FailingIngestionService : IDiscoveryIngestionService
    {
        public Task<BerMap.Application.DTOs.IngestionResult> IngestAsync(
            Guid integrationId, Guid jobId, DiscoveryResult discovery, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated ingestion failure");

        public Task<BerMap.Application.DTOs.IngestionResult> IngestAsync(
            Guid integrationId, Guid jobId, DiscoveryResult discovery, bool isCompleteSnapshot, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated ingestion failure");
    }
}
