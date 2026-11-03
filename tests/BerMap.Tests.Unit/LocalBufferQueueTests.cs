using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using BerMap.Collector.Services;
using BerMap.Contracts;

namespace BerMap.Tests.Unit;

public class LocalBufferQueueTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LocalBufferQueue _queue;

    public LocalBufferQueueTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bermap_queue_test_{Guid.NewGuid():N}");
        _queue = new LocalBufferQueue(_tempDir, maxDiskMegabytes: 10);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task EnqueueAndDequeue_MaintainsFifoOrder()
    {
        // Arrange
        var item1 = new DiscoveryResult { Success = true, Duration = TimeSpan.FromSeconds(1) };
        var item2 = new DiscoveryResult { Success = true, Duration = TimeSpan.FromSeconds(2) };

        // Act
        await _queue.EnqueueAsync("test_queue", item1);
        await Task.Delay(10); // Ensure timestamp difference
        await _queue.EnqueueAsync("test_queue", item2);

        var depth = _queue.GetQueueDepth("test_queue");
        var dequeued1 = await _queue.DequeueAsync<DiscoveryResult>("test_queue");
        var dequeued2 = await _queue.DequeueAsync<DiscoveryResult>("test_queue");
        var dequeuedEmpty = await _queue.DequeueAsync<DiscoveryResult>("test_queue");

        // Assert
        Assert.Equal(2, depth);
        Assert.NotNull(dequeued1);
        Assert.NotNull(dequeued2);
        Assert.Null(dequeuedEmpty);
        Assert.Equal(TimeSpan.FromSeconds(1), dequeued1.Payload.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), dequeued2.Payload.Duration);
        Assert.Equal(0, _queue.GetQueueDepth("test_queue"));
    }
}
