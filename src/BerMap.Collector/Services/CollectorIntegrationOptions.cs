using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BerMap.Application.Services.Scheduling;
using BerMap.Contracts;
using Microsoft.Extensions.Configuration;

namespace BerMap.Collector.Services;

/// <summary>
/// One validated entry of <c>BerMap:Collector:Integrations</c>: an integration the
/// collector discovers itself and submits to the server.
///   IntegrationId              id of a collector-managed integration on the server
///   AdapterType                "ActiveDirectory" or "VMware"
///   Schedule                   5-field cron, collector local time (default "0 */6 * * *")
///   RunOnStart                 run once when the service starts (default true)
///   TimeoutSeconds             bound for one discovery run (default 1800)
///   Configuration              adapter configuration, as the server wizard stores it
///   SecretFile                 file that contains only the secret, or
///   SecretEnvironmentVariable  environment variable that holds the secret
/// </summary>
public sealed class CollectorIntegration
{
    public const string DefaultSchedule = "0 */6 * * *";
    public const int DefaultTimeoutSeconds = 1800;
    public const int MinTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 86400;

    public Guid IntegrationId { get; init; }
    public string AdapterType { get; init; } = string.Empty;
    public CronSchedule Schedule { get; init; } = CronSchedule.Parse(DefaultSchedule);
    public bool RunOnStart { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
    public string ConfigurationJson { get; init; } = "{}";
    public string? SecretFile { get; init; }
    public string? SecretEnvironmentVariable { get; init; }

    /// <summary>Log-safe description of where the secret comes from (never the secret itself).</summary>
    public string SecretSourceDescription => SecretFile != null
        ? $"file '{SecretFile}'"
        : $"environment variable '{SecretEnvironmentVariable}'";
}

/// <summary>An entry that could not be used. The message never contains a secret.</summary>
public sealed record CollectorIntegrationError(int Index, Guid? IntegrationId, string Message);

/// <summary>The usable entries plus the rejected ones.</summary>
public sealed class CollectorIntegrationSet
{
    public IReadOnlyList<CollectorIntegration> Integrations { get; init; } = Array.Empty<CollectorIntegration>();
    public IReadOnlyList<CollectorIntegrationError> Errors { get; init; } = Array.Empty<CollectorIntegrationError>();

    /// <summary>Canonical adapter types of every entry whose adapter type is known, valid or not.</summary>
    public IReadOnlyList<string> AdapterTypes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Reads <c>BerMap:Collector:Integrations</c> by hand instead of with the
/// configuration binder: a typo in one entry must reject that entry with a
/// clear message, not stop the service with a binder exception.
/// </summary>
public static class CollectorIntegrationConfiguration
{
    public const string SectionPath = "BerMap:Collector:Integrations";

    public static CollectorIntegrationSet Load(IConfiguration configuration, IEnumerable<IIntegrationAdapter> adapters)
    {
        var adapterByType = adapters.ToDictionary(a => a.AdapterType, StringComparer.OrdinalIgnoreCase);
        var integrations = new List<CollectorIntegration>();
        var errors = new List<CollectorIntegrationError>();
        var adapterTypes = new List<string>();
        var seen = new HashSet<Guid>();

        var entries = configuration.GetSection(SectionPath).GetChildren()
            .Select(child => (Section: child, Index: int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1))
            .OrderBy(e => e.Index)
            .ToList();

        foreach (var (section, index) in entries)
        {
            Guid? integrationId = Guid.TryParse(section["IntegrationId"], out var parsedId) && parsedId != Guid.Empty
                ? parsedId
                : null;
            var problems = new List<string>();

            if (integrationId == null)
                problems.Add("IntegrationId must be the id (GUID) of a collector-managed integration on the server.");
            else if (!seen.Add(integrationId.Value))
                problems.Add($"IntegrationId {integrationId} is configured more than once.");

            var adapterTypeText = section["AdapterType"]?.Trim();
            IIntegrationAdapter? adapter = null;
            if (string.IsNullOrEmpty(adapterTypeText))
                problems.Add($"AdapterType is required ({string.Join(" or ", adapterByType.Keys.Order(StringComparer.Ordinal))}).");
            else if (!adapterByType.TryGetValue(adapterTypeText, out adapter))
                problems.Add($"AdapterType '{adapterTypeText}' is not supported ({string.Join(" or ", adapterByType.Keys.Order(StringComparer.Ordinal))}).");
            else if (!adapterTypes.Contains(adapter.AdapterType, StringComparer.Ordinal))
                adapterTypes.Add(adapter.AdapterType);

            var scheduleText = string.IsNullOrWhiteSpace(section["Schedule"]) ? CollectorIntegration.DefaultSchedule : section["Schedule"]!.Trim();
            if (!CronSchedule.TryParse(scheduleText, out var schedule, out var cronError))
                problems.Add($"Schedule '{scheduleText}' is not a valid cron expression: {cronError}");

            var runOnStart = true;
            if (!string.IsNullOrWhiteSpace(section["RunOnStart"]) && !bool.TryParse(section["RunOnStart"], out runOnStart))
                problems.Add("RunOnStart must be true or false.");

            var timeoutSeconds = CollectorIntegration.DefaultTimeoutSeconds;
            if (!string.IsNullOrWhiteSpace(section["TimeoutSeconds"]) &&
                (!int.TryParse(section["TimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutSeconds) ||
                 timeoutSeconds is < CollectorIntegration.MinTimeoutSeconds or > CollectorIntegration.MaxTimeoutSeconds))
            {
                problems.Add($"TimeoutSeconds must be a whole number between {CollectorIntegration.MinTimeoutSeconds} and {CollectorIntegration.MaxTimeoutSeconds}.");
            }

            var secretFile = Normalize(section["SecretFile"]);
            var secretVariable = Normalize(section["SecretEnvironmentVariable"]);
            if (secretFile != null && secretVariable != null)
                problems.Add("Configure either SecretFile or SecretEnvironmentVariable, not both.");
            else if (secretFile == null && secretVariable == null)
                problems.Add("A secret source is required: SecretFile (a file that contains only the secret) or SecretEnvironmentVariable.");

            string configurationJson = "{}";
            try
            {
                var schema = adapter?.GetConfigurationSchema();
                configurationJson = ConfigurationJson.Build(section.GetSection("Configuration"), schema?.JsonSchema);

                // The secret belongs in its own file or variable, never next to the settings.
                var document = JsonNode.Parse(configurationJson)!.AsObject();
                var inlineSecrets = (schema?.SecretFields ?? new List<string>())
                    .Where(field => document.Any(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (inlineSecrets.Count > 0)
                    problems.Add($"Configuration must not contain '{string.Join("', '", inlineSecrets)}'; use SecretFile or SecretEnvironmentVariable.");
            }
            catch (FormatException ex)
            {
                problems.Add(ex.Message);
            }

            if (problems.Count > 0)
            {
                errors.Add(new CollectorIntegrationError(index, integrationId, string.Join(" ", problems)));
                continue;
            }

            integrations.Add(new CollectorIntegration
            {
                IntegrationId = integrationId!.Value,
                AdapterType = adapter!.AdapterType,
                Schedule = schedule!,
                RunOnStart = runOnStart,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                ConfigurationJson = configurationJson,
                SecretFile = secretFile == null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(secretFile)),
                SecretEnvironmentVariable = secretVariable
            });
        }

        return new CollectorIntegrationSet { Integrations = integrations, Errors = errors, AdapterTypes = adapterTypes };
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Turns a configuration section back into the JSON document the adapter expects.
/// Configuration values are untyped strings, while the adapters deserialize
/// strictly ("636" is not an int), so each value is typed from the adapter's
/// JSON schema; properties the schema does not describe fall back to
/// true/false and whole numbers. Arrays come back from their "0", "1", ... keys.
/// A section holding a single string value is taken as a JSON document as-is
/// (handy for environment variables).
/// </summary>
public static class ConfigurationJson
{
    public static string Build(IConfigurationSection section, string? jsonSchema)
    {
        if (!section.Exists())
            return "{}";

        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(section.Value))
                return "{}";
            try
            {
                var document = JsonNode.Parse(section.Value);
                if (document is not JsonObject)
                    throw new FormatException("Configuration must be a JSON object.");
                return document.ToJsonString();
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Configuration is not valid JSON: {ex.Message}");
            }
        }

        JsonElement? schema = null;
        if (!string.IsNullOrWhiteSpace(jsonSchema))
        {
            try
            {
                schema = JsonDocument.Parse(jsonSchema).RootElement.Clone();
            }
            catch (JsonException)
            {
                schema = null;
            }
        }

        return ToNode(section, schema)!.ToJsonString();
    }

    private static JsonNode? ToNode(IConfigurationSection section, JsonElement? schema)
    {
        var children = section.GetChildren().ToList();
        var type = SchemaType(schema);

        if (children.Count == 0)
        {
            // An empty JSON object/array appears as a key with an empty value.
            if (string.IsNullOrEmpty(section.Value))
                return type switch { "array" => new JsonArray(), "object" => new JsonObject(), "string" => JsonValue.Create(string.Empty), _ => null };
            return ToValue(section.Value, type, section.Path);
        }

        var isArray = type == "array" || (type == null && IsArrayShaped(children));
        if (isArray)
        {
            var array = new JsonArray();
            var items = schema is { } s && s.TryGetProperty("items", out var itemSchema) ? itemSchema : (JsonElement?)null;
            foreach (var child in children.OrderBy(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : int.MaxValue))
                array.Add(ToNode(child, items));
            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            var node = ToNode(child, PropertySchema(schema, child.Key));
            if (node != null)
                obj[child.Key] = node;
        }
        return obj;
    }

    private static JsonNode ToValue(string value, string? type, string path)
    {
        switch (type)
        {
            case "string":
                return JsonValue.Create(value);
            case "boolean":
                return bool.TryParse(value, out var flag)
                    ? JsonValue.Create(flag)
                    : throw new FormatException($"{path} must be true or false.");
            case "integer":
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                    ? JsonValue.Create(whole)
                    : throw new FormatException($"{path} must be a whole number.");
            case "number":
                return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw new FormatException($"{path} must be a number.");
        }

        if (bool.TryParse(value, out var inferredFlag))
            return JsonValue.Create(inferredFlag);
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var inferredWhole) &&
            inferredWhole.ToString(CultureInfo.InvariantCulture) == value)
            return JsonValue.Create(inferredWhole);
        return JsonValue.Create(value);
    }

    private static bool IsArrayShaped(List<IConfigurationSection> children)
    {
        var keys = new HashSet<int>();
        foreach (var child in children)
        {
            if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                return false;
            keys.Add(index);
        }
        return Enumerable.Range(0, children.Count).All(keys.Contains);
    }

    private static string? SchemaType(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static JsonElement? PropertySchema(JsonElement? schema, string name)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } s ||
            !s.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in properties.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }
}
