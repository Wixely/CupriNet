namespace CupriNet.Vessel;

/// <summary>
/// Implemented by a vessel whose transport caps a single message at a size it did not choose — a WebRTC DataChannel,
/// whose limit is negotiated per association, rather than a byte stream, whose frame size this library sets.
///
/// <para>It is a separate interface rather than a member on <see cref="IVessel"/> because most transports have no
/// such limit to report, and a property every implementation had to answer with "not applicable" would say less than
/// nothing. A caller that cares asks <c>vessel is IMessageSizeLimited</c>; one that does not is unaffected.</para>
/// </summary>
public interface IMessageSizeLimited
{
    /// <summary>The largest single message this transport will carry, or 0 when unknown or unbounded.</summary>
    int MaxMessageBytes { get; }
}
