using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CupriNet.Persistence;

/// <summary>
/// Cross-platform helpers for restricting access to secret files. On Unix (Ubuntu) this sets POSIX mode bits
/// (0600 for files, 0700 for directories). On Windows it sets an owner-only DACL (inheritance disabled), so the
/// secret is not readable by other standard users even outside the profile directory. Both are best-effort on
/// Windows: a filesystem that can't hold an ACL (FAT, some network shares) is left as-is, and since the secret
/// bytes are also AEAD-encrypted at rest, a failure here is not a disclosure by itself.
/// </summary>
internal static class FilePermissions
{
    public static void RestrictFileToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictToOwnerWindows(path, isDirectory: false);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RestrictDirectoryToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictToOwnerWindows(path, isDirectory: true);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToOwnerWindows(string path, bool isDirectory)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null)
                return;

            if (isDirectory)
            {
                var security = new DirectorySecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // drop inherited ACEs
                security.AddAccessRule(new FileSystemAccessRule(
                    user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
        }
        catch
        {
            // Best-effort: a filesystem that can't store the ACL leaves the secret as-is (still AEAD-encrypted).
        }
    }
}
