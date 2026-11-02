using System.Globalization;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Objects.ProfileGeneric;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Services;

public static class ProfileGenericTableBuilder
{
    public static ProfileTableViewModel Build(DlmsValue captureObjects, DlmsValue buffer,
        IEnumerable<MeterObjectViewModel> objects)
    {
        if (captureObjects.Type != DlmsType.Array)
            throw new FormatException("capture_objects должен быть массивом.");

        var captures = captureObjects.Items.Select(CaptureObject.Parse).ToArray();
        var rows = buffer.Type switch
        {
            DlmsType.Array => buffer.Items,
            DlmsType.CompactArray => ((DlmsCompactArray)buffer.Value!).Rows,
            _ => throw new FormatException("buffer должен быть массивом или compact array.")
        };
        if (rows.Any(row => row.Type != DlmsType.Structure))
            throw new FormatException("Строки buffer должны быть структурами.");

        var knownObjects = objects.ToLookup(obj => (obj.ClassId, obj.LogicalName));
        int width = Math.Max(captures.Length, rows.Length == 0 ? 0 : rows.Max(row => row.Items.Length));
        var columns = new ProfileColumnViewModel[width];
        for (int index = 0; index < width; index++)
        {
            if (index >= captures.Length)
            {
                columns[index] = new("—", $"Колонка {index + 1}");
                continue;
            }

            var capture = captures[index];
            var source = knownObjects[(capture.ClassId, capture.LogicalName)].FirstOrDefault();
            string name = capture.AttributeIndex == 0
                ? "Все атрибуты"
                : source?.Attributes.FirstOrDefault(attribute => attribute.Index == capture.AttributeIndex)?.Name
                  ?? $"Атрибут {capture.AttributeIndex}";
            if (capture.DataIndex != 0)
                name += $" · элемент {capture.DataIndex}";
            columns[index] = new(capture.LogicalName.ToString(), name);
        }

        var tableRows = new ProfileRowViewModel[rows.Length];
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var fields = rows[rowIndex].Items;
            var cells = new ProfileCellViewModel[width];
            for (int columnIndex = 0; columnIndex < width; columnIndex++)
            {
                string text = columnIndex >= fields.Length
                    ? "—"
                    : DisplayCell(fields[columnIndex], columnIndex < captures.Length ? captures[columnIndex] : null);
                cells[columnIndex] = new(text);
            }
            tableRows[rowIndex] = new(rowIndex + 1, cells);
        }
        return new(columns, tableRows);
    }

    private static string DisplayCell(DlmsValue value, CaptureObject? capture)
    {
        if (value.Type == DlmsType.DateTime ||
            capture is { ClassId: CosemClassIds.Clock, AttributeIndex: 2 } &&
            value.Type == DlmsType.OctetString && value.Bytes.Length == 12)
        {
            try
            {
                return CosemDateTime.FromValue(value).ToDateTimeOffset()
                    .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                // Special date fields are retained in their original byte form below.
            }
        }
        return DlmsDisplay.Value(value);
    }
}
