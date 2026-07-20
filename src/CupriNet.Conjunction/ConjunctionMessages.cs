using CupriNet.Abstractions;
using CupriNet.Codex;

namespace CupriNet.Conjunction;

/// <summary>Handshake message types carried on the Vessel control stream.</summary>
internal enum ConjunctionMessageType : byte
{
    Hello = 1,
    Binding = 2,
}

/// <summary>The opening message each side sends: who I am and a fresh nonce.</summary>
internal sealed record Hello(byte Version, Concordium Network, byte[] SealPublicKey, byte[] Nonce)
{
    public byte[] Encode()
    {
        var w = new CodexWriter();
        w.WriteByte((byte)ConjunctionMessageType.Hello);
        w.WriteByte(Version);
        w.WriteString(Network.Value);
        w.WriteBytes(SealPublicKey);
        w.WriteBytes(Nonce);
        return w.ToArray();
    }

    public static Hello Decode(ReadOnlySpan<byte> payload)
    {
        var r = new CodexReader(payload);
        var type = (ConjunctionMessageType)r.ReadByte();
        if (type != ConjunctionMessageType.Hello)
            throw new CodexFormatException($"Expected Hello, got {type}.");
        var version = r.ReadByte();
        var network = new Concordium(r.ReadString());
        var sealKey = r.ReadBytes().ToArray();
        var nonce = r.ReadBytes().ToArray();
        return new Hello(version, network, sealKey, nonce);
    }
}

/// <summary>The proof message: a signature over the shared handshake transcript.</summary>
internal sealed record Binding(byte[] Signature)
{
    public byte[] Encode()
    {
        var w = new CodexWriter();
        w.WriteByte((byte)ConjunctionMessageType.Binding);
        w.WriteBytes(Signature);
        return w.ToArray();
    }

    public static Binding Decode(ReadOnlySpan<byte> payload)
    {
        var r = new CodexReader(payload);
        var type = (ConjunctionMessageType)r.ReadByte();
        if (type != ConjunctionMessageType.Binding)
            throw new CodexFormatException($"Expected Binding, got {type}.");
        return new Binding(r.ReadBytes().ToArray());
    }
}
