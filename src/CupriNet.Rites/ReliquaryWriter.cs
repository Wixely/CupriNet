namespace CupriNet.Rites;

/// <summary>
/// Writes a received file to disk safely: the relative path is guarded and confined under a destination
/// root, the bytes are written to a temporary file and then atomically moved into place, and (on Ubuntu)
/// the file is restricted to the owner. Cross-platform (Windows and Linux).
/// </summary>
public static class ReliquaryWriter
{
    public static async Task WriteAsync(string rootDirectory, string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);

        var safe = FilePathGuard.Normalize(relativePath)
                   ?? throw new ReliquaryException($"Unsafe or invalid relative path: '{relativePath}'.");

        var root = Path.GetFullPath(rootDirectory);
        var destination = Path.GetFullPath(Path.Combine(root, safe));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (destination != root && !destination.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new ReliquaryException("Resolved path escapes the destination root.");

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        var temp = destination + ".part-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, content, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(temp, destination, overwrite: true); // atomic finalisation
    }
}
