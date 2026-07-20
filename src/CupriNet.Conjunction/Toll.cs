using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using CupriNet.Codex;
using CupriNet.Vessel;

namespace CupriNet.Conjunction;

/// <summary>
/// A pre-handshake stateless cookie — the "Toll". Before the responder allocates any Noise handshake
/// state (an ephemeral key, a handshake object, a Diffie–Hellman), it issues a cookie bound to the
/// initiator's observed address under a per-node secret and requires the initiator to echo it. The
/// responder keeps NO per-connection state to validate the echo — it recomputes the HMAC — so an attacker
/// cannot exhaust responder memory just by opening connections and stalling before the expensive crypto.
/// The Toll also provides a cheap, pre-crypto point at which to rate-limit by address.
/// </summary>
public static class Toll
{
    private const ushort TollStream = 0;
    private const byte Version = 1;

    /// <summary>The size in bytes of a node's Toll secret.</summary>
    public const int SecretSize = 32;

    /// <summary>The size in bytes of the cookie (a truncated HMAC).</summary>
    public const int CookieSize = 16;

    /// <summary>How long an issued cookie remains acceptable.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    /// <summary>Generates a fresh per-node Toll secret. Held in memory only; rotating it simply invalidates in-flight cookies.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretSize);

    /// <summary>
    /// Responder: issue a cookie bound to the initiator's <paramref name="clientAddress"/> and await the echo.
    /// Throws if the echo is missing, malformed, expired, or fails the HMAC — all before Noise state is created.
    /// </summary>
    public static async Task IssueAndVerifyAsync(
        IVessel vessel, ReadOnlyMemory<byte> secret, EndPoint? clientAddress, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var address = AddressBytes(clientAddress);
        var issuedAt = now.ToUnixTimeSeconds();

        var challenge = new CodexWriter();
        challenge.WriteByte(Version);
        challenge.WriteUInt64((ulong)issuedAt);
        challenge.WriteBytes(Compute(secret.Span, address, issuedAt));
        await vessel.SendAsync(TollStream, challenge.ToArray(), cancellationToken).ConfigureAwait(false);

        var echo = await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false);
        var r = new CodexReader(echo);
        if (r.ReadByte() != Version)
            throw new NoiseConjunctionException("Unsupported Toll version.");
        var echoedAt = (long)r.ReadUInt64();
        var echoedCookie = r.ReadBytes();

        if (Math.Abs(now.ToUnixTimeSeconds() - echoedAt) > (long)MaxAge.TotalSeconds)
            throw new NoiseConjunctionException("Toll cookie has expired.");
        var expected = Compute(secret.Span, address, echoedAt);
        if (!CryptographicOperations.FixedTimeEquals(echoedCookie, expected))
            throw new NoiseConjunctionException("Toll cookie failed verification.");
    }

    /// <summary>Initiator: read the responder's challenge and echo it back verbatim.</summary>
    public static async Task SolveAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var challenge = await ReceiveAsync(vessel, cancellationToken).ConfigureAwait(false);
        var r = new CodexReader(challenge);
        if (r.ReadByte() != Version)
            throw new NoiseConjunctionException("Unsupported Toll version.");
        var issuedAt = (long)r.ReadUInt64();
        var cookie = r.ReadBytes().ToArray();

        var echo = new CodexWriter();
        echo.WriteByte(Version);
        echo.WriteUInt64((ulong)issuedAt);
        echo.WriteBytes(cookie);
        await vessel.SendAsync(TollStream, echo.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static byte[] Compute(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> address, long issuedAt)
    {
        Span<byte> message = stackalloc byte[address.Length + 8];
        address.CopyTo(message);
        BinaryPrimitives.WriteInt64BigEndian(message[address.Length..], issuedAt);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(secret, message, mac);
        return mac[..CookieSize].ToArray();
    }

    private static byte[] AddressBytes(EndPoint? endpoint)
        => endpoint is IPEndPoint ip ? ip.Address.GetAddressBytes() : [];

    private static async Task<byte[]> ReceiveAsync(IVessel vessel, CancellationToken cancellationToken)
    {
        var frame = await vessel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new NoiseConjunctionException("Vessel closed during the Toll exchange.");
        if (frame.StreamId != TollStream)
            throw new NoiseConjunctionException($"Unexpected frame on stream {frame.StreamId} during the Toll exchange.");
        return frame.Payload;
    }
}
