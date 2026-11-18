using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner;

/// <summary>
/// Describes what to scan and what to keep, then produces a <see cref="ScanResult"/>.
/// Start one with <see cref="Assemblies.Scan()"/>.
/// </summary>
/// <remarks>
/// <para>
/// A scan reads metadata only. No assembly is loaded until you call <see cref="ScanResult.Load"/>,
/// <see cref="ScanResult.LoadIsolated"/> or one of the other loading methods, so native DLLs, assemblies
/// built for another runtime and assemblies you filter out never enter the process.
/// </para>
/// <para>
/// With no source configured the scanner reads the top level of the application base directory.
/// With no include pattern every assembly found is a candidate.
/// </para>
/// <para>
/// Filters run concurrently on several threads unless you call <see cref="WithParallelism"/> with 1,
/// so a predicate passed to <see cref="Where"/> must be thread-safe.
/// </para>
/// </remarks>
public sealed class AssemblyScanner
{
    private const int ParallelThreshold = 8;

    private readonly List<Source> _sources = [];
    private readonly List<NamePattern> _includes = [];
    private readonly List<NamePattern> _excludes = [];
    private readonly List<NamedFilter> _filters = [];
    private int _maxDegreeOfParallelism = Environment.ProcessorCount;
    private bool _useCache = true;

    internal AssemblyScanner()
    {
    }

    // ---------------------------------------------------------------- sources

    /// <summary>Scans the top level of the application base directory.</summary>
    public AssemblyScanner FromBaseDirectory() => FromDirectory(AppContext.BaseDirectory);

    /// <summary>Scans the <c>*.dll</c> files of a directory.</summary>
    /// <param name="path">The directory. A missing directory is reported as a diagnostic, not an exception.</param>
    /// <param name="recursive">Whether to descend into subdirectories.</param>
    public AssemblyScanner FromDirectory(string path, bool recursive = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _sources.Add(new DirectorySource(Path.GetFullPath(path), recursive));
        return this;
    }

    /// <summary>Scans the top level of several directories.</summary>
    public AssemblyScanner FromDirectories(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
            FromDirectory(path);

        return this;
    }

    /// <summary>Scans specific files.</summary>
    public AssemblyScanner FromFiles(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            _sources.Add(new FileSource(Path.GetFullPath(path)));
        }

        return this;
    }

    /// <summary>Includes every assembly already loaded in the current application domain at scan time.</summary>
    public AssemblyScanner FromLoadedAssemblies()
    {
        _sources.Add(new LoadedSource(() => AppDomain.CurrentDomain.GetAssemblies()));
        return this;
    }

    /// <summary>Includes specific assemblies that are already loaded.</summary>
    public AssemblyScanner FromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        Assembly[] copy = [.. assemblies];
        _sources.Add(new LoadedSource(() => copy));
        return this;
    }

    /// <summary>Includes the assembly that declares <typeparamref name="T"/>.</summary>
    public AssemblyScanner FromAssemblyOf<T>() => FromAssemblies(typeof(T).Assembly);

    /// <summary>
    /// Includes a root assembly and everything it references, directly or indirectly, including assemblies
    /// the runtime has not loaded yet. References are followed through metadata, so nothing is loaded.
    /// </summary>
    /// <param name="root">The assembly to start from. Defaults to the entry assembly.</param>
    /// <param name="includeFrameworkAssemblies">Whether to follow references into the .NET base class library.</param>
    /// <exception cref="InvalidOperationException">No root was given and the process has no entry assembly.</exception>
    public AssemblyScanner FromDependencyClosure(Assembly? root = null, bool includeFrameworkAssemblies = false)
    {
        root ??= Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("The process has no entry assembly. Pass the root assembly explicitly.");

        _sources.Add(new ClosureSource(root, includeFrameworkAssemblies));
        return this;
    }

    // --------------------------------------------------------------- patterns

    /// <summary>
    /// Selects assemblies whose file name matches any of the glob patterns. A pattern that starts with
    /// <c>!</c> is an exclusion, and a <c>regex:</c> or <c>exact:</c> prefix switches syntax.
    /// See <see cref="NamePattern"/>.
    /// </summary>
    public AssemblyScanner Include(params string[] patterns) => AddPatterns(patterns, PatternSyntax.Glob, exclude: false);

    /// <summary>Removes assemblies whose file name matches any of the glob patterns. Exclusions always win.</summary>
    public AssemblyScanner Exclude(params string[] patterns) => AddPatterns(patterns, PatternSyntax.Glob, exclude: true);

    /// <summary>Selects assemblies whose file name matches any of the regular expressions.</summary>
    public AssemblyScanner IncludeRegex(params string[] patterns) => AddPatterns(patterns, PatternSyntax.Regex, exclude: false);

    /// <summary>Removes assemblies whose file name matches any of the regular expressions.</summary>
    public AssemblyScanner ExcludeRegex(params string[] patterns) => AddPatterns(patterns, PatternSyntax.Regex, exclude: true);

    /// <summary>Adds patterns that were built with <see cref="NamePattern"/>.</summary>
    public AssemblyScanner Matching(params NamePattern[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (NamePattern pattern in patterns)
            (pattern.IsExclusion ? _excludes : _includes).Add(pattern);

        return this;
    }

    // ---------------------------------------------------------------- filters

    /// <summary>Keeps assemblies for which the predicate returns <c>true</c>.</summary>
    /// <param name="predicate">The test. It may run on several threads at once.</param>
    /// <param name="description">A name for the filter, shown in diagnostics when it rejects an assembly.</param>
    public AssemblyScanner Where(Func<AssemblyDescriptor, bool> predicate, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        string name = description ?? "custom predicate";
        _filters.Add(new NamedFilter(name, a => predicate(a) ? null : $"Rejected by filter: {name}.", SkipReason.ExcludedByFilter));
        return this;
    }

    /// <summary>Keeps assemblies whose version lies within the inclusive range. Either bound may be <c>null</c>.</summary>
    public AssemblyScanner WithVersionRange(Version? minimum, Version? maximum)
        => Where(
            a => (minimum is null || a.Version >= minimum) && (maximum is null || a.Version <= maximum),
            $"version in [{minimum?.ToString() ?? "*"}, {maximum?.ToString() ?? "*"}]");

    /// <summary>Keeps assemblies whose version is at least the given one, for example <c>"2.0.0.0"</c>.</summary>
    public AssemblyScanner WithMinimumVersion(string version) => WithVersionRange(Version.Parse(version), null);

    /// <summary>Keeps assemblies whose version is at most the given one, for example <c>"2.9.0.0"</c>.</summary>
    public AssemblyScanner WithMaximumVersion(string version) => WithVersionRange(null, Version.Parse(version));

    /// <summary>Keeps assemblies whose public key token is one of the given tokens (hex, any case).</summary>
    public AssemblyScanner WithPublicKeyToken(params string[] tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var allowed = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        return Where(a => a.PublicKeyToken is not null && allowed.Contains(a.PublicKeyToken), "public key token allow-list");
    }

    /// <summary>Keeps only assemblies that carry a public key.</summary>
    public AssemblyScanner RequireStrongName() => Where(a => a.IsStrongNamed, "strong name required");

    /// <summary>
    /// Keeps only assemblies whose file hash is on the allow-list. This is the tamper check: a file that was
    /// modified in any way no longer matches.
    /// </summary>
    /// <param name="sha256Hashes">The allowed SHA-256 hashes in hex, any case.</param>
    public AssemblyScanner RequireHash(params string[] sha256Hashes)
    {
        ArgumentNullException.ThrowIfNull(sha256Hashes);
        var allowed = new HashSet<string>(sha256Hashes.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        return Where(a => a.Sha256 is string hash && allowed.Contains(hash), "SHA-256 allow-list");
    }

    /// <summary>
    /// Keeps assemblies built for a target framework. Accepts a moniker such as <c>net8.0</c>,
    /// <c>netstandard2.0</c> or <c>net48</c>, or any part of the framework name, such as <c>.NETCoreApp</c>.
    /// </summary>
    public AssemblyScanner TargetingFramework(string framework)
    {
        ArgumentException.ThrowIfNullOrEmpty(framework);
        string expected = NormalizeFramework(framework);
        return Where(
            a => a.TargetFramework is not null && a.TargetFramework.Contains(expected, StringComparison.OrdinalIgnoreCase),
            $"targeting {framework}");
    }

    /// <summary>Keeps assemblies that reference at least one of the named assemblies.</summary>
    public AssemblyScanner Referencing(params string[] assemblyNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);
        string[] names = [.. assemblyNames];
        return Where(a => names.Any(a.IsReferencing), $"referencing {string.Join(" or ", names)}");
    }

    /// <summary>Keeps assemblies that reference the given assembly.</summary>
    public AssemblyScanner Referencing(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Referencing(assembly.GetName().Name!);
    }

    /// <summary>Keeps assemblies that reference the assembly declaring <typeparamref name="T"/>.</summary>
    public AssemblyScanner Referencing<T>() => Referencing(typeof(T).Assembly);

    /// <summary>Keeps assemblies marked with an assembly-level attribute, given by its full type name.</summary>
    public AssemblyScanner WithAssemblyAttribute(string attributeFullName)
    {
        ArgumentException.ThrowIfNullOrEmpty(attributeFullName);
        return Where(a => a.HasAssemblyAttribute(attributeFullName), $"assembly attribute {attributeFullName}");
    }

    /// <summary>Keeps assemblies marked with an assembly-level <typeparamref name="TAttribute"/>.</summary>
    public AssemblyScanner WithAssemblyAttribute<TAttribute>() where TAttribute : Attribute
        => WithAssemblyAttribute(typeof(TAttribute).FullName!);

    /// <summary>
    /// Keeps assemblies that declare <c>[assembly: AssemblyMetadata(key, value)]</c>.
    /// With a <c>null</c> <paramref name="value"/> any value is accepted.
    /// </summary>
    public AssemblyScanner WithMetadata(string key, string? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return Where(
            a => a.Metadata.TryGetValue(key, out string? actual) && (value is null || string.Equals(actual, value, StringComparison.OrdinalIgnoreCase)),
            value is null ? $"metadata '{key}'" : $"metadata '{key}' = '{value}'");
    }

    /// <summary>Keeps assemblies that declare at least one type matching the predicate, judged from metadata.</summary>
    public AssemblyScanner ContainingTypes(Func<TypeDescriptor, bool> predicate, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Where(a => a.Types.Any(predicate), description ?? "containing matching types");
    }

    /// <summary>
    /// Keeps assemblies that declare a concrete class implementing or deriving from the given type,
    /// judged from metadata. See <see cref="AssemblyDescriptor.DeclaresImplementationOf"/> for the limits.
    /// </summary>
    public AssemblyScanner ContainingImplementationsOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string fullName = (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName
            ?? throw new ArgumentException("The type has no full name.", nameof(type));

        return Where(a => a.DeclaresImplementationOf(fullName), $"containing implementations of {fullName}");
    }

    /// <summary>Keeps assemblies that declare a concrete class implementing or deriving from <typeparamref name="T"/>.</summary>
    public AssemblyScanner ContainingImplementationsOf<T>() => ContainingImplementationsOf(typeof(T));

    /// <summary>Keeps assemblies that declare at least one type marked with <typeparamref name="TAttribute"/>.</summary>
    public AssemblyScanner ContainingTypesWithAttribute<TAttribute>() where TAttribute : Attribute
    {
        string fullName = typeof(TAttribute).FullName!;
        return Where(a => a.Types.Any(t => t.HasAttribute(fullName)), $"containing types with {fullName}");
    }

    /// <summary>Keeps assemblies that embed a resource whose name matches the glob pattern.</summary>
    public AssemblyScanner WithResource(string pattern)
    {
        NamePattern glob = NamePattern.Glob(pattern);
        return Where(a => a.ResourceNames.Any(r => glob.IsMatch(r)), $"embedding resource '{pattern}'");
    }

    /// <summary>Removes the .NET base class library: <c>System.*</c>, <c>mscorlib</c>, <c>netstandard</c> and the shared frameworks.</summary>
    public AssemblyScanner ExcludeFrameworkAssemblies() => Where(a => !a.IsFrameworkAssembly, "not a framework assembly");

    /// <summary>Removes the base class library and every Microsoft-signed <c>Microsoft.*</c> assembly.</summary>
    public AssemblyScanner ExcludeMicrosoftAssemblies()
        => Where(a => !FrameworkAssemblies.IsMicrosoft(a.Name, a.PublicKeyToken, a.Path), "not a Microsoft assembly");

    /// <summary>Removes satellite resource assemblies.</summary>
    public AssemblyScanner ExcludeSatelliteAssemblies() => Where(a => !a.IsSatellite, "not a satellite assembly");

    /// <summary>Removes reference assemblies, which cannot be loaded for execution.</summary>
    public AssemblyScanner ExcludeReferenceAssemblies() => Where(a => !a.IsReferenceAssembly, "not a reference assembly");

    /// <summary>
    /// Removes assemblies that would fail at run time because they use a type or member the host would not provide,
    /// judged from metadata without loading anything. Each one left out gets an <see cref="SkipReason.Incompatible"/>
    /// diagnostic that names the first problem; <see cref="ScanResult.CheckCompatibility"/> gives the full report.
    /// </summary>
    /// <remarks>
    /// Referenced assemblies are looked for in the host application, the folder of the scanned file and
    /// <see cref="CompatibilityOptions.ProbingDirectories"/>. The other files of the same scan are not consulted
    /// unless they sit in those places. An assembly without a file cannot be checked and is kept.
    /// </remarks>
    /// <param name="configure">Changes where referenced assemblies are looked for and what is checked.</param>
    public AssemblyScanner RequireCompatibility(Action<CompatibilityOptions>? configure = null)
    {
        var options = new CompatibilityOptions();
        configure?.Invoke(options);

        _filters.Add(new NamedFilter("binary compatible with the host", assembly =>
        {
            if (!options.CheckFrameworkAssemblies && assembly.IsFrameworkAssembly)
                return null;

            List<CompatibilityIssue>? issues = new CompatibilityChecker(options, []).Check(assembly);
            if (issues is null || issues.Count == 0)
                return null;

            string more = issues.Count == 1 ? string.Empty : $" ({issues.Count - 1} more problem{(issues.Count == 2 ? "" : "s")})";
            return $"Not compatible: {issues[0].Kind} {issues[0].Member} - {issues[0].Message}{more}";
        }, SkipReason.Incompatible));

        return this;
    }

    // ---------------------------------------------------------------- options

    /// <summary>Sets how many files are inspected at once. Pass 1 to scan on the calling thread only.</summary>
    public AssemblyScanner WithParallelism(int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        return this;
    }

    /// <summary>Reads every file again instead of using <see cref="ScanCache"/>.</summary>
    public AssemblyScanner WithoutCache()
    {
        _useCache = false;
        return this;
    }

    // -------------------------------------------------------------- execution

    /// <summary>Runs the scan.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public ScanResult Scan(CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<ScanDiagnostic>();
        List<Candidate> candidates = CollectCandidates(diagnostics, cancellationToken);
        var outcomes = new Outcome[candidates.Count];

        if (_maxDegreeOfParallelism == 1 || candidates.Count < ParallelThreshold)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes[i] = Evaluate(candidates[i]);
            }
        }
        else
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism, CancellationToken = cancellationToken };
            try
            {
                Parallel.For(0, candidates.Count, options, i => outcomes[i] = Evaluate(candidates[i]));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
        }

        var accepted = new List<AssemblyDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Outcome outcome in outcomes)
            Collect(outcome, seen, accepted, diagnostics);

        return new ScanResult(accepted, diagnostics, stopwatch.Elapsed, candidates.Count);
    }

    /// <summary>Runs the scan on the thread pool.</summary>
    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(cancellationToken), cancellationToken);

    /// <summary>
    /// Streams matching assemblies as they are found, in discovery order, without waiting for the whole scan.
    /// </summary>
    public async IAsyncEnumerable<AssemblyDescriptor> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<ScanDiagnostic>();
        List<Candidate> candidates = await Task.Run(() => CollectCandidates(diagnostics, cancellationToken), cancellationToken).ConfigureAwait(false);

        var accepted = new List<AssemblyDescriptor>(1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Outcome outcome = await Task.Run(() => Evaluate(candidate), cancellationToken).ConfigureAwait(false);

            accepted.Clear();
            Collect(outcome, seen, accepted, diagnostics);
            if (accepted.Count == 1)
                yield return accepted[0];
        }
    }

    /// <summary>Scans and loads the matching assemblies into the default load context.</summary>
    public Assembly[] Load() => Scan().Load();

    /// <summary>Scans, loads the matching assemblies and starts a type query over them.</summary>
    public TypeQuery Types() => Scan().Types();

    /// <summary>
    /// Watches the scanned directories and raises <see cref="AssemblyWatcher.Changed"/> when the set of
    /// matching assemblies changes.
    /// </summary>
    /// <param name="debounce">How long to wait after the last file event before rescanning. Defaults to 500 ms.</param>
    /// <exception cref="InvalidOperationException">The scanner has sources but none of them is a directory.</exception>
    /// <exception cref="ArgumentException">A directory to watch does not exist.</exception>
    public AssemblyWatcher Watch(TimeSpan? debounce = null)
    {
        List<DirectorySource> directories = [.. _sources.OfType<DirectorySource>()];
        if (_sources.Count == 0)
            directories.Add(new DirectorySource(Path.GetFullPath(AppContext.BaseDirectory), false));

        if (directories.Count == 0)
            throw new InvalidOperationException("Only directories can be watched. Add one with FromDirectory.");

        return new AssemblyWatcher(this, directories.Select(d => (d.Path, d.Recursive)).ToList(), debounce ?? TimeSpan.FromMilliseconds(500));
    }

    // ---------------------------------------------------------- implementation

    internal static string NormalizeFramework(string framework)
    {
        string text = framework.Trim();
        if (text.StartsWith('.'))
            return text;

        string lower = text.ToLowerInvariant();
        if (lower.StartsWith("netstandard", StringComparison.Ordinal))
            return ".NETStandard,Version=v" + lower["netstandard".Length..];

        if (lower.StartsWith("netcoreapp", StringComparison.Ordinal))
            return ".NETCoreApp,Version=v" + lower["netcoreapp".Length..];

        if (lower.StartsWith("net", StringComparison.Ordinal) && lower.Length > 3 && char.IsDigit(lower[3]))
        {
            string version = lower[3..];
            int platform = version.IndexOf('-');
            if (platform >= 0)
                version = version[..platform];

            // net8.0 is .NET (Core); net48 and net472 are .NET Framework 4.8 and 4.7.2.
            return version.Contains('.')
                ? ".NETCoreApp,Version=v" + version
                : ".NETFramework,Version=v" + string.Join('.', version.ToCharArray());
        }

        return text;
    }

    private static void Collect(Outcome outcome, HashSet<string> seen, List<AssemblyDescriptor> accepted, List<ScanDiagnostic> diagnostics)
    {
        if (outcome.Descriptor is null)
        {
            diagnostics.Add(outcome.Diagnostic!);
        }
        else if (!seen.Add(outcome.Descriptor.IdentityKey))
        {
            diagnostics.Add(new ScanDiagnostic(
                outcome.Descriptor.Path ?? outcome.Descriptor.FileName,
                SkipReason.Duplicate,
                $"The same build of '{outcome.Descriptor.Name}' was already found elsewhere."));
        }
        else
        {
            accepted.Add(outcome.Descriptor);
        }
    }

    private AssemblyScanner AddPatterns(string[] patterns, PatternSyntax syntax, bool exclude)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (string text in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            NamePattern pattern = NamePattern.Parse(text, syntax);
            if (exclude)
                pattern = pattern.AsExclusion();

            (pattern.IsExclusion ? _excludes : _includes).Add(pattern);
        }

        return this;
    }

    private Outcome Evaluate(Candidate candidate)
    {
        string? mismatch = ExplainPatternMismatch(candidate);
        if (mismatch is not null)
            return Outcome.Skipped(candidate, SkipReason.ExcludedByPattern, mismatch);

        InspectionResult inspection = candidate.Assembly is not null
            ? MetadataInspector.InspectLoaded(candidate.Assembly)
            : ScanCache.Inspect(candidate.Path!, _useCache);

        if (inspection.Snapshot is null)
        {
            SkipReason reason = inspection.Status switch
            {
                InspectionStatus.NotManaged => SkipReason.NotManaged,
                InspectionStatus.NotAnAssembly => SkipReason.NotAnAssembly,
                _ => SkipReason.Unreadable
            };
            return Outcome.Skipped(candidate, reason, inspection.Message ?? "The file could not be inspected.", inspection.Exception);
        }

        var descriptor = new AssemblyDescriptor(inspection.Snapshot, candidate.Path, candidate.FileName, candidate.RelativePath, candidate.Source, candidate.Assembly);

        foreach (NamedFilter filter in _filters)
        {
            try
            {
                string? rejection = filter.Reject(descriptor);
                if (rejection is not null)
                    return Outcome.Skipped(candidate, filter.Reason, rejection);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                return Outcome.Skipped(candidate, SkipReason.Unreadable, $"Filter '{filter.Name}' could not read the file: {ex.Message}", ex);
            }
        }

        return new Outcome(descriptor, null);
    }

    private string? ExplainPatternMismatch(Candidate candidate)
    {
        foreach (NamePattern exclude in _excludes)
        {
            if (exclude.IsMatch(candidate.FileName, candidate.RelativePath))
                return $"Matched exclusion pattern '{exclude.Text}'.";
        }

        if (_includes.Count == 0)
            return null;

        foreach (NamePattern include in _includes)
        {
            if (include.IsMatch(candidate.FileName, candidate.RelativePath))
                return null;
        }

        return "Did not match any include pattern.";
    }

    private List<Candidate> CollectCandidates(List<ScanDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var seenPaths = new HashSet<string>(HostAssemblies.PathComparer);

        IReadOnlyList<Source> sources = _sources.Count > 0
            ? _sources
            : new Source[] { new DirectorySource(Path.GetFullPath(AppContext.BaseDirectory), false) };

        foreach (Source source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (source)
            {
                case DirectorySource directory:
                    AddDirectory(directory, candidates, seenPaths, diagnostics);
                    break;

                case FileSource file:
                    if (!File.Exists(file.Path))
                        diagnostics.Add(new ScanDiagnostic(file.Path, SkipReason.SourceNotFound, "The file does not exist."));
                    else if (seenPaths.Add(file.Path))
                        candidates.Add(Candidate.ForFile(file.Path, Path.GetFileName(file.Path), AssemblySource.File));
                    break;

                case LoadedSource loaded:
                    foreach (Assembly assembly in loaded.Assemblies())
                        AddLoaded(assembly, AssemblySource.Loaded, candidates, seenPaths);
                    break;

                case ClosureSource closure:
                    AddClosure(closure, candidates, seenPaths);
                    break;
            }
        }

        return candidates;
    }

    private static void AddDirectory(DirectorySource directory, List<Candidate> candidates, HashSet<string> seenPaths, List<ScanDiagnostic> diagnostics)
    {
        if (!Directory.Exists(directory.Path))
        {
            diagnostics.Add(new ScanDiagnostic(directory.Path, SkipReason.SourceNotFound, "The directory does not exist."));
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = directory.Recursive,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        string[] files = Directory.GetFiles(directory.Path, "*.dll", options);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files.Where(seenPaths.Add))
            candidates.Add(Candidate.ForFile(file, Path.GetRelativePath(directory.Path, file).Replace('\\', '/'), AssemblySource.File));
    }

    private static void AddLoaded(Assembly assembly, AssemblySource source, List<Candidate> candidates, HashSet<string> seenPaths)
    {
        string? path = string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
        if (path is not null && !seenPaths.Add(path))
            return;

        string fileName = path is not null ? Path.GetFileName(path) : (assembly.GetName().Name ?? "unknown") + ".dll";
        candidates.Add(new Candidate(path, fileName, fileName, source, assembly));
    }

    private void AddClosure(ClosureSource closure, List<Candidate> candidates, HashSet<string> seenPaths)
    {
        AddLoaded(closure.Root, AssemblySource.DependencyClosure, candidates, seenPaths);

        InspectionResult root = MetadataInspector.InspectLoaded(closure.Root);
        if (root.Snapshot is null)
            return;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Snapshot.Name };
        var pending = new Queue<AssemblySnapshot>();
        pending.Enqueue(root.Snapshot);

        while (pending.Count > 0)
        {
            foreach (AssemblyReferenceInfo reference in pending.Dequeue().References.Where(r => visited.Add(r.Name)))
            {
                string? path = HostAssemblies.ResolvePath(reference.Name);
                if (path is null)
                    continue;

                if (!closure.IncludeFramework && FrameworkAssemblies.IsFramework(reference.Name, reference.PublicKeyToken, path))
                    continue;

                InspectionResult inspection = ScanCache.Inspect(path, _useCache);
                if (inspection.Snapshot is null)
                    continue;

                if (seenPaths.Add(path))
                    candidates.Add(Candidate.ForFile(path, Path.GetFileName(path), AssemblySource.DependencyClosure));

                pending.Enqueue(inspection.Snapshot);
            }
        }
    }

    private abstract record Source;

    private sealed record DirectorySource(string Path, bool Recursive) : Source;

    private sealed record FileSource(string Path) : Source;

    private sealed record LoadedSource(Func<IEnumerable<Assembly>> Assemblies) : Source;

    private sealed record ClosureSource(Assembly Root, bool IncludeFramework) : Source;

    /// <summary>A filter that returns <c>null</c> to keep an assembly, or the reason it is left out.</summary>
    private sealed record NamedFilter(string Name, Func<AssemblyDescriptor, string?> Reject, SkipReason Reason);

    private sealed record Candidate(string? Path, string FileName, string RelativePath, AssemblySource Source, Assembly? Assembly)
    {
        public static Candidate ForFile(string path, string relativePath, AssemblySource source)
            => new(path, System.IO.Path.GetFileName(path), relativePath, source, null);
    }

    private readonly record struct Outcome(AssemblyDescriptor? Descriptor, ScanDiagnostic? Diagnostic)
    {
        public static Outcome Skipped(Candidate candidate, SkipReason reason, string message, Exception? exception = null)
            => new(null, new ScanDiagnostic(candidate.Path ?? candidate.FileName, reason, message, exception));
    }
}
