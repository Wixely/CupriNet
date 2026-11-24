namespace CupriNet.Rites;

/// <summary>
/// Validates and normalizes untrusted relative paths from a Reliquary manifest. Rejects absolute/rooted
/// paths, drive/ADS colons, empty or '.'/'..' segments (directory traversal), and invalid filename
/// characters. Output uses forward slashes. Treat every incoming path as hostile.
/// <para>
/// The rules are the same on every OS — the strictest of them, which is Windows' — so a path one node accepts
/// every node accepts, and a name that is harmless where it was made cannot turn into a device or a truncated
/// name where it lands.
/// </para>
/// </summary>
public static class FilePathGuard
{
    // What no segment may contain, anywhere: the Windows set, used everywhere. (The separators and ':' are handled
    // on their own below; control characters by ContainsControlChar.)
    private static readonly char[] InvalidNameChars = ['"', '<', '>', '|', '*', '?'];

    // Names Windows resolves to a device whatever directory they appear in, with or without an extension.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns a safe normalized relative path (forward slashes), or null if the path is unsafe.</summary>
    public static string? Normalize(string path, int maxLength = 1024)
    {
        if (string.IsNullOrEmpty(path) || path.Length > maxLength)
            return null;
        if (Path.IsPathRooted(path))
            return null;
        if (path.Contains(':')) // drive letters, alternate data streams
            return null;

        var segments = path.Split('/', '\\');
        var clean = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)          // leading/trailing/double separator
                return null;
            if (segment is "." or "..")       // current-dir or traversal
                return null;
            if (segment.IndexOfAny(InvalidNameChars) >= 0)
                return null;
            if (ContainsControlChar(segment))
                return null;
            if (segment[^1] is '.' or ' ')    // Windows silently strips these, so two names would become one
                return null;
            var stem = segment.IndexOf('.') is var dot and >= 0 ? segment[..dot] : segment;
            if (ReservedNames.Contains(stem.TrimEnd(' ')))
                return null;
            clean.Add(segment);
        }

        return clean.Count == 0 ? null : string.Join('/', clean);
    }

    /// <summary>True if the path is a safe relative path.</summary>
    public static bool IsSafe(string path) => Normalize(path) is not null;

    /// <summary>
    /// True if the path is safe <em>and already in normal form</em> — what a path on the wire must be. A decoder
    /// checks this so that an unsafe path never reaches the code that would use it.
    /// </summary>
    public static bool IsCanonical(string path, int maxLength = 1024)
        => string.Equals(Normalize(path, maxLength), path, StringComparison.Ordinal);

    private static bool ContainsControlChar(string segment)
    {
        foreach (var c in segment)
        {
            if (char.IsControl(c))
                return true;
        }

        return false;
    }
}
