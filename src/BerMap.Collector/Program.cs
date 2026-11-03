using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BerMap.Collector;
using BerMap.Collector.Services;

var builder = Host.CreateApplicationBuilder(args);

// Register Offline Buffer Queue
builder.Services.AddSingleton<IOfflineQueue, LocalBufferQueue>();

// Register HTTP Client for BerMap Server (outbound-only)
builder.Services.AddHttpClient<ICollectorClient, CollectorClient>(client =>
{
    var serverUrl = builder.Configuration["BerMap:ServerUrl"] ?? "https://localhost:5001";
    client.BaseAddress = new Uri(serverUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// Register Background Worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
