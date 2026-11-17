using System.Collections;

namespace Masterly.AssembliesScanner;

/// <summary>How <see cref="ScanResult.LoadPlugins{TPlugin}"/> loads and creates plugins.</summary>
public sealed class PluginOptions
{
    /// <summary>
    /// Whether plugins are loaded apart from the host application so they can be unloaded.
    /// Defaults to <c>true</c>. When <c>false</c> they are loaded into the default load context for good.
    /// </summary>
    public bool Isolated { get; set; } = true;

    /// <summary>
    /// A service provider used to satisfy plugin constructor parameters.
    /// Without one, plugins need a parameterless constructor.
    /// </summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>
    /// Whether a plugin that fails to load or to construct throws. Defaults to <c>false</c>: the failure is
    /// recorded in <see cref="PluginSet{TPlugin}.Failures"/> and the other plugins still load.
    /// </summary>
    public bool ThrowOnError { get; set; }

    /// <summary>The isolation settings. Each plugin assembly gets its own load context by default.</summary>
    public IsolationOptions Isolation { get; } = new IsolationOptions { PerAssembly = true };
}

/// <summary>
/// The plugin instances created by <see cref="ScanResult.LoadPlugins{TPlugin}"/>. Disposing the set
/// disposes the plugins that implement <see cref="IDisposable"/> and unloads their assemblies.
/// </summary>
/// <typeparam name="TPlugin">The plugin contract.</typeparam>
public sealed class PluginSet<TPlugin> : IReadOnlyList<TPlugin>, IDisposable where TPlugin : class
{
    private readonly List<TPlugin> _instances;
    private readonly List<Type> _types;
    private readonly IsolatedAssemblySet? _isolation;
    private bool _disposed;

    internal PluginSet(List<TPlugin> instances, List<Type> types, IsolatedAssemblySet? isolation, IReadOnlyList<ScanDiagnostic> failures)
    {
        _instances = instances;
        _types = types;
        _isolation = isolation;
        Failures = failures;
    }

    /// <summary>The concrete type of each plugin, in the same order as the instances.</summary>
    public IReadOnlyList<Type> PluginTypes => _types;

    /// <summary>Assemblies that failed to load and plugin types that failed to construct.</summary>
    public IReadOnlyList<ScanDiagnostic> Failures { get; }

    /// <summary>Whether the plugins live in load contexts that <see cref="Dispose"/> unloads.</summary>
    public bool IsIsolated => _isolation is not null;

    /// <inheritdoc />
    public int Count => _instances.Count;

    /// <inheritdoc />
    public TPlugin this[int index] => _instances[index];

    /// <inheritdoc />
    public IEnumerator<TPlugin> GetEnumerator() => _instances.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Disposes the plugins, forgets them and unloads their load contexts.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (TPlugin plugin in _instances)
        {
            try
            {
                (plugin as IDisposable)?.Dispose();
            }
            catch (Exception)
            {
                // One plugin failing to clean up must not keep the others, or the unload, from happening.
            }
        }

        _instances.Clear();
        _types.Clear();
        _isolation?.Dispose();
    }
}
