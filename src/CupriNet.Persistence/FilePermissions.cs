namespace CupriNet.Persistence;

/// <summary>
/// Cross-platform helpers for restricting access to secret files. On Unix (Ubuntu) this sets POSIX
/// mode bits (0600 for files, 0700 for directories). On Windows the calls are no-ops here — files live
/// under the per-user profile directory; tighter Windows ACLs are a later hardening step.
/// </summary>
internal static class FilePermissions
{
    public static void RestrictFileToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RestrictDirectoryToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
