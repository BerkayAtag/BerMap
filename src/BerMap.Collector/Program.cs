using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BerMap.Collector;
using BerMap.Collector.Services;

var builder = Host.CreateApplicationBuilder(args);

var collectorOptions = CollectorOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(collectorOptions);

// Register Offline Buffer Queue (under the writable collector data directory)
builder.Services.AddSingleton<IOfflineQueue>(sp => new LocalBufferQueue(
    collectorOptions.OfflineQueueDirectory,
    collectorOptions.QueueMaxDiskMb,
    sp.GetRequiredService<ILogger<LocalBufferQueue>>()));

// Register HTTP Client for BerMap Server (outbound-only). The enrollment token
// authenticates every collector call (register, heartbeat, submit-batch).
builder.Services.AddHttpClient<ICollectorClient, CollectorClient>(client =>
{
    client.BaseAddress = new Uri(collectorOptions.ServerUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
    if (!string.IsNullOrWhiteSpace(collectorOptions.EnrollmentToken))
        client.DefaultRequestHeaders.TryAddWithoutValidation(CollectorOptions.EnrollmentTokenHeader, collectorOptions.EnrollmentToken);
});

// Register Background Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
