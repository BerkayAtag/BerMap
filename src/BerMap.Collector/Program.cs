using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BerMap.Collector;
using BerMap.Collector.Services;
using BerMap.Contracts;
using BerMap.Integrations.ActiveDirectory;
using BerMap.Integrations.VMware;

var builder = Host.CreateApplicationBuilder(args);

// Installers keep the production settings outside the program directory, so
// an upgrade that replaces the binaries cannot overwrite them. The file is
// added last and therefore overrides every default source.
var externalConfigFile = Environment.GetEnvironmentVariable("BERMAP_CONFIG_FILE");
if (!string.IsNullOrWhiteSpace(externalConfigFile))
{
    builder.Configuration.AddJsonFile(
        Path.GetFullPath(externalConfigFile, builder.Environment.ContentRootPath),
        optional: false,
        reloadOnChange: false);
}

// Release packages run the collector as a Windows service or a systemd unit;
// both calls are no-ops when the process is started from a console.
builder.Services.AddWindowsService(options => options.ServiceName = "BerMapCollector");
builder.Services.AddSystemd();

// A stop during a multi-batch upload buffers the remaining batches to the offline
// queue (local disk writes) before the process exits; give that time beyond the
// 30 s default. systemd waits 90 s (TimeoutStopSec) before it kills the service.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(60));

// Discovery adapters run in-process, exactly as on the server. They are
// stateless, read-only singletons.
var adapters = new IIntegrationAdapter[] { new ActiveDirectoryAdapter(), new VMwareAdapter() };
foreach (var adapter in adapters)
    builder.Services.AddSingleton<IIntegrationAdapter>(adapter);

var collectorOptions = CollectorOptions.FromConfiguration(builder.Configuration);
var integrations = CollectorIntegrationConfiguration.Load(builder.Configuration, adapters);
collectorOptions.AdvertiseAdapters(integrations.AdapterTypes);
builder.Services.AddSingleton(collectorOptions);
builder.Services.AddSingleton(integrations);

// Register Offline Buffer Queue (under the writable collector data directory)
builder.Services.AddSingleton<IOfflineQueue>(sp => new LocalBufferQueue(
    collectorOptions.OfflineQueueDirectory,
    collectorOptions.QueueMaxDiskMb,
    sp.GetRequiredService<ILogger<LocalBufferQueue>>()));

// Register HTTP Client for BerMap Server (outbound-only). The enrollment token
// authenticates every collector call (register, heartbeat, submit-batch).
// The worker and the discovery scheduler share one client instance, so the
// open-snapshot bookkeeping is shared as well.
builder.Services.AddHttpClient<CollectorClient>(client =>
{
    // Ends with '/', and requests use relative paths: a path prefix
    // (https://proxy.corp.local/bermap) is kept instead of silently dropped.
    client.BaseAddress = collectorOptions.ServerBaseAddress;
    // Large batches are ingested synchronously by the server.
    client.Timeout = TimeSpan.FromSeconds(120);
    if (!string.IsNullOrWhiteSpace(collectorOptions.EnrollmentToken))
        client.DefaultRequestHeaders.TryAddWithoutValidation(CollectorOptions.EnrollmentTokenHeader, collectorOptions.EnrollmentToken);
});
builder.Services.AddSingleton<ICollectorClient>(sp => sp.GetRequiredService<CollectorClient>());

builder.Services.AddSingleton<IIntegrationSecretReader, IntegrationSecretReader>();
builder.Services.AddSingleton<ICollectorDiscoveryRunner, CollectorDiscoveryRunner>();

// Register Background Workers: register → heartbeat → drain, and the discovery scheduler.
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService(sp => new CollectorDiscoveryScheduler(
    sp.GetRequiredService<CollectorIntegrationSet>(),
    sp.GetRequiredService<ICollectorDiscoveryRunner>(),
    sp.GetRequiredService<ICollectorClient>(),
    sp.GetRequiredService<ILogger<CollectorDiscoveryScheduler>>()));

var host = builder.Build();
host.Run();
