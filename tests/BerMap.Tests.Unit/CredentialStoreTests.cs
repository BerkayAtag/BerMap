using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using BerMap.Infrastructure.Security;

namespace BerMap.Tests.Unit;

public class CredentialStoreTests : IDisposable
{
    private readonly string _tempVault;
    private readonly CredentialStore _store;

    public CredentialStoreTests()
    {
        _tempVault = Path.Combine(Path.GetTempPath(), $"bermap_vault_test_{Guid.NewGuid():N}");
        _store = new CredentialStore(_tempVault);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempVault))
            {
                Directory.Delete(_tempVault, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task StoreAndRetrieveSecret_DecryptsOriginalPlainText()
    {
        // Arrange
        var key = "vcenter_prod_secret";
        var originalPlainText = "SuperSecure@EnterprisePassword2026!";

        // Act
        await _store.StoreSecretAsync(key, originalPlainText);
        var retrieved = await _store.RetrieveSecretAsync(key);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(originalPlainText, retrieved);
    }

    [Fact]
    public async Task RetrieveNonExistentSecret_ReturnsNull()
    {
        // Act
        var result = await _store.RetrieveSecretAsync("non_existent_key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteSecret_RemovesFromVault()
    {
        // Arrange
        var key = "temp_key";
        await _store.StoreSecretAsync(key, "my-secret");

        // Act
        await _store.DeleteSecretAsync(key);
        var afterDelete = await _store.RetrieveSecretAsync(key);

        // Assert
        Assert.Null(afterDelete);
    }
}
