using EMCL.Core.Codecs;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Protocol.Axdr;

namespace EMCLDesktop.Services;

// Mirrors the duplicate handling used by EMCL.Examples for meters that repeat
// byte-identical object_list entries.
internal static class IdenticalObjectListDuplicateFilter
{
    public static (DlmsValue Value, int SkippedCount) Apply(DlmsValue value, ProtocolLimits limits)
    {
        if (value.Type != DlmsType.Array)
            return (value, 0);

        var codec = new AxdrCodec(limits);
        var seen = new Dictionary<LogicalName, byte[]>();
        var unique = new List<DlmsValue>();
        int skipped = 0;
        foreach (var entry in value.Items)
        {
            if (entry.Type != DlmsType.Structure || entry.Items.Length != 4 ||
                entry.Items[2].Type != DlmsType.OctetString ||
                entry.Items[2].Bytes.Length != LogicalName.EncodedLength)
            {
                unique.Add(entry);
                continue;
            }

            var name = LogicalName.FromBytes(entry.Items[2].Bytes.AsSpan());
            var encoded = codec.EncodeBytes(entry);
            if (seen.TryGetValue(name, out var earlier) && earlier.AsSpan().SequenceEqual(encoded))
            {
                ++skipped;
                continue;
            }

            seen.TryAdd(name, encoded);
            unique.Add(entry);
        }

        return (DlmsValue.Array([.. unique]), skipped);
    }
}
