using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// A collector reports a failed discovery run as one final batch with
/// success=false and no nodes. The server accepts exactly that kind of empty
/// batch, fails the job and marks the integration Error; any other empty batch
/// is still a 400 (see <see cref="ApiSmokeTests"/> for BERMAP_TEST_CONNECTION_STRING).
/// </summary>
public sealed class CollectorFailureBatchTests : IAsyncLifetime
{
    private const string EnrollmentToken = "integration-test-token";

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

    private async Task<BerMap.Domain.Entities.Integration> AddCollectorIntegrationAsync()
    {
        var integration = new BerMap.Domain.Entities.Integration
        {
            Name = "ad-" + Guid.NewGuid().ToString("N")[..6],
            AdapterType = "ActiveDirectory",
            IsEnabled = true,
            CollectorManaged = true,
            Status = IntegrationStatus.Active
        };
        await WithContextAsync(async context =>
        {
            context.Integrations.Add(integration);
            return await context.SaveChangesAsync();
        });
        return integration;
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

    private static DiscoveryResult FailureBatch(Guid integrationId) => new()
    {
        Success = false,
        Status = "Failed",
        IntegrationId = integrationId,
        Errors = { "SECRET_UNAVAILABLE: The secret file '/etc/bermap/ad.secret' could not be read." }
    };

    [Fact]
    public async Task FinalFailureBatchWithoutNodes_IsAccepted_FailsTheJob_AndMarksTheIntegrationError()
    {
        if (_factory == null)
            return;

        var integration = await AddCollectorIntegrationAsync();
        using var client = _factory.CreateClient();

        var response = await SubmitAsync(client, FailureBatch(integration.Id), final: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        var jobId = body.GetProperty("jobId").GetGuid();

        var job = await WithContextAsync(context => context.DiscoveryJobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
        Assert.Equal(DiscoveryJobStatus.Failed, job.Status);
        Assert.Equal("Collector", job.JobType);
        Assert.Equal(0, job.EntitiesDiscovered);
        Assert.NotNull(job.CompletedAt);
        Assert.Contains("SECRET_UNAVAILABLE", job.ErrorsJson);

        var stored = await WithContextAsync(context => context.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id));
        Assert.Equal(IntegrationStatus.Error, stored.Status);
        Assert.StartsWith("SECRET_UNAVAILABLE", stored.LastError);
        Assert.NotNull(stored.LastErrorAt);
        Assert.Equal(jobId, stored.LastJobId);
        Assert.Equal(0, await WithContextAsync(context => context.InfrastructureNodes.CountAsync()));
    }

    [Fact]
    public async Task EmptySuccessfulBatch_AndEmptyNonFinalFailureBatch_AreStillRejected()
    {
        if (_factory == null)
            return;

        var integration = await AddCollectorIntegrationAsync();
        using var client = _factory.CreateClient();

        var emptySuccess = await SubmitAsync(client, new DiscoveryResult { Success = true, IntegrationId = integration.Id }, final: true);
        Assert.Equal(HttpStatusCode.BadRequest, emptySuccess.StatusCode);

        var emptyNonFinalFailure = await SubmitAsync(client, FailureBatch(integration.Id), final: false);
        Assert.Equal(HttpStatusCode.BadRequest, emptyNonFinalFailure.StatusCode);

        Assert.Equal(0, await WithContextAsync(context =>
            context.DiscoveryJobs.CountAsync(j => j.IntegrationId == integration.Id)));
        var stored = await WithContextAsync(context => context.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id));
        Assert.Equal(IntegrationStatus.Active, stored.Status);
    }

    [Fact]
    public async Task FailureBatchClosingAHalfSentSnapshot_KeepsTheData_AndDegradesTheIntegration()
    {
        if (_factory == null)
            return;

        var integration = await AddCollectorIntegrationAsync();
        using var client = _factory.CreateClient();

        var first = await SubmitAsync(client, new DiscoveryResult
        {
            Success = true,
            IntegrationId = integration.Id,
            Nodes =
            {
                new DiscoveredNodeContract
                {
                    ExternalId = "dc-1", NodeType = "DomainController", Name = "dc-1", SourceSystem = "ActiveDirectory"
                }
            }
        }, final: false);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var jobId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();

        var closing = await SubmitAsync(client, FailureBatch(integration.Id), final: true, jobId);
        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);

        var job = await WithContextAsync(context => context.DiscoveryJobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
        Assert.Equal(DiscoveryJobStatus.PartialSuccess, job.Status);
        Assert.Equal(1, job.EntitiesDiscovered);
        var stored = await WithContextAsync(context => context.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id));
        Assert.Equal(IntegrationStatus.Degraded, stored.Status);
        Assert.Equal(1, await WithContextAsync(context => context.InfrastructureNodes.CountAsync(n => !n.IsStale)));
    }
}
