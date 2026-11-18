using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Masterly.AssembliesScanner.Internal;

/// <summary>
/// An assembly file opened for metadata lookups: its types by full name, the types it forwards elsewhere and the
/// assemblies it shows its internals to. The file is read into memory and closed, so it is never locked.
/// </summary>
internal sealed class MetadataModule : IDisposable
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Shared = new(HostAssemblies.PathComparer);

    private readonly PEReader _pe;
    private readonly Lazy<Dictionary<string, TypeDefinitionHandle>> _types;
    private readonly Lazy<Dictionary<string, string>> _forwarders;
    private readonly Lazy<HashSet<string>> _friends;

    private MetadataModule(PEReader pe, string path)
    {
        _pe = pe;
        Reader = pe.GetMetadataReader();
        Path = path;
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;

        AssemblyDefinition definition = Reader.GetAssemblyDefinition();
        Name = Reader.GetString(definition.Name);
        Version = definition.Version;

        _types = new(IndexTypes);
        _forwarders = new(IndexForwarders);
        _friends = new(IndexFriends);
    }

    public MetadataReader Reader { get; }

    public PEReader Image => _pe;

    public string Path { get; }

    public string Directory { get; }

    public string Name { get; }

    public Version Version { get; }

    /// <summary>Opens a file that other assemblies point into. One copy per unchanged file is shared by the process.</summary>
    public static MetadataModule? OpenShared(string path)
    {
        long length;
        DateTime lastWrite;
        try
        {
            var file = new FileInfo(path);
            length = file.Length;
            lastWrite = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (Shared.TryGetValue(path, out CacheEntry? entry) && entry.Length == length && entry.LastWriteTimeUtc == lastWrite)
            return entry.Module;

        MetadataModule? module = Open(path, PEStreamOptions.PrefetchMetadata);
        if (module is not null)
            Shared[path] = new CacheEntry(length, lastWrite, module);

        return module;
    }

    /// <summary>Opens a file together with its method bodies, for the assembly being checked. The caller disposes it.</summary>
    public static MetadataModule? OpenWithBodies(string path) => Open(path, PEStreamOptions.PrefetchEntireImage);

    /// <summary>Forgets the shared copies.</summary>
    public static void ClearShared() => Shared.Clear();

    /// <summary>Finds a type declared in this assembly by its full name (nested types use <c>+</c>).</summary>
    public TypeDefinitionHandle FindType(string fullName)
        => _types.Value.TryGetValue(fullName, out TypeDefinitionHandle handle) ? handle : default;

    /// <summary>The assembly a type was forwarded to, or <c>null</c> when this assembly does not forward it.</summary>
    public string? FindForwarder(string fullName)
    {
        int nested = fullName.IndexOf('+');
        string outermost = nested < 0 ? fullName : fullName[..nested];
        return _forwarders.Value.TryGetValue(outermost, out string? assembly) ? assembly : null;
    }

    /// <summary>Whether <c>[InternalsVisibleTo]</c> grants the named assembly access to internal members.</summary>
    public bool GrantsInternalsTo(string assemblyName) => _friends.Value.Contains(assemblyName);

    public void Dispose() => _pe.Dispose();

    private static MetadataModule? Open(string path, PEStreamOptions options)
    {
        // The reader prefetches what it needs, so the file is closed as soon as the reader exists.
        PEReader pe;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            pe = new PEReader(stream, options | PEStreamOptions.LeaveOpen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null; // Not an assembly the runtime could use either; the caller treats it as absent.
        }

        return Adopt(pe, path);
    }

    /// <summary>
    /// Takes ownership of <paramref name="pe"/>: returns a module that owns it when it holds an assembly,
    /// otherwise disposes it and returns <c>null</c>.
    /// </summary>
    private static MetadataModule? Adopt(PEReader pe, string path)
    {
        try
        {
            if (pe.HasMetadata && pe.GetMetadataReader().IsAssembly)
                return new MetadataModule(pe, path);
        }
        catch (BadImageFormatException)
        {
            // Malformed metadata; treated as absent, like an unreadable file.
        }
        catch
        {
            pe.Dispose();
            throw;
        }

        pe.Dispose();
        return null;
    }

    private Dictionary<string, TypeDefinitionHandle> IndexTypes()
    {
        var index = new Dictionary<string, TypeDefinitionHandle>(Reader.TypeDefinitions.Count, StringComparer.Ordinal);
        foreach (TypeDefinitionHandle handle in Reader.TypeDefinitions)
            index.TryAdd(MetadataInspector.GetTypeName(Reader, handle)!, handle);

        return index;
    }

    private Dictionary<string, string> IndexForwarders()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        IEnumerable<ExportedType> forwarders = Reader.ExportedTypes
            .Select(Reader.GetExportedType)
            .Where(exported => exported.IsForwarder && exported.Implementation.Kind == HandleKind.AssemblyReference);

        foreach (ExportedType exported in forwarders)
        {
            AssemblyReference target = Reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation);
            index.TryAdd(
                MetadataInspector.Qualify(Reader.GetString(exported.Namespace), Reader.GetString(exported.Name)),
                Reader.GetString(target.Name));
        }

        return index;
    }

    private HashSet<string> IndexFriends()
    {
        IEnumerable<string> friends = Reader.GetAssemblyDefinition().GetCustomAttributes()
            .Select(Reader.GetCustomAttribute)
            .Where(attribute => MetadataInspector.GetAttributeTypeName(Reader, attribute) == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(attribute => MetadataInspector.ReadStrings(Reader, attribute, 1)?[0])
            .OfType<string>()
            // "Friend.Assembly, PublicKey=0024..." names the friend by its simple name first.
            .Select(friend => friend.Split(',')[0].Trim());

        return new HashSet<string>(friends, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CacheEntry(long Length, DateTime LastWriteTimeUtc, MetadataModule Module);
}

/// <summary>
/// Renders signatures as text that compares equal exactly when the runtime would consider the types the same:
/// full type names without assembly names (forwarding moves types between assemblies), generic parameters as
/// <c>!0</c> and <c>!!0</c> unless a type's parameters are substituted, and custom modifiers kept.
/// </summary>
internal sealed class SignatureFormatter : ISignatureTypeProvider<string, string[]?>
{
    public static readonly SignatureFormatter Instance = new();

    public static string Format(MethodSignature<string> signature)
    {
        IEnumerable<string> parameters = signature.Header.CallingConvention == SignatureCallingConvention.VarArgs
            ? signature.ParameterTypes.Take(signature.RequiredParameterCount)
            : signature.ParameterTypes;

        string text = (signature.Header.IsInstance ? "instance " : "static ") + signature.ReturnType + "(" + string.Join(",", parameters) + ")";
        return signature.GenericParameterCount == 0 ? text : text + "`" + signature.GenericParameterCount;
    }

    /// <summary>A readable form for messages: <c>Name(Type, Type)</c>.</summary>
    public static string Describe(string name, MethodSignature<string> signature)
    {
        string generic = signature.GenericParameterCount == 0 ? string.Empty : "``" + signature.GenericParameterCount;
        return $"{name}{generic}({string.Join(", ", signature.ParameterTypes)})";
    }

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "method " + Format(signature);

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetGenericMethodParameter(string[]? genericContext, int index) => "!!" + index;

    public string GetGenericTypeParameter(string[]? genericContext, int index)
        => genericContext is not null && index < genericContext.Length ? genericContext[index] : "!" + index;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";

    public string GetPinnedType(string elementType) => elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => MetadataInspector.GetTypeName(reader, handle)!;

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => MetadataInspector.GetTypeName(reader, handle)!;

    public string GetTypeFromSpecification(MetadataReader reader, string[]? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
