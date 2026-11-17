using System.Runtime.Loader;

namespace Masterly.AssembliesScanner.Internal;

/// <summary>What the host application itself can resolve: its trusted platform assemblies and base directory.</summary>
internal static class HostAssemblies
{
    private static readonly Lazy<Dictionary<string, string>> Trusted = new(ReadTrustedPlatformAssemblies);

    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Finds the file the host would load for an assembly simple name, without loading it.</summary>
    public static string? ResolvePath(string simpleName)
    {
        if (Trusted.Value.TryGetValue(simpleName, out string? path))
            return path;

        string candidate = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Finds the file the host would use for an assembly simple name: a trusted platform assembly, a file in the
    /// base directory, or the file of an assembly the default load context already loaded from elsewhere.
    /// </summary>
    public static string? FindFile(string simpleName)
    {
        string? path = ResolvePath(simpleName);
        if (path is not null)
            return path;

        foreach (System.Reflection.Assembly assembly in AssemblyLoadContext.Default.Assemblies)
        {
            if (!assembly.IsDynamic
                && assembly.Location.Length > 0
                && string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return assembly.Location;
            }
        }

        return null;
    }

    /// <summary>Whether the default load context already has, or can find, an assembly with this simple name.</summary>
    public static bool CanProvide(string simpleName)
    {
        if (Trusted.Value.ContainsKey(simpleName))
            return true;

        foreach (System.Reflection.Assembly assembly in AssemblyLoadContext.Default.Assemblies)
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static Dictionary<string, string> ReadTrustedPlatformAssemblies()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list)
        {
            foreach (string path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                map.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }

        return map;
    }
}

/// <summary>Heuristics that recognise the .NET base class library and other Microsoft-signed assemblies.</summary>
internal static class FrameworkAssemblies
{
    private static readonly HashSet<string> MicrosoftTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "b03f5f7f11d50a3a", "b77a5c561934e089", "31bf3856ad364e35",
        "cc7b13ffcd2ddd51", "7cec85d7bea7798e", "adb9793829ddae60",
        "0a613f4dd989e8ae" // Microsoft.Identity.Client (MSAL)
    };

    private static readonly string? SharedFrameworkRoot = FindSharedFrameworkRoot();

    public static bool IsFramework(string name, string? publicKeyToken, string? path)
    {
        if (path is not null && SharedFrameworkRoot is not null && path.StartsWith(SharedFrameworkRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsMicrosoftSigned(publicKeyToken) && HasFrameworkName(name);
    }

    public static bool IsMicrosoft(string name, string? publicKeyToken, string? path)
        => IsFramework(name, publicKeyToken, path)
        || (IsMicrosoftSigned(publicKeyToken) && name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase));

    private static bool IsMicrosoftSigned(string? publicKeyToken)
        => publicKeyToken is not null && MicrosoftTokens.Contains(publicKeyToken);

    private static bool HasFrameworkName(string name)
        => name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.NETCore.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)
        || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
        || name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Microsoft.CSharp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the <c>dotnet/shared</c> directory for framework-dependent apps, or <c>null</c> for
    /// self-contained and single-file apps, where the runtime sits next to the application.
    /// </summary>
    private static string? FindSharedFrameworkRoot()
    {
        string location = typeof(object).Assembly.Location;
        if (string.IsNullOrEmpty(location))
            return null;

        DirectoryInfo? version = new FileInfo(location).Directory;
        DirectoryInfo? framework = version?.Parent;
        DirectoryInfo? shared = framework?.Parent;
        if (framework is null || shared is null)
            return null;

        bool isSharedLayout = framework.Name.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)
            && shared.Name.Equals("shared", StringComparison.OrdinalIgnoreCase);

        return isSharedLayout ? shared.FullName + Path.DirectorySeparatorChar : null;
    }
}
