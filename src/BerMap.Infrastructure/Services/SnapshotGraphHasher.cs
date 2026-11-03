using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BerMap.Domain.Entities;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Produces a deterministic SHA-256 digest of a captured snapshot graph.
/// Object keys in JSON properties are sorted so logically identical metadata
/// always produces the same hash. Snapshot verification recomputes this hash
/// from the immutable snapshot rows.
/// </summary>
public static class SnapshotGraphHasher
{
    public static string Compute(
        IEnumerable<SnapshotNode> nodes,
        IEnumerable<SnapshotRelationship> relationships)
    {
        var root = new JsonObject
        {
            ["format"] = "bermap-snapshot-v1",
            ["nodes"] = BuildNodes(nodes),
            ["relationships"] = BuildRelationships(relationships)
        };

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static JsonArray BuildNodes(IEnumerable<SnapshotNode> nodes)
    {
        var array = new JsonArray();
        foreach (var node in nodes.OrderBy(n => n.NodeId))
        {
            array.Add(new JsonObject
            {
                ["nodeId"] = node.NodeId.ToString("D"),
                ["nodeType"] = node.NodeType.ToString(),
                ["name"] = node.Name,
                ["fqdn"] = node.FQDN,
                ["ipAddress"] = node.IPAddress,
                ["macAddress"] = node.MacAddress,
                ["status"] = node.Status,
                ["discoverySource"] = node.DiscoverySource,
                ["biosUuid"] = node.BiosUuid,
                ["vmInstanceUuid"] = node.VmInstanceUuid,
                ["domainSid"] = node.DomainSid,
                ["isStale"] = node.IsStale,
                ["firstSeen"] = node.FirstSeen.ToUniversalTime().ToString("O"),
                ["lastSeen"] = node.LastSeen.ToUniversalTime().ToString("O"),
                ["graphVersion"] = node.GraphVersion,
                ["properties"] = Sort(ParseObject(node.PropertiesJson))
            });
        }

        return array;
    }

    private static JsonArray BuildRelationships(IEnumerable<SnapshotRelationship> relationships)
    {
        var array = new JsonArray();
        foreach (var relationship in relationships
                     .OrderBy(r => r.SourceNodeId)
                     .ThenBy(r => r.TargetNodeId)
                     .ThenBy(r => r.RelationshipType)
                     .ThenBy(r => r.RelationshipId))
        {
            array.Add(new JsonObject
            {
                ["relationshipId"] = relationship.RelationshipId.ToString("D"),
                ["sourceNodeId"] = relationship.SourceNodeId.ToString("D"),
                ["targetNodeId"] = relationship.TargetNodeId.ToString("D"),
                ["relationshipType"] = relationship.RelationshipType.ToString(),
                ["confidenceLevel"] = relationship.ConfidenceLevel.ToString(),
                ["evidence"] = relationship.Evidence,
                ["sourceSystem"] = relationship.SourceSystem,
                ["isStale"] = relationship.IsStale,
                ["firstSeen"] = relationship.FirstSeen.ToUniversalTime().ToString("O"),
                ["lastSeen"] = relationship.LastSeen.ToUniversalTime().ToString("O"),
                ["graphVersion"] = relationship.GraphVersion,
                ["properties"] = Sort(ParseObject(relationship.PropertiesJson))
            });
        }

        return array;
    }

    internal static JsonNode? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    internal static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var sorted = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sorted[key] = Sort(value);
                return sorted;
            }
            case JsonArray array:
            {
                var sortedArray = new JsonArray();
                foreach (var item in array)
                    sortedArray.Add(Sort(item));
                return sortedArray;
            }
            default:
                return node.DeepClone();
        }
    }

    public static string Canonicalize(string? json)
    {
        var node = ParseObject(json);
        return Sort(node)?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "{}";
    }
}
