using System;
using System.Collections.Generic;
using System.Linq;
using BerMap.Application.DTOs;
using BerMap.Application.Interfaces;
using BerMap.Contracts;

namespace BerMap.Infrastructure.Integrations;

/// <summary>
/// Resolves registered discovery adapters by their stable adapter type. The
/// registry is intentionally read-only; adapters are singletons and must be
/// stateless between calls.
/// </summary>
public sealed class IntegrationAdapterRegistry : IIntegrationAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IIntegrationAdapter> _adapters;
    private readonly IReadOnlyList<AdapterDescriptorDto> _descriptors;

    public IntegrationAdapterRegistry(IEnumerable<IIntegrationAdapter> adapters)
    {
        var list = adapters.ToList();
        var duplicate = list
            .GroupBy(a => a.AdapterType, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate != null)
            throw new InvalidOperationException($"Duplicate adapter type registered: '{duplicate.Key}'.");

        _adapters = list.ToDictionary(a => a.AdapterType, StringComparer.OrdinalIgnoreCase);
        _descriptors = list
            .Select(a => new AdapterDescriptorDto
            {
                AdapterType = a.AdapterType,
                DisplayName = a.DisplayName,
                Version = a.Version,
                Capabilities = a.GetCapabilities(),
                ConfigurationSchema = a.GetConfigurationSchema()
            })
            .OrderBy(d => d.DisplayName)
            .ToList();
    }

    public IReadOnlyList<AdapterDescriptorDto> GetDescriptors() => _descriptors;

    public IIntegrationAdapter? GetAdapter(string adapterType)
    {
        if (string.IsNullOrWhiteSpace(adapterType))
            return null;

        return _adapters.TryGetValue(adapterType.Trim(), out var adapter) ? adapter : null;
    }
}
