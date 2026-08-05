using CupriNet.Codex;
using CupriNet.Marks;

namespace CupriNet.Core;

/// <summary>
/// A node's static WebRTC endpoint parameters, published (signed) inside an <see cref="Intonation"/> so a browser can
/// open a WebRTC DataChannel to the node with <b>no signalling server</b>: it preloads these as the remote
/// description and dials the node's beacon address directly. The host address(es) come from the Intonation's
/// <see cref="Intonation.Beacons"/>; this block adds the WebRTC-specific bits.
///
/// <para>It carries <b>no secret</b> — these authenticate nothing on their own; the Noise handshake pinned to the
/// node's Sigil (run over the DataChannel) is the real authentication. The DTLS fingerprint is trustworthy because
/// the Intonation is signed, which also lets the browser verify the node's cert (MITM-resistant even at DTLS). See
/// <c>design/webrtc-clients.md</c>.</para>
/// </summary>
public sealed record WebRtcEndpoint
{
    /// <summary>ICE username fragment. The node runs ICE-lite with a stable ufrag/password.</summary>
    public required string IceUfrag { get; init; }

    /// <summary>ICE password.</summary>
    public required string IcePassword { get; init; }

    /// <summary>DTLS fingerprint hash algorithm, e.g. <c>sha-256</c>.</summary>
    public required string FingerprintAlgorithm { get; init; }

    /// <summary>Raw DTLS certificate fingerprint bytes (32 for sha-256).</summary>
    public required byte[] Fingerprint { get; init; }

    /// <summary>UDP port of the node's WebRTC host candidate (host address(es) taken from the Intonation's beacons).</summary>
    public required int Port { get; init; }
}

/// <summary>Canonical serialization for a <see cref="WebRtcEndpoint"/> block (embedded in the Intonation body).</summary>
public static class WebRtcCodec
{
    /// <summary>Max length (chars) of an ICE ufrag/password/algorithm string, a Ward. Over-long is clamped.</summary>
    public const int MaxStringLength = 256;

    /// <summary>Max DTLS fingerprint length in bytes, a Ward (sha-256 is 32; sha-512 is 64).</summary>
    public const int MaxFingerprintBytes = 64;

    public static void Write(CodexWriter writer, WebRtcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(endpoint);

        // Stamp the highest WebRtcEndpoint version this build supports; a reader accepts it via CupriMarks.Accepts,
        // so the block range-negotiates independently of the Intonation envelope's own version.
        writer.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.WebRtcEndpoint).Max));
        writer.WriteString(Clamp(endpoint.IceUfrag));
        writer.WriteString(Clamp(endpoint.IcePassword));
        writer.WriteString(Clamp(endpoint.FingerprintAlgorithm));
        writer.WriteBytes(endpoint.Fingerprint);
        writer.WriteVarUInt((ulong)(uint)endpoint.Port);
    }

    public static WebRtcEndpoint Read(ref CodexReader reader)
    {
        var version = reader.ReadByte();
        if (!CupriMarks.Accepts(CupriMarks.WebRtcEndpoint, version))
            throw new CodexFormatException($"Unsupported WebRtcEndpoint version {version}.");

        var ufrag = Clamp(reader.ReadString());
        var password = Clamp(reader.ReadString());
        var algorithm = Clamp(reader.ReadString());

        var fingerprint = reader.ReadBytes();
        if (fingerprint.Length is 0 or > MaxFingerprintBytes)
            throw new CodexFormatException("Invalid WebRTC DTLS fingerprint length.");

        var port = (long)reader.ReadVarUInt();
        if (port is < 1 or > 65535)
            throw new CodexFormatException("WebRTC endpoint port is out of range.");

        return new WebRtcEndpoint
        {
            IceUfrag = ufrag,
            IcePassword = password,
            FingerprintAlgorithm = algorithm,
            Fingerprint = fingerprint.ToArray(),
            Port = (int)port,
        };
    }

    private static string Clamp(string value) => value.Length > MaxStringLength ? value[..MaxStringLength] : value;
}
