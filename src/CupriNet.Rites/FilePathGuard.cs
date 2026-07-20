namespace CupriNet.Rites;

/// <summary>
/// Validates and normalizes untrusted relative paths from a Reliquary manifest. Rejects absolute/rooted
/// paths, drive/ADS colons, empty or '.'/'..' segments (directory traversal), and invalid filename
/// characters. Output uses forward slashes. Treat every incoming path as hostile.
/// </summary>
public static class FilePathGuard
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

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
            clean.Add(segment);
        }

        return clean.Count == 0 ? null : string.Join('/', clean);
    }

    /// <summary>True if the path is a safe relative path.</summary>
    public static bool IsSafe(string path) => Normalize(path) is not null;

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
