using CupriNet.Codex;
using CupriNet.Marks;

namespace CupriNet.Rites;

/// <summary>A single Oracle header — an HTTP-shaped name/value pair. Names are compared case-insensitively.</summary>
public sealed record OracleHeader(string Name, string Value);

/// <summary>
/// An Oracle request — the "consult" half of the request/response rite a Pilgrim sends to a Shrine. HTTP-shaped:
/// a method, a path, headers, and an optional body. The rite itself is authenticated and encrypted by the
/// session (the Pilgrimage handshake + Veil), so requests carry no per-frame author envelope.
/// </summary>
public sealed record OracleRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public IReadOnlyList<OracleHeader> Headers { get; init; } = [];
    public byte[] Body { get; init; } = [];

    /// <summary>A body-less GET request for <paramref name="path"/>.</summary>
    public static OracleRequest Get(string path, params OracleHeader[] headers)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new OracleRequest { Method = "GET", Path = path, Headers = headers ?? [] };
    }

    /// <summary>The value of the first header named <paramref name="name"/> (case-insensitive), or null.</summary>
    public string? Header(string name)
        => Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>An Oracle response — the "answer" a Shrine returns for a request: a status, headers, and a body.</summary>
public sealed record OracleResponse
{
    public required uint Status { get; init; }
    public IReadOnlyList<OracleHeader> Headers { get; init; } = [];
    public byte[] Body { get; init; } = [];

    /// <summary>The value of the first header named <paramref name="name"/> (case-insensitive), or null.</summary>
    public string? Header(string name)
        => Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>The <c>content-type</c> header, if present.</summary>
    public string? ContentType => Header("content-type");

    /// <summary>A 200 response carrying <paramref name="body"/> with the given content type.</summary>
    public static OracleResponse Ok(ReadOnlySpan<byte> body, string contentType)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        return new OracleResponse
        {
            Status = 200,
            Headers = [new OracleHeader("content-type", contentType)],
            Body = body.ToArray(),
        };
    }

    /// <summary>A 200 UTF-8 text response.</summary>
    public static OracleResponse Text(string text, string contentType = "text/plain; charset=utf-8")
    {
        ArgumentNullException.ThrowIfNull(text);
        return Ok(System.Text.Encoding.UTF8.GetBytes(text), contentType);
    }

    /// <summary>A 404 response with an empty body.</summary>
    public static OracleResponse NotFound() => new() { Status = 404 };

    /// <summary>The payload decoded as UTF-8 text.</summary>
    public string AsText() => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>Canonical serialization for <see cref="OracleRequest"/> and <see cref="OracleResponse"/>.</summary>
public static class OracleCodec
{
    /// <summary>An upper bound on header count, so a malformed frame cannot force a huge allocation.</summary>
    private const ulong MaxHeaders = 4096;

    /// <summary>
    /// Every frame leads with the Oracle version this build speaks, accepted by the reader through
    /// <see cref="CupriMarks.Accepts"/>. The rites used to inherit their compatibility from the handshake — every node
    /// that spoke a given Conjunction spoke all the rites, because they all shipped together. The Oracle and the
    /// Auspice are the first rites added <em>after</em> a release, so that no longer holds: two peers can agree on a
    /// Conjunction version and still disagree about which rites exist. Stamping the version makes the mismatch a
    /// typed refusal instead of a frame nobody can parse.
    /// </summary>
    private static byte Version => checked((byte)CupriMarks.Supported(CupriMarks.Oracle).Max);

    private static void ReadAndCheckVersion(ref CodexReader r)
    {
        var version = r.ReadByte();
        if (!CupriMarks.Accepts(CupriMarks.Oracle, version))
            throw new CodexFormatException($"Unsupported Oracle frame version {version}.");
    }

    public static byte[] EncodeRequest(OracleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var w = new CodexWriter();
        w.WriteByte(Version);
        w.WriteString(request.Method);
        w.WriteString(request.Path);
        WriteHeaders(w, request.Headers);
        w.WriteBytes(request.Body);
        return w.ToArray();
    }

    public static OracleRequest DecodeRequest(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        ReadAndCheckVersion(ref r);
        var method = r.ReadString();
        var path = r.ReadString();
        var headers = ReadHeaders(ref r);
        var body = r.ReadBytes().ToArray();
        return new OracleRequest { Method = method, Path = path, Headers = headers, Body = body };
    }

    public static byte[] EncodeResponse(OracleResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var w = new CodexWriter();
        w.WriteByte(Version);
        w.WriteUInt32(response.Status);
        WriteHeaders(w, response.Headers);
        w.WriteBytes(response.Body);
        return w.ToArray();
    }

    public static OracleResponse DecodeResponse(ReadOnlySpan<byte> data)
    {
        var r = new CodexReader(data);
        ReadAndCheckVersion(ref r);
        var status = r.ReadUInt32();
        var headers = ReadHeaders(ref r);
        var body = r.ReadBytes().ToArray();
        return new OracleResponse { Status = status, Headers = headers, Body = body };
    }

    private static void WriteHeaders(CodexWriter w, IReadOnlyList<OracleHeader> headers)
    {
        w.WriteVarUInt((ulong)headers.Count);
        foreach (var h in headers)
        {
            w.WriteString(h.Name);
            w.WriteString(h.Value);
        }
    }

    private static IReadOnlyList<OracleHeader> ReadHeaders(ref CodexReader r)
    {
        var count = r.ReadVarUInt();
        if (count > MaxHeaders)
            throw new CodexFormatException($"Oracle frame declares too many headers ({count}).");

        var headers = new List<OracleHeader>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            var name = r.ReadString();
            var value = r.ReadString();
            headers.Add(new OracleHeader(name, value));
        }

        return headers;
    }
}
