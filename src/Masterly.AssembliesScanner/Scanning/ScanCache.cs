using System.Collections.Concurrent;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner;

/// <summary>
/// The process-wide cache of assembly metadata. An entry is reused for as long as the file keeps the same
/// size and last-write time, so repeated scans of an unchanged folder do not reopen any file.
/// </summary>
public static class ScanCache
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(HostAssemblies.PathComparer);

    /// <summary>The number of files whose metadata is cached.</summary>
    public static int Count => Entries.Count;

    /// <summary>Removes every cached entry, including the metadata kept for compatibility checks.</summary>
    public static void Clear()
    {
        Entries.Clear();
        MetadataModule.ClearShared();
    }

    internal static InspectionResult Inspect(string path, bool useCache)
    {
        if (!useCache)
            return MetadataInspector.InspectFile(path);

        long length;
        DateTime lastWrite;
        try
        {
            var file = new FileInfo(path);
            length = file.Length;
            lastWrite = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new InspectionResult(InspectionStatus.Unreadable, null, "The file could not be read: " + ex.Message, ex);
        }

        if (Entries.TryGetValue(path, out Entry? entry) && entry.Length == length && entry.LastWriteTimeUtc == lastWrite)
            return entry.Result;

        InspectionResult result = MetadataInspector.InspectFile(path);

        // An unreadable file is usually locked or half-written; do not remember that.
        if (result.Status != InspectionStatus.Unreadable)
            Entries[path] = new Entry(length, lastWrite, result);

        return result;
    }

    private sealed record Entry(long Length, DateTime LastWriteTimeUtc, InspectionResult Result);
}
