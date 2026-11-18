using Avalonia;
using EMCL.Core.Values;
using EMCLDesktop.Services;
using EMCLDesktop.Services.PacketInspection;

namespace EMCLDesktop.ViewModels;

public sealed record InspectorFieldPreviewRow(string Text, int Depth, bool IsFormat = false)
{
    public Thickness Indent => new(Math.Min(Depth, 12) * 12, 0, 0, 0);

    internal static IReadOnlyList<InspectorFieldPreviewRow> Create(InspectionField? field)
    {
        if (field is null) return [];
        var rows = new List<InspectorFieldPreviewRow>();
        AppendField(rows, field, 0);
        return rows;
    }

    private static void AppendField(List<InspectorFieldPreviewRow> rows, InspectionField field, int depth)
    {
        if (field.DecodedValue is not { } value)
        {
            rows.Add(new(field.Label, depth));
            foreach (var child in field.Children) AppendField(rows, child, depth + 1);
            return;
        }

        var items = DlmsDisplay.CollectionItems(value);
        if (!items.IsDefault)
        {
            rows.Add(new($"{field.Name} · {DlmsDisplay.CollectionTitle(value.Type, items.Length)}", depth));
            foreach (var child in field.Children)
                if (child.DecodedValue is not null) AppendField(rows, child, depth + 1);
            return;
        }

        if (value.Type == DlmsType.OctetString)
        {
            rows.Add(new($"{field.Name} · OctetString ({value.Bytes.Length} байт)", depth));
            foreach (var format in OctetStringFormat.ReadOptions)
                if (DlmsDisplay.OctetPreview(value, format) is { } text)
                    rows.Add(new($"{format.Label}: {text}", depth + 1, IsFormat: true));
            if (DlmsDisplay.OctetPreview(value, OctetStringFormat.DlmsDate) is { } date)
                rows.Add(new($"DLMS date: {date}", depth + 1, IsFormat: true));
            return;
        }

        string summary = DlmsDisplay.IsNumeric(value.Type) ? field.Value.ReplaceLineEndings(" · ") : field.Value;
        rows.Add(new($"{field.Name} · {value.Type}: {summary}", depth));
    }
}
