using System.Text.Json;
using System.Text.Json.Serialization;
using EMCL.Core.Objects;
using EMCLDesktop.ViewModels;

namespace EMCLDesktop.Services;

internal static class SavedConfiguration
{
    public const string Format = "EMCLDesktop";
    public const string LegacyFormat = "EMCLDirector";
    public const int Version = 1;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static SavedCatalog CaptureCatalog(IEnumerable<MeterObjectViewModel> objects) =>
        new(objects.Select(obj => new SavedObject(obj.Obis, obj.ClassId, obj.ClassVersion,
            obj.ClassName,
            obj.Attributes.Select(attribute => new SavedAttribute(attribute.Index, attribute.Name,
                attribute.Type, attribute.HasDeclaredType, attribute.Access, attribute.CanRead,
                attribute.Value)).ToArray(),
            obj.Methods.Select(method => new SavedMethod(method.Index, method.Name,
                method.ParameterType, method.Access)).ToArray())).ToArray());

    public static MeterObjectViewModel[] RestoreCatalog(SavedCatalog catalog)
    {
        if (catalog.Objects is null)
            throw new FormatException("В сохранённом каталоге отсутствует список объектов.");
        var objects = new List<MeterObjectViewModel>(catalog.Objects.Length);
        var seen = new HashSet<LogicalName>();
        foreach (var saved in catalog.Objects)
        {
            if (saved is null || saved.Obis is null || saved.Attributes is null || saved.Methods is null)
                throw new FormatException("В сохранённом каталоге есть неполный объект.");
            var logicalName = LogicalName.Parse(saved.Obis);
            if (!seen.Add(logicalName))
                throw new FormatException($"Повторяющийся OBIS в каталоге: {saved.Obis}.");
            var definition = saved.ClassName is null or MeterObjectViewModel.UnknownClassName
                ? CatalogDefinitions.Find(saved.ClassId, saved.ClassVersion)
                : null;
            var obj = new MeterObjectViewModel(logicalName, saved.ClassId,
                saved.ClassVersion, definition?.Name ?? saved.ClassName);
            foreach (var item in saved.Attributes)
            {
                if (item is null)
                    throw new FormatException($"У {saved.Obis} есть пустое описание атрибута.");
                var member = definition?.Attributes.FirstOrDefault(attribute => attribute.Index == item.Index);
                string? name = item.Name is null or AttributeViewModel.UnknownName ? member?.Name ?? item.Name : item.Name;
                string? declaredType = item.HasDeclaredType ? item.Type : null;
                if (item.Type is null or AttributeViewModel.UnspecifiedType)
                    declaredType = member?.Type?.ToString() ?? declaredType;
                string? displayedType = declaredType ?? item.Type;
                var attribute = new AttributeViewModel(item.Index, name,
                    declaredType, item.Access ?? "Не указан", item.CanRead)
                {
                    Type = displayedType ?? AttributeViewModel.UnspecifiedType,
                    WriteType = DlmsTypeText.Parse(displayedType),
                    Value = item.Value ?? "Не считано"
                };
                obj.Attributes.Add(attribute);
            }
            foreach (var item in saved.Methods)
            {
                if (item is null)
                    throw new FormatException($"У {saved.Obis} есть пустое описание метода.");
                var member = definition?.Methods.FirstOrDefault(method => method.Index == item.Index);
                string? name = item.Name is null or MethodViewModel.UnknownName ? member?.Name ?? item.Name : item.Name;
                string? parameterType = item.ParameterType is null or MethodViewModel.UnspecifiedParameterType
                    ? member?.ParameterType?.ToString() ?? item.ParameterType
                    : item.ParameterType;
                obj.Methods.Add(new MethodViewModel(item.Index, name, parameterType, item.Access ?? "Не указан"));
            }
            objects.Add(obj);
        }
        return objects.ToArray();
    }
}

internal sealed record SavedCatalog(SavedObject[] Objects);
internal sealed record SavedObject(string Obis, ushort ClassId, byte ClassVersion, string? ClassName,
    SavedAttribute[] Attributes, SavedMethod[] Methods);
internal sealed record SavedAttribute(sbyte Index, string? Name, string? Type, bool HasDeclaredType,
    string? Access, bool CanRead, string? Value);
internal sealed record SavedMethod(sbyte Index, string? Name, string? ParameterType, string? Access);
