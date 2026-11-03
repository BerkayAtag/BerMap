using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BerMap.Contracts;

namespace BerMap.Integrations.VMware;

/// <summary>
/// Real VMware vCenter discovery adapter.
///
/// Primary data comes from the vSphere Automation REST API:
///   POST   /api/session
///   GET    /api/vcenter/datacenter
///   GET    /api/vcenter/cluster
///   GET    /api/vcenter/host
///   GET    /api/vcenter/vm
///   GET    /api/vcenter/datastore
///   GET    /api/vcenter/network
///   GET    /api/vcenter/resource-pool
///   GET    /api/vcenter/vm/{vm}
///   GET    /api/vcenter/host/{host}/hardware
///   GET    /api/vcenter/host?filter.clusters={cluster}
///   GET    /api/vcenter/vm?filter.hosts={host}
///
/// Optional SOAP enrichment (enabled by default) reads host/VM quickStats from
/// /sdk using a read-only session. If SOAP is unavailable, discovery still
/// completes with REST capacity data and an explicit warning.
/// </summary>
public sealed class VMwareAdapter : IIntegrationAdapter
{
    public string AdapterType => "VMware";
    public string DisplayName => "VMware vCenter Server (vSphere REST + SOAP)";
    public string Version => "2.0.0";

    public AdapterCapabilities GetCapabilities() => new()
    {
        SupportsFullDiscovery = true,
        SupportsIncremental = false,
        SupportsCredentialsValidation = true,
        SupportsHealthCheck = true,
        SupportsMetrics = true,
        MaxConcurrency = 8,
        EstimatedDiscoveryDuration = TimeSpan.FromMinutes(15),
        SupportedEntityTypes = new List<string>
        {
            "Datacenter", "Cluster", "Hypervisor", "VirtualMachine", "Datastore",
            "Network", "ResourcePool"
        },
        SupportedRelationshipTypes = new List<string>
        {
            "Contains", "MemberOf", "HostedOn", "ConnectedTo", "UsesStorage", "MountedOn"
        }
    };

    public AdapterConfigurationSchema GetConfigurationSchema() => new()
    {
        SecretFields = new List<string> { "password" },
        Notes = new List<string>
        {
            "Use a vCenter service account with the Read-Only role at the root inventory level.",
            "TLS certificate validation is strict by default. Disable it only for isolated lab environments.",
            "SOAP quickStats enrichment requires read access to the PerformanceManager/PropertyCollector.",
            "Discovery never changes VM, host, datastore or network state."
        },
        JsonSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["server", "username"],
          "properties": {
            "server": { "type": "string", "title": "vCenter Server", "examples": ["vcenter.corp.local"] },
            "port": { "type": "integer", "title": "Port", "default": 443 },
            "username": { "type": "string", "title": "Read-only service account" },
            "allowUntrustedCertificate": { "type": "boolean", "title": "Allow untrusted TLS certificate", "default": false },
            "ignoreSslErrors": { "type": "boolean", "title": "Alias for allowUntrustedCertificate", "default": false },
            "timeoutSeconds": { "type": "integer", "title": "HTTP timeout (seconds)", "default": 30 },
            "maxConcurrency": { "type": "integer", "title": "Concurrent API calls", "default": 8 },
            "retryCount": { "type": "integer", "title": "Retry count for 429/5xx", "default": 3 },
            "collectMetrics": { "type": "boolean", "title": "Collect capacity metrics", "default": true },
            "useSoapMetrics": { "type": "boolean", "title": "Enrich with SOAP quickStats", "default": true },
            "includePoweredOffVms": { "type": "boolean", "title": "Include powered-off VMs", "default": true }
          }
        }
        """
    };

    public Task<ConnectionTestResult> TestConnectionAsync(AdapterExecutionContext context, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => TestConnectionCoreAsync(context, cancellationToken), cancellationToken);
    }

    public Task<DiscoveryResult> DiscoverAsync(AdapterExecutionContext context, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DiscoverCoreAsync(context, progress, cancellationToken), cancellationToken);
    }

    private static async Task<ConnectionTestResult> TestConnectionCoreAsync(AdapterExecutionContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        try
        {
            var config = VMwareConfiguration.Parse(context, warnings);
            await using var client = new VCenterRestClient(config, warnings);
            await client.ConnectAsync(cancellationToken);
            var about = await client.GetAsync("api/vcenter/system/version", cancellationToken);
            string? version = null;
            if (about != null)
            {
                version = JsonHelpers.GetString(about.RootElement, "product_version")
                    ?? JsonHelpers.GetString(about.RootElement, "version");
            }

            sw.Stop();
            return new ConnectionTestResult
            {
                Success = true,
                Message = "vCenter session opened with read-only credentials.",
                Latency = sw.Elapsed,
                ServerVersion = version,
                Warnings = warnings,
                Details = new Dictionary<string, string>
                {
                    ["apiVersion"] = version ?? "unknown",
                    ["tlsValidation"] = config.AllowUntrustedCertificate ? "disabled" : "strict"
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new ConnectionTestResult
            {
                Success = false,
                Message = "VMware vCenter connection test failed.",
                Latency = sw.Elapsed,
                ErrorCode = ex is AdapterConfigurationException config ? config.Code : "VMWARE_CONNECTION_ERROR",
                ErrorDetail = SafeMessage(ex),
                Warnings = warnings
            };
        }
    }

    private static async Task<DiscoveryResult> DiscoverCoreAsync(AdapterExecutionContext context, IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = new DiscoveryResult();
        var config = VMwareConfiguration.Parse(context, result.Warnings);
        var nodes = new Dictionary<string, DiscoveredNodeContract>(StringComparer.OrdinalIgnoreCase);
        var relationships = new Dictionary<string, DiscoveredRelationshipContract>(StringComparer.OrdinalIgnoreCase);

        void AddNode(DiscoveredNodeContract node)
        {
            if (string.IsNullOrWhiteSpace(node.ExternalId))
                return;
            node.SourceSystem = "VMware";
            node.DiscoveredAt = DateTime.UtcNow;
            nodes[node.ExternalId] = node;
        }

        void AddRelationship(string source, string target, string type, string evidence, string confidence = "Verified", Dictionary<string, string>? props = null)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) ||
                string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return;
            var key = $"{source}|{type}|{target}";
            relationships[key] = new DiscoveredRelationshipContract
            {
                SourceExternalId = source,
                TargetExternalId = target,
                RelationshipType = type,
                ConfidenceLevel = confidence,
                Evidence = evidence,
                SourceSystem = "VMware",
                DiscoveredAt = DateTime.UtcNow,
                Properties = props ?? new Dictionary<string, string>()
            };
        }

        void Report(string phase, string message, int percent)
        {
            progress?.Report(new DiscoveryProgress
            {
                Phase = phase,
                Message = message,
                Percent = Math.Clamp(percent, 0, 100),
                NodesDiscovered = nodes.Count,
                RelationshipsDiscovered = relationships.Count
            });
        }

        VCenterRestClient? client = null;
        try
        {
            Report("Connect", "Opening read-only vCenter session...", 2);
            client = new VCenterRestClient(config, result.Warnings);
            await client.ConnectAsync(cancellationToken);

            Report("Inventory", "Reading datacenters, clusters, hosts and VMs...", 8);
            var datacenters = await client.GetDatacentersAsync(cancellationToken);
            var clusters = await client.GetClustersAsync(cancellationToken);
            var hosts = await client.GetHostsAsync(cancellationToken);
            var vmSummaries = await client.GetVmSummariesAsync(cancellationToken);
            var datastores = await client.GetDatastoresAsync(cancellationToken);
            var networks = await client.GetNetworksAsync(cancellationToken);
            var resourcePools = await client.GetResourcePoolsAsync(cancellationToken);

            result.Statistics["Datacenters"] = datacenters.Count;
            result.Statistics["Clusters"] = clusters.Count;
            result.Statistics["Hosts"] = hosts.Count;
            result.Statistics["Vms"] = vmSummaries.Count;
            result.Statistics["Datastores"] = datastores.Count;
            result.Statistics["Networks"] = networks.Count;
            result.Statistics["ResourcePools"] = resourcePools.Count;

            Report("Hosts", "Reading ESXi host hardware and cluster membership...", 20);
            var hostHardware = new ConcurrentDictionary<string, HostHardware>(StringComparer.OrdinalIgnoreCase);
            await ForEachConcurrentAsync(hosts, config.MaxConcurrency, async host =>
            {
                try
                {
                    var hardware = await client.GetHostHardwareAsync(host.Id, cancellationToken);
                    if (hardware != null)
                        hostHardware[host.Id] = hardware;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (result.Warnings)
                        result.Warnings.Add($"Host hardware query failed for {host.Name}: {SafeMessage(ex)}");
                }
            }, cancellationToken);

            var clusterHosts = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            await ForEachConcurrentAsync(clusters, config.MaxConcurrency, async cluster =>
            {
                try
                {
                    var ids = await client.GetHostIdsForClusterAsync(cluster.Id, cancellationToken);
                    clusterHosts[cluster.Id] = ids;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (result.Warnings)
                        result.Warnings.Add($"Cluster host membership query failed for {cluster.Name}: {SafeMessage(ex)}");
                }
            }, cancellationToken);

            Report("Topology", "Reading VM-to-host placement...", 34);
            var vmToHost = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await ForEachConcurrentAsync(hosts, config.MaxConcurrency, async host =>
            {
                try
                {
                    var vmIds = await client.GetVmIdsForHostAsync(host.Id, cancellationToken);
                    foreach (var vmId in vmIds)
                        vmToHost[vmId] = host.Id;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (result.Warnings)
                        result.Warnings.Add($"VM placement query failed for host {host.Name}: {SafeMessage(ex)}");
                }
            }, cancellationToken);

            Report("VMs", "Reading VM identity, guest, disk and NIC details...", 48);
            var vmDetails = new ConcurrentDictionary<string, VmDetail>(StringComparer.OrdinalIgnoreCase);
            var processedVms = 0;
            await ForEachConcurrentAsync(vmSummaries, config.MaxConcurrency, async vm =>
            {
                try
                {
                    var detail = await client.GetVmDetailAsync(vm.Id, cancellationToken);
                    if (detail != null)
                        vmDetails[vm.Id] = detail;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (result.Warnings)
                        result.Warnings.Add($"VM detail query failed for {vm.Name}: {SafeMessage(ex)}");
                }
                finally
                {
                    var count = Interlocked.Increment(ref processedVms);
                    if (count % 50 == 0 || count == vmSummaries.Count)
                        Report("VMs", $"Read {count}/{vmSummaries.Count} VM details...", 48 + (int)(20.0 * count / Math.Max(1, vmSummaries.Count)));
                }
            }, cancellationToken);

            // Optional SOAP quickStats enrichment.
            var hostStats = new Dictionary<string, QuickStats>(StringComparer.OrdinalIgnoreCase);
            var vmStats = new Dictionary<string, QuickStats>(StringComparer.OrdinalIgnoreCase);
            if (config.CollectMetrics && config.UseSoapMetrics)
            {
                Report("Metrics", "Reading host/VM quickStats over SOAP...", 72);
                try
                {
                    await using var soap = new VMwareSoapClient(config, result.Warnings);
                    await soap.ConnectAsync(cancellationToken);
                    var stats = await soap.RetrieveQuickStatsAsync(cancellationToken);
                    hostStats = stats.Where(kv => kv.Key.StartsWith("host-", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                    vmStats = stats.Where(kv => kv.Key.StartsWith("vm-", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Warnings.Add($"SOAP quickStats enrichment failed; capacity-only metrics will be used: {SafeMessage(ex)}");
                }
            }

            Report("Graph", "Building normalized vCenter graph...", 80);

            var datacenterById = datacenters.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var clusterById = clusters.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            var hostById = hosts.ToDictionary(h => h.Id, StringComparer.OrdinalIgnoreCase);
            var datastoreById = datastores.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var datastoreByName = datastores
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var networkById = networks.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            var resourcePoolById = resourcePools.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            var hostToCluster = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (clusterId, hostIds) in clusterHosts)
                foreach (var hostId in hostIds)
                    hostToCluster[hostId] = clusterId;

            // Datacenters.
            foreach (var dc in datacenters)
            {
                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = $"datacenter:{dc.Id}",
                    NodeType = "Datacenter",
                    Name = dc.Name,
                    FQDN = string.Empty,
                    Status = "healthy",
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = dc.Id,
                        ["Source"] = "vSphere REST /api/vcenter/datacenter"
                    }
                });
            }

            // Clusters.
            foreach (var cluster in clusters)
            {
                var clusterHostIds = clusterHosts.GetValueOrDefault(cluster.Id) ?? new List<string>();
                var clusterHostHardware = clusterHostIds
                    .Select(id => hostHardware.GetValueOrDefault(id))
                    .Where(h => h != null)
                    .Cast<HostHardware>()
                    .ToList();
                var totalCpuMhz = clusterHostHardware.Sum(h => h.CpuCapacityMhz);
                var totalMemoryMb = clusterHostHardware.Sum(h => h.MemoryMb);
                var clusterCpuUsage = clusterHostIds.Sum(id => hostStats.GetValueOrDefault(id)?.CpuUsageMhz ?? 0);
                var clusterMemoryUsage = clusterHostIds.Sum(id => hostStats.GetValueOrDefault(id)?.MemoryUsageMb ?? 0);
                var clusterVmCount = vmToHost.Count(kv => clusterHostIds.Contains(kv.Value, StringComparer.OrdinalIgnoreCase));

                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = $"cluster:{cluster.Id}",
                    NodeType = "Cluster",
                    Name = cluster.Name,
                    FQDN = string.Empty,
                    Status = "healthy",
                    ParentExternalId = datacenters.Count == 1 ? $"datacenter:{datacenters[0].Id}" : null,
                    ParentName = datacenters.Count == 1 ? datacenters[0].Name : null,
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = cluster.Id,
                        ["HaEnabled"] = cluster.HaEnabled.ToString(),
                        ["DrsEnabled"] = cluster.DrsEnabled.ToString(),
                        ["HostCount"] = clusterHostIds.Count.ToString(CultureInfo.InvariantCulture),
                        ["VmCount"] = clusterVmCount.ToString(CultureInfo.InvariantCulture),
                        ["CpuCapacityMhz"] = totalCpuMhz.ToString("F1", CultureInfo.InvariantCulture),
                        ["MemoryCapacityMb"] = totalMemoryMb.ToString("F1", CultureInfo.InvariantCulture),
                        ["CpuUsageMhz"] = clusterCpuUsage.ToString("F1", CultureInfo.InvariantCulture),
                        ["MemoryUsageMb"] = clusterMemoryUsage.ToString("F1", CultureInfo.InvariantCulture),
                        ["CpuUsagePercent"] = Percent(clusterCpuUsage, totalCpuMhz).ToString("F2", CultureInfo.InvariantCulture),
                        ["MemoryUsagePercent"] = Percent(clusterMemoryUsage, totalMemoryMb).ToString("F2", CultureInfo.InvariantCulture),
                        ["Source"] = "vSphere REST + SOAP quickStats"
                    }
                });
            }

            // Datastores.
            foreach (var datastore in datastores)
            {
                var used = Math.Max(0, datastore.Capacity - datastore.FreeSpace);
                var usage = datastore.Capacity > 0 ? 100.0 * used / datastore.Capacity : 0;
                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = $"datastore:{datastore.Id}",
                    NodeType = "Datastore",
                    Name = datastore.Name,
                    FQDN = string.Empty,
                    Status = usage >= 90 ? "critical" : usage >= 80 ? "warning" : "healthy",
                    ParentExternalId = datacenters.Count == 1 ? $"datacenter:{datacenters[0].Id}" : null,
                    ParentName = datacenters.Count == 1 ? datacenters[0].Name : null,
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = datastore.Id,
                        ["Type"] = datastore.Type,
                        ["CapacityBytes"] = datastore.Capacity.ToString(CultureInfo.InvariantCulture),
                        ["FreeSpaceBytes"] = datastore.FreeSpace.ToString(CultureInfo.InvariantCulture),
                        ["CapacityGb"] = BytesToGb(datastore.Capacity).ToString("F2", CultureInfo.InvariantCulture),
                        ["FreeGb"] = BytesToGb(datastore.FreeSpace).ToString("F2", CultureInfo.InvariantCulture),
                        ["UsedGb"] = BytesToGb(used).ToString("F2", CultureInfo.InvariantCulture),
                        ["UsagePercent"] = usage.ToString("F2", CultureInfo.InvariantCulture),
                        ["Accessible"] = datastore.Accessible.ToString(),
                        ["Source"] = "vSphere REST /api/vcenter/datastore"
                    }
                });
            }

            // Networks.
            foreach (var network in networks)
            {
                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = $"network:{network.Id}",
                    NodeType = "Network",
                    Name = network.Name,
                    FQDN = string.Empty,
                    Status = network.Accessible ? "healthy" : "warning",
                    ParentExternalId = datacenters.Count == 1 ? $"datacenter:{datacenters[0].Id}" : null,
                    ParentName = datacenters.Count == 1 ? datacenters[0].Name : null,
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = network.Id,
                        ["Type"] = network.Type,
                        ["Accessible"] = network.Accessible.ToString(),
                        ["Source"] = "vSphere REST /api/vcenter/network"
                    }
                });
            }

            // Resource pools.
            foreach (var pool in resourcePools)
            {
                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = $"resourcepool:{pool.Id}",
                    NodeType = "ResourcePool",
                    Name = pool.Name,
                    FQDN = string.Empty,
                    Status = "healthy",
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = pool.Id,
                        ["Source"] = "vSphere REST /api/vcenter/resource-pool"
                    }
                });
            }

            // Hosts.
            foreach (var host in hosts)
            {
                var hardware = hostHardware.GetValueOrDefault(host.Id);
                var stats = hostStats.GetValueOrDefault(host.Id);
                var cpuCapacity = hardware?.CpuCapacityMhz ?? 0;
                var memoryMb = hardware?.MemoryMb ?? 0;
                var clusterId = hostToCluster.GetValueOrDefault(host.Id);
                var clusterName = clusterId != null && clusterById.TryGetValue(clusterId, out var cluster) ? cluster.Name : string.Empty;
                var dcId = datacenters.Count == 1 ? datacenters[0].Id : string.Empty;
                var dcName = datacenters.Count == 1 ? datacenters[0].Name : string.Empty;

                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = $"host:{host.Id}",
                    NodeType = "Hypervisor",
                    Name = host.Name,
                    FQDN = host.Name.Contains('.') ? host.Name : string.Empty,
                    IPAddress = IPAddress.TryParse(host.Name, out var hostIp) ? hostIp.ToString() : null,
                    Status = host.ConnectionState switch
                    {
                        "CONNECTED" => "healthy",
                        "DISCONNECTED" => "critical",
                        _ => "warning"
                    },
                    BiosUuid = hardware?.HardwareUuid ?? string.Empty,
                    ParentExternalId = clusterId != null ? $"cluster:{clusterId}" : dcId.Length > 0 ? $"datacenter:{dcId}" : null,
                    ParentName = !string.IsNullOrWhiteSpace(clusterName) ? clusterName : dcName,
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = host.Id,
                        ["ConnectionState"] = host.ConnectionState,
                        ["PowerState"] = host.PowerState,
                        ["Cluster"] = clusterName,
                        ["Datacenter"] = dcName,
                        ["Vendor"] = hardware?.Vendor ?? string.Empty,
                        ["Model"] = hardware?.Model ?? string.Empty,
                        ["CpuCores"] = hardware?.CpuCores.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ["CpuThreads"] = hardware?.CpuThreads.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        ["CpuMhz"] = hardware?.CpuMhz.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty,
                        ["CpuCapacityMhz"] = cpuCapacity.ToString("F1", CultureInfo.InvariantCulture),
                        ["MemoryMb"] = memoryMb.ToString("F1", CultureInfo.InvariantCulture),
                        ["CpuUsageMhz"] = (stats?.CpuUsageMhz ?? 0).ToString("F1", CultureInfo.InvariantCulture),
                        ["MemoryUsageMb"] = (stats?.MemoryUsageMb ?? 0).ToString("F1", CultureInfo.InvariantCulture),
                        ["CpuUsagePercent"] = Percent(stats?.CpuUsageMhz ?? 0, cpuCapacity).ToString("F2", CultureInfo.InvariantCulture),
                        ["MemoryUsagePercent"] = Percent(stats?.MemoryUsageMb ?? 0, memoryMb).ToString("F2", CultureInfo.InvariantCulture),
                        ["MetricsSource"] = stats != null ? "SOAP quickStats" : "REST capacity only",
                        ["Source"] = "vSphere REST + SOAP quickStats"
                    }
                });

                if (clusterId != null)
                    AddRelationship($"cluster:{clusterId}", $"host:{host.Id}", "Contains", "vCenter cluster host membership", "Verified");
                else if (dcId.Length > 0)
                    AddRelationship($"datacenter:{dcId}", $"host:{host.Id}", "Contains", "vCenter datacenter host membership", "Verified");
            }

            // Virtual machines.
            foreach (var summary in vmSummaries)
            {
                if (!config.IncludePoweredOffVms && string.Equals(summary.PowerState, "POWERED_OFF", StringComparison.OrdinalIgnoreCase))
                    continue;

                var detail = vmDetails.GetValueOrDefault(summary.Id);
                var stats = vmStats.GetValueOrDefault(summary.Id);
                var hostId = vmToHost.GetValueOrDefault(summary.Id);
                var clusterId = hostId != null ? hostToCluster.GetValueOrDefault(hostId) : null;
                var clusterName = clusterId != null && clusterById.TryGetValue(clusterId, out var cluster) ? cluster.Name : string.Empty;
                var hostName = hostId != null && hostById.TryGetValue(hostId, out var host) ? host.Name : string.Empty;
                var dcId = datacenters.Count == 1 ? datacenters[0].Id : string.Empty;
                var dcName = datacenters.Count == 1 ? datacenters[0].Name : string.Empty;
                var cpuCount = detail?.CpuCount ?? summary.CpuCount;
                var memoryMb = detail?.MemoryMb ?? summary.MemoryMiB;
                var cpuPercent = Percent(stats?.CpuUsageMhz ?? 0, cpuCount * (hostHardware.GetValueOrDefault(hostId ?? string.Empty)?.CpuMhz ?? 0));
                var memoryPercent = Percent(stats?.MemoryUsageMb ?? 0, memoryMb);

                var node = new DiscoveredNodeContract
                {
                    ExternalId = $"vm:{summary.Id}",
                    NodeType = "VirtualMachine",
                    Name = summary.Name,
                    FQDN = detail?.Fqdn ?? (summary.Name.Contains('.') ? summary.Name : string.Empty),
                    IPAddress = detail?.IpAddress,
                    MacAddress = detail?.MacAddress,
                    Status = summary.PowerState switch
                    {
                        "POWERED_ON" => "healthy",
                        "SUSPENDED" => "warning",
                        "POWERED_OFF" => "unknown",
                        _ => "unknown"
                    },
                    BiosUuid = detail?.BiosUuid ?? string.Empty,
                    VmInstanceUuid = detail?.InstanceUuid ?? string.Empty,
                    ParentExternalId = hostId != null ? $"host:{hostId}" : null,
                    ParentName = hostName,
                    Properties = new Dictionary<string, string>
                    {
                        ["VCenterId"] = summary.Id,
                        ["PowerState"] = summary.PowerState,
                        ["CpuCount"] = cpuCount.ToString(CultureInfo.InvariantCulture),
                        ["CoresPerSocket"] = (detail?.CoresPerSocket ?? 0).ToString(CultureInfo.InvariantCulture),
                        ["MemoryMb"] = memoryMb.ToString("F1", CultureInfo.InvariantCulture),
                        ["GuestOS"] = detail?.GuestOs ?? string.Empty,
                        ["GuestHostName"] = detail?.GuestHostName ?? string.Empty,
                        ["GuestIpAddress"] = detail?.IpAddress ?? string.Empty,
                        ["ToolsStatus"] = detail?.ToolsStatus ?? string.Empty,
                        ["HostId"] = hostId ?? string.Empty,
                        ["HostName"] = hostName,
                        ["ClusterId"] = clusterId ?? string.Empty,
                        ["Cluster"] = clusterName,
                        ["Datacenter"] = dcName,
                        ["ResourcePool"] = detail?.ResourcePoolId ?? string.Empty,
                        ["Datastores"] = string.Join(",", detail?.DatastoreNames ?? new List<string>()),
                        ["Networks"] = string.Join(",", detail?.NetworkIds ?? new List<string>()),
                        ["DiskCount"] = (detail?.DiskCount ?? 0).ToString(CultureInfo.InvariantCulture),
                        ["NicCount"] = (detail?.NicCount ?? 0).ToString(CultureInfo.InvariantCulture),
                        ["CpuUsageMhz"] = (stats?.CpuUsageMhz ?? 0).ToString("F1", CultureInfo.InvariantCulture),
                        ["MemoryUsageMb"] = (stats?.MemoryUsageMb ?? 0).ToString("F1", CultureInfo.InvariantCulture),
                        ["CpuUsagePercent"] = cpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                        ["MemoryUsagePercent"] = memoryPercent.ToString("F2", CultureInfo.InvariantCulture),
                        ["MetricsSource"] = stats != null ? "SOAP quickStats" : "vCenter summary",
                        ["Source"] = "vSphere REST + SOAP quickStats"
                    }
                };
                AddNode(node);

                if (hostId != null)
                    AddRelationship($"vm:{summary.Id}", $"host:{hostId}", "HostedOn", "vCenter VM host placement (filter.hosts)", "Verified");
                if (clusterId != null)
                    AddRelationship($"vm:{summary.Id}", $"cluster:{clusterId}", "MemberOf", "VM host cluster membership", "Verified");
                if (!string.IsNullOrWhiteSpace(detail?.ResourcePoolId) && resourcePoolById.ContainsKey(detail.ResourcePoolId))
                    AddRelationship($"vm:{summary.Id}", $"resourcepool:{detail.ResourcePoolId}", "MemberOf", "vCenter VM resource pool", "Verified");

                foreach (var datastoreName in detail?.DatastoreNames ?? new List<string>())
                {
                    if (datastoreByName.TryGetValue(datastoreName, out var ds))
                    {
                        AddRelationship($"vm:{summary.Id}", $"datastore:{ds.Id}", "UsesStorage",
                            "VM disk backing vmdk_file datastore", "Verified",
                            new Dictionary<string, string> { ["Datastore"] = datastoreName });
                        if (hostId != null)
                            AddRelationship($"host:{hostId}", $"datastore:{ds.Id}", "MountedOn",
                                "Observed via VM disk placement on this host", "Observed");
                    }
                    else
                    {
                        result.Warnings.Add($"VM '{summary.Name}' uses datastore '{datastoreName}' which was not returned by the datastore API.");
                    }
                }

                foreach (var networkId in detail?.NetworkIds ?? new List<string>())
                {
                    if (networkById.ContainsKey(networkId))
                        AddRelationship($"vm:{summary.Id}", $"network:{networkId}", "ConnectedTo",
                            "VM NIC backing network", "Verified");
                }

                // If datacenter/cluster parent is unambiguous, expose it as a container relationship too.
                if (clusterId != null && clusterById.ContainsKey(clusterId))
                    AddRelationship($"cluster:{clusterId}", $"vm:{summary.Id}", "Contains", "VM placement in cluster", "Observed");
                else if (dcId.Length > 0)
                    AddRelationship($"datacenter:{dcId}", $"vm:{summary.Id}", "Contains", "VM placement in datacenter", "Observed");
            }

            // Datacenter containment for datastores, networks and clusters.
            if (datacenters.Count == 1)
            {
                var dcExternal = $"datacenter:{datacenters[0].Id}";
                foreach (var cluster in clusters)
                    AddRelationship(dcExternal, $"cluster:{cluster.Id}", "Contains", "vCenter datacenter cluster membership", "Verified");
                foreach (var datastore in datastores)
                    AddRelationship(dcExternal, $"datastore:{datastore.Id}", "Contains", "vCenter datacenter datastore membership", "Verified");
                foreach (var network in networks)
                    AddRelationship(dcExternal, $"network:{network.Id}", "Contains", "vCenter datacenter network membership", "Verified");
            }

            result.Nodes = nodes.Values.ToList();
            result.Relationships = relationships.Values.ToList();
            result.Success = true;
            result.Status = result.Errors.Count > 0 ? "PartialSuccess" : "Completed";
            result.Statistics["Nodes"] = result.Nodes.Count;
            result.Statistics["Relationships"] = result.Relationships.Count;
            result.Statistics["VirtualMachines"] = result.Nodes.Count(n => n.NodeType == "VirtualMachine");
            result.Statistics["Hypervisors"] = result.Nodes.Count(n => n.NodeType == "Hypervisor");
            result.Statistics["Datastores"] = result.Nodes.Count(n => n.NodeType == "Datastore");
            sw.Stop();
            result.Duration = sw.Elapsed;
            result.CompletedAt = DateTime.UtcNow;
            Report("Completed", $"vCenter discovery completed: {result.Nodes.Count} nodes, {result.Relationships.Count} relationships.", 100);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            result.Success = false;
            result.Status = "Failed";
            result.Errors.Add(SafeMessage(ex));
            result.Nodes = nodes.Values.ToList();
            result.Relationships = relationships.Values.ToList();
            result.Duration = sw.Elapsed;
            result.CompletedAt = DateTime.UtcNow;
            result.Statistics["Nodes"] = result.Nodes.Count;
            result.Statistics["Relationships"] = result.Relationships.Count;
            return result;
        }
        finally
        {
            if (client != null)
                await client.DisposeAsync();
        }
    }

    private static double Percent(double usage, double capacity)
        => capacity > 0 ? Math.Clamp(100.0 * usage / capacity, 0, 100) : 0;

    private static double BytesToGb(long bytes) => bytes / 1024d / 1024d / 1024d;

    private static string SafeMessage(Exception ex)
    {
        if (ex is AdapterConfigurationException)
            return ex.Message;
        return ex.Message;
    }

    private static async Task ForEachConcurrentAsync<T>(
        IEnumerable<T> items,
        int maxConcurrency,
        Func<T, Task> action,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 32));
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await action(item);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    // ---------------------------------------------------------------------
    // REST client
    // ---------------------------------------------------------------------

    private sealed class VCenterRestClient : IAsyncDisposable
    {
        private readonly VMwareConfiguration _config;
        private readonly List<string> _warnings;
        private readonly HttpClient _http;
        private string? _sessionId;

        public VCenterRestClient(VMwareConfiguration config, List<string> warnings)
        {
            _config = config;
            _warnings = warnings;
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };
            if (config.AllowUntrustedCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                warnings.Add("TLS certificate validation is disabled for this integration. This must only be used in an isolated lab.");
            }

            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://{config.Server}:{config.Port}/"),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            _sessionId = null;
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.Username}:{_config.Password}")));
            request.Content = new ByteArrayContent(Array.Empty<byte>());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new AdapterConfigurationException("VMWARE_AUTH",
                    $"vCenter authentication failed ({(int)response.StatusCode} {response.StatusCode}). {Truncate(body, 300)}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(json);
                _sessionId = doc.RootElement.ValueKind == JsonValueKind.String
                    ? doc.RootElement.GetString()
                    : JsonHelpers.GetString(doc.RootElement, "session_id");
            }
            catch (JsonException)
            {
                _sessionId = json.Trim().Trim('"');
            }

            if (string.IsNullOrWhiteSpace(_sessionId))
                throw new AdapterConfigurationException("VMWARE_SESSION", "vCenter returned an empty session id.");
        }

        public async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_sessionId))
                throw new InvalidOperationException("vCenter session is not established.");

            for (var attempt = 0; attempt <= _config.RetryCount; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.TryAddWithoutValidation("vmware-api-session-id", _sessionId);
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    await ConnectAsync(cancellationToken);
                    continue;
                }

                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) &&
                    attempt < _config.RetryCount)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"vCenter GET {path} failed with {(int)response.StatusCode}: {Truncate(body, 300)}");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(json))
                    return null;
                return JsonDocument.Parse(json);
            }

            return null;
        }

        public async Task<List<VCenterDatacenter>> GetDatacentersAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/datacenter", ct);
            return ParseArray(doc, e => new VCenterDatacenter(
                JsonHelpers.GetString(e, "datacenter") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty))
                .Where(d => d.Id.Length > 0).ToList();
        }

        public async Task<List<VCenterCluster>> GetClustersAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/cluster", ct);
            return ParseArray(doc, e => new VCenterCluster(
                JsonHelpers.GetString(e, "cluster") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty,
                JsonHelpers.GetBool(e, "ha_enabled"),
                JsonHelpers.GetBool(e, "drs_enabled")))
                .Where(c => c.Id.Length > 0).ToList();
        }

        public async Task<List<VCenterHost>> GetHostsAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/host", ct);
            return ParseArray(doc, e => new VCenterHost(
                JsonHelpers.GetString(e, "host") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty,
                JsonHelpers.GetString(e, "connection_state") ?? "UNKNOWN",
                JsonHelpers.GetString(e, "power_state") ?? "UNKNOWN"))
                .Where(h => h.Id.Length > 0).ToList();
        }

        public async Task<List<VCenterVmSummary>> GetVmSummariesAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/vm", ct);
            return ParseArray(doc, e => new VCenterVmSummary(
                JsonHelpers.GetString(e, "vm") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty,
                JsonHelpers.GetString(e, "power_state") ?? "UNKNOWN",
                JsonHelpers.GetInt(e, "cpu_count"),
                JsonHelpers.GetLong(e, "memory_size_MiB")))
                .Where(v => v.Id.Length > 0).ToList();
        }

        public async Task<List<VCenterDatastore>> GetDatastoresAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/datastore", ct);
            return ParseArray(doc, e => new VCenterDatastore(
                JsonHelpers.GetString(e, "datastore") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty,
                JsonHelpers.GetString(e, "type") ?? string.Empty,
                JsonHelpers.GetLong(e, "capacity"),
                JsonHelpers.GetLong(e, "free_space"),
                JsonHelpers.GetBool(e, "accessible")))
                .Where(d => d.Id.Length > 0).ToList();
        }

        public async Task<List<VCenterNetwork>> GetNetworksAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/network", ct);
            return ParseArray(doc, e => new VCenterNetwork(
                JsonHelpers.GetString(e, "network") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty,
                JsonHelpers.GetString(e, "type") ?? string.Empty,
                JsonHelpers.GetBool(e, "accessible")))
                .Where(n => n.Id.Length > 0).ToList();
        }

        public async Task<List<VCenterResourcePool>> GetResourcePoolsAsync(CancellationToken ct)
        {
            using var doc = await GetAsync("api/vcenter/resource-pool", ct);
            return ParseArray(doc, e => new VCenterResourcePool(
                JsonHelpers.GetString(e, "resource_pool") ?? string.Empty,
                JsonHelpers.GetString(e, "name") ?? string.Empty))
                .Where(r => r.Id.Length > 0).ToList();
        }

        public async Task<HostHardware?> GetHostHardwareAsync(string hostId, CancellationToken ct)
        {
            using var doc = await GetAsync($"api/vcenter/host/{Uri.EscapeDataString(hostId)}/hardware", ct);
            if (doc == null)
                return null;
            var root = doc.RootElement;
            var cpu = JsonHelpers.GetProperty(root, "cpu");
            var memory = JsonHelpers.GetProperty(root, "memory");
            var system = JsonHelpers.GetProperty(root, "system");
            return new HostHardware(
                JsonHelpers.GetInt(cpu, "num_cpu_cores"),
                JsonHelpers.GetInt(cpu, "num_cpu_threads"),
                JsonHelpers.GetDouble(cpu, "cpu_mhz"),
                JsonHelpers.GetLong(memory, "size_MiB"),
                JsonHelpers.GetString(system, "vendor") ?? string.Empty,
                JsonHelpers.GetString(system, "model") ?? string.Empty,
                JsonHelpers.GetString(system, "uuid") ?? string.Empty);
        }

        public async Task<List<string>> GetHostIdsForClusterAsync(string clusterId, CancellationToken ct)
        {
            using var doc = await GetAsync($"api/vcenter/host?filter.clusters={Uri.EscapeDataString(clusterId)}", ct);
            return ParseArray(doc, e => JsonHelpers.GetString(e, "host") ?? string.Empty)
                .Where(id => id.Length > 0).ToList();
        }

        public async Task<List<string>> GetVmIdsForHostAsync(string hostId, CancellationToken ct)
        {
            using var doc = await GetAsync($"api/vcenter/vm?filter.hosts={Uri.EscapeDataString(hostId)}", ct);
            return ParseArray(doc, e => JsonHelpers.GetString(e, "vm") ?? string.Empty)
                .Where(id => id.Length > 0).ToList();
        }

        public async Task<VmDetail?> GetVmDetailAsync(string vmId, CancellationToken ct)
        {
            using var doc = await GetAsync($"api/vcenter/vm/{Uri.EscapeDataString(vmId)}", ct);
            if (doc == null)
                return null;
            var root = doc.RootElement;
            var identity = JsonHelpers.GetProperty(root, "identity");
            var guest = JsonHelpers.GetProperty(root, "guest");
            var cpu = JsonHelpers.GetProperty(root, "cpu");
            var memory = JsonHelpers.GetProperty(root, "memory");

            var fqdn = JsonHelpers.GetString(guest, "host_name") ?? string.Empty;
            var ip = JsonHelpers.GetString(guest, "ip_address");
            if (string.IsNullOrWhiteSpace(ip))
            {
                var guestNet = JsonHelpers.GetProperty(guest, "net");
                if (guestNet.ValueKind == JsonValueKind.Array)
                {
                    foreach (var nic in guestNet.EnumerateArray())
                    {
                        var addresses = JsonHelpers.GetProperty(nic, "ip_address");
                        if (addresses.ValueKind == JsonValueKind.Array)
                        {
                            var candidate = addresses.EnumerateArray()
                                .Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null)
                                .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && a != "127.0.0.1" && !a.Contains(':'));
                            if (!string.IsNullOrWhiteSpace(candidate))
                            {
                                ip = candidate;
                                break;
                            }
                        }
                    }
                }
            }

            var datastores = new List<string>();
            var disks = JsonHelpers.GetProperty(root, "disks");
            foreach (var disk in JsonHelpers.EnumerateObjectValues(disks))
            {
                var vmdk = JsonHelpers.GetString(JsonHelpers.GetProperty(disk, "backing"), "vmdk_file");
                if (string.IsNullOrWhiteSpace(vmdk))
                    continue;
                var open = vmdk.IndexOf('[');
                var close = vmdk.IndexOf(']');
                if (open >= 0 && close > open)
                {
                    var name = vmdk[(open + 1)..close];
                    if (!datastores.Contains(name, StringComparer.OrdinalIgnoreCase))
                        datastores.Add(name);
                }
            }

            var networkIds = new List<string>();
            var macAddress = string.Empty;
            var nics = JsonHelpers.GetProperty(root, "nics");
            foreach (var nic in JsonHelpers.EnumerateObjectValues(nics))
            {
                var mac = JsonHelpers.GetString(nic, "mac_address");
                if (string.IsNullOrWhiteSpace(macAddress) && !string.IsNullOrWhiteSpace(mac))
                    macAddress = mac;
                var backing = JsonHelpers.GetProperty(nic, "backing");
                var network = JsonHelpers.GetString(backing, "network");
                if (!string.IsNullOrWhiteSpace(network) && !networkIds.Contains(network, StringComparer.OrdinalIgnoreCase))
                    networkIds.Add(network);
            }

            return new VmDetail
            {
                BiosUuid = JsonHelpers.GetString(identity, "bios_uuid") ?? string.Empty,
                InstanceUuid = JsonHelpers.GetString(identity, "instance_uuid") ?? string.Empty,
                GuestHostName = JsonHelpers.GetString(guest, "host_name") ?? string.Empty,
                Fqdn = fqdn,
                IpAddress = ip,
                MacAddress = macAddress,
                GuestOs = JsonHelpers.GetString(guest, "guest_full_name")
                    ?? JsonHelpers.GetString(guest, "guest_id")
                    ?? string.Empty,
                ToolsStatus = JsonHelpers.GetString(guest, "tools_status") ?? string.Empty,
                CpuCount = JsonHelpers.GetInt(cpu, "count"),
                CoresPerSocket = JsonHelpers.GetInt(cpu, "cores_per_socket"),
                MemoryMb = JsonHelpers.GetLong(memory, "size_MiB"),
                DatastoreNames = datastores,
                NetworkIds = networkIds,
                DiskCount = JsonHelpers.CountObjectValues(disks),
                NicCount = JsonHelpers.CountObjectValues(nics),
                ResourcePoolId = JsonHelpers.GetString(root, "resource_pool") ?? string.Empty
            };
        }

        private static IEnumerable<T> ParseArray<T>(JsonDocument? doc, Func<JsonElement, T> map)
        {
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<T>();
            return doc.RootElement.EnumerateArray().Select(map).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            if (!string.IsNullOrWhiteSpace(_sessionId))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Delete, "api/session");
                    request.Headers.TryAddWithoutValidation("vmware-api-session-id", _sessionId);
                    using var response = await _http.SendAsync(request);
                }
                catch
                {
                    // Session cleanup is best effort.
                }
            }
            _http.Dispose();
        }

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..max];
    }

    // ---------------------------------------------------------------------
    // SOAP quickStats client
    // ---------------------------------------------------------------------

    private sealed class VMwareSoapClient : IAsyncDisposable
    {
        private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace Vim = "urn:vim25";
        private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

        private readonly VMwareConfiguration _config;
        private readonly List<string> _warnings;
        private readonly HttpClient _http;
        private string _propertyCollector = "propertyCollector";
        private string _sessionManager = "sessionManager";
        private string _viewManager = "viewManager";
        private string _rootFolder = "group-h5";
        private bool _loggedIn;

        public VMwareSoapClient(VMwareConfiguration config, List<string> warnings)
        {
            _config = config;
            _warnings = warnings;
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };
            if (config.AllowUntrustedCertificate)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://{config.Server}:{config.Port}/"),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var serviceContent = await PostAsync(new XElement(Vim + "RetrieveServiceContent",
                new XElement(Vim + "_this", new XAttribute("type", "ServiceInstance"), "ServiceInstance")), cancellationToken);

            var returnval = serviceContent.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval");
            if (returnval == null)
                throw new InvalidOperationException("vCenter SOAP RetrieveServiceContent returned no service content.");

            _propertyCollector = ChildValue(returnval, "propertyCollector") ?? _propertyCollector;
            _sessionManager = ChildValue(returnval, "sessionManager") ?? _sessionManager;
            _viewManager = ChildValue(returnval, "viewManager") ?? _viewManager;
            _rootFolder = ChildValue(returnval, "rootFolder") ?? _rootFolder;

            await PostAsync(new XElement(Vim + "Login",
                new XElement(Vim + "_this", new XAttribute("type", "SessionManager"), _sessionManager),
                new XElement(Vim + "userName", _config.Username),
                new XElement(Vim + "password", _config.Password)), cancellationToken);
            _loggedIn = true;
        }

        public async Task<Dictionary<string, QuickStats>> RetrieveQuickStatsAsync(CancellationToken cancellationToken)
        {
            var view = await CreateContainerViewAsync(cancellationToken);
            var stats = new Dictionary<string, QuickStats>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string? token = null;
                do
                {
                    var response = token == null
                        ? await RetrievePropertiesAsync(view, cancellationToken)
                        : await ContinueRetrievePropertiesAsync(token, cancellationToken);

                    var objects = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "objects");
                    if (objects != null)
                        ParseObjects(objects, stats);

                    token = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "token")?.Value;
                } while (!string.IsNullOrWhiteSpace(token));
            }
            finally
            {
                await DestroyViewAsync(view, cancellationToken);
            }
            return stats;
        }

        private async Task<XElement> RetrievePropertiesAsync(string view, CancellationToken cancellationToken)
        {
            var body = new XElement(Vim + "RetrievePropertiesEx",
                new XElement(Vim + "_this", new XAttribute("type", "PropertyCollector"), _propertyCollector),
                new XElement(Vim + "specSet",
                    new XElement(Vim + "propSet",
                        new XElement(Vim + "type", "HostSystem"),
                        new XElement(Vim + "pathSet", "summary.quickStats.overallCpuUsage"),
                        new XElement(Vim + "pathSet", "summary.quickStats.overallMemoryUsage"),
                        new XElement(Vim + "pathSet", "summary.hardware.cpuMhz"),
                        new XElement(Vim + "pathSet", "summary.hardware.numCpuCores"),
                        new XElement(Vim + "pathSet", "summary.hardware.memorySize")),
                    new XElement(Vim + "propSet",
                        new XElement(Vim + "type", "VirtualMachine"),
                        new XElement(Vim + "pathSet", "summary.quickStats.overallCpuUsage"),
                        new XElement(Vim + "pathSet", "summary.quickStats.overallMemoryUsage"),
                        new XElement(Vim + "pathSet", "config.hardware.numCPU"),
                        new XElement(Vim + "pathSet", "config.hardware.memoryMB")),
                    new XElement(Vim + "objectSet",
                        new XElement(Vim + "obj", new XAttribute("type", "ContainerView"), view),
                        new XElement(Vim + "skip", "false"),
                        new XElement(Vim + "selectSet",
                            new XElement(Vim + "name", "view"),
                            new XElement(Vim + "type", "ContainerView"),
                            new XElement(Vim + "path", "view"),
                            new XElement(Vim + "skip", "false")))),
                new XElement(Vim + "options"));
            return await PostAsync(body, cancellationToken);
        }

        private async Task<XElement> ContinueRetrievePropertiesAsync(string token, CancellationToken cancellationToken)
        {
            var body = new XElement(Vim + "ContinueRetrievePropertiesEx",
                new XElement(Vim + "_this", new XAttribute("type", "PropertyCollector"), _propertyCollector),
                new XElement(Vim + "token", token));
            return await PostAsync(body, cancellationToken);
        }

        private async Task<string> CreateContainerViewAsync(CancellationToken cancellationToken)
        {
            var body = new XElement(Vim + "CreateContainerView",
                new XElement(Vim + "_this", new XAttribute("type", "ViewManager"), _viewManager),
                new XElement(Vim + "container", new XAttribute("type", "Folder"), _rootFolder),
                new XElement(Vim + "type", "HostSystem"),
                new XElement(Vim + "type", "VirtualMachine"),
                new XElement(Vim + "recursive", "true"));
            var response = await PostAsync(body, cancellationToken);
            var view = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "returnval")?.Value;
            if (string.IsNullOrWhiteSpace(view))
                throw new InvalidOperationException("vCenter SOAP CreateContainerView returned no view.");
            return view;
        }

        private async Task DestroyViewAsync(string view, CancellationToken cancellationToken)
        {
            try
            {
                var body = new XElement(Vim + "DestroyView",
                    new XElement(Vim + "_this", new XAttribute("type", "ContainerView"), view));
                await PostAsync(body, cancellationToken);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static void ParseObjects(XElement objects, Dictionary<string, QuickStats> stats)
        {
            foreach (var obj in objects.Elements().Where(e => e.Name.LocalName == "objects"))
            {
                var moRef = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "obj")?.Value;
                if (string.IsNullOrWhiteSpace(moRef))
                    continue;

                var quick = stats.GetValueOrDefault(moRef) ?? new QuickStats();
                foreach (var prop in obj.Elements().Where(e => e.Name.LocalName == "propSet"))
                {
                    var name = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                    var value = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "val")?.Value;
                    if (name == null || value == null)
                        continue;

                    switch (name)
                    {
                        case "summary.quickStats.overallCpuUsage":
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuUsage))
                                quick.CpuUsageMhz = cpuUsage;
                            break;
                        case "summary.quickStats.overallMemoryUsage":
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var memUsage))
                                quick.MemoryUsageMb = memUsage;
                            break;
                        case "summary.hardware.cpuMhz":
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpuMhz))
                                quick.CpuMhz = cpuMhz;
                            break;
                        case "summary.hardware.numCpuCores":
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cores))
                                quick.CpuCores = cores;
                            break;
                        case "summary.hardware.memorySize":
                            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memBytes))
                                quick.MemoryBytes = memBytes;
                            break;
                        case "config.hardware.numCPU":
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vmCpu))
                                quick.VmCpuCount = vmCpu;
                            break;
                        case "config.hardware.memoryMB":
                            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vmMem))
                                quick.VmMemoryMb = vmMem;
                            break;
                    }
                }
                stats[moRef] = quick;
            }
        }

        private async Task<XElement> PostAsync(XElement body, CancellationToken cancellationToken)
        {
            var envelope = new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", Soap),
                new XAttribute(XNamespace.Xmlns + "urn", Vim),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XAttribute(XNamespace.Xmlns + "xsd", Xsd),
                new XElement(Soap + "Body", body));

            using var request = new HttpRequestMessage(HttpMethod.Post, "sdk")
            {
                Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"urn:vim25/8.0\"");

            using var response = await _http.SendAsync(request, cancellationToken);
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"vCenter SOAP returned invalid XML ({(int)response.StatusCode}): {ex.Message}");
            }

            var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault != null)
            {
                var message = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                    ?? fault.Value;
                throw new InvalidOperationException($"vCenter SOAP fault: {message}");
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"vCenter SOAP request failed with {(int)response.StatusCode} {response.StatusCode}.");

            return doc.Root ?? throw new InvalidOperationException("vCenter SOAP returned an empty document.");
        }

        private static string? ChildValue(XElement parent, string localName)
            => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

        public async ValueTask DisposeAsync()
        {
            if (_loggedIn)
            {
                try
                {
                    await PostAsync(new XElement(Vim + "Logout",
                        new XElement(Vim + "_this", new XAttribute("type", "SessionManager"), _sessionManager)), CancellationToken.None);
                }
                catch
                {
                    // Best effort.
                }
            }
            _http.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // DTOs and configuration
    // ---------------------------------------------------------------------

    private sealed record VCenterDatacenter(string Id, string Name);
    private sealed record VCenterCluster(string Id, string Name, bool HaEnabled, bool DrsEnabled);
    private sealed record VCenterHost(string Id, string Name, string ConnectionState, string PowerState);
    private sealed record VCenterVmSummary(string Id, string Name, string PowerState, int CpuCount, long MemoryMiB);
    private sealed record VCenterDatastore(string Id, string Name, string Type, long Capacity, long FreeSpace, bool Accessible);
    private sealed record VCenterNetwork(string Id, string Name, string Type, bool Accessible);
    private sealed record VCenterResourcePool(string Id, string Name);
    private sealed record HostHardware(int CpuCores, int CpuThreads, double CpuMhz, long MemoryMb, string Vendor, string Model, string HardwareUuid)
    {
        public double CpuCapacityMhz => CpuMhz * Math.Max(1, CpuCores);
    }

    private sealed class VmDetail
    {
        public string BiosUuid { get; init; } = string.Empty;
        public string InstanceUuid { get; init; } = string.Empty;
        public string GuestHostName { get; init; } = string.Empty;
        public string Fqdn { get; init; } = string.Empty;
        public string? IpAddress { get; init; }
        public string? MacAddress { get; init; }
        public string GuestOs { get; init; } = string.Empty;
        public string ToolsStatus { get; init; } = string.Empty;
        public int CpuCount { get; init; }
        public int CoresPerSocket { get; init; }
        public long MemoryMb { get; init; }
        public List<string> DatastoreNames { get; init; } = new();
        public List<string> NetworkIds { get; init; } = new();
        public int DiskCount { get; init; }
        public int NicCount { get; init; }
        public string ResourcePoolId { get; init; } = string.Empty;
    }

    private sealed class QuickStats
    {
        public double CpuUsageMhz { get; set; }
        public double MemoryUsageMb { get; set; }
        public double CpuMhz { get; set; }
        public int CpuCores { get; set; }
        public long MemoryBytes { get; set; }
        public int VmCpuCount { get; set; }
        public long VmMemoryMb { get; set; }
    }

    private sealed class VMwareConfiguration
    {
        public string Server { get; set; } = string.Empty;
        public int Port { get; set; } = 443;
        public string Username { get; set; } = string.Empty;
        public string? Password { get; set; }
        public bool AllowUntrustedCertificate { get; set; }
        public bool IgnoreSslErrors { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public int MaxConcurrency { get; set; } = 8;
        public int RetryCount { get; set; } = 3;
        public bool CollectMetrics { get; set; } = true;
        public bool UseSoapMetrics { get; set; } = true;
        public bool IncludePoweredOffVms { get; set; } = true;

        public static VMwareConfiguration Parse(AdapterExecutionContext context, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(context.ConfigurationJson))
                throw new AdapterConfigurationException("VMWARE_CONFIG", "vCenter configuration is empty.");

            VMwareConfiguration? config;
            try
            {
                config = JsonSerializer.Deserialize<VMwareConfiguration>(context.ConfigurationJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException ex)
            {
                throw new AdapterConfigurationException("VMWARE_CONFIG_JSON", $"vCenter configuration is not valid JSON: {ex.Message}");
            }

            if (config == null)
                throw new AdapterConfigurationException("VMWARE_CONFIG", "vCenter configuration could not be parsed.");

            config.Password = context.Secret;
            if (string.IsNullOrWhiteSpace(config.Server))
                throw new AdapterConfigurationException("VMWARE_SERVER", "server is required.");
            if (string.IsNullOrWhiteSpace(config.Username))
                throw new AdapterConfigurationException("VMWARE_USERNAME", "username is required.");
            if (string.IsNullOrWhiteSpace(config.Password))
                throw new AdapterConfigurationException("VMWARE_PASSWORD", "password is required in the credential vault.");
            if (config.Port is <= 0 or > 65535)
                throw new AdapterConfigurationException("VMWARE_PORT", "port must be between 1 and 65535.");

            if (config.IgnoreSslErrors)
            {
                config.AllowUntrustedCertificate = true;
                warnings.Add("ignoreSslErrors is set; TLS certificate validation is disabled.");
            }

            config.TimeoutSeconds = Math.Clamp(config.TimeoutSeconds, 5, 300);
            config.MaxConcurrency = Math.Clamp(config.MaxConcurrency, 1, 32);
            config.RetryCount = Math.Clamp(config.RetryCount, 0, 10);
            return config;
        }
    }

    private static class JsonHelpers
    {
        public static JsonElement GetProperty(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
                return value;
            return default;
        }

        public static string? GetString(JsonElement element, string name)
        {
            var value = GetProperty(element, name);
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        public static int GetInt(JsonElement element, string name)
        {
            var value = GetProperty(element, name);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            return int.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        public static long GetLong(JsonElement element, string name)
        {
            var value = GetProperty(element, name);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            return long.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        public static double GetDouble(JsonElement element, string name)
        {
            var value = GetProperty(element, name);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;
            return double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        public static bool GetBool(JsonElement element, string name)
        {
            var value = GetProperty(element, name);
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            return bool.TryParse(GetString(element, name), out var parsed) && parsed;
        }

        public static IEnumerable<JsonElement> EnumerateObjectValues(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        var value = GetProperty(property.Value, "value");
                        yield return value.ValueKind == JsonValueKind.Object ? value : property.Value;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    yield return item;
            }
        }

        public static int CountObjectValues(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                return element.EnumerateObject().Count();
            if (element.ValueKind == JsonValueKind.Array)
                return element.GetArrayLength();
            return 0;
        }
    }
}
