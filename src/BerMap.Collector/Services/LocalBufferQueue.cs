using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BerMap.Collector.Services;

/// <summary>
/// Durable FIFO buffer for payloads that could not be delivered to the BerMap server.
/// Items are leased by <see cref="DequeueAsync{T}"/> and stay on disk until the caller
/// either acknowledges delivery (<see cref="AckAsync"/>), returns them to the queue
/// (<see cref="ReleaseAsync"/>) or gives up on them (<see cref="DeadLetterAsync"/>).
/// </summary>
public interface IOfflineQueue
{
    /// <summary>Persists a payload. Returns false when it was rejected by the disk quota.</summary>
    Task<bool> EnqueueAsync<T>(string queueName, T payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a payload with a <paramref name="tag"/> (letters, digits and '-', at most 128
    /// characters) that becomes part of the item id. <see cref="ItemsDiscarded"/> reports the tag
    /// of an item lost without being dequeued, so the owner knows what was lost without reading it.
    /// </summary>
    Task<bool> EnqueueAsync<T>(string queueName, T payload, string tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when pending items are discarded without being dequeued: evicted by the disk quota or
    /// dead-lettered because they could not be read. Raised while the queue is locked: handlers must
    /// not call back into the queue.
    /// </summary>
    event EventHandler<DiscardedQueueItem>? ItemsDiscarded;

    /// <summary>
    /// Leases the oldest item. Unreadable items are moved to dead-letter and skipped.
    /// Returns null when no item is available.
    /// </summary>
    Task<QueueItem<T>?> DequeueAsync<T>(string queueName, CancellationToken cancellationToken = default);

    /// <summary>Deletes a leased item after the server acknowledged it.</summary>
    Task AckAsync(string queueName, string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a leased item to its original position. When <paramref name="countAsFailure"/> is set the
    /// retry count is incremented and the item is dead-lettered once it reaches the retry limit.
    /// Returns false when the item was dead-lettered instead of re-queued.
    /// </summary>
    Task<bool> ReleaseAsync(string queueName, string itemId, bool countAsFailure = true, CancellationToken cancellationToken = default);

    /// <summary>Moves a leased item to the dead-letter folder (e.g. permanently rejected by the server).</summary>
    Task DeadLetterAsync(string queueName, string itemId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Pending plus leased (not yet acknowledged) items.</summary>
    int GetQueueDepth(string queueName);

    int GetDeadLetterCount(string queueName);

    long GetStorageSizeBytes();
}

/// <summary>A pending item the queue discarded; <see cref="Tag"/> is the tag given at enqueue time.</summary>
public sealed record DiscardedQueueItem(string QueueName, string ItemId, string? Tag, string Reason);

public class QueueItem<T>
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string QueueName { get; set; } = string.Empty;
    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public T Payload { get; set; } = default!;
}

/// <summary>
/// File-backed <see cref="IOfflineQueue"/>. Layout per queue:
///   {root}/{queue}/{id}.json        pending item (id sorts by enqueue time; "{ticks}_{guid}" or
///                                   "{ticks}_{guid}~{tag}")
///   {root}/{queue}/{id}.inflight    leased item awaiting Ack/Release
///   {root}/{queue}/dead-letter/     unreadable or abandoned items (kept for inspection)
/// Writes go to a .tmp file first and are moved into place, so a crash never leaves a
/// truncated .json behind.
/// </summary>
public class LocalBufferQueue : IOfflineQueue
{
    public const string DeadLetterFolder = "dead-letter";
    private const string PendingExtension = ".json";
    private const string InflightExtension = ".inflight";
    private const string TempExtension = ".tmp";
    private const char TagSeparator = '~';
    private const int MaxTagLength = 128;

    private readonly string _storageDirectory;
    private readonly long _maxDiskBytes;
    private readonly int _maxRetries;
    private readonly ILogger<LocalBufferQueue> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalBufferQueue(
        string? storageDirectory = null,
        long maxDiskMegabytes = 500,
        ILogger<LocalBufferQueue>? logger = null,
        int maxRetries = 10)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(CollectorOptions.ResolveDataDirectory(null), "offline_queue");
        _maxDiskBytes = Math.Max(1, maxDiskMegabytes) * 1024 * 1024;
        _maxRetries = Math.Max(1, maxRetries);
        _logger = logger ?? NullLogger<LocalBufferQueue>.Instance;

        Directory.CreateDirectory(_storageDirectory);
        RecoverAfterRestart();
    }

    public event EventHandler<DiscardedQueueItem>? ItemsDiscarded;

    public Task<bool> EnqueueAsync<T>(string queueName, T payload, CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(queueName, payload, null, cancellationToken);

    public Task<bool> EnqueueAsync<T>(string queueName, T payload, string tag, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength ||
            !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException($"Invalid queue item tag '{tag}'.", nameof(tag));
        return EnqueueCoreAsync(queueName, payload, tag, cancellationToken);
    }

    /// <summary>The tag part of an item id, or null for an untagged item.</summary>
    public static string? TagOf(string itemId)
    {
        var separator = itemId.IndexOf(TagSeparator);
        return separator < 0 ? null : itemId[(separator + 1)..];
    }

    private async Task<bool> EnqueueCoreAsync<T>(string queueName, T payload, string? tag, CancellationToken cancellationToken)
    {
        var item = new QueueItem<T>
        {
            Id = NewItemId(tag),
            QueueName = queueName,
            EnqueuedAt = DateTime.UtcNow,
            Payload = payload
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!EnsureCapacity(bytes.LongLength))
            {
                _logger.LogError(
                    "Offline queue '{Queue}' rejected an item of {Bytes} bytes: disk quota of {QuotaMb} MB cannot be satisfied.",
                    queueName, bytes.LongLength, _maxDiskBytes / (1024 * 1024));
                return false;
            }

            var queueDir = QueueDirectory(queueName);
            Directory.CreateDirectory(queueDir);
            await WriteAtomicAsync(Path.Combine(queueDir, item.Id + PendingExtension), bytes, cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<QueueItem<T>?> DequeueAsync<T>(string queueName, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var queueDir = QueueDirectory(queueName);
            if (!Directory.Exists(queueDir))
                return null;

            foreach (var file in PendingFiles(queueDir))
            {
                QueueItem<T>? item;
                try
                {
                    var json = await File.ReadAllBytesAsync(file, cancellationToken);
                    item = JsonSerializer.Deserialize<QueueItem<T>>(json);
                }
                catch (JsonException ex)
                {
                    MoveToDeadLetter(queueDir, file, $"unreadable: {ex.Message}");
                    RaiseDiscarded(queueName, file, "unreadable");
                    continue;
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                if (item == null || item.Payload == null)
                {
                    MoveToDeadLetter(queueDir, file, "empty payload");
                    RaiseDiscarded(queueName, file, "empty payload");
                    continue;
                }

                // The file name is the authoritative id; it determines the item's position in the queue.
                item.Id = Path.GetFileNameWithoutExtension(file);
                item.QueueName = queueName;
                File.Move(file, Path.Combine(queueDir, item.Id + InflightExtension), overwrite: false);
                return item;
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AckAsync(string queueName, string itemId, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var inflight = InflightPath(queueName, itemId);
            if (File.Exists(inflight))
                File.Delete(inflight);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ReleaseAsync(string queueName, string itemId, bool countAsFailure = true, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var queueDir = QueueDirectory(queueName);
            var inflight = InflightPath(queueName, itemId);
            if (!File.Exists(inflight))
                return false;

            if (!countAsFailure)
            {
                File.Move(inflight, Path.Combine(queueDir, itemId + PendingExtension), overwrite: false);
                return true;
            }

            // Update the retry count on the item itself; the payload is kept opaque.
            var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(inflight, cancellationToken));
            if (node is not System.Text.Json.Nodes.JsonObject obj)
            {
                MoveToDeadLetter(queueDir, inflight, "unreadable on release");
                return false;
            }

            var retries = (obj[nameof(QueueItem<object>.RetryCount)]?.GetValue<int>() ?? 0) + 1;
            obj[nameof(QueueItem<object>.RetryCount)] = retries;
            obj[nameof(QueueItem<object>.LastAttemptAt)] = DateTime.UtcNow;

            if (retries >= _maxRetries)
            {
                File.WriteAllText(inflight, obj.ToJsonString());
                MoveToDeadLetter(queueDir, inflight, $"delivery failed {retries} times");
                return false;
            }

            // Same id => same position: the item is retried before anything enqueued after it.
            await WriteAtomicAsync(Path.Combine(queueDir, itemId + PendingExtension),
                JsonSerializer.SerializeToUtf8Bytes(obj), cancellationToken);
            File.Delete(inflight);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeadLetterAsync(string queueName, string itemId, string reason, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var inflight = InflightPath(queueName, itemId);
            if (File.Exists(inflight))
                MoveToDeadLetter(QueueDirectory(queueName), inflight, reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    public int GetQueueDepth(string queueName)
    {
        var queueDir = QueueDirectory(queueName);
        if (!Directory.Exists(queueDir))
            return 0;

        return Directory.EnumerateFiles(queueDir)
            .Count(f => f.EndsWith(PendingExtension, StringComparison.Ordinal) ||
                        f.EndsWith(InflightExtension, StringComparison.Ordinal));
    }

    public int GetDeadLetterCount(string queueName)
    {
        var deadLetterDir = Path.Combine(QueueDirectory(queueName), DeadLetterFolder);
        return Directory.Exists(deadLetterDir) ? Directory.EnumerateFiles(deadLetterDir).Count() : 0;
    }

    public long GetStorageSizeBytes()
    {
        if (!Directory.Exists(_storageDirectory))
            return 0;

        return Directory.EnumerateFiles(_storageDirectory, "*", SearchOption.AllDirectories)
            .Sum(f => SafeLength(f));
    }

    // ---------------------------------------------------------------------
    // Internals (callers hold _lock unless noted)
    // ---------------------------------------------------------------------

    private string QueueDirectory(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName) ||
            queueName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            queueName is "." or ".." or DeadLetterFolder)
            throw new ArgumentException($"Invalid queue name '{queueName}'.", nameof(queueName));
        return Path.Combine(_storageDirectory, queueName);
    }

    private string InflightPath(string queueName, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"Invalid queue item id '{itemId}'.", nameof(itemId));
        return Path.Combine(QueueDirectory(queueName), itemId + InflightExtension);
    }

    private static IEnumerable<string> PendingFiles(string queueDir)
        => Directory.EnumerateFiles(queueDir)
            .Where(f => f.EndsWith(PendingExtension, StringComparison.Ordinal))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

    /// <summary>Fixed-width UTC ticks keep ordinal file-name order equal to enqueue order.</summary>
    private static string NewItemId(string? tag)
    {
        var id = $"{DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}";
        return tag == null ? id : id + TagSeparator + tag;
    }

    /// <summary>Tells the owner of a pending item that it is gone; a handler failure never breaks the queue.</summary>
    private void RaiseDiscarded(string queueName, string file, string reason)
    {
        var handler = ItemsDiscarded;
        if (handler == null)
            return;
        var itemId = Path.GetFileNameWithoutExtension(file);
        try
        {
            handler(this, new DiscardedQueueItem(queueName, itemId, TagOf(itemId), reason));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offline queue discard handler failed for item {ItemId}.", itemId);
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private void MoveToDeadLetter(string queueDir, string file, string reason)
    {
        var deadLetterDir = Path.Combine(queueDir, DeadLetterFolder);
        Directory.CreateDirectory(deadLetterDir);
        var target = Path.Combine(deadLetterDir, Path.GetFileNameWithoutExtension(file) + PendingExtension);
        if (File.Exists(target))
            target = Path.Combine(deadLetterDir, $"{Path.GetFileNameWithoutExtension(file)}_{Guid.NewGuid():N}{PendingExtension}");

        try
        {
            File.Move(file, target, overwrite: false);
            _logger.LogWarning("Offline queue item {File} moved to dead-letter ({Reason}).", Path.GetFileName(file), reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never let one bad file block the queue: fall back to deleting it.
            _logger.LogError(ex, "Could not dead-letter offline queue item {File} ({Reason}); deleting it.", Path.GetFileName(file), reason);
            TryDelete(file);
        }
    }

    /// <summary>
    /// Makes room for <paramref name="incomingBytes"/> by evicting dead-lettered items first and then
    /// the oldest pending items. Leased items are never evicted. Returns false if the quota still
    /// cannot be met.
    /// </summary>
    private bool EnsureCapacity(long incomingBytes)
    {
        if (incomingBytes > _maxDiskBytes)
            return false;

        var used = GetStorageSizeBytes();
        if (used + incomingBytes <= _maxDiskBytes)
            return true;

        var queueDirs = Directory.EnumerateDirectories(_storageDirectory).ToList();
        var deadLetters = queueDirs
            .Select(d => Path.Combine(d, DeadLetterFolder))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);
        var pending = queueDirs
            .SelectMany(d => Directory.EnumerateFiles(d)
                .Where(f => f.EndsWith(PendingExtension, StringComparison.Ordinal))
                .Select(f => (File: f, Queue: (string?)Path.GetFileName(d))))
            .OrderBy(p => Path.GetFileName(p.File), StringComparer.Ordinal);

        var evicted = 0;
        foreach (var (file, queue) in deadLetters.Select(f => (File: f, Queue: (string?)null)).Concat(pending).ToList())
        {
            if (used + incomingBytes <= _maxDiskBytes)
                break;
            var length = SafeLength(file);
            if (TryDelete(file))
            {
                used -= length;
                evicted++;
                // A dead-lettered item was already given up on; a pending one is lost now.
                if (queue != null)
                    RaiseDiscarded(queue, file, "evicted by the disk quota");
            }
        }

        if (evicted > 0)
        {
            _logger.LogWarning(
                "Offline queue disk quota ({QuotaMb} MB) reached: evicted {Count} oldest item(s) to make room.",
                _maxDiskBytes / (1024 * 1024), evicted);
        }

        return used + incomingBytes <= _maxDiskBytes;
    }

    /// <summary>
    /// Runs once at startup (no concurrent access yet): removes partial writes and returns items that
    /// were leased when the process stopped to the queue so they are retried in their original order.
    /// </summary>
    private void RecoverAfterRestart()
    {
        foreach (var queueDir in Directory.EnumerateDirectories(_storageDirectory))
        {
            foreach (var temp in Directory.EnumerateFiles(queueDir, "*" + TempExtension))
                TryDelete(temp);

            foreach (var inflight in Directory.EnumerateFiles(queueDir)
                         .Where(f => f.EndsWith(InflightExtension, StringComparison.Ordinal)))
            {
                var pending = Path.ChangeExtension(inflight, PendingExtension);
                try
                {
                    if (File.Exists(pending))
                    {
                        // A release already rewrote the item; the lease file is stale.
                        File.Delete(inflight);
                    }
                    else
                    {
                        File.Move(inflight, pending, overwrite: false);
                        _logger.LogInformation("Recovered leased offline queue item {File}.", Path.GetFileName(pending));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not recover leased offline queue item {File}.", Path.GetFileName(inflight));
                }
            }
        }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
