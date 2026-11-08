using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace BerMap.Collector.Services;

/// <summary>
/// Collector settings bound from configuration:
///   BerMap:ServerUrl                     BerMap server base URL (outbound HTTPS only); a path
///                                        prefix (https://proxy/bermap) is kept
///   BerMap:Collector:EnrollmentToken     shared token sent as X-BerMap-Enrollment-Token on every request
///   BerMap:Collector:DataDirectory       writable folder for the collector id and offline queue
///   BerMap:Collector:QueueMaxDiskMb      disk quota for the offline queue
///   BerMap:Collector:Name                display name used at registration (defaults to the host name)
///   BerMap:Collector:SupportedAdapters   adapter types advertised at registration, in addition
///                                        to the adapter types of the configured integrations
///   BerMap:Collector:BatchSize           nodes per submit-batch request (default 500)
///   BerMap:Collector:RelationshipBatchSize relationships per submit-batch request (default 5000)
///   BerMap:Collector:MaxBatchMb          serialized size limit of one submit-batch request
///                                        (default 8 MB; the server refuses bodies over 30 MB)
///   BerMap:Collector:Integrations        integrations discovered by this collector, see
///                                        <see cref="CollectorIntegration"/>
/// </summary>
public sealed class CollectorOptions
{
    public const string EnrollmentTokenHeader = "X-BerMap-Enrollment-Token";

    public string ServerUrl { get; set; } = "https://localhost:5001";
    public string EnrollmentToken { get; set; } = string.Empty;
    public string DataDirectory { get; set; } = string.Empty;
    public long QueueMaxDiskMb { get; set; } = 500;
    public string Name { get; set; } = Environment.MachineName;
    public List<string> SupportedAdapters { get; set; } = new();
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int BatchSize { get; set; } = DefaultBatchSize;
    public int RelationshipBatchSize { get; set; } = DefaultRelationshipBatchSize;
    public int MaxBatchMb { get; set; } = DefaultMaxBatchMb;

    public const int DefaultBatchSize = 500;
    public const int MaxBatchSize = 10000;
    public const int DefaultRelationshipBatchSize = 5000;
    public const int MaxRelationshipBatchSize = 50000;
    public const int DefaultMaxBatchMb = 8;
    /// <summary>Stays below the server's 30,000,000-byte request body limit.</summary>
    public const int MaxMaxBatchMb = 24;

    public long MaxBatchBytes => MaxBatchMb * 1024L * 1024L;

    public string OfflineQueueDirectory => Path.Combine(DataDirectory, "offline_queue");
    public string CollectorIdFile => Path.Combine(DataDirectory, "collector-id");

    /// <summary>
    /// <see cref="ServerUrl"/> as the HttpClient base address. The trailing slash
    /// keeps a path prefix (https://proxy.corp.local/bermap/): requests use relative
    /// paths ("api/collectors/register"), which resolve below it.
    /// </summary>
    public Uri ServerBaseAddress => ToBaseAddress(ServerUrl);

    public static Uri ToBaseAddress(string serverUrl)
    {
        if (!Uri.TryCreate((serverUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"BerMap:ServerUrl '{serverUrl}' is not a valid server address (expected https://host[:port][/path]).");
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
            builder.Path += "/";
        return builder.Uri;
    }

    /// <summary>
    /// Adds the adapter types of the configured integrations to the advertised
    /// adapters (distinct, case-insensitive, explicit entries first).
    /// </summary>
    public void AdvertiseAdapters(IEnumerable<string> adapterTypes)
    {
        foreach (var adapterType in adapterTypes)
        {
            if (!SupportedAdapters.Contains(adapterType, StringComparer.OrdinalIgnoreCase))
                SupportedAdapters.Add(adapterType);
        }
    }

    public static CollectorOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("BerMap:Collector");
        var options = new CollectorOptions();
        section.Bind(options);
        options.ServerUrl = configuration["BerMap:ServerUrl"] ?? options.ServerUrl;
        options.EnrollmentToken = options.EnrollmentToken?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(options.Name))
            options.Name = Environment.MachineName;
        options.QueueMaxDiskMb = Math.Max(1, options.QueueMaxDiskMb);
        options.BatchSize = options.BatchSize <= 0 ? DefaultBatchSize : Math.Min(options.BatchSize, MaxBatchSize);
        options.RelationshipBatchSize = options.RelationshipBatchSize <= 0
            ? DefaultRelationshipBatchSize
            : Math.Min(options.RelationshipBatchSize, MaxRelationshipBatchSize);
        options.MaxBatchMb = options.MaxBatchMb <= 0 ? DefaultMaxBatchMb : Math.Min(options.MaxBatchMb, MaxMaxBatchMb);
        options.SupportedAdapters = (options.SupportedAdapters ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        options.DataDirectory = ResolveDataDirectory(options.DataDirectory);
        return options;
    }

    /// <summary>
    /// An explicitly configured directory is used as-is (and must be writable). Otherwise the
    /// machine-wide service location is preferred (ProgramData on Windows, /var/lib on Linux),
    /// falling back to the per-user LocalApplicationData folder when that is not writable.
    /// </summary>
    public static string ResolveDataDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            Directory.CreateDirectory(path);
            return path;
        }

        string? preferred = null;
        if (OperatingSystem.IsWindows())
            preferred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BerMap", "Collector");
        else if (OperatingSystem.IsLinux())
            preferred = "/var/lib/bermap/collector";

        if (preferred != null && IsWritable(preferred))
            return preferred;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallback = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(localAppData, "BerMap", "Collector");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
