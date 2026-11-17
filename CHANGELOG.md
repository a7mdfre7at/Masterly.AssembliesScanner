## 2.0.0

Version 2 turns the single `Assemblies.Get` helper into a full assembly scanner. The new entry point is `Assemblies.Scan()`; `Assemblies.Get` is still there and behaves exactly as before.

### Breaking changes

- The package now targets **.NET 8** and **.NET 10**. The `netstandard2.0`, `net462`, `net5.0` and `netcoreapp2.0` targets are gone; stay on 1.0.0 for those.
- New dependency: `Microsoft.Extensions.DependencyInjection.Abstractions` 11.0.0. On .NET 8 that package warns at build time that it is not tested there; set `SuppressTfmSupportBuildWarnings` to `true` in your project to silence it.

### Added

**Scanning**

- Fluent scanner: `Assemblies.Scan()`, with `Assemblies.Scan("Contoso.*", "!*.Tests")` as a shortcut.
- Metadata-only scanning: nothing is loaded until you ask. Native DLLs and broken files are skipped and reported instead of throwing.
- Glob, regular expression and exact name patterns, `!` exclusions, and path globs such as `plugins/**/*.dll`.
- Sources: directories (optionally recursive), files, loaded assemblies, and `FromDependencyClosure`, which follows references to assemblies the runtime has not loaded yet.
- `AssemblyDescriptor`: version, culture, public key token, target framework, informational and file version, company, product, `AssemblyMetadata` pairs, references, resources, declared types, MVID, file size and SHA-256.
- Metadata filters: version range, target framework, referenced assemblies, assembly attributes, `AssemblyMetadata`, public key token, strong name, embedded resources, custom predicates, and exclusions for framework, Microsoft, satellite and reference assemblies.
- Type filters that need no loading: `ContainingTypes`, `ContainingImplementationsOf`, `ContainingTypesWithAttribute`.
- Diagnostics for every file left out, and `ScanResult.Explain` to answer why one assembly was or was not selected.
- Parallel inspection, a process-wide metadata cache (`ScanCache`), `ScanAsync`, streaming with `EnumerateAsync`, and cancellation.

**Loading**

- `ScanResult.Load` and `TryLoad`, which prefer the host application's own copy of an assembly.
- `LoadIsolated`: unloadable load contexts, shared or one per assembly, with private dependency resolution and contracts shared with the host.
- In-memory loading (`LoadInMemory`) that leaves files unlocked.
- `LoadPlugins<T>`: plugin instances with constructor injection, per-plugin isolation, failure reporting, and unloading on dispose.

**Types and dependency injection**

- `TypeQuery`: a fluent, immutable, fault-tolerant type filter with closed-generic matching, attribute and method discovery, and `CreateInstances`.
- `AddFromAssemblies`: convention-based registration for `IServiceCollection` (`AsImplementedInterfaces`, `AsMatchingInterface`, `AsSelf`, `AsSelfWithInterfaces`, `As<T>`, `AddClosedTypesOf`, open generics, keyed services, duplicate strategies).
- `[Service]` and `AddAttributedServices`: attribute-based registration, loading only the assemblies that contain attributed classes.

**Analysis and tooling**

- `CheckCompatibility` and `RequireCompatibility`: a binary compatibility check that predicts, from metadata and without loading, every `MissingMethodException`, `MissingFieldException`, `TypeLoadException`, `TypeAccessException`, `MethodAccessException` and `FileLoadException` an assembly would hit against what the host provides, including interface members and abstract methods a class no longer implements, and names the methods that would throw.
- `DependencyGraph`: load order, dependents, transitive dependencies, missing references, version conflicts, cycles, and Mermaid and DOT output.
- `AssemblyWatcher`: debounced hot-reload notifications with the added, removed and changed assemblies.
- `ScanDiff`, `ScanResult.Fingerprint`, `ToJson`, `ToMarkdown` and `FindResources`.
- `RequireHash`: a SHA-256 allow-list for assemblies.

### Changed

- New logo and package icon, and a fully rewritten README with a complete API reference, recipes and troubleshooting.
- Releases are published with NuGet trusted publishing (no stored API key), from the exact package the build tested, and the GitHub release and `v<version>` tag are created from the project version automatically.
- Build, CodeQL and publish workflows use the .NET 8 and .NET 10 SDKs and current action versions.
- The tests run on xunit.v3 and Microsoft Testing Platform, assert with Shouldly, and cover about 98% of the library's lines.

## 1.0.0

- Initial release
