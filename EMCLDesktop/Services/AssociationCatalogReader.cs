using System.Collections.Immutable;
using System.Diagnostics;
using EMCL.Core.Access;
using EMCL.Core.Codecs;
using EMCL.Core.Descriptors;
using EMCL.Core.Objects;
using EMCL.Core.Values;
using EMCL.Objects.Association;
using EMCL.Objects.Catalog;
using EMCL.Objects.Definitions;

namespace EMCLDesktop.Services;

internal static class AssociationCatalogReader
{
    private const int AccessRightsField = 3;
    private const int ObjectEntryFieldCount = 4;
    private static readonly DlmsValue EmptyList = DlmsValue.Array();
    private static readonly DlmsValue EmptyRights = DlmsValue.Structure(EmptyList, EmptyList);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(50);

    private enum AccessList { Attributes, Methods }

    private readonly record struct ParsedObject(CosemClassDescriptor Descriptor, LogicalName Name,
        ObjectAccessRights Rights, string? Error = null);

    public static (ImportedAssociation Association, int Skipped, ImmutableDictionary<LogicalName, string> Errors) Import(
        DlmsValue list, byte version, bool ignoreDuplicates, ProtocolLimits limits,
        IProgress<ObjectListProgress>? progress, CancellationToken cancellationToken)
    {
        if (version is not (AssociationLnMembers.Version1 or AssociationLnMembers.Version3))
            throw new NotSupportedException("Association LN versions 1 and 3 are supported.");
        if (list.Type != DlmsType.Array)
            throw new AssociationImportException($"object_list: ожидался массив, получено {list.Type}.");

        var (entries, skipped) = ignoreDuplicates
            ? IdenticalObjectListDuplicateFilter.Apply(list.Items, limits, cancellationToken)
            : (list.Items, 0);
        if (entries.Length > limits.MaxCollectionItems)
            throw new AssociationImportException("Invalid object_list: Too many objects.");

        var importer = CatalogDefinitions.CreateImporter(limits);
        var catalog = new ObjectCatalog("meter");
        var rights = ImmutableDictionary.CreateBuilder<LogicalName, ObjectAccessRights>();
        var errors = ImmutableDictionary.CreateBuilder<LogicalName, string>();
        progress?.Report(new ObjectListProgress(0, entries.Length));
        var lastReport = Stopwatch.GetTimestamp();
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = ParseObject(entries[index], importer, version);
            try
            {
                catalog.Add(new CosemObject(parsed.Descriptor, parsed.Name));
            }
            catch (ArgumentException error)
            {
                throw new AssociationImportException($"Invalid object_list: {error.Message}", error);
            }
            rights.Add(parsed.Name, parsed.Rights);
            if (parsed.Error is { } diagnostic)
                errors.Add(parsed.Name, $"Ошибка в object_list. {diagnostic}");

            int current = index + 1;
            if (progress is not null &&
                (current == entries.Length || Stopwatch.GetElapsedTime(lastReport) >= ProgressInterval))
            {
                progress.Report(new ObjectListProgress(current, entries.Length));
                lastReport = Stopwatch.GetTimestamp();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var association = new ImportedAssociation(catalog, new("current", version, rights.ToImmutable()));
        return (association, skipped, errors.ToImmutable());
    }

    private static ParsedObject ParseObject(DlmsValue entry, AssociationImporter importer, byte version)
    {
        try
        {
            var (descriptor, name, rights) = importer.ParseEntry(entry, version);
            return new(descriptor, name, rights);
        }
        catch (FormatException originalError) when (entry.Type == DlmsType.Structure && entry.Items.Length >= AccessRightsField)
        {
            var (recoveredRights, diagnostic) = RecoverAccessRights(entry, importer, version);
            // The importer still validates the identity and resolves the descriptor from the recovered rights.
            var (descriptor, name, rights) = importer.ParseEntry(WithRights(entry, recoveredRights), version);
            return new(descriptor, name, rights, diagnostic ?? originalError.Message);
        }
    }

    private static (DlmsValue Rights, string? Error) RecoverAccessRights(DlmsValue entry,
        AssociationImporter importer, byte version)
    {
        if (entry.Items.Length != ObjectEntryFieldCount)
            return (EmptyRights, $"Запись объекта: {StructureError(entry, ObjectEntryFieldCount)}");

        var rights = entry.Items[AccessRightsField];
        if (StructureError(rights, 2) is { } structureError)
            return (EmptyRights, $"access_rights: {structureError}");

        var (attributes, attributeError) = RecoverAccessList(entry, rights.Items[0], AccessList.Attributes, importer, version);
        var (methods, methodError) = RecoverAccessList(entry, rights.Items[1], AccessList.Methods, importer, version);
        string? error = (attributeError, methodError) switch
        {
            (null, _) => methodError,
            (_, null) => attributeError,
            _ => $"{attributeError}\n{methodError}"
        };
        return (DlmsValue.Structure(attributes, methods), error);
    }

    private static (DlmsValue Value, string? Error) RecoverAccessList(DlmsValue entry, DlmsValue list,
        AccessList kind, AssociationImporter importer, byte version)
    {
        var isolatedRights = kind == AccessList.Methods
            ? DlmsValue.Structure(EmptyList, list)
            : DlmsValue.Structure(list, EmptyList);
        try
        {
            importer.ParseEntry(WithRights(entry, isolatedRights), version);
            return (list, null);
        }
        catch (FormatException error)
        {
            return (EmptyList, DescribeAccessListError(list, kind, error.Message));
        }
    }

    private static string DescribeAccessListError(DlmsValue list, AccessList kind, string message)
    {
        string path = kind == AccessList.Methods ? "method_access" : "attribute_access";
        if (list.Type != DlmsType.Array)
            return $"{path}: ожидался массив, получено {list.Type}.";

        int expectedFields = kind == AccessList.Methods ? 2 : 3;
        for (int index = 0; index < list.Items.Length; index++)
        {
            if (StructureError(list.Items[index], expectedFields) is { } error)
                return $"{path}, запись {index + 1}: {error}";
        }
        return $"{path}: {message}";
    }

    private static string? StructureError(DlmsValue value, int expectedFields)
    {
        if (value.Type != DlmsType.Structure)
            return $"ожидалась структура, получено {value.Type}.";
        return value.Items.Length == expectedFields ? null :
            $"ожидалось полей: {expectedFields}, получено: {value.Items.Length}.";
    }

    private static DlmsValue WithRights(DlmsValue entry, DlmsValue rights) =>
        DlmsValue.Structure(entry.Items[0], entry.Items[1], entry.Items[2], rights);
}
