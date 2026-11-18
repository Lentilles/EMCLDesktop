namespace EMCLDesktop.Services.PacketInspection;

public readonly record struct PacketAssemblyLine(int Lane, bool Above, bool Below, bool Branch);

/// <summary>Places overlapping assemblies on separate lines in the packet list.</summary>
public sealed class PacketAssemblyLayout
{
    private sealed record Assembly(long ResultId, long[] SourceIds)
    {
        public long StartId => SourceIds[0];
    }

    private readonly List<List<Assembly>> lanes = [];
    private long oldestLastSourceId = long.MaxValue;

    internal static PacketAssemblyLayout Empty { get; } = new();
    public int LaneCount => lanes.Count;
    internal bool HasExpiredSources(long firstPacketId) => oldestLastSourceId < firstPacketId;

    internal static PacketAssemblyLayout Create(IReadOnlyList<InspectedPacket> packets)
    {
        if (packets.Count == 0) return Empty;

        long firstPacketId = packets[0].Id;
        var assemblies = packets.Where(packet => packet.AssemblySourcePacketIds.Count > 0)
            .Select(packet => new Assembly(packet.Id, packet.AssemblySourcePacketIds.Order().ToArray()))
            .Where(assembly => assembly.SourceIds[^1] >= firstPacketId)
            .OrderBy(assembly => assembly.StartId);
        var layout = new PacketAssemblyLayout();
        foreach (var assembly in assemblies)
        {
            var lane = layout.lanes.FirstOrDefault(candidate => candidate[^1].ResultId < assembly.StartId);
            if (lane is null)
            {
                lane = [];
                layout.lanes.Add(lane);
            }
            lane.Add(assembly);
            layout.oldestLastSourceId = Math.Min(layout.oldestLastSourceId, assembly.SourceIds[^1]);
        }
        return layout.LaneCount > 0 ? layout : Empty;
    }

    public IEnumerable<PacketAssemblyLine> LinesAt(long packetId)
    {
        for (int laneIndex = 0; laneIndex < lanes.Count; laneIndex++)
        {
            var lane = lanes[laneIndex];
            int start = 0, end = lane.Count - 1;
            while (start <= end)
            {
                int middle = start + (end - start) / 2;
                var assembly = lane[middle];
                if (packetId < assembly.StartId) end = middle - 1;
                else if (packetId > assembly.ResultId) start = middle + 1;
                else
                {
                    bool isResult = packetId == assembly.ResultId;
                    yield return new(laneIndex, packetId > assembly.StartId, !isResult,
                        isResult || Array.BinarySearch(assembly.SourceIds, packetId) >= 0);
                    break;
                }
            }
        }
    }
}
