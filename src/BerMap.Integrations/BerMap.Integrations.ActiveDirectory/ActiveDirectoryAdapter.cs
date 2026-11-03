using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Contracts;
using System.DirectoryServices.Protocols;

namespace BerMap.Integrations.ActiveDirectory;

/// <summary>
/// Real Active Directory discovery adapter. It uses LDAP/LDAPS read-only searches
/// (System.DirectoryServices.Protocols) and never writes to the directory.
///
/// Supported configuration (JSON):
/// {
///   "server": "dc01.corp.local",
///   "port": 636,
///   "useSsl": true,
///   "useStartTls": false,
///   "baseDn": "DC=corp,DC=local",
///   "username": "svc_bermap_ro@corp.local",
///   "allowUntrustedCertificate": false,
///   "allowPlaintext": false,
///   "pageSize": 500,
///   "timeoutSeconds": 30,
///   "maxGroupMembers": 5000,
///   "includeGroups": true,
///   "includeComputers": true,
///   "includeOus": true,
///   "includeSites": true,
///   "includeTrusts": true,
///   "discoverAllDomains": true,
///   "additionalDomains": [
///     { "server": "dc01.other.local", "port": 636, "baseDn": "DC=other,DC=local", "username": "svc@other.local" }
///   ]
/// }
/// The password is supplied separately in AdapterExecutionContext.Secret.
/// </summary>
public sealed class ActiveDirectoryAdapter : IIntegrationAdapter
{
    public string AdapterType => "ActiveDirectory";
    public string DisplayName => "Active Directory Domain Services (LDAP/LDAPS)";
    public string Version => "2.0.0";

    public AdapterCapabilities GetCapabilities() => new()
    {
        SupportsFullDiscovery = true,
        SupportsIncremental = false,
        SupportsCredentialsValidation = true,
        SupportsHealthCheck = true,
        SupportsMetrics = false,
        MaxConcurrency = 1,
        EstimatedDiscoveryDuration = TimeSpan.FromMinutes(10),
        SupportedEntityTypes = new List<string>
        {
            "Forest", "Domain", "Site", "Network", "OrganizationalUnit", "DomainController",
            "Computer", "Group"
        },
        SupportedRelationshipTypes = new List<string>
        {
            "Contains", "MemberOf", "Trusts", "ReplicatesTo"
        }
    };

    public AdapterConfigurationSchema GetConfigurationSchema() => new()
    {
        SecretFields = new List<string> { "password" },
        Notes = new List<string>
        {
            "Use an LDAPS service account with read-only directory permissions.",
            "Plaintext LDAP is refused unless allowPlaintext=true is explicitly configured.",
            "Certificate validation is strict by default; allowUntrustedCertificate is intended only for isolated lab domains.",
            "Multi-domain discovery follows forest cross-references. Additional forests require an entry in additionalDomains."
        },
        JsonSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["server", "username"],
          "properties": {
            "server": { "type": "string", "title": "Domain Controller / LDAP Server", "examples": ["dc01.corp.local"] },
            "port": { "type": "integer", "title": "Port", "default": 636 },
            "useSsl": { "type": "boolean", "title": "Use LDAPS", "default": true },
            "useStartTls": { "type": "boolean", "title": "Use StartTLS", "default": false },
            "baseDn": { "type": "string", "title": "Base DN (optional)", "examples": ["DC=corp,DC=local"] },
            "username": { "type": "string", "title": "Read-only service account (UPN or DOMAIN\\user)" },
            "allowUntrustedCertificate": { "type": "boolean", "title": "Allow untrusted TLS certificate", "default": false },
            "allowPlaintext": { "type": "boolean", "title": "Allow plaintext LDAP", "default": false },
            "pageSize": { "type": "integer", "title": "LDAP page size", "default": 500 },
            "timeoutSeconds": { "type": "integer", "title": "Timeout (seconds)", "default": 30 },
            "maxGroupMembers": { "type": "integer", "title": "Maximum group members per group", "default": 5000 },
            "includeGroups": { "type": "boolean", "default": true },
            "includeComputers": { "type": "boolean", "default": true },
            "includeOus": { "type": "boolean", "default": true },
            "includeSites": { "type": "boolean", "default": true },
            "includeTrusts": { "type": "boolean", "default": true },
            "discoverAllDomains": { "type": "boolean", "title": "Follow forest cross-references", "default": true },
            "additionalDomains": { "type": "array", "title": "Additional domains/forests", "items": { "type": "object" } }
          }
        }
        """
    };

    public Task<ConnectionTestResult> TestConnectionAsync(AdapterExecutionContext context, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => TestConnectionCore(context, cancellationToken), cancellationToken);
    }

    public Task<DiscoveryResult> DiscoverAsync(AdapterExecutionContext context, IProgress<DiscoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DiscoverCore(context, progress, cancellationToken), cancellationToken);
    }

    private static ConnectionTestResult TestConnectionCore(AdapterExecutionContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        try
        {
            var config = ActiveDirectoryConfiguration.Parse(context);
            using var connection = LdapConnectionFactory.Create(config, warnings);
            var root = SearchSingle(connection, string.Empty, "(objectClass=*)", SearchScope.Base,
                new[] { "defaultNamingContext", "configurationNamingContext", "rootDomainNamingContext", "dnsHostName", "vendorVersion", "serverName" },
                config.TimeoutSeconds, cancellationToken);

            sw.Stop();
            return new ConnectionTestResult
            {
                Success = true,
                Message = "LDAP bind and read-only RootDSE query succeeded.",
                Latency = sw.Elapsed,
                ServerVersion = GetString(root, "vendorVersion") ?? GetString(root, "serverName"),
                Warnings = warnings,
                Details = new Dictionary<string, string>
                {
                    ["defaultNamingContext"] = GetString(root, "defaultNamingContext") ?? string.Empty,
                    ["configurationNamingContext"] = GetString(root, "configurationNamingContext") ?? string.Empty,
                    ["rootDomainNamingContext"] = GetString(root, "rootDomainNamingContext") ?? string.Empty,
                    ["dnsHostName"] = GetString(root, "dnsHostName") ?? string.Empty,
                    ["tlsMode"] = config.UseSsl ? "LDAPS" : config.UseStartTls ? "StartTLS" : "Plaintext"
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new ConnectionTestResult
            {
                Success = false,
                Message = "Active Directory connection test failed.",
                Latency = sw.Elapsed,
                ErrorCode = ClassifyError(ex),
                ErrorDetail = SafeMessage(ex),
                Warnings = warnings
            };
        }
    }

    private static DiscoveryResult DiscoverCore(AdapterExecutionContext context, IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var result = new DiscoveryResult();
        var config = ActiveDirectoryConfiguration.Parse(context);

        var nodes = new Dictionary<string, DiscoveredNodeContract>(StringComparer.OrdinalIgnoreCase);
        var relationships = new Dictionary<string, DiscoveredRelationshipContract>(StringComparer.OrdinalIgnoreCase);
        var dnToExternalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var domainConnections = new List<DomainConnection>();

        void AddNode(DiscoveredNodeContract node)
        {
            if (string.IsNullOrWhiteSpace(node.ExternalId))
                return;
            node.SourceSystem = "ActiveDirectory";
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
                SourceSystem = "ActiveDirectory",
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

        try
        {
            Report("Connect", "Connecting to the primary domain controller...", 2);
            var primaryWarnings = new List<string>();
            using var primaryConnection = LdapConnectionFactory.Create(config, primaryWarnings);
            result.Warnings.AddRange(primaryWarnings);

            var root = SearchSingle(primaryConnection, string.Empty, "(objectClass=*)", SearchScope.Base,
                new[] { "defaultNamingContext", "configurationNamingContext", "rootDomainNamingContext", "dnsHostName", "vendorVersion" },
                config.TimeoutSeconds, cancellationToken);

            var baseDn = string.IsNullOrWhiteSpace(config.BaseDn)
                ? GetString(root, "defaultNamingContext")
                : config.BaseDn;
            var configNc = GetString(root, "configurationNamingContext");
            var forestRootDn = GetString(root, "rootDomainNamingContext") ?? baseDn;

            if (string.IsNullOrWhiteSpace(baseDn))
                throw new AdapterConfigurationException("AD_BASE_DN", "Could not determine the domain naming context. Set baseDn explicitly.");

            result.SourceVersion = GetString(root, "vendorVersion");

            var primaryDomainDns = DnToDns(baseDn);
            var forestDns = DnToDns(forestRootDn!);
            var forestExternalId = $"forest:{forestDns}";
            var primaryDomainExternalId = $"domain:{primaryDomainDns}";

            AddNode(new DiscoveredNodeContract
            {
                ExternalId = forestExternalId,
                NodeType = "Forest",
                Name = forestDns,
                FQDN = forestDns,
                Status = "healthy",
                Properties = new Dictionary<string, string>
                {
                    ["DnsName"] = forestDns,
                    ["DistinguishedName"] = forestRootDn,
                    ["DiscoveryMethod"] = "RootDSE"
                }
            });

            AddNode(new DiscoveredNodeContract
            {
                ExternalId = primaryDomainExternalId,
                NodeType = "Domain",
                Name = primaryDomainDns,
                FQDN = primaryDomainDns,
                Status = "healthy",
                ParentExternalId = forestExternalId,
                ParentName = forestDns,
                Properties = new Dictionary<string, string>
                {
                    ["DnsName"] = primaryDomainDns,
                    ["DistinguishedName"] = baseDn,
                    ["Forest"] = forestDns,
                    ["IsPrimary"] = "true",
                    ["NamingContext"] = baseDn
                }
            });
            dnToExternalId[baseDn] = primaryDomainExternalId;
            AddRelationship(forestExternalId, primaryDomainExternalId, "Contains", "Forest root domain naming context");

            domainConnections.Add(new DomainConnection
            {
                DnsName = primaryDomainDns,
                BaseDn = baseDn,
                Connection = primaryConnection,
                IsPrimary = true
            });

            // Enumerate all domains in the primary forest via cross-reference objects.
            var forestDomains = new List<(string Dns, string Dn)>();
            if (config.DiscoverAllDomains && !string.IsNullOrWhiteSpace(configNc))
            {
                Report("Forest", "Enumerating forest domain cross-references...", 8);
                try
                {
                    var partitionsDn = $"CN=Partitions,{configNc}";
                    var crossRefs = SearchPaged(primaryConnection, partitionsDn, "(objectClass=crossRef)",
                        SearchScope.OneLevel, new[] { "nCName", "dnsRoot", "trustParent", "systemFlags" },
                        config.PageSize, config.TimeoutSeconds, cancellationToken).ToList();

                    foreach (var entry in crossRefs)
                    {
                        var nc = GetString(entry, "nCName");
                        var dnsRoot = GetString(entry, "dnsRoot");
                        if (string.IsNullOrWhiteSpace(nc) || string.IsNullOrWhiteSpace(dnsRoot) ||
                            !nc.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                            continue;

                        forestDomains.Add((dnsRoot, nc));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Warnings.Add($"Forest domain enumeration failed: {SafeMessage(ex)}");
                }
            }

            // Add same-forest domains and open a connection per domain using the same credentials.
            foreach (var (domainDns, domainDn) in forestDomains
                .Where(d => !string.Equals(d.Dns, primaryDomainDns, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(d => d.Dns.ToLowerInvariant()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var domainExternalId = $"domain:{domainDns}";
                AddNode(new DiscoveredNodeContract
                {
                    ExternalId = domainExternalId,
                    NodeType = "Domain",
                    Name = domainDns,
                    FQDN = domainDns,
                    Status = "healthy",
                    ParentExternalId = forestExternalId,
                    ParentName = forestDns,
                    Properties = new Dictionary<string, string>
                    {
                        ["DnsName"] = domainDns,
                        ["DistinguishedName"] = domainDn,
                        ["Forest"] = forestDns,
                        ["IsPrimary"] = "false",
                        ["NamingContext"] = domainDn
                    }
                });
                dnToExternalId[domainDn] = domainExternalId;
                AddRelationship(forestExternalId, domainExternalId, "Contains", "Forest cross-reference (CN=Partitions)");

                try
                {
                    var warnings = new List<string>();
                    var domainConfig = config.CloneForDomain(domainDns, domainDn, config.Username);
                    var connection = LdapConnectionFactory.Create(domainConfig, warnings);
                    result.Warnings.AddRange(warnings);
                    domainConnections.Add(new DomainConnection
                    {
                        DnsName = domainDns,
                        BaseDn = domainDn,
                        Connection = connection,
                        IsPrimary = false
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Warnings.Add($"Domain '{domainDns}' is in the forest but could not be opened read-only: {SafeMessage(ex)}");
                }
            }

            // Additional forests/domains explicitly configured by the administrator.
            foreach (var additional in config.AdditionalDomains)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var additionalBaseDn = string.IsNullOrWhiteSpace(additional.BaseDn)
                        ? DnFromDns(additional.Server)
                        : additional.BaseDn;
                    var additionalDns = DnToDns(additionalBaseDn);
                    var additionalForestDns = string.IsNullOrWhiteSpace(additional.Forest)
                        ? additionalDns
                        : additional.Forest;
                    var additionalForestId = $"forest:{additionalForestDns}";
                    var additionalDomainId = $"domain:{additionalDns}";

                    if (!nodes.ContainsKey(additionalForestId))
                    {
                        AddNode(new DiscoveredNodeContract
                        {
                            ExternalId = additionalForestId,
                            NodeType = "Forest",
                            Name = additionalForestDns,
                            FQDN = additionalForestDns,
                            Status = "healthy",
                            Properties = new Dictionary<string, string>
                            {
                                ["DnsName"] = additionalForestDns,
                                ["DiscoveryMethod"] = "Explicit additionalDomains configuration"
                            }
                        });
                    }

                    AddNode(new DiscoveredNodeContract
                    {
                        ExternalId = additionalDomainId,
                        NodeType = "Domain",
                        Name = additionalDns,
                        FQDN = additionalDns,
                        Status = "healthy",
                        ParentExternalId = additionalForestId,
                        ParentName = additionalForestDns,
                        Properties = new Dictionary<string, string>
                        {
                            ["DnsName"] = additionalDns,
                            ["DistinguishedName"] = additionalBaseDn,
                            ["Forest"] = additionalForestDns,
                            ["IsPrimary"] = "false",
                            ["NamingContext"] = additionalBaseDn,
                            ["DiscoveryMethod"] = "Explicit additionalDomains configuration"
                        }
                    });
                    dnToExternalId[additionalBaseDn] = additionalDomainId;
                    AddRelationship(additionalForestId, additionalDomainId, "Contains", "Explicit additional domain configuration");

                    var warnings = new List<string>();
                    var domainConfig = config.CloneForDomain(
                        string.IsNullOrWhiteSpace(additional.Server) ? additionalDns : additional.Server,
                        additionalBaseDn,
                        string.IsNullOrWhiteSpace(additional.Username) ? config.Username : additional.Username,
                        additional.Port,
                        additional.UseSsl);
                    var connection = LdapConnectionFactory.Create(domainConfig, warnings);
                    result.Warnings.AddRange(warnings);
                    domainConnections.Add(new DomainConnection
                    {
                        DnsName = additionalDns,
                        BaseDn = additionalBaseDn,
                        Connection = connection,
                        IsPrimary = false
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Warnings.Add($"Additional domain '{additional.Server}' could not be opened: {SafeMessage(ex)}");
                }
            }

            var processed = 0;
            foreach (var domain in domainConnections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                var percent = 10 + (int)(70.0 * processed / Math.Max(1, domainConnections.Count));
                Report("Domain", $"Discovering domain {domain.DnsName}...", percent);

                try
                {
                    DiscoverDomain(
                        domain,
                        config,
                        nodes,
                        relationships,
                        dnToExternalId,
                        AddNode,
                        AddRelationship,
                        result,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Warnings.Add($"Domain '{domain.DnsName}' discovery was incomplete: {SafeMessage(ex)}");
                }
            }

            // Forest-level AD Sites and subnets (configuration naming context).
            if (config.IncludeSites && !string.IsNullOrWhiteSpace(configNc))
            {
                Report("Sites", "Discovering AD sites and subnets...", 84);
                try
                {
                    DiscoverSites(primaryConnection, configNc, forestExternalId, config, AddNode, AddRelationship, result, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Warnings.Add($"AD Sites/subnet discovery failed: {SafeMessage(ex)}");
                }
            }

            result.Nodes = nodes.Values.ToList();
            result.Relationships = relationships.Values.ToList();
            result.Success = result.Errors.Count == 0 || result.Nodes.Count > 0;
            result.Status = result.Errors.Count > 0
                ? (result.Nodes.Count > 0 ? "PartialSuccess" : "Failed")
                : "Completed";
            result.Statistics = nodes.Values
                .GroupBy(n => n.NodeType)
                .ToDictionary(g => g.Key, g => g.Count());
            result.Statistics["Relationships"] = result.Relationships.Count;
            result.Statistics["Domains"] = nodes.Values.Count(n => n.NodeType == "Domain");
            result.Statistics["DomainControllers"] = nodes.Values.Count(n => n.NodeType == "DomainController");
            result.Statistics["Computers"] = nodes.Values.Count(n => n.NodeType == "Computer");
            result.Statistics["Groups"] = nodes.Values.Count(n => n.NodeType == "Group");
            result.Statistics["OrganizationalUnits"] = nodes.Values.Count(n => n.NodeType == "OrganizationalUnit");
            sw.Stop();
            result.Duration = sw.Elapsed;
            result.CompletedAt = DateTime.UtcNow;
            Report("Completed", $"AD discovery completed: {result.Nodes.Count} nodes, {result.Relationships.Count} relationships.", 100);
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
            foreach (var domain in domainConnections)
            {
                if (!domain.IsPrimary)
                    domain.Connection.Dispose();
            }
        }
    }

    private static void DiscoverDomain(
        DomainConnection domain,
        ActiveDirectoryConfiguration config,
        Dictionary<string, DiscoveredNodeContract> nodes,
        Dictionary<string, DiscoveredRelationshipContract> relationships,
        Dictionary<string, string> dnToExternalId,
        Action<DiscoveredNodeContract> addNode,
        Action<string, string, string, string, string, Dictionary<string, string>?> addRelationship,
        DiscoveryResult result,
        CancellationToken cancellationToken)
    {
        var domainExternalId = $"domain:{domain.DnsName}";
        var computersByDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groupsByDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ouEntries = new List<SearchResultEntry>();
        var computerEntries = new List<SearchResultEntry>();
        var groupEntries = new List<SearchResultEntry>();
        var domainControllers = new List<(string ExternalId, string DnsName)>();

        // Organizational units.
        if (config.IncludeOus)
        {
            try
            {
                ouEntries.AddRange(SearchPaged(domain.Connection, domain.BaseDn, "(objectClass=organizationalUnit)",
                    SearchScope.Subtree,
                    new[] { "ou", "distinguishedName", "objectGUID", "description", "whenCreated", "whenChanged" },
                    config.PageSize, config.TimeoutSeconds, cancellationToken));

                foreach (var entry in ouEntries)
                {
                    var dn = entry.DistinguishedName;
                    var externalId = ExternalIdFor("ou", entry);
                    var parentDn = ParentDn(dn);
                    var parentExternalId = ResolveParentExternalId(parentDn, domain.BaseDn, domainExternalId, dnToExternalId);
                    addNode(new DiscoveredNodeContract
                    {
                        ExternalId = externalId,
                        NodeType = "OrganizationalUnit",
                        Name = GetString(entry, "ou") ?? FirstRdnValue(dn),
                        FQDN = string.Empty,
                        Status = "healthy",
                        ParentExternalId = parentExternalId,
                        ParentName = parentDn,
                        Properties = new Dictionary<string, string>
                        {
                            ["DistinguishedName"] = dn,
                            ["Description"] = GetString(entry, "description") ?? string.Empty,
                            ["Domain"] = domain.DnsName,
                            ["WhenCreated"] = FormatLdapTime(GetString(entry, "whenCreated")),
                            ["WhenChanged"] = FormatLdapTime(GetString(entry, "whenChanged"))
                        }
                    });
                    dnToExternalId[dn] = externalId;
                }

                foreach (var entry in ouEntries)
                {
                    var dn = entry.DistinguishedName;
                    var externalId = dnToExternalId[dn];
                    var parentDn = ParentDn(dn);
                    var parentExternalId = ResolveParentExternalId(parentDn, domain.BaseDn, domainExternalId, dnToExternalId);
                    addRelationship(parentExternalId, externalId, "Contains", "AD distinguished-name hierarchy", "Verified", null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Warnings.Add($"OU discovery failed for {domain.DnsName}: {SafeMessage(ex)}");
            }
        }

        // Computers (including domain controllers).
        if (config.IncludeComputers)
        {
            try
            {
                computerEntries.AddRange(SearchPaged(domain.Connection, domain.BaseDn,
                    "(&(objectCategory=computer)(objectClass=computer))", SearchScope.Subtree,
                    new[]
                    {
                        "cn", "dNSHostName", "distinguishedName", "objectGUID", "objectSid",
                        "operatingSystem", "operatingSystemVersion", "userAccountControl",
                        "primaryGroupID", "whenCreated", "whenChanged", "lastLogonTimestamp",
                        "description", "servicePrincipalName", "memberOf"
                    },
                    config.PageSize, config.TimeoutSeconds, cancellationToken));

                foreach (var entry in computerEntries)
                {
                    var dn = entry.DistinguishedName;
                    var dnsHostName = GetString(entry, "dNSHostName") ?? string.Empty;
                    var cn = GetString(entry, "cn") ?? FirstRdnValue(dn);
                    var uac = GetInt(entry, "userAccountControl") ?? 0;
                    var primaryGroupId = GetInt(entry, "primaryGroupID") ?? 0;
                    var isDc = (uac & 8192) != 0 || primaryGroupId == 516;
                    var externalId = ExternalIdFor("computer", entry);
                    var parentDn = ParentDn(dn);
                    var parentExternalId = ResolveParentExternalId(parentDn, domain.BaseDn, domainExternalId, dnToExternalId);

                    addNode(new DiscoveredNodeContract
                    {
                        ExternalId = externalId,
                        NodeType = isDc ? "DomainController" : "Computer",
                        Name = cn,
                        FQDN = dnsHostName,
                        IPAddress = null,
                        Status = (uac & 2) != 0 ? "unknown" : "healthy", // ACCOUNTDISABLE
                        BiosUuid = string.Empty,
                        DomainSid = GetSid(entry, "objectSid") ?? string.Empty,
                        ParentExternalId = parentExternalId,
                        ParentName = parentDn,
                        Properties = new Dictionary<string, string>
                        {
                            ["DistinguishedName"] = dn,
                            ["Domain"] = domain.DnsName,
                            ["OperatingSystem"] = GetString(entry, "operatingSystem") ?? string.Empty,
                            ["OperatingSystemVersion"] = GetString(entry, "operatingSystemVersion") ?? string.Empty,
                            ["Enabled"] = ((uac & 2) == 0).ToString(),
                            ["IsDomainController"] = isDc.ToString(),
                            ["UserAccountControl"] = uac.ToString(CultureInfo.InvariantCulture),
                            ["LastLogonTimestamp"] = FormatFileTime(GetString(entry, "lastLogonTimestamp")),
                            ["WhenCreated"] = FormatLdapTime(GetString(entry, "whenCreated")),
                            ["WhenChanged"] = FormatLdapTime(GetString(entry, "whenChanged")),
                            ["Description"] = GetString(entry, "description") ?? string.Empty,
                            ["ServicePrincipalNames"] = string.Join(",", GetStrings(entry, "servicePrincipalName").Take(20))
                        }
                    });

                    computersByDn[dn] = externalId;
                    dnToExternalId[dn] = externalId;
                    addRelationship(parentExternalId, externalId, "Contains", "AD distinguished-name hierarchy", "Verified", null);

                    if (isDc)
                        domainControllers.Add((externalId, dnsHostName));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Warnings.Add($"Computer discovery failed for {domain.DnsName}: {SafeMessage(ex)}");
            }
        }

        // Groups and their computer/group membership.
        if (config.IncludeGroups)
        {
            try
            {
                groupEntries.AddRange(SearchPaged(domain.Connection, domain.BaseDn, "(objectClass=group)", SearchScope.Subtree,
                    new[] { "cn", "distinguishedName", "objectGUID", "objectSid", "sAMAccountName", "groupType", "description", "member" },
                    config.PageSize, config.TimeoutSeconds, cancellationToken));

                foreach (var entry in groupEntries)
                {
                    var dn = entry.DistinguishedName;
                    var externalId = ExternalIdFor("group", entry);
                    var parentDn = ParentDn(dn);
                    var parentExternalId = ResolveParentExternalId(parentDn, domain.BaseDn, domainExternalId, dnToExternalId);
                    var groupType = GetInt(entry, "groupType") ?? 0;

                    addNode(new DiscoveredNodeContract
                    {
                        ExternalId = externalId,
                        NodeType = "Group",
                        Name = GetString(entry, "cn") ?? FirstRdnValue(dn),
                        FQDN = string.Empty,
                        Status = "healthy",
                        ParentExternalId = parentExternalId,
                        ParentName = parentDn,
                        Properties = new Dictionary<string, string>
                        {
                            ["DistinguishedName"] = dn,
                            ["SamAccountName"] = GetString(entry, "sAMAccountName") ?? string.Empty,
                            ["Domain"] = domain.DnsName,
                            ["GroupScope"] = GroupScope(groupType),
                            ["GroupType"] = GroupType(groupType),
                            ["Description"] = GetString(entry, "description") ?? string.Empty
                        }
                    });

                    groupsByDn[dn] = externalId;
                    dnToExternalId[dn] = externalId;
                    addRelationship(parentExternalId, externalId, "Contains", "AD distinguished-name hierarchy", "Verified", null);
                }

                // Membership relationships are added after all computers/groups exist.
                foreach (var entry in groupEntries)
                {
                    var groupDn = entry.DistinguishedName;
                    if (!groupsByDn.TryGetValue(groupDn, out var groupExternalId))
                        continue;

                    var members = GetRangedMembers(domain.Connection, groupDn, config.MaxGroupMembers, config.TimeoutSeconds, cancellationToken);
                    foreach (var memberDn in members)
                    {
                        if (computersByDn.TryGetValue(memberDn, out var computerExternalId))
                        {
                            addRelationship(computerExternalId, groupExternalId, "MemberOf",
                                "AD group member attribute", "Verified",
                                new Dictionary<string, string> { ["GroupDn"] = groupDn });
                        }
                        else if (groupsByDn.TryGetValue(memberDn, out var nestedGroupExternalId))
                        {
                            addRelationship(nestedGroupExternalId, groupExternalId, "MemberOf",
                                "AD nested group member attribute", "Verified",
                                new Dictionary<string, string> { ["GroupDn"] = groupDn });
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Warnings.Add($"Group discovery failed for {domain.DnsName}: {SafeMessage(ex)}");
            }
        }

        // Domain trusts.
        if (config.IncludeTrusts)
        {
            try
            {
                var systemDn = $"CN=System,{domain.BaseDn}";
                var trusts = SearchPaged(domain.Connection, systemDn, "(objectClass=trustedDomain)", SearchScope.OneLevel,
                    new[] { "trustPartner", "trustDirection", "trustType", "trustAttributes", "flatName", "whenCreated" },
                    config.PageSize, config.TimeoutSeconds, cancellationToken);

                foreach (var entry in trusts)
                {
                    var partner = GetString(entry, "trustPartner");
                    if (string.IsNullOrWhiteSpace(partner))
                        continue;

                    var partnerExternalId = $"domain:{partner.ToLowerInvariant()}";
                    if (!nodes.ContainsKey(partnerExternalId))
                    {
                        addNode(new DiscoveredNodeContract
                        {
                            ExternalId = partnerExternalId,
                            NodeType = "Domain",
                            Name = partner,
                            FQDN = partner,
                            Status = "unknown",
                            Properties = new Dictionary<string, string>
                            {
                                ["DnsName"] = partner,
                                ["External"] = "true",
                                ["DiscoveryMethod"] = "AD trust relationship"
                            }
                        });
                    }

                    addRelationship(domainExternalId, partnerExternalId, "Trusts",
                        "AD trustedDomain object", "Verified",
                        new Dictionary<string, string>
                        {
                            ["Direction"] = TrustDirection(GetInt(entry, "trustDirection") ?? 0),
                            ["Type"] = TrustType(GetInt(entry, "trustType") ?? 0),
                            ["Attributes"] = (GetInt(entry, "trustAttributes") ?? 0).ToString(CultureInfo.InvariantCulture),
                            ["FlatName"] = GetString(entry, "flatName") ?? string.Empty
                        });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Warnings.Add($"Trust discovery failed for {domain.DnsName}: {SafeMessage(ex)}");
            }
        }

        // Actual NTDS replication connections (best effort).
        if (domainControllers.Count > 1)
        {
            try
            {
                DiscoverReplication(domain, config, domainControllers, computersByDn, addRelationship, result, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Warnings.Add($"Replication topology discovery failed for {domain.DnsName}: {SafeMessage(ex)}");
            }
        }
    }

    private static void DiscoverSites(
        LdapConnection connection,
        string configNc,
        string forestExternalId,
        ActiveDirectoryConfiguration config,
        Action<DiscoveredNodeContract> addNode,
        Action<string, string, string, string, string, Dictionary<string, string>?> addRelationship,
        DiscoveryResult result,
        CancellationToken cancellationToken)
    {
        var sitesDn = $"CN=Sites,{configNc}";
        var siteByDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sites = SearchPaged(connection, sitesDn, "(objectClass=site)", SearchScope.OneLevel,
            new[] { "cn", "distinguishedName", "description", "location", "whenCreated" },
            config.PageSize, config.TimeoutSeconds, cancellationToken);

        foreach (var entry in sites)
        {
            var dn = entry.DistinguishedName;
            var siteId = $"site:{dn.ToLowerInvariant()}";
            siteByDn[dn] = siteId;
            addNode(new DiscoveredNodeContract
            {
                ExternalId = siteId,
                NodeType = "Site",
                Name = GetString(entry, "cn") ?? FirstRdnValue(dn),
                FQDN = string.Empty,
                Status = "healthy",
                ParentExternalId = forestExternalId,
                ParentName = "AD Sites",
                Properties = new Dictionary<string, string>
                {
                    ["DistinguishedName"] = dn,
                    ["Description"] = GetString(entry, "description") ?? string.Empty,
                    ["Location"] = GetString(entry, "location") ?? string.Empty,
                    ["WhenCreated"] = FormatLdapTime(GetString(entry, "whenCreated"))
                }
            });
            addRelationship(forestExternalId, siteId, "Contains", "AD Sites container", "Verified", null);
        }

        var subnetsDn = $"CN=Subnets,{sitesDn}";
        var subnets = SearchPaged(connection, subnetsDn, "(objectClass=subnet)", SearchScope.OneLevel,
            new[] { "cn", "distinguishedName", "siteObject", "location", "description" },
            config.PageSize, config.TimeoutSeconds, cancellationToken);

        foreach (var entry in subnets)
        {
            var cidr = GetString(entry, "cn");
            if (string.IsNullOrWhiteSpace(cidr))
                continue;

            var dn = entry.DistinguishedName;
            var subnetId = $"subnet:{cidr.ToLowerInvariant()}";
            var siteObject = GetString(entry, "siteObject");
            var siteExternalId = siteObject != null && siteByDn.TryGetValue(siteObject, out var siteId)
                ? siteId
                : forestExternalId;

            addNode(new DiscoveredNodeContract
            {
                ExternalId = subnetId,
                NodeType = "Network",
                Name = cidr,
                FQDN = string.Empty,
                Status = "healthy",
                ParentExternalId = siteExternalId,
                ParentName = siteObject ?? "AD Subnets",
                Properties = new Dictionary<string, string>
                {
                    ["Cidr"] = cidr,
                    ["DistinguishedName"] = dn,
                    ["SiteObject"] = siteObject ?? string.Empty,
                    ["Location"] = GetString(entry, "location") ?? string.Empty,
                    ["Description"] = GetString(entry, "description") ?? string.Empty
                }
            });
            addRelationship(siteExternalId, subnetId, "Contains", "AD subnet siteObject attribute", "Verified", null);
        }
    }

    private static void DiscoverReplication(
        DomainConnection domain,
        ActiveDirectoryConfiguration config,
        List<(string ExternalId, string DnsName)> domainControllers,
        Dictionary<string, string> computersByDn,
        Action<string, string, string, string, string, Dictionary<string, string>?> addRelationship,
        DiscoveryResult result,
        CancellationToken cancellationToken)
    {
        var configNc = GetString(SearchSingle(domain.Connection, string.Empty, "(objectClass=*)", SearchScope.Base,
            new[] { "configurationNamingContext" }, config.TimeoutSeconds, cancellationToken), "configurationNamingContext");
        if (string.IsNullOrWhiteSpace(configNc))
            return;

        var sitesDn = $"CN=Sites,{configNc}";
        var servers = SearchPaged(domain.Connection, sitesDn, "(objectClass=server)", SearchScope.Subtree,
            new[] { "distinguishedName", "serverReference", "dNSHostName" },
            config.PageSize, config.TimeoutSeconds, cancellationToken).ToList();

        var serverToComputer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            var serverReference = GetString(server, "serverReference");
            if (!string.IsNullOrWhiteSpace(serverReference))
                serverToComputer[server.DistinguishedName] = serverReference;
        }

        var ntdsDsas = SearchPaged(domain.Connection, sitesDn, "(objectClass=nTDSDSA)", SearchScope.Subtree,
            new[] { "distinguishedName", "options" },
            config.PageSize, config.TimeoutSeconds, cancellationToken).ToList();

        var ntdsToComputer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dsa in ntdsDsas)
        {
            var serverDn = ParentDn(dsa.DistinguishedName);
            if (serverDn != null && serverToComputer.TryGetValue(serverDn, out var computerDn))
                ntdsToComputer[dsa.DistinguishedName] = computerDn;
        }

        var connections = SearchPaged(domain.Connection, sitesDn, "(objectClass=nTDSConnection)", SearchScope.Subtree,
            new[] { "distinguishedName", "fromServer", "options", "whenCreated" },
            config.PageSize, config.TimeoutSeconds, cancellationToken).ToList();

        var found = 0;
        foreach (var connection in connections)
        {
            var localDsaDn = ParentDn(connection.DistinguishedName);
            var remoteDsaDn = GetString(connection, "fromServer");
            if (localDsaDn == null || remoteDsaDn == null)
                continue;

            if (!ntdsToComputer.TryGetValue(localDsaDn, out var localComputerDn) ||
                !ntdsToComputer.TryGetValue(remoteDsaDn, out var remoteComputerDn))
                continue;

            if (!computersByDn.TryGetValue(localComputerDn, out var sourceId) ||
                !computersByDn.TryGetValue(remoteComputerDn, out var targetId))
                continue;

            addRelationship(sourceId, targetId, "ReplicatesTo",
                "AD nTDSConnection fromServer attribute", "Verified",
                new Dictionary<string, string>
                {
                    ["Options"] = (GetInt(connection, "options") ?? 0).ToString(CultureInfo.InvariantCulture),
                    ["WhenCreated"] = FormatLdapTime(GetString(connection, "whenCreated"))
                });
            found++;
        }

        if (found == 0)
            result.Warnings.Add($"No nTDSConnection replication links were readable for {domain.DnsName}; replication topology was not inferred.");
    }

    // ---------------------------------------------------------------------
    // LDAP helpers
    // ---------------------------------------------------------------------

    private static IEnumerable<SearchResultEntry> SearchPaged(
        LdapConnection connection,
        string baseDn,
        string filter,
        SearchScope scope,
        string[] attributes,
        int pageSize,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        byte[]? cookie = null;
        var page = Math.Clamp(pageSize, 50, 5000);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new SearchRequest(baseDn, filter, scope, attributes)
            {
                TimeLimit = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))
            };
            var pageControl = cookie == null
                ? new PageResultRequestControl(page)
                : new PageResultRequestControl(cookie) { PageSize = page };
            request.Controls.Add(pageControl);

            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
                yield return entry;

            cookie = null;
            foreach (DirectoryControl control in response.Controls)
            {
                if (control is PageResultResponseControl pageResponse && pageResponse.Cookie is { Length: > 0 })
                {
                    cookie = pageResponse.Cookie;
                    break;
                }
            }
        } while (cookie is { Length: > 0 });
    }

    private static SearchResultEntry SearchSingle(
        LdapConnection connection,
        string baseDn,
        string filter,
        SearchScope scope,
        string[] attributes,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new SearchRequest(baseDn, filter, scope, attributes)
        {
            TimeLimit = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))
        };
        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count == 0)
            throw new InvalidOperationException($"LDAP search returned no result for base '{baseDn}' and filter '{filter}'.");
        return response.Entries[0];
    }

    private static string? GetString(SearchResultEntry entry, string attributeName)
    {
        var attribute = FindAttribute(entry, attributeName);
        if (attribute == null || attribute.Count == 0)
            return null;
        var value = attribute[0];
        return value switch
        {
            null => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static IEnumerable<string> GetStrings(SearchResultEntry entry, string attributeName)
    {
        var attribute = FindAttribute(entry, attributeName);
        if (attribute == null)
            yield break;

        foreach (var value in attribute.GetValues(typeof(string)))
        {
            if (value != null)
                yield return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static DirectoryAttribute? FindAttribute(SearchResultEntry entry, string attributeName)
    {
        if (entry.Attributes.Contains(attributeName))
            return entry.Attributes[attributeName];

        foreach (DirectoryAttribute attribute in entry.Attributes.Values)
        {
            if (string.Equals(attribute.Name, attributeName, StringComparison.OrdinalIgnoreCase))
                return attribute;
        }
        return null;
    }

    private static int? GetInt(SearchResultEntry entry, string attributeName)
    {
        var value = GetString(entry, attributeName);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? GetSid(SearchResultEntry entry, string attributeName)
    {
        var attribute = FindAttribute(entry, attributeName);
        if (attribute == null || attribute.Count == 0 || attribute[0] is not byte[] bytes)
            return null;
        return BinarySidToString(bytes);
    }

    private static string BinarySidToString(byte[] sid)
    {
        if (sid.Length < 8)
            return string.Empty;

        var revision = sid[0];
        var subAuthorityCount = sid[1];
        ulong authority = 0;
        for (var i = 2; i < 8; i++)
            authority = (authority << 8) | sid[i];

        var parts = new List<string> { $"S-{revision}-{authority}" };
        for (var i = 0; i < subAuthorityCount; i++)
        {
            var offset = 8 + (i * 4);
            if (offset + 4 > sid.Length)
                break;
            var subAuthority = (uint)(sid[offset] | (sid[offset + 1] << 8) | (sid[offset + 2] << 16) | (sid[offset + 3] << 24));
            parts.Add(subAuthority.ToString(CultureInfo.InvariantCulture));
        }
        return string.Join("-", parts);
    }

    private static string ExternalIdFor(string prefix, SearchResultEntry entry)
    {
        var attribute = FindAttribute(entry, "objectGUID");
        if (attribute != null && attribute.Count > 0 && attribute[0] is byte[] guidBytes && guidBytes.Length == 16)
            return $"{prefix}:{new Guid(guidBytes):D}";
        return $"{prefix}:{entry.DistinguishedName.ToLowerInvariant()}";
    }

    private static string? ParentDn(string dn)
    {
        var index = FindFirstUnescapedComma(dn);
        return index < 0 ? null : dn[(index + 1)..].Trim();
    }

    private static int FindFirstUnescapedComma(string dn)
    {
        var escaped = false;
        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == ',')
                return i;
        }
        return -1;
    }

    private static string FirstRdnValue(string dn)
    {
        var comma = FindFirstUnescapedComma(dn);
        var rdn = comma < 0 ? dn : dn[..comma];
        var equals = rdn.IndexOf('=');
        return equals < 0 ? rdn : rdn[(equals + 1)..].Trim();
    }

    private static string ResolveParentExternalId(
        string? parentDn,
        string baseDn,
        string domainExternalId,
        Dictionary<string, string> dnToExternalId)
    {
        if (parentDn == null)
            return domainExternalId;
        if (dnToExternalId.TryGetValue(parentDn, out var parentId))
            return parentId;
        if (string.Equals(parentDn, baseDn, StringComparison.OrdinalIgnoreCase))
            return domainExternalId;
        return domainExternalId;
    }

    private static List<string> GetRangedMembers(
        LdapConnection connection,
        string groupDn,
        int maxMembers,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var members = new List<string>();
        var start = 0;
        while (members.Count < maxMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attribute = start == 0 ? "member" : $"member;range={start}-*";
            var request = new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, attribute)
            {
                TimeLimit = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))
            };
            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0)
                break;

            var entry = response.Entries[0];
            DirectoryAttribute? memberAttribute = null;
            foreach (DirectoryAttribute candidate in entry.Attributes.Values)
            {
                if (candidate.Name.StartsWith("member", StringComparison.OrdinalIgnoreCase))
                {
                    memberAttribute = candidate;
                    break;
                }
            }

            if (memberAttribute == null || memberAttribute.Count == 0)
                break;

            foreach (var value in memberAttribute.GetValues(typeof(string)))
            {
                var dn = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(dn))
                    members.Add(dn);
                if (members.Count >= maxMembers)
                    break;
            }

            var name = memberAttribute.Name;
            if (!name.Contains(';') || name.EndsWith("-*", StringComparison.OrdinalIgnoreCase))
                break;

            var rangeStart = name.IndexOf("range=", StringComparison.OrdinalIgnoreCase);
            var dash = name.IndexOf('-', rangeStart < 0 ? 0 : rangeStart);
            if (rangeStart < 0 || dash < 0)
                break;

            var startText = name[(rangeStart + 6)..dash];
            if (!int.TryParse(startText, out var parsedStart))
                break;

            var endText = name[(dash + 1)..];
            if (!int.TryParse(endText, out var parsedEnd))
                break;

            start = parsedEnd + 1;
            if (parsedEnd - parsedStart + 1 <= 0)
                break;
        }
        return members;
    }

    private static string DnToDns(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            return string.Empty;

        var parts = new List<string>();
        foreach (var rdn in SplitDn(dn))
        {
            var equals = rdn.IndexOf('=');
            if (equals <= 0)
                continue;
            var key = rdn[..equals].Trim();
            if (!key.Equals("DC", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("CN", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = rdn[(equals + 1)..].Trim().Replace("\\,", ",");
            parts.Add(value);
        }
        return string.Join(".", parts);
    }

    private static IEnumerable<string> SplitDn(string dn)
    {
        var current = new System.Text.StringBuilder();
        var escaped = false;
        foreach (var c in dn)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\')
            {
                escaped = true;
                current.Append(c);
                continue;
            }
            if (c == ',')
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    private static string DnFromDns(string dns)
    {
        if (string.IsNullOrWhiteSpace(dns))
            return string.Empty;
        return string.Join(",", dns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => $"DC={p}"));
    }

    private static string FormatLdapTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var trimmed = value.Split('.')[0];
        return DateTime.TryParseExact(trimmed, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToString("O", CultureInfo.InvariantCulture)
            : value;
    }

    private static string FormatFileTime(string? value)
    {
        if (!long.TryParse(value, out var fileTime) || fileTime <= 0)
            return string.Empty;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GroupScope(int groupType)
    {
        if ((groupType & 0x00000002) != 0) return "Global";
        if ((groupType & 0x00000004) != 0) return "DomainLocal";
        if ((groupType & 0x00000008) != 0) return "Universal";
        return "Unknown";
    }

    private static string GroupType(int groupType)
    {
        return (groupType & 0x80000000) != 0 ? "Security" : "Distribution";
    }

    private static string TrustDirection(int direction) => direction switch
    {
        1 => "Inbound",
        2 => "Outbound",
        3 => "Bidirectional",
        _ => "Disabled"
    };

    private static string TrustType(int type) => type switch
    {
        1 => "Downlevel",
        2 => "Uplevel",
        3 => "MIT",
        4 => "DCE",
        _ => "Unknown"
    };

    private static string ClassifyError(Exception ex) => ex switch
    {
        AdapterConfigurationException config => config.Code,
        LdapException ldap => $"LDAP_{ldap.ErrorCode}",
        _ => "AD_CONNECTION_ERROR"
    };

    private static string SafeMessage(Exception ex)
    {
        if (ex is AdapterConfigurationException)
            return ex.Message;
        if (ex is LdapException ldap)
            return $"LDAP error {ldap.ErrorCode}: {ldap.Message}";
        return ex.Message;
    }

    private sealed class DomainConnection
    {
        public string DnsName { get; init; } = string.Empty;
        public string BaseDn { get; init; } = string.Empty;
        public LdapConnection Connection { get; init; } = null!;
        public bool IsPrimary { get; init; }
    }
}

internal sealed class ActiveDirectoryConfiguration
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public bool UseSsl { get; set; } = true;
    public bool UseStartTls { get; set; }
    public string? BaseDn { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Password { get; set; }
    public bool AllowUntrustedCertificate { get; set; }
    public bool AllowPlaintext { get; set; }
    public int PageSize { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxGroupMembers { get; set; } = 5000;
    public bool IncludeGroups { get; set; } = true;
    public bool IncludeComputers { get; set; } = true;
    public bool IncludeOus { get; set; } = true;
    public bool IncludeSites { get; set; } = true;
    public bool IncludeTrusts { get; set; } = true;
    public bool DiscoverAllDomains { get; set; } = true;
    public List<AdditionalDomainConfiguration> AdditionalDomains { get; set; } = new();

    public static ActiveDirectoryConfiguration Parse(AdapterExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ConfigurationJson))
            throw new AdapterConfigurationException("AD_CONFIG", "Active Directory configuration is empty.");

        ActiveDirectoryConfiguration? config;
        try
        {
            config = JsonSerializer.Deserialize<ActiveDirectoryConfiguration>(context.ConfigurationJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            throw new AdapterConfigurationException("AD_CONFIG_JSON", $"Active Directory configuration is not valid JSON: {ex.Message}");
        }

        if (config == null)
            throw new AdapterConfigurationException("AD_CONFIG", "Active Directory configuration could not be parsed.");

        config.Password = context.Secret;
        if (string.IsNullOrWhiteSpace(config.Server))
            throw new AdapterConfigurationException("AD_SERVER", "server is required.");
        if (string.IsNullOrWhiteSpace(config.Username))
            throw new AdapterConfigurationException("AD_USERNAME", "username is required.");
        if (string.IsNullOrWhiteSpace(config.Password))
            throw new AdapterConfigurationException("AD_PASSWORD", "password is required in the credential vault.");
        if (config.UseSsl && config.UseStartTls)
            throw new AdapterConfigurationException("AD_TLS_MODE", "useSsl and useStartTls are mutually exclusive.");
        if (!config.UseSsl && !config.UseStartTls && !config.AllowPlaintext)
            throw new AdapterConfigurationException("AD_TLS_REQUIRED", "Plaintext LDAP is disabled. Enable LDAPS/StartTLS or explicitly set allowPlaintext=true.");
        if (config.Port is <= 0 or > 65535)
            throw new AdapterConfigurationException("AD_PORT", "port must be between 1 and 65535.");
        if (config.PageSize is < 50 or > 5000)
            config.PageSize = Math.Clamp(config.PageSize, 50, 5000);
        if (config.TimeoutSeconds is < 1 or > 300)
            config.TimeoutSeconds = Math.Clamp(config.TimeoutSeconds, 1, 300);
        if (config.MaxGroupMembers < 0)
            config.MaxGroupMembers = 0;

        config.AdditionalDomains ??= new List<AdditionalDomainConfiguration>();
        return config;
    }

    public ActiveDirectoryConfiguration CloneForDomain(string server, string baseDn, string username, int? port = null, bool? useSsl = null)
    {
        return new ActiveDirectoryConfiguration
        {
            Server = server,
            Port = port ?? Port,
            UseSsl = useSsl ?? UseSsl,
            UseStartTls = useSsl.HasValue ? !useSsl.Value : UseStartTls,
            BaseDn = baseDn,
            Username = username,
            Password = Password,
            AllowUntrustedCertificate = AllowUntrustedCertificate,
            AllowPlaintext = AllowPlaintext,
            PageSize = PageSize,
            TimeoutSeconds = TimeoutSeconds,
            MaxGroupMembers = MaxGroupMembers,
            IncludeGroups = IncludeGroups,
            IncludeComputers = IncludeComputers,
            IncludeOus = IncludeOus,
            IncludeSites = false,
            IncludeTrusts = IncludeTrusts,
            DiscoverAllDomains = false,
            AdditionalDomains = new List<AdditionalDomainConfiguration>()
        };
    }
}

internal sealed class AdditionalDomainConfiguration
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 636;
    public string? BaseDn { get; set; }
    public string? Username { get; set; }
    public bool UseSsl { get; set; } = true;
    public string? Forest { get; set; }
}

internal static class LdapConnectionFactory
{
    public static LdapConnection Create(ActiveDirectoryConfiguration config, List<string> warnings)
    {
        var identifier = new LdapDirectoryIdentifier(config.Server, config.Port, true, false);
        var credential = new NetworkCredential(config.Username, config.Password);
        var connection = new LdapConnection(identifier, credential, AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        var lastCertificateError = string.Empty;
        connection.SessionOptions.VerifyServerCertificate = (_, certificate) =>
        {
            if (config.AllowUntrustedCertificate)
            {
                warnings.Add("TLS certificate validation is disabled for this integration. This must only be used in an isolated lab.");
                return true;
            }

            if (CertificateTrust.Validate(certificate, config.Server, out var error))
                return true;

            lastCertificateError = error;
            return false;
        };

        if (config.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else if (config.UseStartTls)
        {
            connection.SessionOptions.StartTransportLayerSecurity(new DirectoryControlCollection());
        }
        else
        {
            warnings.Add("LDAP is running without TLS. Credentials and directory data are transmitted in plaintext.");
        }

        try
        {
            connection.Bind();
        }
        catch (LdapException) when (!string.IsNullOrWhiteSpace(lastCertificateError))
        {
            connection.Dispose();
            throw new AdapterConfigurationException("AD_TLS_CERTIFICATE", $"TLS certificate validation failed: {lastCertificateError}");
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }
}

internal static class CertificateTrust
{
    public static bool Validate(X509Certificate certificate, string host, out string error)
    {
        error = string.Empty;
        try
        {
            using var cert = new X509Certificate2(certificate);
            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.NoFlag
                }
            };

            if (!chain.Build(cert))
            {
                error = "The server certificate chain is not trusted.";
                return false;
            }

            if (!MatchesHost(cert, host))
            {
                error = $"The server certificate does not match host '{host}'.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool MatchesHost(X509Certificate2 cert, string host)
    {
        if (string.Equals(cert.GetNameInfo(X509NameType.DnsName, false), host, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var extension in cert.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                foreach (var dnsName in san.EnumerateDnsNames())
                {
                    if (string.Equals(dnsName, host, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                foreach (var ip in san.EnumerateIPAddresses())
                {
                    if (string.Equals(ip.ToString(), host, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }
}
