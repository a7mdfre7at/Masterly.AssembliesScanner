2.0.0

- Targets .NET 8 and .NET 10 (stay on 1.0.0 for older frameworks).
- New fluent scanner, Assemblies.Scan(): metadata-only scanning with glob and regex patterns, metadata and type filters, and diagnostics for everything left out.
- Isolated, unloadable loading and a plugin loader with constructor injection.
- Fluent type discovery and convention- or attribute-based dependency injection registration.
- Binary compatibility check that predicts MissingMethodException, TypeLoadException and the like before loading.
- Dependency graph, hot-reload watcher, JSON and Markdown reports, hash allow-lists.
- Assemblies.Get is unchanged.

Full list: https://github.com/a7mdfre7at/Masterly.AssembliesScanner/blob/master/CHANGELOG.md
