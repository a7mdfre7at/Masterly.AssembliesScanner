using System.Reflection;
using System.Text.RegularExpressions;

namespace Masterly.AssembliesScanner;

/// <summary>
/// The entry point of the library. <see cref="Scan()"/> starts a metadata-only scan with the full
/// feature set; <see cref="Get(string[])"/> is the original one-call API, kept unchanged.
/// </summary>
public static class Assemblies
{
    /// <summary>
    /// Starts a scan. Nothing is read until <see cref="AssemblyScanner.Scan"/> is called, and nothing
    /// is loaded until you ask the result to load.
    /// </summary>
    public static AssemblyScanner Scan() => new();

    /// <summary>
    /// Starts a scan of the application base directory for assemblies whose file name matches the glob
    /// patterns. A pattern that starts with <c>!</c> is an exclusion.
    /// </summary>
    /// <param name="patterns">Glob patterns such as <c>Contoso.*</c>. See <see cref="NamePattern"/>.</param>
    public static AssemblyScanner Scan(params string[] patterns) => new AssemblyScanner().Include(patterns);

    /// <summary>Starts a type query over assemblies that are already loaded.</summary>
    public static TypeQuery TypesIn(params Assembly[] assemblies) => new(assemblies);

    /// <summary>
    /// Get all assemblies in the current App Domain according to assembly name patterns.
    /// </summary>
    /// <remarks>
    /// This is the version 1 API. It treats patterns as regular expressions and loads every DLL in the
    /// application base directory before filtering. Prefer <see cref="Scan()"/> for new code.
    /// </remarks>
    /// <param name="assemblyPatterns">Name pattern to scan</param>
    /// <returns>Array of matched assemblies</returns>
    public static Assembly[] Get(params string[] assemblyPatterns)
    {
        if (!assemblyPatterns.Any())
            return [];

        string[] assemblyExcludePatterns = GetAssemblyExcludePatterns(assemblyPatterns);

        IEnumerable<Assembly> allAssemblies = GetAllAssemblies();

        var referencedProjectAssemblies = new List<Assembly>();

        foreach (string pattern in assemblyPatterns)
        {
            if (string.IsNullOrEmpty(pattern.Trim()))
                continue;

            List<Assembly> matchedAssemblies = GetMatchedAssemblies(allAssemblies, pattern);

            if (matchedAssemblies?.Count > 0)
                referencedProjectAssemblies.AddRange(matchedAssemblies);
        }

        foreach (var pattern in assemblyExcludePatterns)
            referencedProjectAssemblies.RemoveAll(a => Regex.IsMatch(a.ManifestModule.Name.ToLower(), pattern.ToLower()));

        return [.. referencedProjectAssemblies];
    }

    /// <summary>
    /// Get all assemblies in the current App Domain according to assembly name patterns.
    /// </summary>
    /// <param name="assemblyPatterns">Name pattern to scan</param>
    /// <returns>Array of matched assemblies</returns>
    public static Assembly[] Get(IEnumerable<string> assemblyPatterns)
    {
        if (!assemblyPatterns.Any())
            return [];

        return Get([.. assemblyPatterns]);
    }

    private static List<Assembly> GetMatchedAssemblies(IEnumerable<Assembly> allAssemblies, string pattern) 
        => [.. allAssemblies.Where(a => Regex.IsMatch(a.ManifestModule.Name.ToLower(), pattern.ToLower()))];

    private static List<Assembly> GetAllAssemblies()
        => [.. Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll").Select(x => Assembly.Load(AssemblyName.GetAssemblyName(x)))];

    private static string[] GetAssemblyExcludePatterns(string[] assemblyPatterns) 
        => [.. assemblyPatterns.Where(a => a.StartsWith("!")).Select(a => a.Replace("!", string.Empty).Trim())];
}