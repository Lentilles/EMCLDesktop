using EMCL.Core.Values;

namespace EMCLDesktop.Services.PacketInspection;

public readonly record struct InspectionRange(int Offset, int Length)
{
    public bool Contains(int offset) => offset >= Offset && offset - Offset < Length;
}

public sealed record InspectionField(string Name, string Value, IReadOnlyList<InspectionRange> Ranges,
    IReadOnlyList<InspectionField> Children)
{
    public string Label => string.IsNullOrEmpty(Value) ? Name : $"{Name}: {Value}";
    public int ByteCount => Ranges.Sum(r => r.Length);
    public bool Contains(int offset) => Ranges.Any(r => r.Contains(offset));
    public static InspectionField At(string name, string value, int offset, int length,
        params InspectionField[] children) => new(name, value, length > 0 ? [new(offset, length)] : [], children);
}

public sealed record InspectionTarget(ushort ClassId, string Obis, sbyte Index, bool IsMethod)
{
    public string Description => $"{Obis} · класс {ClassId} · {(IsMethod ? "метод" : "атрибут")} {Index}";
}

public sealed record InspectedPacket(long Id, string Direction, string Timestamp, string Framing,
    byte[] Bytes, string Origin, string SourceText)
{
    public string Kind { get; init; } = "Неизвестные данные";
    public string Diagnostic { get; init; } = "";
    public bool HasError { get; init; }
    public bool IsIncomplete { get; init; }
    public IReadOnlyList<InspectionField> Fields { get; init; } = [];
    public InspectionTarget? Target { get; init; }
    public DlmsValue? DataValue { get; init; }
    public string ValueText { get; init; } = "";
    public IReadOnlyList<long> RelatedPacketIds { get; init; } = [];
    public string Summary => $"№ {Id} · {Timestamp} · {Direction} · {Kind}";
    public string Details => Target is { } target ? target.Description : Diagnostic;
}

public sealed record InspectionInput(byte[] Bytes, string Direction = "?", string Framing = "Auto",
    string Timestamp = "", string Origin = "", string SourceText = "", int SessionId = 0,
    bool ExplicitApdu = false);

/// <summary>Maps a decoded payload to the original bytes, including disjoint ranges across frames.</summary>
internal sealed class MappedPayload(byte[] bytes, int[] offsets)
{
    public byte[] Bytes { get; } = bytes;
    public int[] Offsets { get; } = offsets;
    public static MappedPayload Contiguous(byte[] bytes, int start, int length) =>
        new(bytes.AsSpan(start, length).ToArray(), Enumerable.Range(start, length).ToArray());
    public MappedPayload Slice(int start, int length) =>
        new(Bytes.AsSpan(start, length).ToArray(), Offsets.AsSpan(start, length).ToArray());

    public IReadOnlyList<InspectionRange> Ranges(int start, int length)
    {
        if (start < 0 || length < 0 || length > Offsets.Length - start)
            throw new InvalidDataException("Поле выходит за границы исходного пакета.");
        var ranges = new List<InspectionRange>();
        for (int i = start; i < start + length; i++)
        {
            int offset = Offsets[i];
            if (ranges.Count > 0 && ranges[^1].Offset + ranges[^1].Length == offset)
                ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + 1 };
            else ranges.Add(new(offset, 1));
        }
        return ranges;
    }

    public InspectionField Field(string name, string value, int start, int length,
        params InspectionField[] children) => new(name, value, Ranges(start, length), children);
}
