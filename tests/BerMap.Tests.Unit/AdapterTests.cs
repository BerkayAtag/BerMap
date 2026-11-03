using System;
using System.Threading.Tasks;
using BerMap.Contracts;
using BerMap.Integrations.ActiveDirectory;
using BerMap.Integrations.VMware;
using Xunit;

namespace BerMap.Tests.Unit;

public class AdapterTests
{
    [Fact]
    public async Task ActiveDirectoryAdapter_EmptyConfiguration_FailsExplicitly()
    {
        var adapter = new ActiveDirectoryAdapter();
        var context = new AdapterExecutionContext { ConfigurationJson = "{}" };

        var testResult = await adapter.TestConnectionAsync(context);
        var exception = await Assert.ThrowsAsync<AdapterConfigurationException>(
            () => adapter.DiscoverAsync(context));

        Assert.Equal("ActiveDirectory", adapter.AdapterType);
        Assert.False(testResult.Success);
        Assert.Equal("AD_SERVER", testResult.ErrorCode);
        Assert.Equal("AD_SERVER", exception.Code);
    }

    [Fact]
    public async Task ActiveDirectoryAdapter_MissingSecret_FailsWithPasswordCode()
    {
        var adapter = new ActiveDirectoryAdapter();
        var context = new AdapterExecutionContext
        {
            ConfigurationJson = """{"server":"dc01.corp.local","username":"svc-bermap"}"""
        };

        var testResult = await adapter.TestConnectionAsync(context);

        Assert.False(testResult.Success);
        Assert.Equal("AD_PASSWORD", testResult.ErrorCode);
    }

    [Fact]
    public async Task ActiveDirectoryAdapter_PlaintextLdapRequiresExplicitOptIn()
    {
        var adapter = new ActiveDirectoryAdapter();
        var context = new AdapterExecutionContext
        {
            ConfigurationJson = """{"server":"dc01.corp.local","username":"svc-bermap","useSsl":false}""",
            Secret = "not-a-real-password"
        };

        var testResult = await adapter.TestConnectionAsync(context);

        Assert.False(testResult.Success);
        Assert.Equal("AD_TLS_REQUIRED", testResult.ErrorCode);
    }

    [Fact]
    public async Task ActiveDirectoryAdapter_UnreachableServer_FailsWithLdapErrorNotPlatformError()
    {
        // VerifyServerCertificate is Windows-only; on Linux/macOS the factory must not touch it,
        // so an unreachable DC surfaces as an LDAP error (or a missing native libldap), never as
        // PlatformNotSupportedException.
        var adapter = new ActiveDirectoryAdapter();
        var context = new AdapterExecutionContext
        {
            ConfigurationJson = """{"server":"127.0.0.1","port":1,"username":"svc-bermap","timeoutSeconds":2}""",
            Secret = "not-a-real-password"
        };

        var testResult = await adapter.TestConnectionAsync(context);
        var discovery = await adapter.DiscoverAsync(context);

        Assert.False(testResult.Success);
        Assert.DoesNotContain("platform", testResult.ErrorDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(discovery.Success);
        Assert.Equal("Failed", discovery.Status);
        Assert.NotEmpty(discovery.Errors);
    }

    [Fact]
    public void ActiveDirectoryAdapter_AdvertisesExpectedCapabilities()
    {
        var adapter = new ActiveDirectoryAdapter();
        var capabilities = adapter.GetCapabilities();

        Assert.True(capabilities.SupportsFullDiscovery);
        Assert.Contains("Computer", capabilities.SupportedEntityTypes);
        Assert.Contains("Domain", capabilities.SupportedEntityTypes);
        Assert.Contains("Trusts", capabilities.SupportedRelationshipTypes);
    }

    [Fact]
    public async Task VMwareAdapter_EmptyConfiguration_FailsExplicitly()
    {
        var adapter = new VMwareAdapter();
        var context = new AdapterExecutionContext { ConfigurationJson = "{}" };

        var testResult = await adapter.TestConnectionAsync(context);
        var exception = await Assert.ThrowsAsync<AdapterConfigurationException>(
            () => adapter.DiscoverAsync(context));

        Assert.Equal("VMware", adapter.AdapterType);
        Assert.False(testResult.Success);
        Assert.Equal("VMWARE_SERVER", testResult.ErrorCode);
        Assert.Equal("VMWARE_SERVER", exception.Code);
    }

    [Fact]
    public async Task VMwareAdapter_MissingSecret_FailsWithPasswordCode()
    {
        var adapter = new VMwareAdapter();
        var context = new AdapterExecutionContext
        {
            ConfigurationJson = """{"server":"vcenter01.corp.local","username":"svc-bermap"}"""
        };

        var testResult = await adapter.TestConnectionAsync(context);

        Assert.False(testResult.Success);
        Assert.Equal("VMWARE_PASSWORD", testResult.ErrorCode);
    }

    [Fact]
    public void VMwareAdapter_AdvertisesExpectedCapabilities()
    {
        var adapter = new VMwareAdapter();
        var capabilities = adapter.GetCapabilities();

        Assert.Contains("Hypervisor", capabilities.SupportedEntityTypes);
        Assert.Contains("VirtualMachine", capabilities.SupportedEntityTypes);
        Assert.True(capabilities.SupportsMetrics);
    }
}
