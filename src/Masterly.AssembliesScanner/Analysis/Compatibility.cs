using System.Collections;
using System.Text;

namespace Masterly.AssembliesScanner;

/// <summary>What would go wrong at run time. Each kind names the exception the runtime would throw.</summary>
public enum CompatibilityIssueKind
{
    /// <summary>A referenced assembly cannot be found. The runtime throws <see cref="FileNotFoundException"/>.</summary>
    MissingAssembly,

    /// <summary>
    /// A referenced assembly is found, but in an older version than the one compiled against.
    /// The runtime throws <see cref="FileLoadException"/>.
    /// </summary>
    AssemblyVersionTooLow,

    /// <summary>A used type no longer exists in its assembly. The runtime throws <see cref="TypeLoadException"/>.</summary>
    MissingType,

    /// <summary>A used type is no longer public. The runtime throws <see cref="TypeAccessException"/>.</summary>
    TypeNotAccessible,

    /// <summary>
    /// A called method or constructor no longer exists with that signature.
    /// The runtime throws <see cref="MissingMethodException"/>.
    /// </summary>
    MissingMethod,

    /// <summary>A used field no longer exists with that type. The runtime throws <see cref="MissingFieldException"/>.</summary>
    MissingField,

    /// <summary>
    /// A used method or field is no longer accessible from outside its assembly.
    /// The runtime throws <see cref="MethodAccessException"/> or <see cref="FieldAccessException"/>.
    /// </summary>
    MemberNotAccessible,

    /// <summary>
    /// A class does not implement an interface member or abstract method that the other assembly now declares.
    /// The runtime throws <see cref="TypeLoadException"/> when it loads the class.
    /// </summary>
    MissingImplementation,

    /// <summary>
    /// A class derives from a type that is now sealed or an interface, or implements a type that is no longer an
    /// interface. The runtime throws <see cref="TypeLoadException"/> when it loads the class.
    /// </summary>
    InvalidInheritance
}

/// <summary>One thing that would fail if the assembly were loaded and run against what the host provides.</summary>
public sealed class CompatibilityIssue
{
    internal CompatibilityIssue(
        AssemblyDescriptor assembly,
        CompatibilityIssueKind kind,
        string targetAssembly,
        string member,
        string message,
        Type expectedException,
        IReadOnlyList<string> usedBy)
    {
        Assembly = assembly;
        Kind = kind;
        TargetAssembly = targetAssembly;
        Member = member;
        Message = message;
        ExpectedException = expectedException;
        UsedBy = usedBy;
    }

    /// <summary>The checked assembly that has the problem.</summary>
    public AssemblyDescriptor Assembly { get; }

    /// <summary>What kind of problem it is.</summary>
    public CompatibilityIssueKind Kind { get; }

    /// <summary>The simple name of the assembly that was expected to provide the missing piece.</summary>
    public string TargetAssembly { get; }

    /// <summary>The assembly, type or member in question, for example <c>Contoso.IHost.Log(System.String)</c>.</summary>
    public string Member { get; }

    /// <summary>A sentence that explains the problem and, where possible, what is there instead.</summary>
    public string Message { get; }

    /// <summary>The exception the runtime would throw.</summary>
    public Type ExpectedException { get; }

    /// <summary>
    /// The methods (<c>Namespace.Type.Method</c>) and types in the checked assembly that use the missing piece,
    /// found by reading their IL. Code that never runs those methods is not affected.
    /// </summary>
    public IReadOnlyList<string> UsedBy { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Assembly.Name}: {Kind} {Member} - {Message}";
}

/// <summary>Where <see cref="ScanResult.CheckCompatibility"/> looks for the assemblies that checked assemblies reference.</summary>
public sealed class CompatibilityOptions
{
    /// <summary>
    /// Whether a referenced assembly is taken from the host application first, before the scanned set and the
    /// folder of the referencing assembly. Defaults to <c>true</c>, which is what loading into the default context
    /// or an isolated context with <see cref="IsolationOptions.PreferSharedTypes"/> does. Set it to <c>false</c> to
    /// check plugins that bring private copies of their dependencies.
    /// </summary>
    public bool PreferHostAssemblies { get; set; } = true;

    /// <summary>More directories to look in for referenced assemblies, after the folder of the referencing assembly.</summary>
    public IList<string> ProbingDirectories { get; } = [];

    /// <summary>
    /// Whether uses of the .NET base class library are checked too. Defaults to <c>true</c>, which catches an
    /// assembly built for a newer runtime than the one running. Turn it off to check only your own contracts.
    /// </summary>
    public bool CheckFrameworkReferences { get; set; } = true;

    /// <summary>
    /// Whether assemblies of the base class library are themselves checked when they are part of the result.
    /// Defaults to <c>false</c>: the runtime ships them as a consistent set.
    /// </summary>
    public bool CheckFrameworkAssemblies { get; set; }
}

/// <summary>
/// The outcome of <see cref="ScanResult.CheckCompatibility"/>: every type and member the checked assemblies use from
/// other assemblies that the host would not be able to provide, found from metadata without loading anything.
/// </summary>
public sealed class CompatibilityReport : IReadOnlyList<CompatibilityIssue>
{
    private readonly IReadOnlyList<CompatibilityIssue> _issues;

    internal CompatibilityReport(IReadOnlyList<AssemblyDescriptor> checkedAssemblies, IReadOnlyList<AssemblyDescriptor> skipped, IReadOnlyList<CompatibilityIssue> issues)
    {
        CheckedAssemblies = checkedAssemblies;
        Skipped = skipped;
        _issues = issues;
    }

    /// <summary>The assemblies that were checked.</summary>
    public IReadOnlyList<AssemblyDescriptor> CheckedAssemblies { get; }

    /// <summary>
    /// Assemblies that could not be checked because they have no file to read, or that were left out because they
    /// belong to the base class library (see <see cref="CompatibilityOptions.CheckFrameworkAssemblies"/>).
    /// </summary>
    public IReadOnlyList<AssemblyDescriptor> Skipped { get; }

    /// <summary>Whether no problem was found.</summary>
    public bool IsCompatible => _issues.Count == 0;

    /// <summary>The checked assemblies without problems.</summary>
    public IReadOnlyList<AssemblyDescriptor> CompatibleAssemblies => [.. CheckedAssemblies.Where(a => !_issues.Any(i => ReferenceEquals(i.Assembly, a)))];

    /// <summary>The checked assemblies with at least one problem.</summary>
    public IReadOnlyList<AssemblyDescriptor> IncompatibleAssemblies => [.. CheckedAssemblies.Where(a => _issues.Any(i => ReferenceEquals(i.Assembly, a)))];

    /// <inheritdoc />
    public int Count => _issues.Count;

    /// <inheritdoc />
    public CompatibilityIssue this[int index] => _issues[index];

    /// <inheritdoc />
    public IEnumerator<CompatibilityIssue> GetEnumerator() => _issues.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The problems of one assembly.</summary>
    public IReadOnlyList<CompatibilityIssue> For(AssemblyDescriptor assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return [.. _issues.Where(i => ReferenceEquals(i.Assembly, assembly))];
    }

    /// <summary>The problems of the assembly with the given simple name or file name, ignoring case.</summary>
    public IReadOnlyList<CompatibilityIssue> For(string nameOrFileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameOrFileName);
        return [.. _issues.Where(i => string.Equals(i.Assembly.Name, nameOrFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.Assembly.FileName, nameOrFileName, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Throws when any problem was found, listing the problems in the message.</summary>
    /// <exception cref="InvalidOperationException">At least one checked assembly is not compatible.</exception>
    public void ThrowIfIncompatible()
    {
        if (IsCompatible)
            return;

        var message = new StringBuilder()
            .Append(IncompatibleAssemblies.Count).Append(" assembl").Append(IncompatibleAssemblies.Count == 1 ? "y is" : "ies are")
            .Append(" not compatible with what the host provides:");
        foreach (CompatibilityIssue issue in _issues)
            message.AppendLine().Append("  ").Append(issue);

        throw new InvalidOperationException(message.ToString());
    }

    /// <summary>Writes the report as Markdown: one table row per problem.</summary>
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Compatibility check");
        builder.AppendLine();
        builder.AppendLine($"{CheckedAssemblies.Count} checked, {IncompatibleAssemblies.Count} with problems, {_issues.Count} problems.");

        if (_issues.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("| Assembly | Problem | Member | Would throw | Used by |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (CompatibilityIssue issue in _issues)
            {
                string usedBy = issue.UsedBy.Count == 0 ? "-" : string.Join("<br>", issue.UsedBy.Select(Escape));
                builder.AppendLine($"| {issue.Assembly.Name} | {issue.Kind} | {Escape(issue.Member)} | {issue.ExpectedException.Name} | {usedBy} |");
            }
        }

        return builder.ToString();
    }

    // Generic names such as List`1<T> would otherwise be read as HTML tags.
    private static string Escape(string text) => text.Replace("|", "\\|").Replace("<", "&lt;").Replace(">", "&gt;");
}
