using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Collector.Services;
using BerMap.Contracts;
using BerMap.Integrations.ActiveDirectory;
using BerMap.Integrations.VMware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

/// <summary>
/// Collector-side discovery: BerMap:Collector:Integrations parsing, batching
/// of a run into one snapshot, failure reports, the scheduler's no-double-run
/// guarantee and keeping the secret out of logs and payloads.
/// </summary>
public class CollectorDiscoveryTests : IDisposable
{
    private const string Secret = "Sup3r-S3cret-Value!";
    private static readonly Guid IntegrationA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IntegrationB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly IIntegrationAdapter[] RealAdapters = { new ActiveDirectoryAdapter(), new VMwareAdapter() };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bermap_collector_discovery_{Guid.NewGuid():N}");

    public CollectorDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration JsonConfig(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static Dictionary<string, string?> Entry(int index, Guid id, string adapterType, params (string Key, string? Value)[] extra)
    {
        var prefix = $"BerMap:Collector:Integrations:{index}:";
        var values = new Dictionary<string, string?>
        {
            [prefix + "IntegrationId"] = id.ToString(),
            [prefix + "AdapterType"] = adapterType,
            [prefix + "SecretEnvironmentVariable"] = "BERMAP_TEST_SECRET"
        };
        foreach (var (key, value) in extra)
            values[prefix + key] = value;
        return values;
    }

    [Fact]
    public void Configuration_ValidEntry_UsesDefaults_AndTypesAdapterConfigurationFromSchema()
    {
        var configuration = JsonConfig($$"""
        {
          "BerMap": { "Collector": { "Integrations": [ {
            "IntegrationId": "{{IntegrationA}}",
            "AdapterType": "activedirectory",
            "SecretFile": "secret.txt",
            "Configuration": {
              "server": "dc01.corp.local",
              "port": 636,
              "useSsl": true,
              "username": "12345",
              "additionalDomains": [ { "server": "dc02.other.local", "port": 3269, "useSsl": true } ]
            }
          } ] } }
        }
        """);

        var set = CollectorIntegrationConfiguration.Load(configuration, RealAdapters);

        Assert.Empty(set.Errors);
        var integration = Assert.Single(set.Integrations);
        Assert.Equal(IntegrationA, integration.IntegrationId);
        Assert.Equal("ActiveDirectory", integration.AdapterType);
        Assert.Equal(CollectorIntegration.DefaultSchedule, integration.Schedule.Expression);
        Assert.True(integration.RunOnStart);
        Assert.Equal(TimeSpan.FromSeconds(1800), integration.Timeout);
        Assert.Equal(Path.GetFullPath("secret.txt"), integration.SecretFile);
        Assert.Null(integration.SecretEnvironmentVariable);
        Assert.Equal(new[] { "ActiveDirectory" }, set.AdapterTypes);

        // Typed exactly as the wizard stores it: numbers, booleans, arrays, and a
        // numeric-looking username stays a string because the schema says so.
        var json = JsonDocument.Parse(integration.ConfigurationJson).RootElement;
        Assert.Equal(JsonValueKind.Number, json.GetProperty("port").ValueKind);
        Assert.Equal(636, json.GetProperty("port").GetInt32());
        Assert.Equal(JsonValueKind.True, json.GetProperty("useSsl").ValueKind);
        Assert.Equal("12345", json.GetProperty("username").GetString());
        var domain = Assert.Single(json.GetProperty("additionalDomains").EnumerateArray().ToList());
        Assert.Equal(3269, domain.GetProperty("port").GetInt32());
        Assert.Equal("dc02.other.local", domain.GetProperty("server").GetString());
    }

    [Fact]
    public void Configuration_ExplicitValues_AndJsonStringConfiguration_AreAccepted()
    {
        var values = Entry(0, IntegrationB, "VMware",
            ("Schedule", "*/15 * * * *"), ("RunOnStart", "false"), ("TimeoutSeconds", "600"),
            ("Configuration", """{"server":"vc.corp.local","username":"svc","port":443}"""));

        var set = CollectorIntegrationConfiguration.Load(Config(values), RealAdapters);

        var integration = Assert.Single(set.Integrations);
        Assert.Equal("*/15 * * * *", integration.Schedule.Expression);
        Assert.False(integration.RunOnStart);
        Assert.Equal(TimeSpan.FromMinutes(10), integration.Timeout);
        Assert.Equal("BERMAP_TEST_SECRET", integration.SecretEnvironmentVariable);
        Assert.Equal(443, JsonDocument.Parse(integration.ConfigurationJson).RootElement.GetProperty("port").GetInt32());
    }

    [Theory]
    [InlineData("Schedule", "every hour", "Schedule 'every hour' is not a valid cron expression")]
    [InlineData("Schedule", "61 * * * *", "minute")]
    [InlineData("AdapterType", "Hyper-V", "AdapterType 'Hyper-V' is not supported")]
    [InlineData("TimeoutSeconds", "5", "TimeoutSeconds must be")]
    [InlineData("RunOnStart", "sometimes", "RunOnStart must be true or false")]
    [InlineData("IntegrationId", "not-a-guid", "IntegrationId must be")]
    public void Configuration_InvalidField_RejectsTheEntryWithAClearMessage(string key, string value, string expected)
    {
        var values = Entry(0, IntegrationA, "ActiveDirectory", (key, value));

        var set = CollectorIntegrationConfiguration.Load(Config(values), RealAdapters);

        Assert.Empty(set.Integrations);
        var error = Assert.Single(set.Errors);
        Assert.Contains(expected, error.Message);
        Assert.Equal(0, error.Index);
    }

    [Fact]
    public void Configuration_BothOrNeitherSecretSource_IsRejected()
    {
        var both = Entry(0, IntegrationA, "ActiveDirectory", ("SecretFile", "/etc/bermap/ad.secret"));
        var neither = Entry(0, IntegrationB, "VMware", ("SecretEnvironmentVariable", null));

        var bothSet = CollectorIntegrationConfiguration.Load(Config(both), RealAdapters);
        var neitherSet = CollectorIntegrationConfiguration.Load(Config(neither), RealAdapters);

        Assert.Contains("not both", Assert.Single(bothSet.Errors).Message);
        Assert.Contains("A secret source is required", Assert.Single(neitherSet.Errors).Message);
        Assert.Equal(IntegrationB, neitherSet.Errors[0].IntegrationId);
    }

    [Fact]
    public void Configuration_InlinePassword_AndDuplicateIds_AreRejected_OtherEntriesStillLoad()
    {
        var values = Entry(0, IntegrationA, "ActiveDirectory",
            ("Configuration:server", "dc01"), ("Configuration:Password", Secret));
        foreach (var pair in Entry(1, IntegrationB, "VMware"))
            values[pair.Key] = pair.Value;
        foreach (var pair in Entry(2, IntegrationB, "VMware"))
            values[pair.Key] = pair.Value;

        var set = CollectorIntegrationConfiguration.Load(Config(values), RealAdapters);

        Assert.Equal(IntegrationB, Assert.Single(set.Integrations).IntegrationId);
        Assert.Equal(2, set.Errors.Count);
        Assert.Contains("must not contain 'password'", set.Errors[0].Message);
        Assert.DoesNotContain(Secret, set.Errors[0].Message);
        Assert.Contains("more than once", set.Errors[1].Message);
        Assert.Equal(new[] { "ActiveDirectory", "VMware" }, set.AdapterTypes);
    }

    [Fact]
    public void Options_BatchSizeDefaults_AndAdvertisedAdaptersMergeConfiguredTypes()
    {
        var options = CollectorOptions.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["BerMap:Collector:DataDirectory"] = _dir,
            ["BerMap:Collector:SupportedAdapters:0"] = "vmware"
        }));
        Assert.Equal(CollectorOptions.DefaultBatchSize, options.BatchSize);

        options.AdvertiseAdapters(new[] { "VMware", "ActiveDirectory" });
        Assert.Equal(new[] { "vmware", "ActiveDirectory" }, options.SupportedAdapters);

        var sized = CollectorOptions.FromConfiguration(Config(new Dictionary<string, string?>
        {
            ["BerMap:Collector:DataDirectory"] = _dir,
            ["BerMap:Collector:BatchSize"] = "0"
        }));
        Assert.Equal(CollectorOptions.DefaultBatchSize, sized.BatchSize);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void ShippedSettings_Load_WithAnEmptyIntegrationList(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "BerMap.Collector", fileName)))
            directory = directory.Parent;
        Assert.NotNull(directory);

        // The development file documents an example in comments, which the JSON provider skips.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory!.FullName, "src", "BerMap.Collector", fileName), optional: false)
            .Build();

        var set = CollectorIntegrationConfiguration.Load(configuration, RealAdapters);
        Assert.Empty(set.Integrations);
        Assert.Empty(set.Errors);
        Assert.False(string.IsNullOrWhiteSpace(configuration["BerMap:ServerUrl"]));
    }

    // ------------------------------------------------------------------
    // Batching and failure reports
    // ------------------------------------------------------------------

    private static DiscoveryResult Result(int nodes, int relationships, bool success = true) => new()
    {
        Success = success,
        Status = success ? "Completed" : "PartialSuccess",
        SourceVersion = "8.0",
        Nodes = Enumerable.Range(0, nodes)
            .Select(i => new DiscoveredNodeContract { ExternalId = $"n{i}", NodeType = "Server", Name = $"n{i}", SourceSystem = "VMware" })
            .ToList(),
        Relationships = Enumerable.Range(0, relationships)
            .Select(i => new DiscoveredRelationshipContract { SourceExternalId = $"n{i}", TargetExternalId = "n0", RelationshipType = "RunsOn" })
            .ToList(),
        Errors = success ? new List<string>() : new List<string> { "cluster-2 unreachable" },
        Warnings = new List<string> { "one warning" },
        Statistics = new Dictionary<string, int> { ["VirtualMachines"] = nodes }
    };

    [Fact]
    public void BuildBatches_ChunksNodes_RelationshipsAndOutcomeOnlyInTheFinalBatch()
    {
        var batches = CollectorDiscoveryRunner.BuildBatches(Result(1201, 30, success: false), IntegrationA, 500);

        Assert.Equal(new[] { 500, 500, 201 }, batches.Select(b => b.Nodes.Count));
        Assert.All(batches, b => Assert.Equal(IntegrationA, b.IntegrationId));
        Assert.Equal(1201, batches.SelectMany(b => b.Nodes).Select(n => n.ExternalId).Distinct().Count());

        foreach (var early in batches.Take(2))
        {
            Assert.True(early.Success);
            Assert.Empty(early.Relationships);
            Assert.Empty(early.Errors);
            Assert.Empty(early.Warnings);
            Assert.Empty(early.Statistics);
        }

        var final = batches[^1];
        Assert.False(final.Success);
        Assert.Equal("PartialSuccess", final.Status);
        Assert.Equal(30, final.Relationships.Count);
        Assert.Equal(new[] { "cluster-2 unreachable" }, final.Errors);
        Assert.Equal(new[] { "one warning" }, final.Warnings);
        Assert.Equal(1201, final.Statistics["VirtualMachines"]);
    }

    [Fact]
    public void BuildBatches_SmallResult_IsOneFinalBatch()
    {
        var batches = CollectorDiscoveryRunner.BuildBatches(Result(3, 2), IntegrationA, 500);

        var only = Assert.Single(batches);
        Assert.True(only.Success);
        Assert.Equal("Completed", only.Status);
        Assert.Equal(3, only.Nodes.Count);
        Assert.Equal(2, only.Relationships.Count);
    }

    [Fact]
    public async Task Run_SendsOneSnapshot_SequencedBatches_FinalOnlyOnTheLast()
    {
        var client = new RecordingClient();
        var runner = Runner(new FakeAdapter((_, _) => Task.FromResult(Result(7, 4))), client, batchSize: 3);

        var outcome = await runner.RunAsync(Integration(IntegrationA), CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Completed, outcome.Status);
        Assert.Equal(3, outcome.Batches);
        Assert.Equal(new[] { 0, 1, 2 }, client.Calls.Select(c => c.Sequence));
        Assert.Equal(new[] { false, false, true }, client.Calls.Select(c => c.Final));
        Assert.Single(client.Calls.Select(c => c.SnapshotId).Distinct());
        Assert.Equal(new[] { 3, 3, 1 }, client.Calls.Select(c => c.Batch.Nodes.Count));
        Assert.Equal(4, client.Calls[^1].Batch.Relationships.Count);
    }

    [Fact]
    public async Task Run_ThroughCollectorClient_LaterBatchesJoinTheJobOpenedByTheFirst()
    {
        var jobId = Guid.NewGuid();
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"jobId\":\"{jobId}\"}}")
            }
        };
        var options = new CollectorOptions { DataDirectory = _dir, EnrollmentToken = "t", BatchSize = 2 };
        var client = new CollectorClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://bermap.test") },
            new LocalBufferQueue(options.OfflineQueueDirectory, 10),
            NullLogger<CollectorClient>.Instance, options);
        var runner = new CollectorDiscoveryRunner(
            new[] { new FakeAdapter((_, _) => Task.FromResult(Result(5, 1))) }, client, new FixedSecret(),
            options, NullLogger<CollectorDiscoveryRunner>.Instance);

        await runner.RunAsync(Integration(IntegrationA), CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain("jobId=", handler.Requests[0].Query);
        Assert.Contains("final=false", handler.Requests[0].Query);
        Assert.Contains($"jobId={jobId}", handler.Requests[1].Query);
        Assert.Contains($"jobId={jobId}", handler.Requests[2].Query);
        Assert.Contains("final=true", handler.Requests[2].Query);
        // The snapshot is closed, so nothing is left in the persisted open-snapshot list.
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dir, "snapshot-jobs.json")));
    }

    [Fact]
    public async Task Client_LaterBatchWithoutAKnownJob_IsRejectedLocally_AndTheJobMapSurvivesARestart()
    {
        var jobId = Guid.NewGuid();
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"jobId\":\"{jobId}\"}}") }
        };
        var options = new CollectorOptions { DataDirectory = _dir, EnrollmentToken = "t" };
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10);
        CollectorClient NewClient() => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://bermap.test") }, queue,
            NullLogger<CollectorClient>.Instance, options);

        var orphan = await NewClient().SubmitSnapshotBatchAsync(Result(1, 0), Guid.NewGuid(), 1, final: true);
        Assert.Equal(BatchDelivery.Rejected, orphan);
        Assert.Empty(handler.Requests);

        var snapshot = Guid.NewGuid();
        Assert.Equal(BatchDelivery.Delivered, await NewClient().SubmitSnapshotBatchAsync(Result(1, 0), snapshot, 0, final: false));
        // A new client instance (service restart) still knows the open job.
        Assert.Equal(BatchDelivery.Delivered, await NewClient().SubmitSnapshotBatchAsync(Result(1, 0), snapshot, 1, final: true));
        Assert.Contains($"jobId={jobId}", handler.Requests[1].Query);
    }

    [Fact]
    public async Task Run_AdapterThrows_ReportsOneFinalFailureBatchWithoutNodes()
    {
        var client = new RecordingClient();
        var runner = Runner(new FakeAdapter((_, _) => throw new InvalidOperationException("LDAP server unavailable")), client);

        var outcome = await runner.RunAsync(Integration(IntegrationA), CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Failed, outcome.Status);
        var call = Assert.Single(client.Calls);
        Assert.True(call.Final);
        Assert.Equal(0, call.Sequence);
        Assert.False(call.Batch.Success);
        Assert.Empty(call.Batch.Nodes);
        Assert.Equal(IntegrationA, call.Batch.IntegrationId);
        Assert.Equal("DISCOVERY_FAILED: LDAP server unavailable", Assert.Single(call.Batch.Errors));
    }

    [Fact]
    public async Task Run_AdapterReturnsFailureWithoutNodes_ReportsItsErrors()
    {
        var client = new RecordingClient();
        var failed = new DiscoveryResult { Success = false, Status = "Failed", Errors = { "Invalid credentials" }, Warnings = { "w" } };
        var runner = Runner(new FakeAdapter((_, _) => Task.FromResult(failed)), client);

        await runner.RunAsync(Integration(IntegrationA), CancellationToken.None);

        var batch = Assert.Single(client.Calls).Batch;
        Assert.False(batch.Success);
        Assert.Empty(batch.Nodes);
        Assert.Equal(new[] { "DISCOVERY_FAILED: Invalid credentials", "Invalid credentials" }, batch.Errors);
        Assert.Equal(new[] { "w" }, batch.Warnings);
    }

    [Fact]
    public async Task Run_AdapterConfigurationErrorAndTimeout_AreReportedWithTheirCodes()
    {
        var client = new RecordingClient();
        await Runner(new FakeAdapter((_, _) => throw new AdapterConfigurationException("AD_SERVER", "server is required.")), client)
            .RunAsync(Integration(IntegrationA), CancellationToken.None);
        Assert.Equal("AD_SERVER: server is required.", client.Calls[^1].Batch.Errors[0]);

        var slow = new FakeAdapter(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Result(1, 0);
        });
        var timedOut = new CollectorIntegration
        {
            IntegrationId = IntegrationA, AdapterType = FakeAdapter.Type, Timeout = TimeSpan.FromMilliseconds(50),
            SecretEnvironmentVariable = "X"
        };
        var outcome = await Runner(slow, client).RunAsync(timedOut, CancellationToken.None);
        Assert.Equal(DiscoveryRunStatus.Failed, outcome.Status);
        Assert.StartsWith("DISCOVERY_TIMEOUT:", client.Calls[^1].Batch.Errors[0]);
    }

    [Fact]
    public async Task Run_CancelledByShutdown_ReportsNothing()
    {
        var client = new RecordingClient();
        using var cts = new CancellationTokenSource();
        var adapter = new FakeAdapter(async (_, token) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Result(1, 0);
        });

        var outcome = await Runner(adapter, client).RunAsync(Integration(IntegrationA), cts.Token);

        Assert.Equal(DiscoveryRunStatus.Cancelled, outcome.Status);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Run_MissingSecretFile_FailsTheRunWithAClearError_WithoutCallingTheAdapter()
    {
        var client = new RecordingClient();
        var called = false;
        var adapter = new FakeAdapter((_, _) => { called = true; return Task.FromResult(Result(1, 0)); });
        var runner = new CollectorDiscoveryRunner(new[] { adapter }, client, new IntegrationSecretReader(),
            new CollectorOptions { DataDirectory = _dir }, NullLogger<CollectorDiscoveryRunner>.Instance);
        var missing = Path.Combine(_dir, "missing.secret");

        await runner.RunAsync(new CollectorIntegration
        {
            IntegrationId = IntegrationA, AdapterType = FakeAdapter.Type, SecretFile = missing
        }, CancellationToken.None);

        Assert.False(called);
        var error = Assert.Single(Assert.Single(client.Calls).Batch.Errors);
        Assert.StartsWith("SECRET_UNAVAILABLE: The secret file", error);
        Assert.Contains(missing, error);
    }

    [Fact]
    public async Task SecretReader_ReadsFileAndVariable_TrimsTheLineBreak_RejectsEmpty()
    {
        var reader = new IntegrationSecretReader();
        var file = Path.Combine(_dir, "ad.secret");
        await File.WriteAllTextAsync(file, Secret + "\r\n");
        Assert.Equal(Secret, await reader.ReadSecretAsync(new CollectorIntegration { SecretFile = file }));

        var variable = "BERMAP_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, Secret);
        try
        {
            Assert.Equal(Secret, await reader.ReadSecretAsync(new CollectorIntegration { SecretEnvironmentVariable = variable }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        var unset = await Assert.ThrowsAsync<CollectorSecretException>(() =>
            reader.ReadSecretAsync(new CollectorIntegration { SecretEnvironmentVariable = variable }));
        Assert.Contains("is not set", unset.Message);

        await File.WriteAllTextAsync(file, "\n");
        var empty = await Assert.ThrowsAsync<CollectorSecretException>(() =>
            reader.ReadSecretAsync(new CollectorIntegration { SecretFile = file }));
        Assert.Contains("is empty", empty.Message);
    }

    [Fact]
    public async Task Secret_NeverAppearsInLogsOrSubmittedBatches()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var client = new RecordingClient();
        var file = Path.Combine(_dir, "vc.secret");
        await File.WriteAllTextAsync(file, Secret);

        string? seenSecret = null;
        var results = new Queue<Func<AdapterExecutionContext, DiscoveryResult>>(new Func<AdapterExecutionContext, DiscoveryResult>[]
        {
            context => { seenSecret = context.Secret; return Result(4, 1); },
            _ => throw new InvalidOperationException("authentication failed"),
            _ => new DiscoveryResult { Success = false, Errors = { "denied" } }
        });
        var adapter = new FakeAdapter((context, _) => Task.FromResult(results.Dequeue()(context)));
        var runner = new CollectorDiscoveryRunner(new[] { adapter }, client, new IntegrationSecretReader(),
            new CollectorOptions { DataDirectory = _dir, BatchSize = 2 }, factory.CreateLogger<CollectorDiscoveryRunner>());
        var integration = new CollectorIntegration { IntegrationId = IntegrationA, AdapterType = FakeAdapter.Type, SecretFile = file };

        for (var i = 0; i < 3; i++)
            await runner.RunAsync(integration, CancellationToken.None);

        Assert.Equal(Secret, seenSecret);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(client.Calls, c => JsonSerializer.Serialize(c.Batch).Contains(Secret, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Scheduler
    // ------------------------------------------------------------------

    [Fact]
    public async Task Scheduler_NeverRunsTheSameIntegrationTwiceAtOnce()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 10, 0, 30, TimeSpan.Zero));
        var runner = new BlockingRunner();
        var scheduler = Scheduler(runner, clock, Integration(IntegrationA, "* * * * *", runOnStart: true));

        Assert.Equal(new[] { IntegrationA }, scheduler.Tick(CancellationToken.None));
        await runner.WaitForActiveAsync(1);

        // The next occurrences fall while the first run is still busy: skipped, not queued.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(scheduler.Tick(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(scheduler.Tick(CancellationToken.None));
        Assert.Equal(1, runner.Started);

        runner.ReleaseAll();
        await scheduler.WhenIdleAsync();
        Assert.Empty(scheduler.RunningIntegrations);
        runner.Rearm();

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new[] { IntegrationA }, scheduler.Tick(CancellationToken.None));
        await runner.WaitForActiveAsync(1);
        Assert.Equal(2, runner.Started);
        Assert.Equal(1, runner.MaxConcurrentPerIntegration);

        runner.ReleaseAll();
        await scheduler.WhenIdleAsync();
    }

    [Fact]
    public async Task Scheduler_RunsAtMostTwoIntegrationsInParallel()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        var runner = new BlockingRunner();
        var third = Guid.NewGuid();
        var scheduler = Scheduler(runner, clock,
            Integration(IntegrationA), Integration(IntegrationB), Integration(third));

        Assert.Equal(3, scheduler.Tick(CancellationToken.None).Count);
        await runner.WaitForActiveAsync(2);
        await Task.Delay(100);
        Assert.Equal(2, runner.Active);
        Assert.Equal(3, scheduler.RunningIntegrations.Count);

        runner.ReleaseAll();
        await scheduler.WhenIdleAsync();
        Assert.Equal(3, runner.Started);
        Assert.Equal(CollectorDiscoveryScheduler.MaxParallelRuns, runner.MaxActive);
    }

    [Fact]
    public async Task Scheduler_WithoutRunOnStart_WaitsForTheNextCronOccurrence()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 10, 7, 0, TimeSpan.Zero));
        var runner = new BlockingRunner();
        var scheduler = Scheduler(runner, clock, Integration(IntegrationA, "0 */6 * * *", runOnStart: false));

        Assert.Empty(scheduler.Tick(CancellationToken.None));
        Assert.Equal(new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc), scheduler.GetNextRunUtc(IntegrationA));

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Single(scheduler.Tick(CancellationToken.None));
        Assert.Equal(new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc), scheduler.GetNextRunUtc(IntegrationA));
        runner.ReleaseAll();
        await scheduler.WhenIdleAsync();
    }

    [Fact]
    public async Task Scheduler_ReportsInvalidEntriesToTheServerOnce()
    {
        var client = new RecordingClient();
        var set = new CollectorIntegrationSet
        {
            Errors = new[]
            {
                new CollectorIntegrationError(0, IntegrationA, "Schedule 'x' is not a valid cron expression."),
                new CollectorIntegrationError(1, null, "IntegrationId must be ...")
            }
        };
        var scheduler = new CollectorDiscoveryScheduler(set, new BlockingRunner(), client,
            NullLogger<CollectorDiscoveryScheduler>.Instance, new ManualClock(DateTimeOffset.UtcNow), TimeZoneInfo.Utc);

        await scheduler.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.Calls.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await scheduler.StopAsync(CancellationToken.None);

        var call = Assert.Single(client.Calls);
        Assert.True(call.Final);
        Assert.False(call.Batch.Success);
        Assert.Equal(IntegrationA, call.Batch.IntegrationId);
        Assert.StartsWith("COLLECTOR_CONFIG_INVALID: Schedule 'x'", call.Batch.Errors[0]);
    }

    // ------------------------------------------------------------------
    // Helpers and fakes
    // ------------------------------------------------------------------

    private static CollectorIntegration Integration(Guid id, string schedule = "0 */6 * * *", bool runOnStart = true) => new()
    {
        IntegrationId = id,
        AdapterType = FakeAdapter.Type,
        Schedule = BerMap.Application.Services.Scheduling.CronSchedule.Parse(schedule),
        RunOnStart = runOnStart,
        SecretEnvironmentVariable = "BERMAP_TEST_SECRET"
    };

    private CollectorDiscoveryRunner Runner(IIntegrationAdapter adapter, ICollectorClient client, int batchSize = 500) =>
        new(new[] { adapter }, client, new FixedSecret(), new CollectorOptions { DataDirectory = _dir, BatchSize = batchSize },
            NullLogger<CollectorDiscoveryRunner>.Instance);

    private static CollectorDiscoveryScheduler Scheduler(ICollectorDiscoveryRunner runner, TimeProvider clock, params CollectorIntegration[] integrations) =>
        new(new CollectorIntegrationSet { Integrations = integrations }, runner, new RecordingClient(),
            NullLogger<CollectorDiscoveryScheduler>.Instance, clock, TimeZoneInfo.Utc);

    private sealed class FixedSecret : IIntegrationSecretReader
    {
        public Task<string> ReadSecretAsync(CollectorIntegration integration, CancellationToken cancellationToken = default) =>
            Task.FromResult(Secret);
    }

    private sealed class FakeAdapter : IIntegrationAdapter
    {
        public const string Type = "Fake";
        private readonly Func<AdapterExecutionContext, CancellationToken, Task<DiscoveryResult>> _discover;

        public FakeAdapter(Func<AdapterExecutionContext, CancellationToken, Task<DiscoveryResult>> discover) => _discover = discover;

        public string AdapterType => Type;
        public string DisplayName => "Fake";
        public string Version => "1.0";
        public AdapterCapabilities GetCapabilities() => new() { MaxConcurrency = 4 };
        public AdapterConfigurationSchema GetConfigurationSchema() => new();

        public Task<ConnectionTestResult> TestConnectionAsync(AdapterExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionTestResult { Success = true });

        public Task<DiscoveryResult> DiscoverAsync(AdapterExecutionContext context, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(new DiscoveryProgress { Phase = "Connecting", Message = "connecting", Percent = 5 });
            return _discover(context, cancellationToken);
        }
    }

    private sealed record SubmitCall(DiscoveryResult Batch, Guid SnapshotId, int Sequence, bool Final);

    private sealed class RecordingClient : ICollectorClient
    {
        public List<SubmitCall> Calls { get; } = new();
        public bool IsRegistered => true;
        public Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SendHeartbeatAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> DrainOfflineQueueAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> SubmitDiscoveryBatchAsync(DiscoveryResult batch, Guid? snapshotId = null, bool final = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The runner submits sequenced snapshot batches.");

        public Task<BatchDelivery> SubmitSnapshotBatchAsync(DiscoveryResult batch, Guid snapshotId, int sequence, bool final, CancellationToken cancellationToken = default)
        {
            lock (Calls)
                Calls.Add(new SubmitCall(batch, snapshotId, sequence, final));
            return Task.FromResult(BatchDelivery.Delivered);
        }
    }

    /// <summary>
    /// Runs block until <see cref="ReleaseAll"/>; after it they pass straight
    /// through until <see cref="Rearm"/>. Tracks concurrency overall and per integration.
    /// </summary>
    private sealed class BlockingRunner : ICollectorDiscoveryRunner
    {
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<Guid, int> _perIntegration = new();
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started { get; private set; }
        public int Active { get; private set; }
        public int MaxActive { get; private set; }
        public int MaxConcurrentPerIntegration { get; private set; }

        public async Task<DiscoveryRunOutcome> RunAsync(CollectorIntegration integration, CancellationToken cancellationToken)
        {
            Task release;
            lock (_lock)
            {
                Started++;
                Active++;
                MaxActive = Math.Max(MaxActive, Active);
                var mine = _perIntegration.AddOrUpdate(integration.IntegrationId, 1, (_, n) => n + 1);
                MaxConcurrentPerIntegration = Math.Max(MaxConcurrentPerIntegration, mine);
                release = _release.Task;
            }

            try
            {
                await release.WaitAsync(cancellationToken);
                return new DiscoveryRunOutcome(DiscoveryRunStatus.Completed, 0, 0, 0);
            }
            finally
            {
                lock (_lock)
                {
                    Active--;
                    _perIntegration.AddOrUpdate(integration.IntegrationId, 0, (_, n) => n - 1);
                }
            }
        }

        public void ReleaseAll()
        {
            lock (_lock)
                _release.TrySetResult();
        }

        public void Rearm()
        {
            lock (_lock)
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitForActiveAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (_lock)
                {
                    if (Active >= count)
                        return;
                }
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Expected {count} active runs.");
                await Task.Delay(10);
            }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;
            public CapturingLogger(ConcurrentQueue<string> messages) => _messages = messages;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _messages.Enqueue(formatter(state, exception) + (exception != null ? " " + exception : string.Empty));
        }
    }
}
