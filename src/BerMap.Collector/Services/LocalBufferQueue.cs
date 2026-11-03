using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BerMap.Collector.Services;

public interface IOfflineQueue
{
    Task EnqueueAsync<T>(string queueName, T payload, CancellationToken cancellationToken = default);
    Task<QueueItem<T>?> DequeueAsync<T>(string queueName, CancellationToken cancellationToken = default);
    int GetQueueDepth(string queueName);
    long GetStorageSizeBytes();
}

public class QueueItem<T>
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string QueueName { get; set; } = string.Empty;
    public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
    public T Payload { get; set; } = default!;
}

public class LocalBufferQueue : IOfflineQueue
{
    private readonly string _storageDirectory;
    private readonly long _maxDiskBytes;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalBufferQueue(string? storageDirectory = null, long maxDiskMegabytes = 500)
    {
        _storageDirectory = storageDirectory ?? Path.Combine(AppContext.BaseDirectory, "offline_queue");
        _maxDiskBytes = maxDiskMegabytes * 1024 * 1024;

        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
    }

    public async Task EnqueueAsync<T>(string queueName, T payload, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            EnforceDiskQuota();

            var queueDir = Path.Combine(_storageDirectory, queueName);
            if (!Directory.Exists(queueDir))
            {
                Directory.CreateDirectory(queueDir);
            }

            var item = new QueueItem<T>
            {
                Id = $"{DateTime.UtcNow.Ticks}_{Guid.NewGuid():N}",
                QueueName = queueName,
                EnqueuedAt = DateTime.UtcNow,
                Payload = payload
            };

            var filePath = Path.Combine(queueDir, $"{item.Id}.json");
            var json = JsonSerializer.Serialize(item);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
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
            var queueDir = Path.Combine(_storageDirectory, queueName);
            if (!Directory.Exists(queueDir))
                return null;

            var oldestFile = Directory.GetFiles(queueDir, "*.json")
                .OrderBy(f => f)
                .FirstOrDefault();

            if (oldestFile == null)
                return null;

            var json = await File.ReadAllTextAsync(oldestFile, cancellationToken);
            var item = JsonSerializer.Deserialize<QueueItem<T>>(json);

            // Remove file after successful read
            File.Delete(oldestFile);

            return item;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public int GetQueueDepth(string queueName)
    {
        var queueDir = Path.Combine(_storageDirectory, queueName);
        if (!Directory.Exists(queueDir))
            return 0;

        return Directory.GetFiles(queueDir, "*.json").Length;
    }

    public long GetStorageSizeBytes()
    {
        if (!Directory.Exists(_storageDirectory))
            return 0;

        return Directory.GetFiles(_storageDirectory, "*.*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    private void EnforceDiskQuota()
    {
        var currentBytes = GetStorageSizeBytes();
        if (currentBytes > _maxDiskBytes)
        {
            // Delete oldest 10% of queued files
            var files = Directory.GetFiles(_storageDirectory, "*.json", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            var countToDelete = Math.Max(1, files.Count / 10);
            foreach (var file in files.Take(countToDelete))
            {
                try { file.Delete(); } catch { }
            }
        }
    }
}
