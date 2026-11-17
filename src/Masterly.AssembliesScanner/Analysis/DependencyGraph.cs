using System.Text;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner;

/// <summary>One assembly asking for another, with the version it asked for.</summary>
public sealed class ReferenceRequest
{
    internal ReferenceRequest(AssemblyDescriptor referrer, AssemblyReferenceInfo reference)
    {
        Referrer = referrer;
        Reference = reference;
    }

    /// <summary>The assembly that holds the reference.</summary>
    public AssemblyDescriptor Referrer { get; }

    /// <summary>What it references.</summary>
    public AssemblyReferenceInfo Reference { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Referrer.Name} -> {Reference}";
}

/// <summary>A referenced assembly that is asked for in incompatible versions.</summary>
public sealed class VersionConflict
{
    internal VersionConflict(string assemblyName, Version? availableVersion, IReadOnlyList<ReferenceRequest> requests)
    {
        AssemblyName = assemblyName;
        AvailableVersion = availableVersion;
        Requests = requests;
    }

    /// <summary>The simple name of the assembly in question.</summary>
    public string AssemblyName { get; }

    /// <summary>The version present in the scanned set, or <c>null</c> when the assembly is not in the set.</summary>
    public Version? AvailableVersion { get; }

    /// <summary>Who asks for it, and in which version.</summary>
    public IReadOnlyList<ReferenceRequest> Requests { get; }

    /// <summary>Whether somebody asks for a newer version than the one present, which fails at run time.</summary>
    public bool IsDowngrade => AvailableVersion is not null && Requests.Any(r => r.Reference.Version > AvailableVersion);

    /// <inheritdoc />
    public override string ToString()
        => $"{AssemblyName}: available {AvailableVersion?.ToString() ?? "none"}, requested {string.Join(", ", Requests.Select(r => r.Reference.Version).Distinct())}";
}

/// <summary>
/// The references between a set of scanned assemblies: who depends on whom, a safe load order, and the
/// problems worth knowing about before loading anything.
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<AssemblyDescriptor, List<AssemblyDescriptor>> _dependencies = [];
    private readonly Dictionary<AssemblyDescriptor, List<AssemblyDescriptor>> _dependents = [];
    private readonly List<IReadOnlyList<AssemblyDescriptor>> _cycles = [];
    private IReadOnlyList<AssemblyDescriptor>? _order;

    internal DependencyGraph(IReadOnlyList<AssemblyDescriptor> assemblies)
    {
        Assemblies = assemblies;

        var byName = new Dictionary<string, AssemblyDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (AssemblyDescriptor assembly in assemblies)
        {
            byName.TryAdd(assembly.Name, assembly);
            _dependencies[assembly] = [];
            _dependents[assembly] = [];
        }

        var missing = new List<ReferenceRequest>();
        var requests = new Dictionary<string, List<ReferenceRequest>>(StringComparer.OrdinalIgnoreCase);

        foreach (AssemblyDescriptor assembly in assemblies)
        {
            foreach (AssemblyReferenceInfo reference in assembly.References)
            {
                bool inSet = byName.TryGetValue(reference.Name, out AssemblyDescriptor? target);
                if (inSet && !ReferenceEquals(target, assembly) && !_dependencies[assembly].Contains(target!))
                {
                    _dependencies[assembly].Add(target!);
                    _dependents[target!].Add(assembly);
                }

                // References the runtime itself satisfies are not interesting.
                if (!inSet && FrameworkAssemblies.IsFramework(reference.Name, reference.PublicKeyToken, null))
                    continue;

                if (!requests.TryGetValue(reference.Name, out List<ReferenceRequest>? list))
                    requests[reference.Name] = list = [];

                var request = new ReferenceRequest(assembly, reference);
                list.Add(request);

                if (!inSet && !HostAssemblies.CanProvide(reference.Name))
                    missing.Add(request);
            }
        }

        var conflicts = new List<VersionConflict>();
        foreach (KeyValuePair<string, List<ReferenceRequest>> entry in requests)
        {
            Version? available = byName.TryGetValue(entry.Key, out AssemblyDescriptor? present) ? present.Version : null;
            bool mixed = entry.Value.Select(r => r.Reference.Version).Distinct().Count() > 1;
            bool downgrade = available is not null && entry.Value.Any(r => r.Reference.Version > available);
            if (mixed || downgrade)
                conflicts.Add(new VersionConflict(entry.Key, available, entry.Value));
        }

        MissingReferences = missing;
        VersionConflicts = conflicts;
    }

    /// <summary>The assemblies in the graph.</summary>
    public IReadOnlyList<AssemblyDescriptor> Assemblies { get; }

    /// <summary>
    /// References that neither the scanned set nor the host application can satisfy.
    /// Loading the referrer will fail as soon as it touches one of these.
    /// </summary>
    public IReadOnlyList<ReferenceRequest> MissingReferences { get; }

    /// <summary>
    /// Assemblies requested in more than one version, or in a newer version than the one present.
    /// </summary>
    public IReadOnlyList<VersionConflict> VersionConflicts { get; }

    /// <summary>Groups of assemblies that reference each other in a circle.</summary>
    public IReadOnlyList<IReadOnlyList<AssemblyDescriptor>> Cycles
    {
        get
        {
            GetLoadOrder();
            return _cycles;
        }
    }

    /// <summary>The assemblies in the set that <paramref name="assembly"/> references directly.</summary>
    public IReadOnlyList<AssemblyDescriptor> GetDependencies(AssemblyDescriptor assembly) => Lookup(_dependencies, assembly);

    /// <summary>The assemblies in the set that reference <paramref name="assembly"/> directly.</summary>
    public IReadOnlyList<AssemblyDescriptor> GetDependents(AssemblyDescriptor assembly) => Lookup(_dependents, assembly);

    /// <summary>Everything in the set that <paramref name="assembly"/> depends on, directly or indirectly.</summary>
    public IReadOnlyList<AssemblyDescriptor> GetTransitiveDependencies(AssemblyDescriptor assembly)
    {
        var visited = new HashSet<AssemblyDescriptor>();
        var result = new List<AssemblyDescriptor>();
        var pending = new Stack<AssemblyDescriptor>(Lookup(_dependencies, assembly));
        while (pending.Count > 0)
        {
            AssemblyDescriptor current = pending.Pop();
            if (ReferenceEquals(current, assembly) || !visited.Add(current))
                continue;

            result.Add(current);
            foreach (AssemblyDescriptor next in _dependencies[current])
                pending.Push(next);
        }

        return result;
    }

    /// <summary>
    /// The assemblies ordered so that each one comes after everything it depends on.
    /// References that would close a circle are ignored and reported in <see cref="Cycles"/>.
    /// </summary>
    public IReadOnlyList<AssemblyDescriptor> GetLoadOrder()
    {
        if (_order is not null)
            return _order;

        var order = new List<AssemblyDescriptor>(Assemblies.Count);
        var state = new Dictionary<AssemblyDescriptor, int>(); // 1 = on the current path, 2 = done
        var path = new List<AssemblyDescriptor>();

        foreach (AssemblyDescriptor assembly in Assemblies)
            Visit(assembly, order, state, path);

        return _order = order;
    }

    /// <summary>Renders the graph as a Mermaid flowchart, ready for a Markdown file.</summary>
    public string ToMermaid()
    {
        var builder = new StringBuilder("graph TD").AppendLine();
        var ids = new Dictionary<AssemblyDescriptor, string>();
        foreach (AssemblyDescriptor assembly in Assemblies)
        {
            ids[assembly] = "n" + ids.Count;
            builder.AppendLine($"""    {ids[assembly]}["{assembly.Name} {assembly.Version}"]""");
        }

        foreach (AssemblyDescriptor assembly in Assemblies)
        {
            foreach (AssemblyDescriptor dependency in _dependencies[assembly])
                builder.AppendLine($"    {ids[assembly]} --> {ids[dependency]}");
        }

        return builder.ToString();
    }

    /// <summary>Renders the graph in Graphviz DOT format.</summary>
    public string ToDot()
    {
        var builder = new StringBuilder("digraph assemblies {").AppendLine();
        foreach (AssemblyDescriptor assembly in Assemblies)
        {
            builder.AppendLine($"""    "{assembly.Name}";""");
            foreach (AssemblyDescriptor dependency in _dependencies[assembly])
                builder.AppendLine($"""    "{assembly.Name}" -> "{dependency.Name}";""");
        }

        return builder.AppendLine("}").ToString();
    }

    private static IReadOnlyList<AssemblyDescriptor> Lookup(Dictionary<AssemblyDescriptor, List<AssemblyDescriptor>> map, AssemblyDescriptor assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return map.TryGetValue(assembly, out List<AssemblyDescriptor>? list)
            ? list
            : throw new ArgumentException($"'{assembly.Name}' is not part of this graph.", nameof(assembly));
    }

    private void Visit(AssemblyDescriptor assembly, List<AssemblyDescriptor> order, Dictionary<AssemblyDescriptor, int> state, List<AssemblyDescriptor> path)
    {
        if (state.TryGetValue(assembly, out int current))
        {
            if (current == 1)
                _cycles.Add(path.Skip(path.IndexOf(assembly)).ToList());

            return;
        }

        state[assembly] = 1;
        path.Add(assembly);
        foreach (AssemblyDescriptor dependency in _dependencies[assembly])
            Visit(dependency, order, state, path);

        path.RemoveAt(path.Count - 1);
        state[assembly] = 2;
        order.Add(assembly);
    }
}
