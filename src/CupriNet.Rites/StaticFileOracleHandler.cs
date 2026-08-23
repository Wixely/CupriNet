namespace CupriNet.Rites;

/// <summary>
/// An <see cref="IOracleHandler"/> that serves static files from a directory. Every request path is validated with
/// <see cref="FilePathGuard"/> and the resolved path is re-checked against the root, so a Pilgrim cannot escape it.
/// GET/HEAD only. Reads whole files into memory, and refuses (500, without reading) a file past
/// <see cref="OracleCodec.MaxBodyBytes"/> — the Oracle carries pages and documents; a bigger artifact is served as a
/// relic (chunked, hash-verified), not as one message.
/// </summary>
public sealed class StaticFileOracleHandler : IOracleHandler
{
    private readonly string _root;
    private readonly string _defaultDocument;

    public StaticFileOracleHandler(string rootDirectory, string defaultDocument = "index.html")
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentException.ThrowIfNullOrEmpty(defaultDocument);
        _root = Path.GetFullPath(rootDirectory);
        _defaultDocument = defaultDocument;
    }

    public async Task<OracleResponse> HandleAsync(OracleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method is not ("GET" or "HEAD"))
            return new OracleResponse { Status = 405 };

        var relative = ResolveRelative(request.Path);
        if (relative is null)
            return OracleResponse.NotFound();

        var fullPath = Path.GetFullPath(Path.Combine(_root, relative));
        if (!IsUnderRoot(fullPath) || !File.Exists(fullPath))
            return OracleResponse.NotFound();

        // Check the size before touching the content: a file past the message ceiling is refused with the reason
        // (and never read into memory), for GET and HEAD alike — a HEAD that promises what GET refuses helps no one.
        if (new FileInfo(fullPath).Length > OracleCodec.MaxBodyBytes)
            return OracleResponse.Text(
                $"'{relative}' exceeds the {OracleCodec.MaxBodyBytes}-byte message ceiling. " +
                "Serve it as a relic (chunked, hash-verified) instead.") with { Status = 500 };

        var body = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var response = OracleResponse.Ok(body, ContentTypeFor(fullPath));
        return request.Method == "HEAD" ? response with { Body = [] } : response;
    }

    /// <summary>Maps a request path to a safe relative file path under the root, or null if it is unsafe/empty.</summary>
    private string? ResolveRelative(string path)
    {
        var p = path;
        var query = p.IndexOf('?');
        if (query >= 0)
            p = p[..query];

        p = p.TrimStart('/');
        if (p.Length == 0 || p.EndsWith('/'))
            p += _defaultDocument;

        return FilePathGuard.Normalize(p);
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, _root, StringComparison.Ordinal))
            return true;

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".wasm" => "application/wasm",
        ".txt" => "text/plain; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".webp" => "image/webp",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => "application/octet-stream",
    };
}
