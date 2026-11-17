using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner;

/// <summary>Where a scanner found an assembly.</summary>
public enum AssemblySource
{
    /// <summary>A file in a scanned directory, or a file given explicitly.</summary>
    File,

    /// <summary>An assembly that was already loaded in the process.</summary>
    Loaded,

    /// <summary>An assembly reached by following references from a root assembly.</summary>
    DependencyClosure
}

/// <summary>A reference from one assembly to another, as recorded in metadata.</summary>
public sealed class AssemblyReferenceInfo
{
    internal AssemblyReferenceInfo(string name, Version version, string culture, string? publicKeyToken)
    {
        Name = name;
        Version = version;
        Culture = culture;
        PublicKeyToken = publicKeyToken;
    }

    /// <summary>The simple name of the referenced assembly.</summary>
    public string Name { get; }

    /// <summary>The version the referencing assembly was compiled against.</summary>
    public Version Version { get; }

    /// <summary>The culture of the referenced assembly, or an empty string.</summary>
    public string Culture { get; }

    /// <summary>The public key token in lower-case hex, or <c>null</c> when the reference is not strong-named.</summary>
    public string? PublicKeyToken { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Name}, Version={Version}";
}

/// <summary>
/// Everything known about an assembly from its metadata. Creating a descriptor does not load the assembly;
/// call <see cref="Load()"/> when you want it.
/// </summary>
public sealed class AssemblyDescriptor
{
    private readonly AssemblySnapshot _snapshot;
    private readonly object _gate = new();
    private Assembly? _loaded;
    private string? _sha256;

    internal AssemblyDescriptor(AssemblySnapshot snapshot, string? path, string fileName, string relativePath, AssemblySource source, Assembly? loaded)
    {
        _snapshot = snapshot;
        _loaded = loaded;
        Path = path;
        FileName = fileName;
        RelativePath = relativePath;
        Source = source;

        if (path is not null)
        {
            try
            {
                var file = new FileInfo(path);
                FileSize = file.Length;
                LastWriteTimeUtc = file.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file went away between inspection and now; the metadata is still valid.
            }
        }
    }

    /// <summary>The simple name, for example <c>Contoso.Core</c>.</summary>
    public string Name => _snapshot.Name;

    /// <summary>The assembly version.</summary>
    public Version Version => _snapshot.Version;

    /// <summary>The culture, or an empty string for a culture-neutral assembly.</summary>
    public string Culture => _snapshot.Culture;

    /// <summary>The public key token in lower-case hex, or <c>null</c> when the assembly is not strong-named.</summary>
    public string? PublicKeyToken => _snapshot.PublicKeyToken;

    /// <summary>The display name: simple name, version, culture and public key token.</summary>
    public string FullName => _snapshot.FullName;

    /// <summary>The module version id, which changes on every non-deterministic build.</summary>
    public Guid Mvid => _snapshot.Mvid;

    /// <summary>The full path of the file, or <c>null</c> for an assembly that only exists in memory.</summary>
    public string? Path { get; }

    /// <summary>The file name, for example <c>Contoso.Core.dll</c>.</summary>
    public string FileName { get; }

    /// <summary>The path relative to the scanned directory, with forward slashes.</summary>
    public string RelativePath { get; }

    /// <summary>Where the scanner found the assembly.</summary>
    public AssemblySource Source { get; }

    /// <summary>The file size in bytes, or zero when there is no file.</summary>
    public long FileSize { get; }

    /// <summary>When the file was last written, in UTC.</summary>
    public DateTime LastWriteTimeUtc { get; }

    /// <summary>The value of <c>TargetFrameworkAttribute</c>, for example <c>.NETCoreApp,Version=v8.0</c>.</summary>
    public string? TargetFramework => _snapshot.TargetFramework;

    /// <summary>The informational (product) version, which usually carries the package version and commit hash.</summary>
    public string? InformationalVersion => _snapshot.InformationalVersion;

    /// <summary>The file version.</summary>
    public string? FileVersion => _snapshot.FileVersion;

    /// <summary>The company name.</summary>
    public string? Company => _snapshot.Company;

    /// <summary>The product name.</summary>
    public string? Product => _snapshot.Product;

    /// <summary>The assembly title.</summary>
    public string? Title => _snapshot.Title;

    /// <summary>The assembly description.</summary>
    public string? Description => _snapshot.Description;

    /// <summary>The build configuration, for example <c>Release</c>.</summary>
    public string? Configuration => _snapshot.Configuration;

    /// <summary>The key/value pairs declared with <c>[assembly: AssemblyMetadata(key, value)]</c>.</summary>
    public IReadOnlyDictionary<string, string?> Metadata => _snapshot.Metadata;

    /// <summary>The assemblies this assembly references.</summary>
    public IReadOnlyList<AssemblyReferenceInfo> References => _snapshot.References;

    /// <summary>The full names of the attribute types applied at assembly level.</summary>
    public IReadOnlyList<string> AssemblyAttributes => _snapshot.AttributeNames;

    /// <summary>The names of the embedded manifest resources.</summary>
    public IReadOnlyList<string> ResourceNames => _snapshot.ResourceNames;

    /// <summary>
    /// The types the assembly declares, read from metadata on first access without loading the assembly.
    /// </summary>
    public IReadOnlyList<TypeDescriptor> Types => _snapshot.Types.Value;

    /// <summary>Whether the assembly is a reference assembly, which cannot be loaded for execution.</summary>
    public bool IsReferenceAssembly => _snapshot.IsReferenceAssembly;

    /// <summary>Whether the assembly is a satellite resource assembly for a specific culture.</summary>
    public bool IsSatellite => Culture.Length > 0;

    /// <summary>
    /// Whether the assembly carries a public key. .NET does not validate strong-name signatures,
    /// so treat this as part of the identity, not as proof of origin.
    /// </summary>
    public bool IsStrongNamed => PublicKeyToken is not null;

    /// <summary>Whether the assembly belongs to the .NET base class library.</summary>
    public bool IsFrameworkAssembly => FrameworkAssemblies.IsFramework(Name, PublicKeyToken, Path);

    /// <summary>Whether this descriptor already holds a loaded assembly.</summary>
    public bool IsLoaded => _loaded is not null;

    /// <summary>The loaded assembly, or <c>null</c> until <see cref="Load()"/> is called.</summary>
    public Assembly? LoadedAssembly => _loaded;

    /// <summary>
    /// The SHA-256 hash of the file in lower-case hex, computed on first access,
    /// or <c>null</c> when there is no file.
    /// </summary>
    public string? Sha256
    {
        get
        {
            if (_sha256 is null && Path is not null)
            {
                using FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                _sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            return _sha256;
        }
    }

    internal string IdentityKey => FullName + "|" + Mvid.ToString("N");

    /// <summary>Whether this assembly references an assembly with the given simple name.</summary>
    public bool IsReferencing(string assemblyName)
        => References.Any(r => string.Equals(r.Name, assemblyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether an attribute with the given full type name is applied at assembly level.</summary>
    public bool HasAssemblyAttribute(string attributeFullName)
        => AssemblyAttributes.Contains(attributeFullName, StringComparer.Ordinal);

    /// <summary>
    /// Whether the assembly declares a concrete class that implements or derives from the type with the given
    /// full name, judged from metadata alone.
    /// </summary>
    /// <remarks>
    /// Base classes are followed only while they are declared in this same assembly. A class that picks up
    /// the interface through a base class in another assembly is not detected; load the assembly and use
    /// <see cref="TypeQuery"/> when you need exact answers.
    /// </remarks>
    public bool DeclaresImplementationOf(string typeFullName)
    {
        IReadOnlyList<TypeDescriptor> types = Types;
        Dictionary<string, TypeDescriptor>? byName = null;

        foreach (TypeDescriptor type in types)
        {
            if (!type.IsConcreteClass)
                continue;

            TypeDescriptor? current = type;
            for (int depth = 0; current is not null && depth < 64; depth++)
            {
                if (current.Declares(typeFullName))
                    return true;

                if (current.BaseType is null)
                    break;

                byName ??= BuildIndex(types);
                byName.TryGetValue(current.BaseType, out current);
            }
        }

        return false;
    }

    /// <summary>
    /// Loads the assembly into the default load context, or returns it when it is already loaded.
    /// </summary>
    /// <remarks>
    /// The assembly is first requested by name, so the host application's own copy wins when it has one.
    /// Only when the host cannot resolve the name is the scanned file loaded by path.
    /// </remarks>
    public Assembly Load()
    {
        if (_loaded is not null)
            return _loaded;

        lock (_gate)
        {
            return _loaded ??= LoadIntoDefaultContext();
        }
    }

    /// <summary>Loads the file into the given load context. The result is not cached on the descriptor.</summary>
    /// <exception cref="InvalidOperationException">The assembly has no file to load from.</exception>
    public Assembly Load(AssemblyLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Path is null)
            throw new InvalidOperationException($"'{Name}' has no file path, so it cannot be loaded into another load context.");

        return context.LoadFromAssemblyPath(Path);
    }

    /// <summary>Loads the assembly and opens one of its embedded resources.</summary>
    /// <returns>The resource stream, or <c>null</c> when no resource has that name.</returns>
    public Stream? OpenResource(string resourceName) => Load().GetManifestResourceStream(resourceName);

    /// <inheritdoc />
    public override string ToString() => FullName;

    private static Dictionary<string, TypeDescriptor> BuildIndex(IReadOnlyList<TypeDescriptor> types)
    {
        var index = new Dictionary<string, TypeDescriptor>(types.Count, StringComparer.Ordinal);
        foreach (TypeDescriptor type in types)
            index.TryAdd(type.FullName, type);

        return index;
    }

    private Assembly LoadIntoDefaultContext()
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(FullName));
        }
        catch (Exception ex) when (Path is not null && (ex is FileNotFoundException || ex is FileLoadException))
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path);
        }
    }
}
