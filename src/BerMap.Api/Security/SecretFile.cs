using System;
using System.IO;

namespace BerMap.Api.Security;

/// <summary>
/// Writes secret material so that, on Unix hosts, only the service account can
/// read it. On Windows the vault directory ACL is managed by the installer.
/// </summary>
internal static class SecretFile
{
    public static void WriteAllBytes(string path, byte[] content)
    {
        File.WriteAllBytes(path, content);
        RestrictToOwner(path);
    }

    public static void WriteAllText(string path, string content)
    {
        File.WriteAllText(path, content);
        RestrictToOwner(path);
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
