using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BerMap.Infrastructure.Security;

/// <summary>
/// Creates the credential vault directory and writes its files so that only
/// the service account (and administrators/SYSTEM on Windows) can read them.
/// The vault holds the master key, the token signing key and the initial
/// administrator password: anyone who can read it can decrypt every stored
/// credential and mint administrator tokens.
/// </summary>
public static class SecureVault
{
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            RestrictDirectoryOnWindows(path);
        }
        else
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Writes to a temporary file and renames it into place so a crash never
    /// leaves a truncated key or secret behind. On Unix the file is created
    /// owner-only from the start (no window where the umask applies); on
    /// Windows it inherits the protected directory ACL.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] content)
    {
        var temporaryPath = path + ".tmp";
        using (var stream = OperatingSystem.IsWindows()
                   ? new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None)
                   : new FileStream(temporaryPath, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       Share = FileShare.None,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                   }))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictDirectoryOnWindows(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(IdentityReference identity) => security.AddAccessRule(new FileSystemAccessRule(
            identity, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));

        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (WindowsIdentity.GetCurrent().User is { } serviceAccount)
            Allow(serviceAccount);

        new DirectoryInfo(path).SetAccessControl(security);
    }
}
