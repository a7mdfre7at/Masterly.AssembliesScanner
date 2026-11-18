using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Masterly.AssembliesScanner.Internal;

/// <summary>Reads assembly metadata with System.Reflection.Metadata, so nothing is loaded into the runtime.</summary>
internal static class MetadataInspector
{
    public static InspectionResult InspectFile(string path)
    {
        try
        {
            using FileStream stream = OpenRead(path);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata)
                return new InspectionResult(InspectionStatus.NotManaged, null, "The file is a native image without .NET metadata.");

            MetadataReader reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return new InspectionResult(InspectionStatus.NotAnAssembly, null, "The file has .NET metadata but no assembly manifest.");

            return new InspectionResult(InspectionStatus.Ok, ReadSnapshot(reader, () => ReadTypesFromFile(path)));
        }
        catch (BadImageFormatException ex)
        {
            return new InspectionResult(InspectionStatus.NotManaged, null, "The file is not a valid .NET assembly: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InspectionResult(InspectionStatus.Unreadable, null, "The file could not be read: " + ex.Message, ex);
        }
    }

    public static unsafe InspectionResult InspectLoaded(Assembly assembly)
    {
        if (assembly.IsDynamic)
            return new InspectionResult(InspectionStatus.NotManaged, null, "Dynamic assemblies have no readable metadata.");

        if (!assembly.TryGetRawMetadata(out byte* blob, out int length))
        {
            return string.IsNullOrEmpty(assembly.Location)
                ? new InspectionResult(InspectionStatus.Unreadable, null, "The runtime does not expose metadata for this assembly.")
                : InspectFile(assembly.Location);
        }

        try
        {
            var reader = new MetadataReader(blob, length);
            AssemblySnapshot snapshot = ReadSnapshot(reader, () => ReadTypesFromLoaded(assembly));
            GC.KeepAlive(assembly);
            return new InspectionResult(InspectionStatus.Ok, snapshot);
        }
        catch (BadImageFormatException ex)
        {
            return new InspectionResult(InspectionStatus.NotManaged, null, "The assembly metadata is not valid: " + ex.Message, ex);
        }
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static IReadOnlyList<TypeDescriptor> ReadTypesFromFile(string path)
    {
        using FileStream stream = OpenRead(path);
        using var pe = new PEReader(stream);
        return ReadTypes(pe.GetMetadataReader());
    }

    private static unsafe IReadOnlyList<TypeDescriptor> ReadTypesFromLoaded(Assembly assembly)
    {
        if (!assembly.TryGetRawMetadata(out byte* blob, out int length))
            return Array.Empty<TypeDescriptor>();

        IReadOnlyList<TypeDescriptor> types = ReadTypes(new MetadataReader(blob, length));
        GC.KeepAlive(assembly);
        return types;
    }

    private static AssemblySnapshot ReadSnapshot(MetadataReader reader, Func<IReadOnlyList<TypeDescriptor>> typesFactory)
    {
        AssemblyDefinition definition = reader.GetAssemblyDefinition();
        string name = reader.GetString(definition.Name);
        string culture = reader.GetString(definition.Culture);
        Version version = definition.Version;
        string? token = ComputePublicKeyToken(reader.GetBlobBytes(definition.PublicKey));

        List<AssemblyReferenceInfo> references = [.. reader.AssemblyReferences
            .Select(reader.GetAssemblyReference)
            .Select(reference => new AssemblyReferenceInfo(
                reader.GetString(reference.Name),
                reference.Version,
                reader.GetString(reference.Culture),
                GetPublicKeyToken(reader, reference)))];

        var attributeNames = new List<string>();
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? targetFramework = null, informationalVersion = null, fileVersion = null, company = null;
        string? product = null, title = null, description = null, configuration = null;
        bool isReferenceAssembly = false;

        IEnumerable<(CustomAttribute Attribute, string? Name)> attributes = definition.GetCustomAttributes()
            .Select(reader.GetCustomAttribute)
            .Select(attribute => (attribute, GetAttributeTypeName(reader, attribute)));

        foreach ((CustomAttribute attribute, string? attributeName) in attributes.Where(a => a.Name is not null))
        {
            attributeNames.Add(attributeName!);
            switch (attributeName)
            {
                case "System.Runtime.Versioning.TargetFrameworkAttribute":
                    targetFramework = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyInformationalVersionAttribute":
                    informationalVersion = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyFileVersionAttribute":
                    fileVersion = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyCompanyAttribute":
                    company = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyProductAttribute":
                    product = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyTitleAttribute":
                    title = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyDescriptionAttribute":
                    description = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyConfigurationAttribute":
                    configuration = ReadStrings(reader, attribute, 1)?[0];
                    break;
                case "System.Reflection.AssemblyMetadataAttribute":
                    string?[]? pair = ReadStrings(reader, attribute, 2);
                    if (pair?[0] is string key)
                        metadata[key] = pair[1];
                    break;
                case "System.Runtime.CompilerServices.ReferenceAssemblyAttribute":
                    isReferenceAssembly = true;
                    break;
            }
        }

        var resources = new List<string>(reader.ManifestResources.Count);
        foreach (ManifestResourceHandle handle in reader.ManifestResources)
            resources.Add(reader.GetString(reader.GetManifestResource(handle).Name));

        return new AssemblySnapshot
        {
            Name = name,
            Version = version,
            Culture = culture,
            PublicKeyToken = token,
            FullName = $"{name}, Version={version}, Culture={(culture.Length == 0 ? "neutral" : culture)}, PublicKeyToken={token ?? "null"}",
            Mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid),
            TargetFramework = targetFramework,
            InformationalVersion = informationalVersion,
            FileVersion = fileVersion,
            Company = company,
            Product = product,
            Title = title,
            Description = description,
            Configuration = configuration,
            IsReferenceAssembly = isReferenceAssembly,
            References = references,
            AttributeNames = attributeNames,
            Metadata = metadata,
            ResourceNames = resources,
            // PublicationOnly: a failed read (file replaced or removed) is retried instead of cached.
            Types = new Lazy<IReadOnlyList<TypeDescriptor>>(typesFactory, LazyThreadSafetyMode.PublicationOnly)
        };
    }

    private static IReadOnlyList<TypeDescriptor> ReadTypes(MetadataReader reader)
    {
        var types = new List<TypeDescriptor>(reader.TypeDefinitions.Count);
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string name = reader.GetString(definition.Name);
            if (name == "<Module>")
                continue;

            TypeAttributes attributes = definition.Attributes;
            TypeAttributes visibility = attributes & TypeAttributes.VisibilityMask;

            var interfaces = new List<string>();
            foreach (InterfaceImplementationHandle interfaceHandle in definition.GetInterfaceImplementations())
            {
                string? interfaceName = GetTypeName(reader, reader.GetInterfaceImplementation(interfaceHandle).Interface);
                if (interfaceName is not null)
                    interfaces.Add(interfaceName);
            }

            var typeAttributes = new List<string>();
            foreach (CustomAttributeHandle attributeHandle in definition.GetCustomAttributes())
            {
                string? attributeName = GetAttributeTypeName(reader, reader.GetCustomAttribute(attributeHandle));
                if (attributeName is not null)
                    typeAttributes.Add(attributeName);
            }

            TypeDefinitionHandle outermost = handle;
            while (!reader.GetTypeDefinition(outermost).GetDeclaringType().IsNil)
                outermost = reader.GetTypeDefinition(outermost).GetDeclaringType();

            types.Add(new TypeDescriptor(
                fullName: GetTypeName(reader, handle)!,
                @namespace: reader.GetString(reader.GetTypeDefinition(outermost).Namespace),
                name: name,
                isPublic: visibility is TypeAttributes.Public or TypeAttributes.NestedPublic,
                isNested: !definition.GetDeclaringType().IsNil,
                isInterface: (attributes & TypeAttributes.Interface) != 0,
                isAbstract: (attributes & TypeAttributes.Abstract) != 0,
                isSealed: (attributes & TypeAttributes.Sealed) != 0,
                genericParameterCount: definition.GetGenericParameters().Count,
                baseType: definition.BaseType.IsNil ? null : GetTypeName(reader, definition.BaseType),
                interfaces: interfaces,
                attributes: typeAttributes));
        }

        return types;
    }

    /// <summary>The public key token of a reference, whether it records the full key or only the token.</summary>
    internal static string? GetPublicKeyToken(MetadataReader reader, AssemblyReference reference)
    {
        byte[] keyOrToken = reader.GetBlobBytes(reference.PublicKeyOrToken);
        return (reference.Flags & AssemblyFlags.PublicKey) != 0
            ? ComputePublicKeyToken(keyOrToken)
            : keyOrToken.Length == 0 ? null : Convert.ToHexString(keyOrToken).ToLowerInvariant();
    }

    internal static string? GetAttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle constructor = attribute.Constructor;
        return constructor.Kind switch
        {
            HandleKind.MemberReference => GetTypeName(reader, reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition => GetTypeName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => null,
        };
    }

    /// <summary>The full name of a type, with <c>+</c> before nested type names; a constructed generic gives its definition.</summary>
    internal static string? GetTypeName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
            {
                TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                string name = reader.GetString(definition.Name);
                TypeDefinitionHandle declaring = definition.GetDeclaringType();
                if (!declaring.IsNil)
                    return GetTypeName(reader, declaring) + "+" + name;

                return Qualify(reader.GetString(definition.Namespace), name);
            }
            case HandleKind.TypeReference:
            {
                TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)handle);
                string name = reader.GetString(reference.Name);
                if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
                    return GetTypeName(reader, reference.ResolutionScope) + "+" + name;

                return Qualify(reader.GetString(reference.Namespace), name);
            }
            case HandleKind.TypeSpecification:
            {
                // A constructed generic such as IHandler<Order>: report the generic definition, IHandler`1.
                BlobReader signature = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                    return null;

                signature.ReadByte(); // ELEMENT_TYPE_CLASS or ELEMENT_TYPE_VALUETYPE
                return GetTypeName(reader, signature.ReadTypeHandle());
            }
            default:
                return null;
        }
    }

    internal static string Qualify(string @namespace, string name)
        => @namespace.Length == 0 ? name : @namespace + "." + name;

    /// <summary>Reads the leading string arguments of a custom attribute value blob.</summary>
    internal static string?[]? ReadStrings(MetadataReader reader, CustomAttribute attribute, int count)
    {
        try
        {
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 1)
                return null;

            var values = new string?[count];
            for (int i = 0; i < count; i++)
                values[i] = blob.ReadSerializedString();

            return values;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private static string? ComputePublicKeyToken(byte[] publicKey)
    {
        if (publicKey.Length == 0)
            return null;

        // The token is the last eight bytes of the SHA-1 hash of the public key, reversed.
        byte[] hash = SHA1.HashData(publicKey);
        var token = new byte[8];
        for (int i = 0; i < 8; i++)
            token[i] = hash[hash.Length - 1 - i];

        return Convert.ToHexString(token).ToLowerInvariant();
    }
}
