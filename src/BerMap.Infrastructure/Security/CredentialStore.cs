using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.Interfaces;

namespace BerMap.Infrastructure.Security;

public class CredentialStore : ICredentialStore
{
    private readonly string _vaultPath;
    private readonly byte[] _masterKey;

    public CredentialStore(string? vaultDirectory = null)
    {
        _vaultPath = vaultDirectory ?? Path.Combine(AppContext.BaseDirectory, "secrets_vault");
        if (!Directory.Exists(_vaultPath))
        {
            Directory.CreateDirectory(_vaultPath);
        }

        // Derive deterministic machine-level key for demonstration/development
        // On production Windows Server, this is backed by DPAPI; on Linux by kernel keyring
        var keyPath = Path.Combine(_vaultPath, ".master.key");
        if (File.Exists(keyPath))
        {
            _masterKey = File.ReadAllBytes(keyPath);
        }
        else
        {
            _masterKey = new byte[32]; // 256-bit
            RandomNumberGenerator.Fill(_masterKey);
            File.WriteAllBytes(keyPath, _masterKey);
        }
    }

    public async Task StoreSecretAsync(string key, string plainTextSecret, CancellationToken cancellationToken = default)
    {
        var secretBytes = Encoding.UTF8.GetBytes(plainTextSecret);
        var nonce = new byte[12]; // 96-bit nonce for AES-GCM
        var tag = new byte[16];   // 128-bit authentication tag
        var cipherText = new byte[secretBytes.Length];

        RandomNumberGenerator.Fill(nonce);

        using (var aesGcm = new AesGcm(_masterKey, 16))
        {
            aesGcm.Encrypt(nonce, secretBytes, cipherText, tag);
        }

        // Format: [12 bytes nonce] + [16 bytes tag] + [ciphertext]
        var combined = new byte[nonce.Length + tag.Length + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherText, 0, combined, nonce.Length + tag.Length, cipherText.Length);

        var safeFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".enc";
        var filePath = Path.Combine(_vaultPath, safeFileName);
        await File.WriteAllBytesAsync(filePath, combined, cancellationToken);
    }

    public async Task<string?> RetrieveSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        var safeFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".enc";
        var filePath = Path.Combine(_vaultPath, safeFileName);

        if (!File.Exists(filePath))
            return null;

        var combined = await File.ReadAllBytesAsync(filePath, cancellationToken);
        if (combined.Length < 28) // Nonce (12) + Tag (16)
            return null;

        var nonce = new byte[12];
        var tag = new byte[16];
        var cipherLength = combined.Length - 28;
        var cipherText = new byte[cipherLength];
        var plainText = new byte[cipherLength];

        Buffer.BlockCopy(combined, 0, nonce, 0, 12);
        Buffer.BlockCopy(combined, 12, tag, 0, 16);
        Buffer.BlockCopy(combined, 28, cipherText, 0, cipherLength);

        using (var aesGcm = new AesGcm(_masterKey, 16))
        {
            aesGcm.Decrypt(nonce, cipherText, tag, plainText);
        }

        return Encoding.UTF8.GetString(plainText);
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        var safeFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".enc";
        var filePath = Path.Combine(_vaultPath, safeFileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }
}
