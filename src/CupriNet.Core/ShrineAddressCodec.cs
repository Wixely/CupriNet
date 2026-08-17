using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Marks;

namespace CupriNet.Core;

/// <summary>
/// Canonical serialization for the optional Shrine-address block embedded in an <see cref="Intonation"/> body: the
/// <b>Signet</b> (32-byte mark) of an L2 site the node hosts, so discovering a node also discovers what it serves.
///
/// <para><b>Opt-in by design.</b> Publishing it inside the node's signed link ties the Shrine to that node's overlay
/// Sigil, defeating the identity separation a Signet otherwise gives (see <c>design/shrines.md</c>). It is right for
/// a public / branded host ("this node serves this site"); for anonymous hosting, share the address out-of-band and
/// leave this field unset.</para>
///
/// <para>It carries <b>no secret</b>: the address is public by nature, and authenticity comes from the Pilgrimage
/// pinning the Signet, not from this block. The signature over the Intonation body only proves the node meant to
/// advertise it.</para>
/// </summary>
public static class ShrineAddressCodec
{
    /// <summary>
    /// The sentinel written in the <see cref="WebRtcEndpoint"/> slot when that block is absent but a Shrine address
    /// follows it. Block versions start at 1, so 0 can never be a real WebRTC block and is unambiguous.
    /// </summary>
    public const byte AbsentBlock = 0;

    public static void Write(CodexWriter writer, Sigil shrine)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.ShrineAddress).Max));
        writer.WriteBytes(shrine.Span);
    }

    public static Sigil Read(ref CodexReader reader)
    {
        var version = reader.ReadByte();
        if (!CupriMarks.Accepts(CupriMarks.ShrineAddress, version))
            throw new CodexFormatException($"Unsupported ShrineAddress version {version}.");

        var mark = reader.ReadBytes();
        if (mark.Length != Sigil.Size)
            throw new CodexFormatException("Shrine address has an invalid Signet length.");
        return new Sigil(mark);
    }
}
