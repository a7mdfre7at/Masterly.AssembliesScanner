namespace Masterly.AssembliesScanner;

/// <summary>Why a file or assembly did not make it into a result.</summary>
public enum SkipReason
{
    /// <summary>The file is a native image or is not a valid PE file.</summary>
    NotManaged,

    /// <summary>The file has CLI metadata but no assembly manifest, for example a netmodule.</summary>
    NotAnAssembly,

    /// <summary>The file could not be opened or read.</summary>
    Unreadable,

    /// <summary>A directory or file given as a scan source does not exist.</summary>
    SourceNotFound,

    /// <summary>The file name matched an exclusion pattern or no inclusion pattern.</summary>
    ExcludedByPattern,

    /// <summary>A metadata filter rejected the assembly.</summary>
    ExcludedByFilter,

    /// <summary>The same assembly (same identity and module version id) was already found elsewhere.</summary>
    Duplicate,

    /// <summary>The assembly was selected but could not be loaded.</summary>
    LoadFailed,

    /// <summary>A plugin type was found but could not be instantiated.</summary>
    ActivationFailed,

    /// <summary>
    /// The assembly uses a type or member that the host would not provide, so it would fail at run time.
    /// See <see cref="AssemblyScanner.RequireCompatibility"/>.
    /// </summary>
    Incompatible
}

/// <summary>Records one file or assembly that a scan, load or plugin activation left out, and why.</summary>
public sealed class ScanDiagnostic
{
    internal ScanDiagnostic(string path, SkipReason reason, string message, Exception? exception = null)
    {
        Path = path;
        Reason = reason;
        Message = message;
        Exception = exception;
    }

    /// <summary>The file path, or the assembly or type name when there is no file.</summary>
    public string Path { get; }

    /// <summary>The category of the problem.</summary>
    public SkipReason Reason { get; }

    /// <summary>A human-readable explanation.</summary>
    public string Message { get; }

    /// <summary>The exception behind the problem, when there was one.</summary>
    public Exception? Exception { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Reason}: {Path} - {Message}";
}
