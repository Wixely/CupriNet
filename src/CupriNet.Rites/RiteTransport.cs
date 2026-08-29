using CupriNet.Vessel;

namespace CupriNet.Rites;

/// <summary>
/// What the site rites need of a transport, and the check that says so at connect time rather than mid-session.
/// </summary>
public static class RiteTransport
{
    /// <summary>
    /// The largest single message a site rite can put on the wire: the biggest padded frame
    /// (<see cref="AuspiceCodec.MaxPaddedBytes"/>) plus room for the vessel header and Noise's ciphertext expansion.
    /// A transport that cannot carry this cannot carry the rites.
    /// </summary>
    public const int RequiredMessageBytes = AuspiceCodec.MaxPaddedBytes + 8 * 1024;

    /// <summary>
    /// Refuses a transport whose negotiated message size is too small for the rites, naming both numbers.
    ///
    /// <para><b>Why this check exists.</b> A rite's ceiling is a constant of this library; what a DataChannel will
    /// carry is agreed with the peer at runtime, and the interoperable floor (64 KiB) is well below what the rites
    /// use. Nothing between the two reconciles them: <see cref="DataChannelVessel"/> emits one message per frame and
    /// never fragments. So on a pairing that negotiated low, a payload the rite calls legal is refused by the
    /// transport — and <c>MaxPayloadBytes</c>, which callers are told to trust, is wrong on exactly the path where
    /// they were told to trust it.</para>
    ///
    /// <para>Failing here turns that into a connect-time error naming the shortfall, which is the same bargain every
    /// other ceiling in the stack makes: fail at the rite, with a reason, rather than deep inside a transport.
    /// Transports that report 0 — unknown or unbounded — and every byte-stream vessel are unaffected.</para>
    /// </summary>
    public static void EnsureCarriesRiteFrames(IVessel vessel)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        if (vessel is not IMessageSizeLimited limited)
            return;

        var negotiated = limited.MaxMessageBytes;
        if (negotiated <= 0 || negotiated >= RequiredMessageBytes)
            return;

        throw new RiteTransportException(
            $"This transport negotiated a maximum message of {negotiated} bytes, and the site rites need " +
            $"{RequiredMessageBytes}. Nothing below a rite fragments, so a legal frame would be refused on the wire. " +
            "Raise the peer's max-message-size, or serve this visitor over a transport that does not cap messages.");
    }
}

/// <summary>Thrown when a transport cannot carry the site rites' frames.</summary>
public sealed class RiteTransportException(string message) : Exception(message);
