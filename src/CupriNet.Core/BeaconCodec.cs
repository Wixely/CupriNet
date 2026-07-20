using CupriNet.Codex;

namespace CupriNet.Core;

/// <summary>Shared canonical encoding for a bounded list of <see cref="Beacon"/> endpoint candidates.</summary>
public static class BeaconCodec
{
    public static void Write(CodexWriter writer, IReadOnlyList<Beacon> beacons, int max)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(beacons);
        if (beacons.Count > max)
            throw new CodexFormatException($"Beacon list has {beacons.Count} entries, exceeding the maximum of {max}.");

        writer.WriteVarUInt((ulong)beacons.Count);
        foreach (var beacon in beacons)
        {
            writer.WriteByte((byte)beacon.Kind);
            writer.WriteString(beacon.Host);
            writer.WriteVarUInt((ulong)beacon.Port);
        }
    }

    public static IReadOnlyList<Beacon> Read(ref CodexReader reader, int max)
    {
        var count = reader.ReadVarUInt();
        if (count > (ulong)max)
            throw new CodexFormatException($"Beacon list has {count} entries, exceeding the maximum of {max}.");

        var beacons = new List<Beacon>((int)count);
        for (var i = 0UL; i < count; i++)
        {
            var kind = (EndpointKind)reader.ReadByte();
            var host = reader.ReadString();
            var port = (int)reader.ReadVarUInt();
            beacons.Add(new Beacon(kind, host, port));
        }

        return beacons;
    }
}
