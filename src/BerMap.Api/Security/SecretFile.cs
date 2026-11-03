using System.Text;
using BerMap.Infrastructure.Security;

namespace BerMap.Api.Security;

/// <summary>Owner-only secret files inside the credential vault.</summary>
internal static class SecretFile
{
    public static void EnsureDirectory(string path) => SecureVault.EnsureDirectory(path);

    public static void WriteAllBytes(string path, byte[] content) => SecureVault.WriteAllBytes(path, content);

    public static void WriteAllText(string path, string content) =>
        SecureVault.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
}
