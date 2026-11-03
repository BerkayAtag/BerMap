using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace BerMap.Collector.Services;

/// <summary>
/// Collector settings bound from configuration:
///   BerMap:ServerUrl                     BerMap server base URL (outbound HTTPS only)
///   BerMap:Collector:EnrollmentToken     shared token sent as X-BerMap-Enrollment-Token on every request
///   BerMap:Collector:DataDirectory       writable folder for the collector id and offline queue
///   BerMap:Collector:QueueMaxDiskMb      disk quota for the offline queue
///   BerMap:Collector:Name                display name used at registration (defaults to the host name)
///   BerMap:Collector:SupportedAdapters   adapter types advertised at registration
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

    public string OfflineQueueDirectory => Path.Combine(DataDirectory, "offline_queue");
    public string CollectorIdFile => Path.Combine(DataDirectory, "collector-id");

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
