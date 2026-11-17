using System.Text;
using System.Text.RegularExpressions;

namespace Masterly.AssembliesScanner;

/// <summary>The syntax a <see cref="NamePattern"/> is written in.</summary>
public enum PatternSyntax
{
    /// <summary>File-system style wildcards: <c>*</c>, <c>?</c> and <c>**</c>. The whole name must match.</summary>
    Glob,

    /// <summary>A .NET regular expression, matched anywhere in the file name.</summary>
    Regex,

    /// <summary>The exact file name or assembly name, ignoring case.</summary>
    Exact
}

/// <summary>
/// A case-insensitive pattern that selects assemblies by file name.
/// </summary>
/// <remarks>
/// <para>
/// Text form: an optional leading <c>!</c> marks an exclusion, and an optional <c>glob:</c>,
/// <c>regex:</c> or <c>exact:</c> prefix selects the syntax, for example <c>!regex:\.Tests\.dll$</c>.
/// </para>
/// <para>
/// A glob or exact pattern is tried against the file name with and without its <c>.dll</c> or <c>.exe</c>
/// extension, so <c>Contoso.*</c>, <c>Contoso.Core</c> and <c>Contoso.Core.dll</c> all select
/// <c>Contoso.Core.dll</c>. A glob that contains a directory separator is matched against the path
/// relative to the scanned directory instead, for example <c>plugins/**/*.dll</c>.
/// </para>
/// <para>
/// Matching one name is limited to two seconds, so a pathological regular expression raises
/// <see cref="RegexMatchTimeoutException"/> instead of hanging the scan.
/// </para>
/// </remarks>
public sealed class NamePattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private readonly Regex? _regex;
    private readonly bool _matchesPath;

    private NamePattern(string text, PatternSyntax syntax, bool isExclusion)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A name pattern cannot be empty.", nameof(text));

        Text = text;
        Syntax = syntax;
        IsExclusion = isExclusion;

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        switch (syntax)
        {
            case PatternSyntax.Regex:
                _regex = new Regex(text, options, MatchTimeout);
                break;
            case PatternSyntax.Glob:
                _matchesPath = text.Contains('/') || text.Contains('\\');
                _regex = new Regex(GlobToRegex(text), options, MatchTimeout);
                break;
        }
    }

    /// <summary>The pattern text, without the <c>!</c> marker and the syntax prefix.</summary>
    public string Text { get; }

    /// <summary>The syntax of <see cref="Text"/>.</summary>
    public PatternSyntax Syntax { get; }

    /// <summary>Whether a match removes the assembly from the result instead of selecting it.</summary>
    public bool IsExclusion { get; }

    /// <summary>Creates a glob pattern.</summary>
    public static NamePattern Glob(string pattern) => new(pattern, PatternSyntax.Glob, false);

    /// <summary>Creates a regular expression pattern.</summary>
    public static NamePattern Regex(string pattern) => new(pattern, PatternSyntax.Regex, false);

    /// <summary>Creates a pattern that matches one exact file name or assembly name.</summary>
    public static NamePattern Exact(string name) => new(name, PatternSyntax.Exact, false);

    /// <summary>
    /// Parses the text form of a pattern, including the optional <c>!</c> marker and syntax prefix.
    /// </summary>
    /// <param name="pattern">The pattern text.</param>
    /// <param name="defaultSyntax">The syntax to use when the text carries no prefix.</param>
    /// <exception cref="ArgumentException">The pattern is empty or is not a valid regular expression.</exception>
    public static NamePattern Parse(string pattern, PatternSyntax defaultSyntax = PatternSyntax.Glob)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        string text = pattern.Trim();
        bool exclusion = false;
        if (text.StartsWith('!'))
        {
            exclusion = true;
            text = text[1..].TrimStart();
        }

        PatternSyntax syntax = defaultSyntax;
        if (TryStripPrefix(ref text, "regex:"))
            syntax = PatternSyntax.Regex;
        else if (TryStripPrefix(ref text, "glob:"))
            syntax = PatternSyntax.Glob;
        else if (TryStripPrefix(ref text, "exact:"))
            syntax = PatternSyntax.Exact;

        return new NamePattern(text, syntax, exclusion);
    }

    /// <summary>Returns the same pattern as an exclusion.</summary>
    public NamePattern AsExclusion() => IsExclusion ? this : new NamePattern(Text, Syntax, true);

    /// <summary>Tests the pattern against a file name.</summary>
    /// <param name="fileName">The file name including its extension, for example <c>Contoso.Core.dll</c>.</param>
    /// <param name="relativePath">The path relative to the scanned directory, used by globs that contain a directory separator.</param>
    public bool IsMatch(string fileName, string? relativePath = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        switch (Syntax)
        {
            case PatternSyntax.Exact:
                return string.Equals(fileName, Text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(StripExtension(fileName), Text, StringComparison.OrdinalIgnoreCase);

            case PatternSyntax.Regex:
                return _regex!.IsMatch(fileName);

            default:
                string candidate = _matchesPath ? (relativePath ?? fileName).Replace('\\', '/') : fileName;
                return _regex!.IsMatch(candidate) || _regex.IsMatch(StripExtension(candidate));
        }
    }

    /// <inheritdoc />
    public override string ToString()
        => (IsExclusion ? "!" : string.Empty) + Syntax.ToString().ToLowerInvariant() + ":" + Text;

    internal static string StripExtension(string fileName)
        => fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

    private static bool TryStripPrefix(ref string text, string prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        text = text[prefix.Length..];
        return true;
    }

    private static string GlobToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        while (i + 1 < glob.Length && glob[i + 1] == '*')
                            i++;

                        if (i + 1 < glob.Length && (glob[i + 1] == '/' || glob[i + 1] == '\\'))
                        {
                            i++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '/':
                case '\\':
                    builder.Append('/');
                    break;
                default:
                    builder.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }
}
