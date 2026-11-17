namespace Masterly.AssembliesScanner.Internal;

/// <summary>The immutable, cacheable part of what was read from an assembly's metadata.</summary>
internal sealed class AssemblySnapshot
{
    public required string Name { get; init; }
    public required Version Version { get; init; }
    public required string Culture { get; init; }
    public string? PublicKeyToken { get; init; }
    public required string FullName { get; init; }
    public Guid Mvid { get; init; }
    public string? TargetFramework { get; init; }
    public string? InformationalVersion { get; init; }
    public string? FileVersion { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Configuration { get; init; }
    public bool IsReferenceAssembly { get; init; }
    public required IReadOnlyList<AssemblyReferenceInfo> References { get; init; }
    public required IReadOnlyList<string> AttributeNames { get; init; }
    public required IReadOnlyDictionary<string, string?> Metadata { get; init; }
    public required IReadOnlyList<string> ResourceNames { get; init; }
    public required Lazy<IReadOnlyList<TypeDescriptor>> Types { get; init; }
}

internal enum InspectionStatus
{
    Ok,
    NotManaged,
    NotAnAssembly,
    Unreadable
}

internal sealed record InspectionResult(
    InspectionStatus Status,
    AssemblySnapshot? Snapshot,
    string? Message = null,
    Exception? Exception = null);
