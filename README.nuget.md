# Masterly.AssembliesScanner

![Masterly.AssembliesScanner](https://raw.githubusercontent.com/a7mdfre7at/Masterly.AssembliesScanner/master/assets/branding/banner.png)

Find, inspect, check and load .NET assemblies, without loading them first.

```c#
using Masterly.AssembliesScanner;

// Read metadata only: nothing is loaded, native DLLs are skipped and reported.
ScanResult scan = Assemblies.Scan()
    .FromDirectory("plugins", recursive: true)
    .Include("Contoso.*", "!*.Tests")
    .Referencing<IPlugin>()
    .RequireCompatibility()   // leave out plugins that would throw MissingMethodException & co.
    .Scan();

// Then load what you chose: into the host, or isolated and unloadable.
using PluginSet<IPlugin> plugins = scan.LoadPlugins<IPlugin>();
```

- Glob and regex patterns, metadata filters (version, target framework, references, attributes, hashes) and type filters that need no loading
- Diagnostics that explain why a file was left out
- Isolated, unloadable load contexts and a plugin loader with constructor injection
- Fluent type discovery and convention- or attribute-based `IServiceCollection` registration
- Binary compatibility check: predicts `MissingMethodException`, `TypeLoadException` and friends from metadata, and names the methods that would throw
- Dependency graph, hot-reload watcher, JSON and Markdown reports

Targets .NET 8 and .NET 10. The version 1 API, `Assemblies.Get`, is unchanged.

See the [documentation](https://github.com/a7mdfre7at/Masterly.AssembliesScanner#readme) on GitHub.
