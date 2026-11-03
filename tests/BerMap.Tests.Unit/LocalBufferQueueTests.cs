using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using BerMap.Collector.Services;
using BerMap.Contracts;

namespace BerMap.Tests.Unit;

public class LocalBufferQueueTests : IDisposable
{
    private const string Queue = "test_queue";
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

    private string QueueDir => Path.Combine(_tempDir, Queue);

    private static DiscoveryResult Batch(int seconds) => new() { Success = true, Duration = TimeSpan.FromSeconds(seconds) };

    [Fact]
    public async Task EnqueueAndDequeue_MaintainsFifoOrder()
    {
        // Arrange
        await _queue.EnqueueAsync(Queue, Batch(1));
        await _queue.EnqueueAsync(Queue, Batch(2));

        // Act
        var depth = _queue.GetQueueDepth(Queue);
        var dequeued1 = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        var dequeued2 = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        var dequeuedEmpty = await _queue.DequeueAsync<DiscoveryResult>(Queue);

        // Assert
        Assert.Equal(2, depth);
        Assert.NotNull(dequeued1);
        Assert.NotNull(dequeued2);
        Assert.Null(dequeuedEmpty);
        Assert.Equal(TimeSpan.FromSeconds(1), dequeued1.Payload.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), dequeued2.Payload.Duration);

        // Leased items still count until acknowledged.
        Assert.Equal(2, _queue.GetQueueDepth(Queue));
        await _queue.AckAsync(Queue, dequeued1.Id);
        await _queue.AckAsync(Queue, dequeued2.Id);
        Assert.Equal(0, _queue.GetQueueDepth(Queue));
        Assert.Empty(Directory.EnumerateFiles(QueueDir));
    }

    [Fact]
    public async Task CorruptFile_IsMovedToDeadLetter_AndQueueContinues()
    {
        await _queue.EnqueueAsync(Queue, Batch(1));
        // A truncated file that sorts before the valid item (e.g. a crash during a legacy in-place write).
        File.WriteAllText(Path.Combine(QueueDir, "0000000000000000001_corrupt.json"), "{\"Id\":\"x\",\"Payload\":{\"Succ");

        var item = await _queue.DequeueAsync<DiscoveryResult>(Queue);

        Assert.NotNull(item);
        Assert.Equal(TimeSpan.FromSeconds(1), item.Payload.Duration);
        Assert.Equal(1, _queue.GetDeadLetterCount(Queue));
        Assert.True(File.Exists(Path.Combine(QueueDir, LocalBufferQueue.DeadLetterFolder, "0000000000000000001_corrupt.json")));
    }

    [Fact]
    public async Task Release_KeepsOriginalPosition_AndIncrementsRetryCount()
    {
        await _queue.EnqueueAsync(Queue, Batch(1));
        await _queue.EnqueueAsync(Queue, Batch(2));

        var first = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(first);
        Assert.True(await _queue.ReleaseAsync(Queue, first.Id));

        var again = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(again);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(TimeSpan.FromSeconds(1), again.Payload.Duration);
        Assert.Equal(1, again.RetryCount);
        Assert.NotNull(again.LastAttemptAt);

        // Release without counting a failure (e.g. shutdown) leaves the retry count alone.
        Assert.True(await _queue.ReleaseAsync(Queue, again.Id, countAsFailure: false));
        var third = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(third);
        Assert.Equal(1, third.RetryCount);
        Assert.Empty(Directory.EnumerateFiles(QueueDir, "*.tmp"));
    }

    [Fact]
    public async Task Release_DeadLettersAfterMaxRetries()
    {
        var queue = new LocalBufferQueue(_tempDir, maxDiskMegabytes: 10, maxRetries: 3);
        await queue.EnqueueAsync(Queue, Batch(1));

        var results = new System.Collections.Generic.List<bool>();
        for (var i = 0; i < 3; i++)
        {
            var item = await queue.DequeueAsync<DiscoveryResult>(Queue);
            Assert.NotNull(item);
            results.Add(await queue.ReleaseAsync(Queue, item.Id));
        }

        Assert.Equal(new[] { true, true, false }, results);
        Assert.Null(await queue.DequeueAsync<DiscoveryResult>(Queue));
        Assert.Equal(0, queue.GetQueueDepth(Queue));
        Assert.Equal(1, queue.GetDeadLetterCount(Queue));
    }

    [Fact]
    public async Task DeadLetter_RemovesLeasedItemFromQueue()
    {
        await _queue.EnqueueAsync(Queue, Batch(1));
        var item = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(item);

        await _queue.DeadLetterAsync(Queue, item.Id, "rejected");

        Assert.Equal(0, _queue.GetQueueDepth(Queue));
        Assert.Equal(1, _queue.GetDeadLetterCount(Queue));
    }

    [Fact]
    public async Task LeasedItems_AreRecoveredOnRestart_InOriginalOrder()
    {
        await _queue.EnqueueAsync(Queue, Batch(1));
        await _queue.EnqueueAsync(Queue, Batch(2));
        var leased = await _queue.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(leased);
        File.WriteAllText(Path.Combine(QueueDir, "partial.json.abc.tmp"), "{");

        // Simulate a process restart while the item was in flight.
        var restarted = new LocalBufferQueue(_tempDir, maxDiskMegabytes: 10);

        var first = await restarted.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(first);
        Assert.Equal(leased.Id, first.Id);
        Assert.Equal(TimeSpan.FromSeconds(1), first.Payload.Duration);
        Assert.Empty(Directory.EnumerateFiles(QueueDir, "*.tmp"));
    }

    [Fact]
    public async Task Enqueue_EvictsOldestToStayUnderQuota_AndRejectsOversizedItems()
    {
        var queue = new LocalBufferQueue(_tempDir, maxDiskMegabytes: 1);
        var padding = new string('x', 300 * 1024);
        for (var i = 0; i < 5; i++)
        {
            var batch = Batch(i);
            batch.Warnings.Add(padding);
            Assert.True(await queue.EnqueueAsync(Queue, batch));
        }

        Assert.True(queue.GetStorageSizeBytes() <= 1024 * 1024);
        Assert.True(queue.GetQueueDepth(Queue) < 5);

        // Remaining items are the newest ones, still in order.
        var oldest = await queue.DequeueAsync<DiscoveryResult>(Queue);
        Assert.NotNull(oldest);
        Assert.True(oldest.Payload.Duration >= TimeSpan.FromSeconds(2));

        var huge = Batch(99);
        huge.Warnings.Add(new string('y', 2 * 1024 * 1024));
        Assert.False(await queue.EnqueueAsync(Queue, huge));
    }
}
