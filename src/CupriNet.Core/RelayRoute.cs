using CupriNet.Abstractions;
using CupriNet.Codex;
using CupriNet.Marks;

namespace CupriNet.Core;

/// <summary>
/// A way to reach a node that cannot be dialled directly: ask the Ferryman relay at <paramref name="Host"/>:<paramref name="Port"/>
/// — whose identity is <paramref name="RelaySigil"/> — to broker a hole punch to whoever holds <paramref name="Handle"/>.
///
/// <para>The handle is the hash of a rendezvous key the node made for this relay alone. It says nothing about the
/// node's Sigil and cannot be worked out from it, so it has to travel with the link — which the node signs, so a
/// holder of the link knows the handle is the node's. Carrying the relay's Sigil lets the requester decide whether
/// it trusts the relay <em>before</em> it connects, and pin it when it does.</para>
/// </summary>
public sealed record RelayRoute(Sigil RelaySigil, string Host, int Port, byte[] Handle)
{
    /// <summary>The size in bytes of a rendezvous handle.</summary>
    public const int HandleSize = 16;

    public bool Equals(RelayRoute? other)
        => other is not null && RelaySigil == other.RelaySigil && Host == other.Host && Port == other.Port
           && Handle.AsSpan().SequenceEqual(other.Handle);

    public override int GetHashCode() => HashCode.Combine(RelaySigil, Host, Port);
}

/// <summary>
/// The relay-route block of an Intonation: a version byte and a short list of <see cref="RelayRoute"/>s. It goes
/// only in a link — never in a gossiped overlay record or a channel advertisement, where publishing a handle beside
/// the Sigil it belongs to would undo the point of having one.
/// </summary>
public static class RelayRouteCodec
{
    /// <summary>The most routes one link carries: a node reserves with a few relays for diversity, not many.</summary>
    public const int MaxRoutes = 4;

    /// <summary>The longest relay host name accepted (a DNS name is at most 253 characters).</summary>
    public const int MaxHostBytes = 255;

    public static void Write(CodexWriter writer, IReadOnlyList<RelayRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count is 0 or > MaxRoutes)
            throw new CodexFormatException($"A link carries between 1 and {MaxRoutes} relay routes.");

        writer.WriteByte(checked((byte)CupriMarks.Supported(CupriMarks.RelayRoute).Max));
        writer.WriteVarUInt((ulong)routes.Count);
        foreach (var route in routes)
        {
            if (route.Handle.Length != RelayRoute.HandleSize)
                throw new CodexFormatException("Relay route has an invalid handle length.");
            if (route.Port is < 1 or > 65535)
                throw new CodexFormatException("Relay route has an invalid port.");
            writer.WriteBytes(route.RelaySigil.Span);
            writer.WriteString(route.Host);
            writer.WriteVarUInt((ulong)route.Port);
            writer.WriteBytes(route.Handle);
        }
    }

    public static IReadOnlyList<RelayRoute> Read(ref CodexReader reader)
    {
        var version = reader.ReadByte();
        if (!CupriMarks.Accepts(CupriMarks.RelayRoute, version))
            throw new CodexFormatException($"Unsupported RelayRoute version {version}.");

        var count = reader.ReadVarUInt();
        if (count is 0 or > MaxRoutes)
            throw new CodexFormatException("Relay-route block has an invalid route count.");

        var routes = new List<RelayRoute>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            var sigil = reader.ReadBytes();
            if (sigil.Length != Sigil.Size)
                throw new CodexFormatException("Relay route has an invalid relay Sigil length.");
            var relay = new Sigil(sigil);

            var hostBytes = reader.ReadBytes();
            if (hostBytes.Length is 0 or > MaxHostBytes)
                throw new CodexFormatException("Relay route has an invalid host length.");
            string host;
            try { host = new System.Text.UTF8Encoding(false, true).GetString(hostBytes); }
            catch (System.Text.DecoderFallbackException) { throw new CodexFormatException("Relay route host is not well-formed UTF-8."); }

            var port = reader.ReadVarUInt();
            if (port is 0 or > 65535)
                throw new CodexFormatException("Relay route has an invalid port.");

            var handle = reader.ReadBytes();
            if (handle.Length != RelayRoute.HandleSize)
                throw new CodexFormatException("Relay route has an invalid handle length.");

            routes.Add(new RelayRoute(relay, host, (int)port, handle.ToArray()));
        }
        return routes;
    }
}
