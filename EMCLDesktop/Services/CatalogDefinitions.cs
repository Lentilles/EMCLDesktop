using EMCL.Core.Codecs;
using EMCL.Core.Descriptors;
using EMCL.Objects.Association;
using EMCL.Objects.Catalog;
using EMCL.Objects.Definitions;

namespace EMCLDesktop.Services;

internal static class CatalogDefinitions
{
    private static readonly ClassRegistry definitions = CreateRegistry();

    public static AssociationImporter CreateImporter(ProtocolLimits limits) => new(definitions, limits: limits);

    public static CosemClassDescriptor? Find(ushort classId, byte classVersion) =>
        definitions.Find(new ClassKey(classId, classVersion));

    private static ClassRegistry CreateRegistry()
    {
        var registry = new ClassRegistry();
        registry.AddProvider(StandardDefinitions.CreateProvider(CosemProfile.Spodes5));
        registry.AddProvider(StandardDefinitions.CreateProvider(CosemProfile.Spodes4));
        // Keep the COSEM definition for class versions shared with the SPODES profiles.
        registry.AddProvider(StandardDefinitions.CreateProvider());
        return registry;
    }
}
