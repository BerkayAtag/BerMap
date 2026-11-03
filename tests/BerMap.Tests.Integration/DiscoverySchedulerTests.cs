using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BerMap.Tests.Integration;

/// <summary>
/// Scheduled discovery against PostgreSQL (see <see cref="ApiSmokeTests"/> for
/// BERMAP_TEST_CONNECTION_STRING). The background scheduler and discovery
/// worker are switched off so the test drives single passes itself.
/// </summary>
public sealed class DiscoverySchedulerTests : IAsyncLifetime
{
    private const string AdminPassword = "Integration-Test-Pa55";

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
            builder.UseSetting("BerMap:DiscoveryScheduler:Enabled", "false");
            builder.UseSetting("BerMap:DiscoveryScheduler:TimeZone", "UTC");
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
    public async Task InvalidSchedule_IsRejected_AndValidScheduleReportsNextRun()
    {
        if (_factory == null)
            return;

        using var client = await AuthenticatedClientAsync();

        var invalid = await client.PostAsJsonAsync("/api/integrations", IntegrationBody("61 * * * *"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("SCHEDULE_INVALID", await invalid.Content.ReadAsStringAsync());

        var created = await client.PostAsJsonAsync("/api/integrations", IntegrationBody("0 2 * * *"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var integration = await client.GetFromJsonAsync<JsonElement>($"/api/integrations/{id}");
        var nextRun = integration.GetProperty("nextScheduledRunAt").GetDateTime().ToUniversalTime();
        Assert.Equal(2, nextRun.Hour);
        Assert.Equal(0, nextRun.Minute);
        Assert.True(nextRun > DateTime.UtcNow);
        Assert.Equal("UTC", integration.GetProperty("scheduleTimeZone").GetString());
    }

    [Fact]
    public async Task ConcurrentSchedulerPasses_QueueTheDueRunOnlyOnce()
    {
        if (_factory == null)
            return;

        using var client = await AuthenticatedClientAsync();
        var created = await client.PostAsJsonAsync("/api/integrations", IntegrationBody("*/5 * * * *"));
        created.EnsureSuccessStatusCode();
        var id = Guid.Parse((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

        // Pretend the integration has existed for a day so a run is due now.
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<BerMapDbContext>();
            await context.Integrations.Where(i => i.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.CreatedAt, DateTime.UtcNow.AddDays(-1)));
        }

        // Two instances ticking at the same moment: the advisory lock lets one through.
        async Task<int> PassAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var scheduler = scope.ServiceProvider.GetRequiredService<DiscoveryScheduler>();
            return (await scheduler.RunOnceAsync(DateTime.UtcNow)).Count;
        }

        var results = await Task.WhenAll(PassAsync(), PassAsync());
        Assert.Equal(1, results.Sum());
        Assert.Equal(0, await PassAsync());

        var jobs = await client.GetFromJsonAsync<JsonElement>($"/api/integrations/jobs?integrationId={id}");
        var job = Assert.Single(jobs.EnumerateArray());
        Assert.Equal("scheduler", job.GetProperty("requestedBy").GetString());
        Assert.Equal("Queued", job.GetProperty("status").GetString());
    }

    private static object IntegrationBody(string cron) => new
    {
        name = "AD " + Guid.NewGuid().ToString("N")[..6],
        adapterType = "ActiveDirectory",
        configuration = new { server = "dc01.example.test", port = 636, username = "svc@example.test", useLdaps = true },
        secret = "not-a-real-secret",
        scheduleCron = cron,
        isEnabled = true
    };

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = AdminPassword });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
