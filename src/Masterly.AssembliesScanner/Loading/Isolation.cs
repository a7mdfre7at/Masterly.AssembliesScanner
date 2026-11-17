using System.Reflection;
using System.Runtime.Loader;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner;

/// <summary>How <see cref="ScanResult.LoadIsolated"/> separates assemblies from the host application.</summary>
public sealed class IsolationOptions
{
    /// <summary>Whether the load context can be unloaded. Defaults to <c>true</c>.</summary>
    public bool Collectible { get; set; } = true;

    /// <summary>
    /// Whether every scanned assembly gets a load context of its own, so two plugins can carry different
    /// versions of the same dependency. Defaults to <c>false</c>: one context for the whole set.
    /// </summary>
    public bool PerAssembly { get; set; }

    /// <summary>
    /// Whether a dependency the host application already has is taken from the host, so types such as plugin
    /// contracts are the same on both sides. Defaults to <c>true</c>. Turn it off to let the isolated
    /// assemblies use their private copies, and list the contracts in <see cref="SharedAssemblies"/> instead.
    /// </summary>
    public bool PreferSharedTypes { get; set; } = true;

    /// <summary>Simple names of assemblies that are always taken from the host application.</summary>
    public ISet<string> SharedAssemblies { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether assemblies are read into memory instead of being loaded from their file. The files are then
    /// not locked and can be replaced while loaded, which is what hot reload needs. Defaults to <c>false</c>.
    /// </summary>
    public bool LoadInMemory { get; set; }

    /// <summary>
    /// Whether a load failure throws. When <c>false</c> the failure is recorded in
    /// <see cref="IsolatedAssemblySet.Failures"/> and the other assemblies still load. Defaults to <c>true</c>.
    /// </summary>
    public bool ThrowOnError { get; set; } = true;

    /// <summary>Always takes the assembly that declares <typeparamref name="T"/> from the host application.</summary>
    public IsolationOptions Share<T>() => Share(typeof(T).Assembly);

    /// <summary>Always takes the given assembly from the host application.</summary>
    public IsolationOptions Share(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        SharedAssemblies.Add(assembly.GetName().Name!);
        return this;
    }
}

/// <summary>
/// A load context that resolves dependencies next to the assemblies it was created for, falling back to
/// the host application for everything they share.
/// </summary>
public sealed class IsolatedLoadContext : AssemblyLoadContext
{
    private readonly List<AssemblyDependencyResolver> _resolvers = [];
    private readonly Dictionary<string, string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _probingDirectories = [];
    private readonly Dictionary<string, Assembly> _loadedFromMemory = new(HostAssemblies.PathComparer);
    private readonly HashSet<string> _shared;
    private readonly bool _preferShared;
    private readonly bool _loadInMemory;

    internal IsolatedLoadContext(string name, IReadOnlyList<AssemblyDescriptor> components, IsolationOptions options)
        : base(name, options.Collectible)
    {
        _shared = new HashSet<string>(options.SharedAssemblies, StringComparer.OrdinalIgnoreCase);
        _preferShared = options.PreferSharedTypes;
        _loadInMemory = options.LoadInMemory;

        foreach (AssemblyDescriptor component in components)
        {
            string path = component.Path!;
            if (!component.IsSatellite)
                _knownPaths.TryAdd(component.Name, path);

            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && !_probingDirectories.Contains(directory, HostAssemblies.PathComparer))
                _probingDirectories.Add(directory);

            try
            {
                _resolvers.Add(new AssemblyDependencyResolver(path));
            }
            catch (InvalidOperationException)
            {
                // No hostfxr (custom host): the probing directories still cover the common case.
            }
        }
    }

    internal Assembly LoadFile(string path)
    {
        if (!_loadInMemory)
            return LoadFromAssemblyPath(path);

        // Loading the same image from a stream twice is an error, so remember what was loaded.
        lock (_loadedFromMemory)
        {
            if (_loadedFromMemory.TryGetValue(path, out Assembly? loaded))
                return loaded;

            using var image = new MemoryStream(File.ReadAllBytes(path));
            string symbolsPath = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(symbolsPath))
            {
                using var symbols = new MemoryStream(File.ReadAllBytes(symbolsPath));
                loaded = LoadFromStream(image, symbols);
            }
            else
            {
                loaded = LoadFromStream(image);
            }

            _loadedFromMemory[path] = loaded;
            return loaded;
        }
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (name is null || _shared.Contains(name))
            return null; // null hands the request to the default context

        // An assembly that was scanned for this context is always the scanned file.
        if (string.IsNullOrEmpty(assemblyName.CultureName) && _knownPaths.TryGetValue(name, out string? known))
            return LoadFile(known);

        if (_preferShared && HostAssemblies.CanProvide(name))
            return null;

        string? path = ResolvePath(assemblyName);
        return path is null ? null : LoadFile(path);
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (AssemblyDependencyResolver resolver in _resolvers)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is not null)
                return LoadUnmanagedDllFromPath(path);
        }

        return IntPtr.Zero;
    }

    private string? ResolvePath(AssemblyName assemblyName)
    {
        foreach (AssemblyDependencyResolver resolver in _resolvers)
        {
            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null)
                return path;
        }

        foreach (string directory in _probingDirectories)
        {
            string candidate = string.IsNullOrEmpty(assemblyName.CultureName)
                ? Path.Combine(directory, assemblyName.Name + ".dll")
                : Path.Combine(directory, assemblyName.CultureName, assemblyName.Name + ".dll");

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

/// <summary>
/// Assemblies loaded apart from the host application. Dispose the set to unload them; the memory is
/// released once nothing references their types, instances or the assemblies themselves any more.
/// </summary>
public sealed class IsolatedAssemblySet : IDisposable
{
    private IReadOnlyList<Assembly> _assemblies;
    private IReadOnlyList<IsolatedLoadContext> _contexts;

    private IsolatedAssemblySet(IReadOnlyList<Assembly> assemblies, IReadOnlyList<IsolatedLoadContext> contexts, IReadOnlyList<ScanDiagnostic> failures)
    {
        _assemblies = assemblies;
        _contexts = contexts;
        Failures = failures;
    }

    /// <summary>The loaded assemblies. Empty after <see cref="Dispose"/>.</summary>
    public IReadOnlyList<Assembly> Assemblies => _assemblies;

    /// <summary>The load contexts that hold them. Empty after <see cref="Dispose"/>.</summary>
    public IReadOnlyList<IsolatedLoadContext> Contexts => _contexts;

    /// <summary>Assemblies that could not be loaded, when <see cref="IsolationOptions.ThrowOnError"/> is off.</summary>
    public IReadOnlyList<ScanDiagnostic> Failures { get; }

    /// <summary>Whether the set was disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Starts a type query over the isolated assemblies.</summary>
    public TypeQuery Types() => new(_assemblies);

    /// <summary>Releases the assemblies and asks the runtime to unload their collectible load contexts.</summary>
    public void Dispose()
    {
        if (IsDisposed)
            return;

        IsDisposed = true;
        IReadOnlyList<IsolatedLoadContext> contexts = _contexts;
        _assemblies = Array.Empty<Assembly>();
        _contexts = Array.Empty<IsolatedLoadContext>();

        foreach (IsolatedLoadContext context in contexts)
        {
            if (context.IsCollectible)
                context.Unload();
        }
    }

    internal static IsolatedAssemblySet Load(IReadOnlyList<AssemblyDescriptor> descriptors, IsolationOptions options)
    {
        var assemblies = new List<Assembly>();
        var contexts = new List<IsolatedLoadContext>();
        var failures = new List<ScanDiagnostic>();

        List<AssemblyDescriptor> loadable = [];
        foreach (AssemblyDescriptor descriptor in descriptors)
        {
            if (descriptor.Path is not null)
            {
                loadable.Add(descriptor);
                continue;
            }

            var failure = new ScanDiagnostic(descriptor.FileName, SkipReason.LoadFailed, "The assembly has no file path, so it cannot be loaded in isolation.");
            if (options.ThrowOnError)
                throw new InvalidOperationException(failure.Message);

            failures.Add(failure);
        }

        IEnumerable<IReadOnlyList<AssemblyDescriptor>> groups = options.PerAssembly
            ? loadable.Select(d => (IReadOnlyList<AssemblyDescriptor>)[d])
            : loadable.Count == 0 ? [] : [loadable];

        foreach (IReadOnlyList<AssemblyDescriptor> group in groups)
        {
            var context = new IsolatedLoadContext(options.PerAssembly ? group[0].Name : "Masterly.AssembliesScanner", group, options);
            contexts.Add(context);

            foreach (AssemblyDescriptor descriptor in group)
            {
                try
                {
                    assemblies.Add(context.LoadFile(descriptor.Path!));
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException)
                {
                    if (options.ThrowOnError)
                    {
                        // Do not leave half a set behind.
                        new IsolatedAssemblySet(assemblies, contexts, failures).Dispose();
                        throw;
                    }

                    failures.Add(new ScanDiagnostic(descriptor.Path!, SkipReason.LoadFailed, ex.Message, ex));
                }
            }
        }

        return new IsolatedAssemblySet(assemblies, contexts, failures);
    }
}
