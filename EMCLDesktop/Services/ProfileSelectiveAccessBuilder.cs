using System.Globalization;
using EMCL.Core.Operations;
using EMCL.Core.Values;
using EMCL.Objects.ProfileGeneric;

namespace EMCLDesktop.Services;

public static class ProfileSelectiveAccessBuilder
{
    public static SelectiveAccess Entries(uint first, uint last)
    {
        if (first == 0 || last < first)
            throw new ArgumentException("Номера записей должны начинаться с 1; последний номер не меньше первого.");

        return new(ProfileSelectors.Entry, DlmsValue.Structure(
            DlmsValue.Number(DlmsType.UInt32, first),
            DlmsValue.Number(DlmsType.UInt32, last),
            DlmsValue.Number(DlmsType.UInt16, 1),
            DlmsValue.Number(DlmsType.UInt16, 0)));
    }

    public static SelectiveAccess Dates(CaptureObject capture, DlmsType wireType,
        DateTimeOffset? fromDate, DateTimeOffset? toDate, string offsetText)
    {
        if (fromDate is null || toDate is null)
            throw new ArgumentException("Укажите обе даты диапазона.");
        if (fromDate.Value.Date > toDate.Value.Date)
            throw new ArgumentException("Начальная дата должна быть не позже конечной.");
        if (wireType is not (DlmsType.DateTime or DlmsType.OctetString))
            throw new ArgumentException("Для диапазона нужен столбец с типом DateTime или OctetString.");

        var offset = ParseOffset(offsetText);
        var from = new DateTimeOffset(fromDate.Value.Year, fromDate.Value.Month, fromDate.Value.Day,
            0, 0, 0, offset);
        var to = new DateTimeOffset(toDate.Value.Year, toDate.Value.Month, toDate.Value.Day,
            23, 59, 59, offset).AddMilliseconds(990);

        DlmsValue DateValue(DateTimeOffset date)
        {
            var time = CosemDateTime.FromDateTimeOffset(date);
            return wireType == DlmsType.DateTime ? time.ToValue() : DlmsValue.Octets(time.Octets.AsSpan());
        }

        var restricting = DlmsValue.Structure(
            DlmsValue.Number(DlmsType.UInt16, capture.ClassId),
            DlmsValue.Octets(capture.LogicalName.ToBytes()),
            DlmsValue.Number(DlmsType.Int8, capture.AttributeIndex),
            DlmsValue.Number(DlmsType.UInt16, capture.DataIndex));
        return new(ProfileSelectors.Range, DlmsValue.Structure(
            restricting, DateValue(from), DateValue(to), DlmsValue.Array()));
    }

    private static TimeSpan ParseOffset(string text)
    {
        var value = text.Trim();
        if (value.Length != 6 || value[0] is not ('+' or '-') || value[3] != ':' ||
            !int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(value.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            hours > 14 || minutes > 59 || hours == 14 && minutes != 0)
            throw new ArgumentException("Часовой пояс укажите в формате +03:00 (от −14:00 до +14:00).");

        var offset = new TimeSpan(hours, minutes, 0);
        return value[0] == '-' ? -offset : offset;
    }
}
