# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A NuGet library (`Masterly.AssembliesScanner`) that finds, inspects, filters and loads .NET assemblies. Version 2 has two APIs in one static class, `Assemblies`:

- `Assemblies.Scan()` — the fluent scanner. It reads metadata only and loads nothing until asked. Everything new builds on this.
- `Assemblies.Get(...)` — the version 1 one-call API. It is kept **byte-for-byte in behaviour** for compatibility; do not "fix" it (see below).

One xunit.v3 + Shouldly test project lives under `test/`.

## Commands

Run from the repo root (the `.sln` is there).

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test                                   # all tests, on net8.0 and net10.0
dotnet test -f net10.0                        # one target framework
dotnet test --filter "FullyQualifiedName~LoadPlugins"   # tests by name fragment
dotnet test --filter-class "*NamePatternTests"           # xunit.v3 filters: --filter-class, --filter-method, --filter-namespace
dotnet test -f net10.0 --coverage                        # writes a .coverage file under TestResults/
dotnet list package --outdated --include-transitive   # should report nothing
```

- Library and tests both target `net8.0;net10.0`, so `dotnet test` needs both runtimes installed. With only one, pass `-f`.
- `GeneratePackageOnBuild` is on, so every build also writes `Masterly.AssembliesScanner.<version>.nupkg` into `src/Masterly.AssembliesScanner/bin/<Configuration>/`. There is no separate pack step.
- The build is warning-free. `GenerateDocumentationFile` is on, so a public member without an XML doc comment produces `CS1591`; document every public member.
- `global.json` selects the Microsoft Testing Platform runner for `dotnet test`. xunit.v3 4.x runs on that platform, and the .NET 10 SDK refuses to run such a project through VSTest; without the file `dotnet test` fails with "Testing with VSTest target is no longer supported". It pins no SDK version.
- No linter or formatter is configured.

## Constraints on library code

- No `LangVersion` is set, so the `net8.0` target compiles as **C# 12**. Do not use C# 13/14 features (`field` keyword, `params` collections, extension blocks, `System.Threading.Lock`) or APIs that exist only in .NET 9+.
- Both projects have `ImplicitUsings` and `Nullable` enabled; the test project also has global usings for `Shouldly` and `Xunit` (`<Using>` items in its csproj). The code uses the C# 8 to 12 idioms throughout, and new code should too: file-scoped namespaces, target-typed `new`, collection expressions (`[]`, `[a, b]`, `[.. sequence]`), `is A or B` / `is not null` patterns, switch expressions, ranges (`text[1..]`), `required`/`init` members and records for internal data, and raw string literals (`"""`) for text that contains quotes. The switch statements that remain have multi-statement cases. The legacy `Assemblies.Get` got the same syntax changes and nothing else.
- `AllowUnsafeBlocks` is on for one reason: `Assembly.TryGetRawMetadata` in `Internal/MetadataInspector.cs`.
- The only package dependency is `Microsoft.Extensions.DependencyInjection.Abstractions` 11.0.0, for both targets. It supplies `IServiceCollection` and `ActivatorUtilities`. Think twice before adding another. The 11.x line is not tested on net8.0 and says so with a build warning; `SuppressTfmSupportBuildWarnings` silences it in both projects here, but an application on net8.0 that installs this package sees that warning in its own build.
- Every package is on its latest stable version, transitive ones included: `dotnet list package --outdated --include-transitive` reports nothing. The test csproj lists transitive packages explicitly to hold them there. Two of those pins have a cost, described in comments in that file: DiffEngine 21 breaks Shouldly 4.3.0's `ShouldMatchApproved` (no test uses it; 13.0.2 is the newest that works), and Microsoft.ApplicationInsights 3.x brings about 30 Azure, OpenTelemetry and Microsoft.Extensions packages that the test platform's telemetry extension loads into the test process. Tests that look at the loaded assemblies must not assume that every `System.*` or `Azure.*` assembly is framework code. Shouldly 5 drops DiffEngine, but 5.0.0-preview.2 makes `sequence.ShouldBe(new[] { ... })` ambiguous in about 25 places.
- Tests assert with Shouldly (FluentAssertions was dropped: 8.x changed to a paid licence). Shouldly's string `ShouldContain` and `ShouldStartWith` ignore case unless `Case.Sensitive` is passed; `ShouldBe` on two sequences compares in order unless `ignoreOrder: true` is passed.

## Architecture

Every public type is in the `Masterly.AssembliesScanner` namespace, whatever folder its file is in; only `Internal/` has its own namespace (`Masterly.AssembliesScanner.Internal`). The folders are for navigation. Do not make namespaces follow folders (the IDE0130 suggestion): that would break the public API. The test project's folders mirror the library's and likewise keep the namespaces they had (`...UnitTests`, `...UnitTests.Samples`, `...UnitTests.EdgeSamples`).

Paths below are relative to `src/Masterly.AssembliesScanner/`.

| File | Role |
| --- | --- |
| `Assemblies.cs` | Entry points: `Scan()`, `Scan(patterns)`, `TypesIn(...)`, and the legacy `Get`. |
| `Scanning/AssemblyScanner.cs` | The mutable builder: sources, patterns, named filters, options. `Scan()` collects candidates, evaluates them (in parallel above 8 candidates), then de-duplicates sequentially. |
| `Scanning/NamePattern.cs` | Glob / regex / exact patterns, `!` exclusion, `glob:`/`regex:`/`exact:` prefixes. |
| `Scanning/ScanCache.cs` | Process-wide snapshot cache keyed by path, valid while file size and last-write time are unchanged. |
| `Scanning/ScanDiagnostic.cs` | `ScanDiagnostic` and `SkipReason`. |
| `Scanning/ScanResult.cs` | Result list + diagnostics; `Load`, `TryLoad`, `LoadIsolated`, `LoadPlugins`, `Types`, graph, diff, reports. |
| `Scanning/AssemblyWatcher.cs` | Debounced `FileSystemWatcher` wrapper that rescans and diffs. |
| `Descriptors/AssemblyDescriptor.cs`, `Descriptors/TypeDescriptor.cs` | Public view of a snapshot plus per-scan context (path, source) and lazy `Load()`. |
| `Loading/Isolation.cs` | `IsolatedLoadContext`, `IsolationOptions`, `IsolatedAssemblySet`. |
| `Loading/Plugins.cs` | `PluginOptions`, `PluginSet<T>`. |
| `Types/TypeQuery.cs` | Immutable fluent filter over loaded types. |
| `DependencyInjection/DependencyInjection.cs` | `ServiceRegistrationBuilder`, `[Service]`, `IServiceCollection` extensions. |
| `Analysis/DependencyGraph.cs` | Reference graph, load order, missing references, version conflicts, cycles. |
| `Analysis/Compatibility.cs` | Public side of the binary compatibility check: `CompatibilityReport`, `CompatibilityIssue`, `CompatibilityIssueKind`, `CompatibilityOptions`. Entry points are `ScanResult.CheckCompatibility` and `AssemblyScanner.RequireCompatibility`. |
| `Internal/CompatibilityChecker.cs` | The check itself: resolves every type, member and abstract member an assembly uses against the files the host would provide, and reads IL to name the users. Also `ILTokens`, the IL operand walker. |
| `Internal/MetadataModule.cs` | A target assembly opened for lookups (types by name, forwarders, `InternalsVisibleTo`), read into memory and cached process-wide by path, size and timestamp; `ScanCache.Clear` clears it. Also `SignatureFormatter`, which turns signatures into comparable strings. |
| `Internal/MetadataInspector.cs` | Reads a file or a loaded assembly with `System.Reflection.Metadata` into an `AssemblySnapshot`. Never loads. |
| `Internal/AssemblySnapshot.cs` | The metadata snapshot that descriptors wrap and the cache stores, and the inspection status/result types. |
| `Internal/HostAssemblies.cs` | What the host can resolve (trusted platform assemblies), and the framework-assembly heuristics. |

Rules that hold the design together:

- **Scanning must never load an assembly.** Patterns are checked against the file name before the file is even opened; filters run on metadata. Anything that needs a real `Type` belongs after `Load`.
- **Patterns match file names**, not the assembly name inside the metadata. That is what lets them run before inspection.
- **Filters are named** (`Where(predicate, description)`) so a rejected file gets a diagnostic that says which filter rejected it. New filter methods should pass a description. Filters run concurrently.
- **Nothing found is silently dropped.** A skipped file becomes a `ScanDiagnostic` with a `SkipReason`.
- **Results are distinct** by full name + MVID; the first occurrence wins and later ones get a `Duplicate` diagnostic.
- **`AssemblyDescriptor.Load()` asks the default context by name first** and falls back to the scanned path, so the host's copy wins.
- **`IsolatedLoadContext.Load` order:** explicitly shared → a scanned assembly of this context → host-provided (when `PreferSharedTypes`) → `AssemblyDependencyResolver` → probing directories. Returning `null` defers to the default context.
- **`TypeQuery` is immutable**; every filter returns a new query. DI filters are `Func<TypeQuery, TypeQuery>` for that reason.
- **The compatibility check mirrors the runtime's rules; a false positive is a bug.** Member references are resolved up the base-class chain (constructors excepted), explicit overrides (`.override`) are resolved the same way, type forwarders are followed, a missing or too-old assembly is reported only when a type reference points into it, and a non-public type only where the runtime checks access (IL operands, the owner of a member called from IL, base types and interfaces), not in signatures or custom attributes. Type names in signatures carry no assembly name. To look for false positives after a change, run `CheckCompatibility(o => { o.CheckFrameworkAssemblies = true; o.PreferHostAssemblies = false; })` over a shared framework folder such as `dotnet/shared/Microsoft.NETCore.App/<version>`: it must report nothing.
- **Unloading:** `IsolatedAssemblySet.Dispose` and `PluginSet.Dispose` drop their own references before calling `Unload`. Keep it that way; a stored `Assembly`, `Type` or instance anywhere in the library would pin the context.

## How the legacy `Assemblies.Get` behaves

The XML docs describe it loosely; the real semantics matter because existing users depend on them:

- **It scans files, not the AppDomain.** It enumerates `*.dll` in `AppDomain.CurrentDomain.BaseDirectory` (top level only) and calls `Assembly.Load` on **every** one before filtering. A non-managed DLL in that folder makes `AssemblyName.GetAssemblyName` throw.
- **Patterns are regular expressions, not globs.** They run through `Regex.IsMatch` unanchored against `ManifestModule.Name` (the file name including `.dll`). In `"Masterly.*.dll"` the dots are regex wildcards.
- **Matching is case-insensitive by lowercasing both the name and the pattern.** Lowercasing the pattern changes the meaning of escapes such as `\W`, `\D`, `\S`.
- **A leading `!` marks an exclusion.** Exclusions are collected up front and applied after all includes, so pattern order is irrelevant. `Replace("!", "")` strips every `!` in the pattern, not only the leading one.
- **No de-duplication.** An assembly matched by two include patterns appears twice in the result.
- The `IEnumerable<string>` overload only forwards to the `params string[]` overload.

`Assemblies.Scan` deliberately differs on every one of these points.

## Tests

- **Fixtures are compiled at run time** with Roslyn (`Fixture.Compile` in `Support/TestSupport.cs`) into a per-test temp directory, so a test controls versions, references, attributes and resources. `Samples/Samples.cs` holds the types that fixtures and type/DI tests refer to.
- **Give a fixture a unique name (`Fixture.UniqueName`) whenever it ends up in the default load context** (`Load()`, `Types()`, non-isolated plugins). The default context keeps one assembly per simple name for the life of the process, so a reused name returns the earlier test's assembly.
- **Test parallelization is off** (`[assembly: Parallelization(Mode = ParallelMode.None)]` in `Support/TestSupport.cs`) because `ScanCache` and the default load context are process-wide. The two target frameworks still run side by side, each in its own process.
- **The test project is an executable** (`OutputType` is `Exe`; xunit.v3 requires it), so `Assembly.GetEntryAssembly()` is the test assembly. Analyzer rule `xUnit1051` (pass the test's cancellation token everywhere) is switched off in the csproj.
- **`RemoveXunitRunnerOnlyFiles` in the test csproj deletes `xunit.abstractions.dll` and `xunit.v3.runner.utility.netcore.dll` from the output folder.** xunit.v3 copies them there for third-party runners without listing them in `deps.json`, and the legacy `Assemblies.Get` loads every DLL in the base directory by name, so it throws when they are present. After changing xunit versions, delete `bin/` and `obj/` of the test project once; stale runner files cause the same failure.
- **Fixture sources are raw string literals**, so the C# inside them needs no escaped quotes. `Fixture.Create(assembly, "TypeName")` and `Fixture.Call(instance, "Method")` do the reflection for types that only exist in a fixture.
- **Other fixture helpers in `Support/TestSupport.cs`:** `Compile(..., kind: OutputKind.NetModule)` for a file with metadata but no assembly manifest, `Compile(..., withSymbols: true)` for a `.pdb`, `WriteNativeImage` for a PE file without metadata, `WriteAssemblyWithOddMetadata` for malformed attribute values, and `LoadFromMemory` for an assembly without a location.
- **`Samples/EdgeSamples.cs` is a separate namespace on purpose.** Several tests count the types in `Samples` or look for everything that implements one of its interfaces, so new sample types go in `EdgeSamples` and must not implement the `Samples` interfaces or carry `[Service]`.
- **Line coverage is about 98%.** What is left cannot be reached from a test: a file that disappears between inspection and reading its size, a runtime that does not expose metadata for a loaded assembly, a host without the shared-framework layout, a native library found next to a plugin, and a type whose hierarchy fails to load after `GetTypes` succeeded.
- **Some tests depend on the test output folder.** The two original tests in `Legacy/AssembliesTests.cs`, and a few base-directory tests, assert that exactly two `Masterly.*.dll` files are there (the library and the test assembly). Adding another `Masterly.*` project reference or package changes that.
- Unload tests use a `[MethodImpl(NoInlining)]` helper that returns a `WeakReference`, then force GCs. Keep plugin types and instances out of the calling method's locals or the test becomes flaky.
- **`CompatibilityTests` builds contracts in two versions** (`BuildUpgrade`: the plugin is compiled against version 1, then version 2 is put next to it) and checks predictions against the real runtime in `Predictions_MatchWhatTheRuntimeThrows`. One test checks the test output folder and expects the Shouldly → DiffEngine `MissingMethod` that the DiffEngine pin causes; it changes if that pin does. The same check also finds that `Microsoft.Testing.Extensions.Telemetry` 2.5.3 calls a `TelemetryClient.TrackEvent` overload that ApplicationInsights 3.x removed.
- A plugin that calls Newtonsoft.Json's serializer does not unload, even in a plain collectible `AssemblyLoadContext`. That is the dependency's doing; do not chase it as a library bug.

## Release flow (GitHub Actions)

- `build.yml` runs on push and PR to `master`, monthly, and on demand (Windows, .NET 8 + 10 SDKs). Job one restores, builds Release (which also packs), tests, and uploads the `.nupkg` as the `nupkg` artifact. Job two calls `codeql-analysis.yml` (a reusable workflow; it also runs on its own for pushes to other branches). The CodeQL job fails on an error-level result or a security result of severity 7.0 or higher, read from the SARIF it writes, so a Build run, and therefore a publish, needs a clean CodeQL analysis. A dismissed alert on GitHub does not silence that gate; exclude a query in a CodeQL config file instead. Build no longer creates releases.
- `publish.yml` runs on `workflow_run` of `Build` and does work only for a **successful build of a push** (`workflow_run.event == 'push'`; a PR from a fork's `master` also has head branch `master`). It reads `<Version>` from the csproj at the built commit, skips when nuget.org already has that version, otherwise downloads the tested package from the Build run, pushes it with **NuGet trusted publishing** (`NuGet/login@v1` exchanges the OIDC token for a short-lived key; needs `id-token: write`, the nuget.org profile name `a7mdfre7at` written in the workflow as the `user` input, and a trusted-publishing policy on nuget.org for this repo, `publish.yml` and environment `nuget`), then creates tag `v<version>` and a GitHub release (`softprops/action-gh-release@v3`) with `CHANGELOG.md` as the body. No API key is stored; the old `NUGET_API_KEY` secret is unused.
- So a release means two edits: `<Version>` in the csproj and a new entry at the top of `CHANGELOG.md` (the whole file becomes the release body). `RELEASE-NOTES.md` is kept in step but unused (the `PackageReleaseNotes` line in the csproj is commented out).
- Actions are referenced by major tag and are on their latest majors (`checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`, `download-artifact@v8`, `codeql-action@v4`, `NuGet/login@v1`, `action-gh-release@v3`); `.github/dependabot.yml` proposes updates weekly. The workflows pass `actionlint` (`docker run --rm -v "<repo>:/repo" -w /repo rhysd/actionlint:latest`).
- `README.nuget.md` is the readme shown on nuget.org; `README.md` is the GitHub one. Both are packed.
- `assets/branding/` holds the logo, README banner and GitHub social preview. The SVGs are the sources; the PNGs are renders of them (`logo.png` is 512 x 512 and is the package icon, `banner.png` is rendered at 2x). Re-render the PNGs after editing an SVG. `repo_image.png` at the root is the old icon and is no longer used.
