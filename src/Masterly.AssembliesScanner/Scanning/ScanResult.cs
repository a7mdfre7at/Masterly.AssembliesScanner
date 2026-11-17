using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Masterly.AssembliesScanner.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.AssembliesScanner;

/// <summary>An embedded resource found by <see cref="ScanResult.FindResources"/>.</summary>
public sealed class ResourceMatch
{
    internal ResourceMatch(AssemblyDescriptor assembly, string resourceName)
    {
        Assembly = assembly;
        ResourceName = resourceName;
    }

    /// <summary>The assembly that embeds the resource.</summary>
    public AssemblyDescriptor Assembly { get; }

    /// <summary>The manifest resource name.</summary>
    public string ResourceName { get; }

    /// <summary>Loads the assembly and opens the resource.</summary>
    public Stream? Open() => Assembly.OpenResource(ResourceName);
}

/// <summary>The difference between two scans of the same sources.</summary>
public sealed class ScanDiff
{
    internal ScanDiff(IReadOnlyList<AssemblyDescriptor> added, IReadOnlyList<AssemblyDescriptor> removed, IReadOnlyList<AssemblyDescriptor> changed)
    {
        Added = added;
        Removed = removed;
        Changed = changed;
    }

    /// <summary>Assemblies present now that were not there before.</summary>
    public IReadOnlyList<AssemblyDescriptor> Added { get; }

    /// <summary>Assemblies that were there before and are gone now.</summary>
    public IReadOnlyList<AssemblyDescriptor> Removed { get; }

    /// <summary>Assemblies at the same location whose build, version or size changed. These are the new descriptors.</summary>
    public IReadOnlyList<AssemblyDescriptor> Changed { get; }

    /// <summary>Whether anything was added, removed or changed.</summary>
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;
}

/// <summary>
/// The outcome of a scan: the matching assemblies, still unloaded, plus a record of everything that was
/// left out and why.
/// </summary>
public sealed class ScanResult : IReadOnlyList<AssemblyDescriptor>
{
    private readonly IReadOnlyList<AssemblyDescriptor> _assemblies;
    private string? _fingerprint;

    internal ScanResult(IReadOnlyList<AssemblyDescriptor> assemblies, IReadOnlyList<ScanDiagnostic> diagnostics, TimeSpan duration, int filesExamined)
    {
        _assemblies = assemblies;
        Diagnostics = diagnostics;
        Duration = duration;
        FilesExamined = filesExamined;
    }

    /// <summary>The matching assemblies, in discovery order.</summary>
    public IReadOnlyList<AssemblyDescriptor> Assemblies => _assemblies;

    /// <summary>Every file or assembly that was examined and left out, with the reason.</summary>
    public IReadOnlyList<ScanDiagnostic> Diagnostics { get; }

    /// <summary>How long the scan took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>How many files and loaded assemblies the scan looked at.</summary>
    public int FilesExamined { get; }

    /// <inheritdoc />
    public int Count => _assemblies.Count;

    /// <inheritdoc />
    public AssemblyDescriptor this[int index] => _assemblies[index];

    /// <summary>
    /// A SHA-256 over the identity and build id of every matching assembly, independent of order.
    /// Two scans have the same fingerprint exactly when they found the same builds of the same assemblies.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            if (_fingerprint is null)
            {
                string text = string.Join("\n", _assemblies.Select(a => a.IdentityKey).OrderBy(k => k, StringComparer.Ordinal));
                _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            }

            return _fingerprint;
        }
    }

    /// <inheritdoc />
    public IEnumerator<AssemblyDescriptor> GetEnumerator() => _assemblies.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Finds a matching assembly by simple name or file name, ignoring case.</summary>
    public AssemblyDescriptor? Find(string nameOrFileName)
        => _assemblies.FirstOrDefault(a => IsNamed(a.Name, a.FileName, nameOrFileName));

    /// <summary>Loads every matching assembly into the default load context.</summary>
    /// <exception cref="FileLoadException">An assembly could not be loaded.</exception>
    public Assembly[] Load() => [.. _assemblies.Select(a => a.Load())];

    /// <summary>
    /// Loads every matching assembly that can be loaded and reports the rest instead of throwing.
    /// </summary>
    public Assembly[] TryLoad(out IReadOnlyList<ScanDiagnostic> failures)
    {
        var loaded = new List<Assembly>(_assemblies.Count);
        var failed = new List<ScanDiagnostic>();
        foreach (AssemblyDescriptor assembly in _assemblies)
        {
            try
            {
                loaded.Add(assembly.Load());
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                failed.Add(new ScanDiagnostic(assembly.Path ?? assembly.FileName, SkipReason.LoadFailed, ex.Message, ex));
            }
        }

        failures = failed;
        return [.. loaded];
    }

    /// <summary>Loads the matching assemblies into the default load context and starts a type query over them.</summary>
    public TypeQuery Types() => new(Load());

    /// <summary>
    /// Loads the matching assemblies into their own load context, separate from the host application,
    /// so they can carry private dependency versions and be unloaded again.
    /// </summary>
    /// <param name="configure">Adjusts the isolation, for example one context per assembly or in-memory loading.</param>
    public IsolatedAssemblySet LoadIsolated(Action<IsolationOptions>? configure = null)
    {
        var options = new IsolationOptions();
        configure?.Invoke(options);
        return IsolatedAssemblySet.Load(_assemblies, options);
    }

    /// <summary>
    /// Loads the matching assemblies and creates one instance of every concrete class that implements
    /// <typeparamref name="TPlugin"/>. By default each assembly gets its own unloadable load context and a
    /// broken plugin is reported in <see cref="PluginSet{TPlugin}.Failures"/> instead of throwing.
    /// </summary>
    public PluginSet<TPlugin> LoadPlugins<TPlugin>(Action<PluginOptions>? configure = null) where TPlugin : class
    {
        var options = new PluginOptions();
        configure?.Invoke(options);

        var failures = new List<ScanDiagnostic>();
        IsolatedAssemblySet? isolation = null;
        IReadOnlyList<Assembly> assemblies;

        if (options.Isolated)
        {
            options.Isolation.ThrowOnError = options.ThrowOnError;
            isolation = IsolatedAssemblySet.Load(_assemblies, options.Isolation);
            assemblies = isolation.Assemblies;
            failures.AddRange(isolation.Failures);
        }
        else if (options.ThrowOnError)
        {
            assemblies = Load();
        }
        else
        {
            assemblies = TryLoad(out IReadOnlyList<ScanDiagnostic> loadFailures);
            failures.AddRange(loadFailures);
        }

        var instances = new List<TPlugin>();
        var types = new List<Type>();
        foreach (Assembly assembly in assemblies)
        {
            Type[] candidates = TypeQuery.GetLoadableTypes(assembly, null);
            bool found = false;
            foreach (Type type in candidates)
            {
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters || !typeof(TPlugin).IsAssignableFrom(type))
                    continue;

                found = true;
                try
                {
                    object? instance = options.Services is not null
                        ? ActivatorUtilities.CreateInstance(options.Services, type)
                        : Activator.CreateInstance(type, nonPublic: true);

                    instances.Add((TPlugin)instance!);
                    types.Add(type);
                }
                catch (Exception ex)
                {
                    if (options.ThrowOnError)
                    {
                        // Do not leave the plugins created so far, or their load contexts, behind.
                        new PluginSet<TPlugin>(instances, types, isolation, failures).Dispose();
                        throw;
                    }

                    Exception cause = ex is TargetInvocationException && ex.InnerException is not null ? ex.InnerException : ex;
                    failures.Add(new ScanDiagnostic(type.FullName ?? type.Name, SkipReason.ActivationFailed, cause.Message, cause));
                }
            }

            if (!found && HasContractIdentityMismatch(candidates, typeof(TPlugin)))
            {
                failures.Add(new ScanDiagnostic(
                    assembly.GetName().Name ?? "unknown",
                    SkipReason.ActivationFailed,
                    $"The assembly implements a type named '{typeof(TPlugin).FullName}' that comes from a second copy of the contract assembly. " +
                    "Share the contract assembly with the host (IsolationOptions.Share) so both sides see the same type."));
            }
        }

        return new PluginSet<TPlugin>(instances, types, isolation, failures);
    }

    /// <summary>Builds the reference graph between the matching assemblies.</summary>
    public DependencyGraph BuildDependencyGraph() => new(_assemblies);

    /// <summary>
    /// Predicts, without loading anything, what would fail if the matching assemblies ran in this process: every
    /// type, method and field they use from other assemblies is looked up in the copy the host would provide, and
    /// every class is checked against the abstract members of its base classes and interfaces.
    /// </summary>
    /// <remarks>
    /// A referenced assembly is looked for in the host application first (see
    /// <see cref="CompatibilityOptions.PreferHostAssemblies"/>), then among the matching assemblies, then in the
    /// folder of the assembly that references it, then in <see cref="CompatibilityOptions.ProbingDirectories"/>.
    /// Each problem names the exception the runtime would throw and the methods whose IL uses the missing piece.
    /// </remarks>
    /// <param name="configure">Changes where referenced assemblies are looked for and what is checked.</param>
    public CompatibilityReport CheckCompatibility(Action<CompatibilityOptions>? configure = null)
    {
        var options = new CompatibilityOptions();
        configure?.Invoke(options);

        var checker = new CompatibilityChecker(options, _assemblies);
        var checkedAssemblies = new List<AssemblyDescriptor>();
        var skipped = new List<AssemblyDescriptor>();
        var issues = new List<CompatibilityIssue>();

        foreach (AssemblyDescriptor assembly in _assemblies)
        {
            List<CompatibilityIssue>? found = !options.CheckFrameworkAssemblies && assembly.IsFrameworkAssembly
                ? null
                : checker.Check(assembly);

            if (found is null)
            {
                skipped.Add(assembly);
                continue;
            }

            checkedAssemblies.Add(assembly);
            issues.AddRange(found);
        }

        return new CompatibilityReport(checkedAssemblies, skipped, issues);
    }

    /// <summary>Works out what changed since an earlier result, matching assemblies by file path.</summary>
    public ScanDiff DiffFrom(ScanResult previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        Dictionary<string, AssemblyDescriptor> before = Index(previous._assemblies);
        Dictionary<string, AssemblyDescriptor> after = Index(_assemblies);

        var added = new List<AssemblyDescriptor>();
        var changed = new List<AssemblyDescriptor>();
        foreach (KeyValuePair<string, AssemblyDescriptor> entry in after)
        {
            if (!before.TryGetValue(entry.Key, out AssemblyDescriptor? old))
                added.Add(entry.Value);
            else if (old.Mvid != entry.Value.Mvid || old.Version != entry.Value.Version || old.FileSize != entry.Value.FileSize)
                changed.Add(entry.Value);
        }

        List<AssemblyDescriptor> removed = [.. before.Where(e => !after.ContainsKey(e.Key)).Select(e => e.Value)];
        return new ScanDiff(added, removed, changed);
    }

    /// <summary>Lists the embedded resources whose name matches a glob pattern, without loading anything.</summary>
    public IReadOnlyList<ResourceMatch> FindResources(string pattern)
    {
        NamePattern glob = NamePattern.Glob(pattern);
        return _assemblies
            .SelectMany(a => a.ResourceNames.Where(r => glob.IsMatch(r)).Select(r => new ResourceMatch(a, r)))
            .ToList();
    }

    /// <summary>
    /// Explains in one sentence what happened to an assembly: selected, or left out and why.
    /// </summary>
    /// <param name="nameOrFileName">A simple assembly name or a file name.</param>
    public string Explain(string nameOrFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameOrFileName);

        AssemblyDescriptor? match = Find(nameOrFileName);
        if (match is not null)
            return $"Selected: {match.FullName} from {match.Path ?? "memory"}.";

        List<ScanDiagnostic> related = [.. Diagnostics.Where(d => IsNamed(NamePattern.StripExtension(Path.GetFileName(d.Path)), Path.GetFileName(d.Path), nameOrFileName))];

        return related.Count == 0
            ? $"Not seen: no scanned source contains '{nameOrFileName}'."
            : string.Join(Environment.NewLine, related.Select(d => $"Left out ({d.Reason}): {d.Path} - {d.Message}"));
    }

    /// <summary>Writes the result as a JSON inventory.</summary>
    /// <param name="includeHashes">Whether to compute and include the SHA-256 of every file.</param>
    public string ToJson(bool includeHashes = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("filesExamined", FilesExamined);
            writer.WriteNumber("durationMilliseconds", Math.Round(Duration.TotalMilliseconds, 3));
            writer.WriteString("fingerprint", Fingerprint);

            writer.WriteStartArray("assemblies");
            foreach (AssemblyDescriptor assembly in _assemblies)
            {
                writer.WriteStartObject();
                writer.WriteString("name", assembly.Name);
                writer.WriteString("version", assembly.Version.ToString());
                writer.WriteString("culture", assembly.Culture);
                writer.WriteString("publicKeyToken", assembly.PublicKeyToken);
                writer.WriteString("fullName", assembly.FullName);
                writer.WriteString("path", assembly.Path);
                writer.WriteString("source", assembly.Source.ToString());
                writer.WriteString("targetFramework", assembly.TargetFramework);
                writer.WriteString("informationalVersion", assembly.InformationalVersion);
                writer.WriteString("mvid", assembly.Mvid);
                writer.WriteNumber("fileSize", assembly.FileSize);
                if (includeHashes)
                    writer.WriteString("sha256", assembly.Sha256);

                writer.WriteStartArray("references");
                foreach (AssemblyReferenceInfo reference in assembly.References)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", reference.Name);
                    writer.WriteString("version", reference.Version.ToString());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("diagnostics");
            foreach (ScanDiagnostic diagnostic in Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("path", diagnostic.Path);
                writer.WriteString("reason", diagnostic.Reason.ToString());
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the result as a Markdown report: a table of assemblies and a table of what was left out.</summary>
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Assembly scan");
        builder.AppendLine();
        builder.AppendLine($"{Count} selected out of {FilesExamined} examined in {Duration.TotalMilliseconds:0.#} ms.");
        builder.AppendLine();
        builder.AppendLine("| Assembly | Version | Target framework | Public key token | File |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (AssemblyDescriptor assembly in _assemblies)
            builder.AppendLine($"| {assembly.Name} | {assembly.Version} | {assembly.TargetFramework ?? "-"} | {assembly.PublicKeyToken ?? "-"} | {assembly.RelativePath} |");

        if (Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Left out");
            builder.AppendLine();
            builder.AppendLine("| File | Reason | Detail |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (ScanDiagnostic diagnostic in Diagnostics)
                builder.AppendLine($"| {Path.GetFileName(diagnostic.Path)} | {diagnostic.Reason} | {diagnostic.Message.Replace("|", "\\|")} |");
        }

        return builder.ToString();
    }

    private static bool IsNamed(string name, string fileName, string wanted)
        => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, wanted, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, AssemblyDescriptor> Index(IReadOnlyList<AssemblyDescriptor> assemblies)
    {
        var index = new Dictionary<string, AssemblyDescriptor>(HostAssemblies.PathComparer);
        foreach (AssemblyDescriptor assembly in assemblies)
            index.TryAdd(assembly.Path ?? assembly.FullName, assembly);

        return index;
    }

    private static bool HasContractIdentityMismatch(Type[] types, Type contract)
    {
        foreach (Type type in types)
        {
            try
            {
                if (type.GetInterfaces().Any(i => i != contract && i.FullName == contract.FullName))
                    return true;

                for (Type? current = type.BaseType; current is not null; current = current.BaseType)
                {
                    if (current != contract && current.FullName == contract.FullName)
                        return true;
                }
            }
            catch (Exception ex) when (ex is TypeLoadException or IOException)
            {
                // A type that cannot resolve its own hierarchy cannot be the plugin either.
            }
        }

        return false;
    }
}
