using System;
using System.Collections.Generic;
using System.IO;
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

public class CollectorClientTests : IDisposable
{
    private readonly string _dataDir;
    private readonly CollectorOptions _options;
    private readonly LocalBufferQueue _queue;
    private readonly FakeHandler _handler = new();

    public CollectorClientTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"bermap_collector_test_{Guid.NewGuid():N}");
        _options = new CollectorOptions
        {
            DataDirectory = _dataDir,
            EnrollmentToken = "test-token",
            Name = "collector-test",
            SupportedAdapters = new List<string> { "ActiveDirectory" }
        };
        _queue = new LocalBufferQueue(_options.OfflineQueueDirectory, maxDiskMegabytes: 10);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private CollectorClient CreateClient() => new(
        new HttpClient(_handler) { BaseAddress = new Uri("http://bermap.test") },
        _queue,
        NullLogger<CollectorClient>.Instance,
        _options);

    private static DiscoveryResult Batch(int seconds) => new()
    {
        Success = true,
        IntegrationId = Guid.NewGuid(),
        Duration = TimeSpan.FromSeconds(seconds),
        Nodes = new List<DiscoveredNodeContract> { new() { ExternalId = $"n{seconds}", NodeType = "Server", Name = "n" } }
    };

    [Fact]
    public async Task EnsureRegistered_RegistersOnce_AndPersistsCollectorId()
    {
        var collectorId = Guid.NewGuid();
        _handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/api/collectors/register" => Json(HttpStatusCode.OK, new { registered = true, collectorId }),
            "/api/collectors/heartbeat" => Json(HttpStatusCode.OK, new { acknowledged = true }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        var client = CreateClient();
        Assert.True(await client.EnsureRegisteredAsync());
        Assert.True(await client.SendHeartbeatAsync());

        Assert.Equal(collectorId.ToString("D"), File.ReadAllText(_options.CollectorIdFile));
        var heartbeat = JsonDocument.Parse(_handler.Bodies[^1]).RootElement;
        Assert.Equal(collectorId, heartbeat.GetProperty("collectorId").GetGuid());
        var cpu = heartbeat.GetProperty("cpuUsagePercent").GetDouble();
        var memory = heartbeat.GetProperty("memoryUsagePercent").GetDouble();
        Assert.InRange(cpu, 0, 100);
        Assert.InRange(memory, 0, 100);

        var register = JsonDocument.Parse(_handler.Bodies[0]).RootElement;
        Assert.Equal("collector-test", register.GetProperty("name").GetString());
        Assert.Equal("ActiveDirectory", register.GetProperty("supportedAdapters")[0].GetString());

        // A restarted collector reuses the persisted id and does not register again.
        _handler.Requests.Clear();
        var restarted = CreateClient();
        Assert.True(restarted.IsRegistered);
        Assert.True(await restarted.EnsureRegisteredAsync());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Heartbeat_NotFound_ForgetsIdSoCollectorReRegisters()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_options.CollectorIdFile, Guid.NewGuid().ToString());
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var client = CreateClient();
        Assert.True(client.IsRegistered);
        Assert.False(await client.SendHeartbeatAsync());
        Assert.False(client.IsRegistered);
        Assert.False(File.Exists(_options.CollectorIdFile));
    }

    [Fact]
    public async Task MultiBatchSnapshot_LaterBatchesCarryTheJobOpenedByTheFirst()
    {
        var jobId = Guid.NewGuid();
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"jobId\":\"{jobId}\",\"jobCompleted\":false}}")
        };

        var client = CreateClient();
        var snapshot = Guid.NewGuid();
        Assert.True(await client.SubmitDiscoveryBatchAsync(Batch(1), snapshot, final: false));
        Assert.True(await client.SubmitDiscoveryBatchAsync(Batch(2), snapshot, final: false));
        Assert.True(await client.SubmitDiscoveryBatchAsync(Batch(3), snapshot, final: true));

        Assert.DoesNotContain("jobId=", _handler.Requests[0].Query);
        Assert.Contains($"jobId={jobId}", _handler.Requests[1].Query);
        Assert.Contains($"jobId={jobId}", _handler.Requests[2].Query);
        Assert.Contains("final=true", _handler.Requests[2].Query);
    }

    [Fact]
    public async Task Drain_SendsInOrder_AcksOnSuccess_AndKeepsFailedItemAtHead()
    {
        await _queue.EnqueueAsync(CollectorClient.BatchQueue, new QueuedBatch { Batch = Batch(1) });
        await _queue.EnqueueAsync(CollectorClient.BatchQueue, new QueuedBatch { Batch = Batch(2), JobId = Guid.NewGuid(), Final = true });
        await _queue.EnqueueAsync(CollectorClient.BatchQueue, new QueuedBatch { Batch = Batch(3) });

        var calls = 0;
        _handler.Respond = _ => ++calls == 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK);

        var client = CreateClient();
        Assert.False(await client.DrainOfflineQueueAsync());
        Assert.Equal(2, _queue.GetQueueDepth(CollectorClient.BatchQueue));
        Assert.Contains("final=true", _handler.Requests[1].Query);
        Assert.Contains("jobId=", _handler.Requests[1].Query);

        Assert.True(await client.DrainOfflineQueueAsync());
        Assert.Equal(0, _queue.GetQueueDepth(CollectorClient.BatchQueue));

        // Order on the wire: 1, 2 (failed), 2 (retried), 3.
        var durations = new List<string>();
        foreach (var body in _handler.Bodies)
            durations.Add(JsonDocument.Parse(body).RootElement.GetProperty("duration").GetString()!);
        Assert.Equal(new[] { "00:00:01", "00:00:02", "00:00:02", "00:00:03" }, durations);
    }

    [Fact]
    public async Task Drain_ClientErrorResponse_DeadLettersAndContinues()
    {
        await _queue.EnqueueAsync(CollectorClient.BatchQueue, new QueuedBatch { Batch = Batch(1) });
        await _queue.EnqueueAsync(CollectorClient.BatchQueue, new QueuedBatch { Batch = Batch(2) });

        var calls = 0;
        _handler.Respond = _ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : new HttpResponseMessage(HttpStatusCode.OK);

        Assert.True(await CreateClient().DrainOfflineQueueAsync());
        Assert.Equal(0, _queue.GetQueueDepth(CollectorClient.BatchQueue));
        Assert.Equal(1, _queue.GetDeadLetterCount(CollectorClient.BatchQueue));
    }

    [Fact]
    public async Task Submit_WhenServerUnreachable_BuffersBatch()
    {
        _handler.Respond = _ => throw new HttpRequestException("connection refused");

        Assert.False(await CreateClient().SubmitDiscoveryBatchAsync(Batch(1), final: true));
        Assert.Equal(1, _queue.GetQueueDepth(CollectorClient.BatchQueue));

        var item = await _queue.DequeueAsync<QueuedBatch>(CollectorClient.BatchQueue);
        Assert.NotNull(item);
        Assert.True(item.Payload.Final);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public List<Uri> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (request.Content != null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return Respond(request);
        }
    }
}
