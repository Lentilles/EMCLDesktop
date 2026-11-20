using System.Collections.Immutable;
using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;

namespace EMCLDesktop.Services;

// Mirrors the duplicate handling used by EMCL.Examples for meters that repeat
// byte-identical object_list entries.
internal static class IdenticalObjectListDuplicateFilter
{
    public static (ImmutableArray<DlmsValue> Entries, int SkippedCount) Apply(
        ImmutableArray<DlmsValue> entries, ProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        var codec = new AxdrCodec(limits);
        var firstByName = new Dictionary<LogicalName, DlmsValue>();
        var filtered = ImmutableArray.CreateBuilder<DlmsValue>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetLogicalName(entry, out var name))
            {
                if (firstByName.TryGetValue(name, out var first))
                {
                    if (codec.EncodeBytes(first).AsSpan().SequenceEqual(codec.EncodeBytes(entry)))
                        continue;
                }
                else
                {
                    firstByName.Add(name, entry);
                }
            }
            filtered.Add(entry);
        }

        int skipped = entries.Length - filtered.Count;
        return (skipped == 0 ? entries : filtered.ToImmutable(), skipped);
    }

    private static bool TryGetLogicalName(DlmsValue entry, out LogicalName name)
    {
        const int logicalNameField = 2;
        if (entry.Type == DlmsType.Structure && entry.Items.Length > logicalNameField &&
            entry.Items[logicalNameField] is { Type: DlmsType.OctetString } logicalName &&
            logicalName.Bytes.Length == LogicalName.EncodedLength)
        {
            name = LogicalName.FromBytes(logicalName.Bytes.AsSpan());
            return true;
        }

        name = default;
        return false;
    }
}
