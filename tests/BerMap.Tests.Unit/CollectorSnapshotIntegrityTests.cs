using System;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BerMap.Tests.Unit;

/// <summary>
/// A collector snapshot reaches the server complete or is reported as failed, never as a
/// complete snapshot with holes: bounded batches (relationships chunked, size-limited), 413 and
/// other refusals, offline-queue evictions, dead-letters and sequence gaps close the job with a
/// failure report; a stop during an upload buffers the rest; a server URL path prefix is kept.
/// </summary>
public class CollectorSnapshotIntegrityTests : IDisposable
{
    private static readonly Guid Integration = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bermap_collector_integrity_{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();

    public CollectorSnapshotIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------
    // Batching
    // ------------------------------------------------------------------

    [Fact]
    public void BuildBatches_ChunksRelationshipsAfterTheNodes_OnlyTheLastIsFinal()
    {
        var result = Result(1200, 12000);
        var batches = CollectorDiscoveryRunner.BuildBatches(result, Integration, batchSize: 500, relationshipBatchSize: 5000);

        Assert.Equal(new[] { 500, 500, 200, 0, 0 }, batches.Select(b => b.Nodes.Count));
        // The last node batch is topped up with relationships, the rest is chunked.
        Assert.Equal(new[] { 0, 0, 5000, 5000, 2000 }, batches.Select(b => b.Relationships.Count));
        Assert.Equal(12000, batches.SelectMany(b => b.Relationships).Select(r => r.SourceExternalId).Distinct().Count());

        // Every node is sent before the first relationship that might refer to it.
        var lastNodeBatch = batches.Select((b, i) => (b, i)).Last(x => x.b.Nodes.Count > 0).i;
        var firstRelationshipBatch = batches.Select((b, i) => (b, i)).First(x => x.b.Relationships.Count > 0).i;
        Assert.True(lastNodeBatch <= firstRelationshipBatch);

        foreach (var early in batches.Take(batches.Count - 1))
        {
            Assert.True(early.Success);
            Assert.Equal("InProgress", early.Status);
            Assert.Empty(early.Errors);
            Assert.Empty(early.Statistics);
        }
        Assert.Equal("Completed", batches[^1].Status);
        Assert.Equal(1200, batches[^1].Statistics["VirtualMachines"]);
    }

    [Fact]
    public void BuildBatches_BoundsEveryRequestBySerializedSize()
    {
        // 20,000 nodes and 180,000 relationships with ~1 KB of evidence each: a single final batch
        // of these would be far beyond the server's 30 MB request limit.
        var result = Result(20000, 180000, evidenceBytes: 1000);
        const long limit = 8L * 1024 * 1024;

        var batches = CollectorDiscoveryRunner.BuildBatches(result, Integration, batchSize: 10000, relationshipBatchSize: 50000, maxBatchBytes: limit);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var batch in batches)
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(batch, options).LongLength, 1, limit);
        Assert.Equal(20000, batches.Sum(b => b.Nodes.Count));
        Assert.Equal(180000, batches.Sum(b => b.Relationships.Count));
        Assert.True(batches.Count > 20);
    }

    [Fact]
    public void BuildBatches_AnItemLargerThanTheLimit_TravelsAlone()
    {
        var result = Result(3, 0);
        result.Nodes[1].Properties["blob"] = new string('x', 20000);

        var batches = CollectorDiscoveryRunner.BuildBatches(result, Integration, 500, 5000, maxBatchBytes: 10000);

        Assert.Equal(new[] { 1, 1, 1 }, batches.Select(b => b.Nodes.Count));
        Assert.Equal("n1", batches[1].Nodes[0].ExternalId);
    }

    [Fact]
    public async Task Run_LargeResult_IsDeliveredAsOneJob_WithBoundedRelationshipBatches()
    {
        var options = Options(batchSize: 100, relationshipBatchSize: 400);
        var runner = Runner(Result(250, 1000), Client(options), options);

        var outcome = await runner.RunAsync(CollectorIntegrationFor(), CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Completed, outcome.Status);
        // 100, 100, 50 nodes (+400 relationships), 400, 200 relationships.
        Assert.Equal(5, _server.Requests.Count);
        Assert.Single(_server.Requests, r => r.JobId == null);
        Assert.All(_server.Requests.Skip(1), r => Assert.Equal(_server.Requests[0].OpenedJob, r.JobId));
        Assert.Equal(new[] { false, false, false, false, true }, _server.Requests.Select(r => r.Final));
        Assert.Equal(250, _server.Requests.Sum(r => r.Nodes));
        Assert.Equal(1000, _server.Requests.Sum(r => r.Relationships));
        Assert.All(_server.Requests, r => Assert.True(r.Relationships <= 400 && r.Nodes <= 100));
    }

    // ------------------------------------------------------------------
    // Refusals (413 and other 4xx)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_FinalBatchRefusedWith413_ClosesTheJobWithAFailureReport()
    {
        var options = Options(batchSize: 2);
        _server.Respond = r => r.Final && r.Success ? Status(HttpStatusCode.RequestEntityTooLarge, "{\"message\":\"Request body too large.\"}") : null;
        var runner = Runner(Result(5, 3), Client(options), options);

        var outcome = await runner.RunAsync(CollectorIntegrationFor(), CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Rejected, outcome.Status);
        var report = _server.Requests[^1];
        Assert.True(report.Final);
        Assert.False(report.Success);
        Assert.Equal(_server.Requests[0].OpenedJob, report.JobId);
        Assert.Equal(0, report.Nodes);
        Assert.StartsWith("COLLECTOR_BATCH_REJECTED: The server refused batch 3 of the snapshot (413: Request body too large.)", Assert.Single(report.Errors));
        // Nothing reached the server as a complete snapshot.
        Assert.DoesNotContain(_server.Requests, r => r.Final && r.Success && r.Status == 200);
        Assert.Equal(0, _server.Queue(options).GetQueueDepth(CollectorClient.BatchQueue));
    }

    [Fact]
    public async Task Run_MiddleBatchRefused_StopsTheUpload_AndFailsTheJob()
    {
        var options = Options(batchSize: 2);
        var calls = 0;
        _server.Respond = r => ++calls == 2 ? Status(HttpStatusCode.RequestEntityTooLarge) : null;
        var runner = Runner(Result(7, 0), Client(options), options);

        var outcome = await runner.RunAsync(CollectorIntegrationFor(), CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Rejected, outcome.Status);
        Assert.Equal(3, _server.Requests.Count); // batch 1, refused batch 2, failure report
        Assert.False(_server.Requests[2].Success);
        Assert.Equal(_server.Requests[0].OpenedJob, _server.Requests[2].JobId);
        Assert.Contains("refused batch 2", _server.Requests[2].Errors[0]);
    }

    [Fact]
    public async Task Run_FirstBatchRefused_ReportsTheFailureOnAJobOfItsOwn()
    {
        var options = Options(batchSize: 2);
        var calls = 0;
        _server.Respond = r => ++calls == 1 ? Status(HttpStatusCode.BadRequest, "{\"message\":\"bad node\"}") : null;

        var outcome = await Runner(Result(5, 0), Client(options), options).RunAsync(CollectorIntegrationFor(), CancellationToken.None);

        Assert.Equal(DiscoveryRunStatus.Rejected, outcome.Status);
        Assert.Equal(2, _server.Requests.Count);
        var report = _server.Requests[1];
        Assert.Null(report.JobId);
        Assert.True(report.Final);
        Assert.False(report.Success);
        Assert.Equal(Integration, report.IntegrationId);
        Assert.Contains("(400: bad node)", report.Errors[0]);
    }

    // ------------------------------------------------------------------
    // Losses in the offline queue
    // ------------------------------------------------------------------

    [Fact]
    public async Task QuotaEviction_OfAnOpenSnapshot_ReportsAFailure_InsteadOfACompleteSnapshot()
    {
        // Batch 1 is delivered (the job opens), then the server becomes unreachable and the rest
        // is buffered in a queue too small for it: the oldest buffered batches are evicted.
        var options = Options(batchSize: 1, queueMaxDiskMb: 1);
        var queue = _server.Queue(options);
        var client = Client(options, queue);
        var calls = 0;
        _server.Respond = _ => ++calls > 1 ? throw new HttpRequestException("connection refused") : null;
        var result = Result(6, 0, evidenceBytes: 0);
        foreach (var node in result.Nodes)
            node.Properties["padding"] = new string('p', 300 * 1024);

        var outcome = await Runner(result, client, options).RunAsync(CollectorIntegrationFor(), CancellationToken.None);
        Assert.NotEqual(DiscoveryRunStatus.Completed, outcome.Status);
        Assert.True(queue.GetQueueDepth(CollectorClient.BatchQueue) < 5);

        // The server is back; after a restart the backlog is drained.
        _server.Respond = _ => null;
        var restarted = Client(options, new LocalBufferQueue(options.OfflineQueueDirectory, options.QueueMaxDiskMb));
        Assert.True(await restarted.DrainOfflineQueueAsync());

        var afterOutage = _server.Requests.Skip(1).Where(r => r.Status == 200).ToList();
        var report = Assert.Single(afterOutage);
        Assert.True(report.Final);
        Assert.False(report.Success);
        Assert.Equal(_server.Requests[0].OpenedJob, report.JobId);
        Assert.StartsWith("COLLECTOR_BATCH_LOST: Batch 2 of the snapshot was lost from the offline queue (evicted by the disk quota)", report.Errors[0]);
        Assert.Equal(0, _server.Queue(options).GetQueueDepth(CollectorClient.BatchQueue));
    }

    [Fact]
    public async Task DeadLetteredMiddleBatch_DropsTheRest_AndFailsTheJob()
    {
        var options = Options();
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10, maxRetries: 2);
        var snapshot = Guid.NewGuid();
        var jobId = await DeliverFirstBatchAsync(options, queue, snapshot);
        for (var sequence = 1; sequence <= 3; sequence++)
            await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, sequence, final: sequence == 3));

        // Batch 2 keeps failing until it reaches the retry limit.
        var client = Client(options, queue);
        _server.Respond = r => r.Sequence == 1 ? Status(HttpStatusCode.ServiceUnavailable) : null;
        Assert.False(await client.DrainOfflineQueueAsync());
        Assert.False(await client.DrainOfflineQueueAsync());
        Assert.Equal(1, queue.GetDeadLetterCount(CollectorClient.BatchQueue));

        _server.Respond = _ => null;
        Assert.True(await client.DrainOfflineQueueAsync());

        var delivered = _server.Requests.Where(r => r.Status == 200).ToList();
        Assert.Equal(2, delivered.Count); // batch 1 and the failure report; batches 3 and 4 are dropped
        Assert.False(delivered[1].Success);
        Assert.Equal(jobId, delivered[1].JobId);
        Assert.Contains("Batch 2 of the snapshot could not be delivered", delivered[1].Errors[0]);
        Assert.Equal(0, queue.GetQueueDepth(CollectorClient.BatchQueue));
    }

    [Fact]
    public async Task SequenceGap_IsDetectedAtDelivery_AndNeverCompletesTheSnapshot()
    {
        var options = Options();
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10);
        var snapshot = Guid.NewGuid();
        var jobId = await DeliverFirstBatchAsync(options, queue, snapshot);
        // Batch 2 vanished without trace (for example an unreadable file deleted by hand).
        await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, 2, final: true));

        Assert.True(await Client(options, queue).DrainOfflineQueueAsync());

        var report = _server.Requests[^1];
        Assert.Equal(2, _server.Requests.Count);
        Assert.False(report.Success);
        Assert.True(report.Final);
        Assert.Equal(jobId, report.JobId);
        Assert.StartsWith("COLLECTOR_BATCH_LOST: Batch 2 of the snapshot never reached the server", report.Errors[0]);
    }

    [Fact]
    public async Task LaterBatches_OfASnapshotWhoseFirstBatchWasEvicted_AreReportedOnANewJob()
    {
        var options = Options();
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10);
        var snapshot = Guid.NewGuid();
        await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, 1, final: false));
        await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, 2, final: true));

        Assert.True(await Client(options, queue).DrainOfflineQueueAsync());

        var report = Assert.Single(_server.Requests);
        Assert.Null(report.JobId);
        Assert.True(report.Final);
        Assert.False(report.Success);
        Assert.Equal(Integration, report.IntegrationId);
    }

    [Fact]
    public async Task FailureReport_WhileTheServerIsUnreachable_IsSentOnTheNextDrain()
    {
        var options = Options();
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10);
        var snapshot = Guid.NewGuid();
        var jobId = await DeliverFirstBatchAsync(options, queue, snapshot);
        await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, 3, final: true));

        var client = Client(options, queue);
        _server.Respond = r => r.Success ? null : throw new HttpRequestException("connection refused");
        Assert.True(await client.DrainOfflineQueueAsync()); // gap found, report not delivered
        _server.Respond = _ => null;

        // A restart in between does not lose the pending report.
        Assert.True(await Client(options, queue).DrainOfflineQueueAsync());
        var reports = _server.Requests.Where(r => !r.Success && r.Status == 200).ToList();
        Assert.Equal(jobId, Assert.Single(reports).JobId);
        Assert.True(await Client(options, queue).DrainOfflineQueueAsync());
        Assert.Single(_server.Requests, r => !r.Success && r.Status == 200);
    }

    [Fact]
    public async Task BatchDeliveredTwice_AfterACrashBeforeItsAck_IsNotSentAgain()
    {
        var options = Options();
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10);
        var snapshot = Guid.NewGuid();
        await DeliverFirstBatchAsync(options, queue, snapshot);
        // The same batch 1 is still in the queue (the process died before acknowledging it).
        await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, 0, final: false));
        await queue.EnqueueAsync(CollectorClient.BatchQueue, Queued(snapshot, 1, final: true));

        Assert.True(await Client(options, queue).DrainOfflineQueueAsync());

        Assert.Equal(2, _server.Requests.Count);
        Assert.True(_server.Requests[1].Final);
        Assert.True(_server.Requests[1].Success);
    }

    [Fact]
    public async Task Queue_ReportsTheTagOfEvictedAndUnreadableItems()
    {
        var queue = new LocalBufferQueue(Path.Combine(_dir, "q"), maxDiskMegabytes: 1);
        var discarded = new List<DiscardedQueueItem>();
        queue.ItemsDiscarded += (_, item) => discarded.Add(item);
        var padding = new string('x', 400 * 1024);

        Assert.True(await queue.EnqueueAsync("q1", padding, "first-0"));
        Assert.True(await queue.EnqueueAsync("q1", padding, "second-1"));
        Assert.True(await queue.EnqueueAsync("q1", padding, "third-2"));

        var evicted = Assert.Single(discarded);
        Assert.Equal("first-0", evicted.Tag);
        Assert.Equal("q1", evicted.QueueName);
        Assert.Equal("evicted by the disk quota", evicted.Reason);
        Assert.Throws<ArgumentException>(() => { queue.EnqueueAsync("q1", "x", "bad tag").GetAwaiter().GetResult(); });

        // An unreadable item is dead-lettered and reported too.
        var next = Directory.EnumerateFiles(Path.Combine(_dir, "q", "q1"), "*.json").OrderBy(f => f, StringComparer.Ordinal).First();
        File.WriteAllText(next, "{ not json");
        var item = await queue.DequeueAsync<string>("q1");
        Assert.Equal("third-2", LocalBufferQueue.TagOf(item!.Id));
        Assert.Equal("second-1", discarded[^1].Tag);
        Assert.StartsWith("unreadable", discarded[^1].Reason);
    }

    // ------------------------------------------------------------------
    // Shutdown during an upload
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stop_DuringAMultiBatchUpload_BuffersTheRest_AndTheSnapshotCompletesAfterTheRestart()
    {
        var options = Options(batchSize: 2);
        using var stopping = new CancellationTokenSource();
        var calls = 0;
        // The service is asked to stop while batch 2 is on the wire.
        _server.Respond = r =>
        {
            if (++calls == 2)
            {
                stopping.Cancel();
                throw new TaskCanceledException("stopping", null, stopping.Token);
            }
            return null;
        };
        var queue = new LocalBufferQueue(options.OfflineQueueDirectory, 10);

        var outcome = await Runner(Result(7, 2), Client(options, queue), options).RunAsync(CollectorIntegrationFor(), stopping.Token);

        Assert.Equal(DiscoveryRunStatus.Completed, outcome.Status);
        Assert.Equal(3, queue.GetQueueDepth(CollectorClient.BatchQueue)); // batches 2..4
        Assert.Equal(2, _server.Requests.Count);

        _server.Respond = _ => null;
        var restarted = Client(options, new LocalBufferQueue(options.OfflineQueueDirectory, 10));
        Assert.True(await restarted.DrainOfflineQueueAsync());

        var delivered = _server.Requests.Where(r => r.Status == 200).ToList();
        Assert.Equal(4, delivered.Count);
        Assert.All(delivered.Skip(1), r => Assert.Equal(delivered[0].OpenedJob, r.JobId));
        Assert.Equal(new[] { 2, 2, 2, 1 }, delivered.Select(r => r.Nodes));
        Assert.True(delivered[^1].Final && delivered[^1].Success);
        Assert.Equal(7, delivered.Sum(r => r.Nodes));
    }

    // ------------------------------------------------------------------
    // Server URL with a path prefix
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://proxy.corp.local/bermap", "https://proxy.corp.local/bermap/api/collectors/register")]
    [InlineData("https://proxy.corp.local/bermap/", "https://proxy.corp.local/bermap/api/collectors/register")]
    [InlineData("https://bermap.corp.local:8443", "https://bermap.corp.local:8443/api/collectors/register")]
    [InlineData("http://bermap.corp.local/", "http://bermap.corp.local/api/collectors/register")]
    public async Task ServerUrl_PathPrefix_IsKeptForEveryRequest(string serverUrl, string expectedRegisterUrl)
    {
        var options = Options();
        options.ServerUrl = serverUrl;
        var client = new CollectorClient(
            new HttpClient(_server) { BaseAddress = options.ServerBaseAddress },
            new LocalBufferQueue(options.OfflineQueueDirectory, 10), NullLogger<CollectorClient>.Instance, options);

        Assert.True(await client.EnsureRegisteredAsync());
        Assert.True(await client.SendHeartbeatAsync());
        Assert.Equal(BatchDelivery.Delivered, await client.SubmitSnapshotBatchAsync(Batch(0, true), Guid.NewGuid(), 0, final: true));

        var prefix = expectedRegisterUrl[..^"api/collectors/register".Length];
        Assert.Equal(expectedRegisterUrl, _server.Uris[0].ToString());
        Assert.Equal(prefix + "api/collectors/heartbeat", _server.Uris[1].GetLeftPart(UriPartial.Path));
        Assert.Equal(prefix + "api/collectors/submit-batch", _server.Uris[2].GetLeftPart(UriPartial.Path));
    }

    [Theory]
    [InlineData("bermap.corp.local")]
    [InlineData("ftp://bermap.corp.local")]
    [InlineData("https://bermap.corp.local/?x=1")]
    [InlineData("https://bermap.corp.local/#top")]
    public void ServerUrl_Invalid_IsRefusedWithAClearMessage(string serverUrl)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CollectorOptions.ToBaseAddress(serverUrl));
        Assert.Contains("BerMap:ServerUrl", error.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private CollectorOptions Options(int batchSize = 500, int relationshipBatchSize = 5000, long queueMaxDiskMb = 10) => new()
    {
        DataDirectory = _dir,
        EnrollmentToken = "t",
        BatchSize = batchSize,
        RelationshipBatchSize = relationshipBatchSize,
        QueueMaxDiskMb = queueMaxDiskMb
    };

    private CollectorClient Client(CollectorOptions options, IOfflineQueue? queue = null) => new(
        new HttpClient(_server) { BaseAddress = new Uri("http://bermap.test") },
        queue ?? _server.Queue(options),
        NullLogger<CollectorClient>.Instance,
        options);

    private static CollectorDiscoveryRunner Runner(DiscoveryResult result, ICollectorClient client, CollectorOptions options) =>
        new(new IIntegrationAdapter[] { new StaticAdapter(result) }, client, new NoSecret(), options,
            NullLogger<CollectorDiscoveryRunner>.Instance);

    private static CollectorIntegration CollectorIntegrationFor() => new()
    {
        IntegrationId = Integration,
        AdapterType = StaticAdapter.Type,
        SecretEnvironmentVariable = "UNUSED"
    };

    /// <summary>Delivers batch 1 (sequence 0) of <paramref name="snapshot"/> online and returns the job it opened.</summary>
    private async Task<Guid> DeliverFirstBatchAsync(CollectorOptions options, IOfflineQueue queue, Guid snapshot)
    {
        Assert.Equal(BatchDelivery.Delivered,
            await Client(options, queue).SubmitSnapshotBatchAsync(Batch(0, true), snapshot, 0, final: false));
        return _server.Requests[^1].OpenedJob!.Value;
    }

    private static QueuedBatch Queued(Guid snapshot, int sequence, bool final) => new()
    {
        Batch = Batch(sequence, true),
        SnapshotId = snapshot,
        Sequence = sequence,
        Final = final
    };

    private static DiscoveryResult Batch(int sequence, bool success) => new()
    {
        IntegrationId = Integration,
        Success = success,
        Status = "InProgress",
        Nodes = new List<DiscoveredNodeContract> { new() { ExternalId = $"b{sequence}", NodeType = "Server", Name = $"b{sequence}" } },
        // The fake server reads the sequence back from here.
        SourceVersion = sequence.ToString()
    };

    private static DiscoveryResult Result(int nodes, int relationships, int evidenceBytes = 0) => new()
    {
        Success = true,
        Status = "Completed",
        Nodes = Enumerable.Range(0, nodes)
            .Select(i => new DiscoveredNodeContract { ExternalId = $"n{i}", NodeType = "Server", Name = $"n{i}", SourceSystem = "VMware" })
            .ToList(),
        Relationships = Enumerable.Range(0, relationships)
            .Select(i => new DiscoveredRelationshipContract
            {
                SourceExternalId = $"n{i % Math.Max(1, nodes)}-{i}",
                TargetExternalId = "n0",
                RelationshipType = "RunsOn",
                Evidence = evidenceBytes > 0 ? new string('e', evidenceBytes) : string.Empty
            })
            .ToList(),
        Statistics = new Dictionary<string, int> { ["VirtualMachines"] = nodes }
    };

    private static HttpResponseMessage Status(HttpStatusCode status, string? body = null) =>
        new(status) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json") };

    private sealed class NoSecret : IIntegrationSecretReader
    {
        public Task<string> ReadSecretAsync(CollectorIntegration integration, CancellationToken cancellationToken = default) =>
            Task.FromResult("secret");
    }

    private sealed class StaticAdapter : IIntegrationAdapter
    {
        public const string Type = "Static";
        private readonly DiscoveryResult _result;
        public StaticAdapter(DiscoveryResult result) => _result = result;
        public string AdapterType => Type;
        public string DisplayName => Type;
        public string Version => "1.0";
        public AdapterCapabilities GetCapabilities() => new() { MaxConcurrency = 1 };
        public AdapterConfigurationSchema GetConfigurationSchema() => new();

        public Task<ConnectionTestResult> TestConnectionAsync(AdapterExecutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConnectionTestResult { Success = true });

        public Task<DiscoveryResult> DiscoverAsync(AdapterExecutionContext context, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed record SubmitRequest(
        Guid? JobId, bool Final, bool Success, int Nodes, int Relationships, List<string> Errors,
        Guid? IntegrationId, int? Sequence)
    {
        public int Status { get; set; }
        public Guid? OpenedJob { get; set; }
    }

    /// <summary>
    /// submit-batch as the server answers it: a batch without jobId opens a job (its id is in
    /// the response). <see cref="Respond"/> may return another response, or throw to simulate
    /// an unreachable server; null means "accept". Register and heartbeat always succeed.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private LocalBufferQueue? _queue;

        public Func<SubmitRequest, HttpResponseMessage?> Respond { get; set; } = _ => null;
        public List<SubmitRequest> Requests { get; } = new();
        public List<Uri> Uris { get; } = new();

        public LocalBufferQueue Queue(CollectorOptions options) =>
            _queue ??= new LocalBufferQueue(options.OfflineQueueDirectory, options.QueueMaxDiskMb);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Uris.Add(uri);
            var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (uri.AbsolutePath.EndsWith("/register", StringComparison.Ordinal))
                return Status(HttpStatusCode.OK, $"{{\"registered\":true,\"collectorId\":\"{Guid.NewGuid()}\"}}");
            if (!uri.AbsolutePath.EndsWith("/submit-batch", StringComparison.Ordinal))
                return Status(HttpStatusCode.OK, "{}");

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var source = root.TryGetProperty("sourceVersion", out var version) && int.TryParse(version.GetString(), out var parsed) ? parsed : (int?)null;
            var submitted = new SubmitRequest(
                Guid.TryParse(query["jobId"], out var jobId) ? jobId : null,
                query["final"] == "true",
                root.GetProperty("success").GetBoolean(),
                root.GetProperty("nodes").GetArrayLength(),
                root.TryGetProperty("relationships", out var rels) ? rels.GetArrayLength() : 0,
                root.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToList(),
                root.TryGetProperty("integrationId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null,
                source);
            Requests.Add(submitted);

            var response = Respond(submitted);
            if (response != null)
            {
                submitted.Status = (int)response.StatusCode;
                return response;
            }

            submitted.Status = 200;
            submitted.OpenedJob = submitted.JobId ?? Guid.NewGuid();
            return Status(HttpStatusCode.OK, $"{{\"jobId\":\"{submitted.OpenedJob}\",\"jobCompleted\":{(submitted.Final ? "true" : "false")}}}");
        }
    }
}
