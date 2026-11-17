# Masterly.AssembliesScanner

<img src="assets/branding/banner.png" alt="Masterly.AssembliesScanner: find, inspect, check and load .NET assemblies, without loading them first" width="100%">

[![Nuget](https://img.shields.io/nuget/v/Masterly.AssembliesScanner?style=flat-square)](https://www.nuget.org/packages/Masterly.AssembliesScanner) ![Nuget](https://img.shields.io/nuget/dt/Masterly.AssembliesScanner?style=flat-square) ![GitHub last commit](https://img.shields.io/github/last-commit/a7mdfre7at/Masterly.AssembliesScanner?style=flat-square) ![GitHub](https://img.shields.io/github/license/a7mdfre7at/Masterly.AssembliesScanner?style=flat-square) [![Build](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/actions/workflows/build.yml) [![CodeQL Analysis](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/actions/workflows/codeql-analysis.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/actions/workflows/codeql-analysis.yml) [![Publish to NuGet](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/actions/workflows/publish.yml/badge.svg?branch=master)](https://github.com/a7mdfre7at/Masterly.AssembliesScanner/actions/workflows/publish.yml)

Find, inspect, check and load .NET assemblies, without loading them first.

If you like or are using this project please give it a star. Thanks! :star:

## Contents

- [Why](#why)
- [Features](#features)
- [Install](#install)
- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Scanning](#scanning): [sources](#sources), [name patterns](#name-patterns), [filters](#filters), [running a scan](#running-a-scan)
- [The scan result](#the-scan-result): [`ScanResult`](#scanresult), [`AssemblyDescriptor`](#assemblydescriptor), [`TypeDescriptor`](#typedescriptor), [diagnostics](#diagnostics)
- [Loading](#loading): [default context](#into-the-default-context), [isolated and unloadable](#isolated-and-unloadable)
- [Plugins](#plugins)
- [Type discovery](#type-discovery)
- [Dependency injection](#dependency-injection)
- [Compatibility check](#compatibility-check)
- [Dependency graph](#dependency-graph)
- [Hot reload](#hot-reload)
- [Trust](#trust)
- [Reports](#reports)
- [Performance and caching](#performance-and-caching)
- [Recipes](#recipes)
- [The version 1 API](#the-version-1-api)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [License](#license)

## Why

`AppDomain.GetAssemblies()` only shows what the runtime happened to load so far. Calling `Assembly.Load` on every DLL in a folder drags native libraries, test assemblies and the wrong versions into your process, and once an assembly is in the default load context it stays there. A plugin built against last month's contract loads fine and then throws `MissingMethodException` the first time a user clicks the wrong button.

This library reads assembly **metadata** instead. You see every assembly's identity, references, attributes, resources and types, decide which ones you want, check that they will actually run against what your application provides, and only then load them: into the host, or into isolated contexts that can be unloaded again.

```c#
using Masterly.AssembliesScanner;

ScanResult scan = Assemblies.Scan()
    .FromDirectory("plugins", recursive: true)
    .Include("Contoso.*", "!*.Tests")
    .Referencing<IPlugin>()
    .RequireCompatibility()        // drop plugins that would throw MissingMethodException & co.
    .Scan();                       // nothing is loaded yet

using PluginSet<IPlugin> plugins = scan.LoadPlugins<IPlugin>();   // isolated, unloadable
```

## Features

| | |
| --- | --- |
| **Scan without loading** | Reads metadata only. Native DLLs and broken files are skipped and reported, never thrown. |
| **Patterns** | Globs (`Contoso.*`, `plugins/**/*.dll`), regular expressions and exact names, with `!` exclusions. |
| **Sources** | Directories (optionally recursive), files, loaded assemblies, and the full reference closure of an assembly. |
| **Metadata filters** | Version, target framework, references, assembly attributes, `AssemblyMetadata`, public key token, file hash. |
| **Type filters without loading** | "Assemblies that contain an implementation of `IHandler<>`", judged from metadata. |
| **Diagnostics** | Every file left out comes with a reason. `Explain("Foo.dll")` answers "why was it not picked up?". |
| **Compatibility check** | Predicts `MissingMethodException`, `TypeLoadException` and the like before anything is loaded, and names the methods that would throw. |
| **Loading** | Into the host, or into isolated load contexts that can be unloaded. Optional in-memory loading leaves files unlocked. |
| **Plugins** | `LoadPlugins<T>()` with constructor injection, per-plugin isolation and failure reporting. |
| **Type discovery** | A fluent, fault-tolerant `TypeQuery`, including closed generics and attributed methods. |
| **Dependency injection** | Convention-based and attribute-based registration for `IServiceCollection`, keyed services included. |
| **Dependency graph** | Load order, dependents, missing references, version conflicts, cycles; Mermaid and DOT output. |
| **Hot reload** | A debounced watcher that tells you what was added, removed or rebuilt. |
| **Reports** | JSON inventory, Markdown report, and a fingerprint of the whole set. |
| **Fast** | Parallel inspection, a process-wide metadata cache, async and streaming APIs with cancellation. |

## Install

```
dotnet add package Masterly.AssembliesScanner
```

| | |
| --- | --- |
| Target frameworks | .NET 8 and .NET 10. For .NET Framework, .NET Standard 2.0 or .NET 5, stay on version 1.0.0. |
| Dependencies | `Microsoft.Extensions.DependencyInjection.Abstractions` 11.0.0 only. |
| On .NET 8 | That dependency warns at build time that it is not tested on .NET 8. Set `<SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>` in your project to silence it. |

Everything lives in the `Masterly.AssembliesScanner` namespace.

## Quick start

**See what is in a folder**

```c#
ScanResult scan = Assemblies.Scan().FromDirectory("plugins").Scan();

foreach (AssemblyDescriptor assembly in scan)
    Console.WriteLine($"{assembly.Name} {assembly.Version} {assembly.TargetFramework}");

foreach (ScanDiagnostic skipped in scan.Diagnostics)
    Console.WriteLine($"skipped {skipped.Path}: {skipped.Reason}");
```

**Load plugins**

```c#
using PluginSet<IPlugin> plugins = Assemblies.Scan()
    .FromDirectory("plugins", recursive: true)
    .Referencing<IPlugin>()
    .Scan()
    .LoadPlugins<IPlugin>(options => options.Services = serviceProvider);

foreach (IPlugin plugin in plugins)
    plugin.Start();
```

**Register services by convention**

```c#
builder.Services.AddFromAssemblies(
    scan => scan.Include("Contoso.*").Exclude("*.Tests"),
    rules => rules.AddClassesAssignableTo<IRepository>().AsImplementedInterfaces().WithScopedLifetime());
```

**Check plugins against the host before loading them**

```c#
CompatibilityReport report = Assemblies.Scan().FromDirectory("plugins").Scan().CheckCompatibility();
report.ThrowIfIncompatible();
```

## How it works

1. **Configure** an `AssemblyScanner`: where to look, which file names, which metadata. Nothing is read yet.
2. **Scan.** File names are matched first, so files that cannot match are never opened. The rest are read with `System.Reflection.Metadata`, in parallel, and the filters run on what was read. The result is a `ScanResult`: a list of `AssemblyDescriptor`s plus a `ScanDiagnostic` for every file that was left out.
3. **Decide.** Inspect descriptors, build the dependency graph, run the compatibility check, diff against an earlier scan. Still nothing is loaded.
4. **Load** what you chose: into the default load context, into isolated contexts, or as plugin instances.

Results are distinct by identity and build (full name and MVID): when the same assembly is found twice, the first one wins and the second gets a `Duplicate` diagnostic.

## Scanning

### Sources

| Method | Scans |
| --- | --- |
| *(no source)* | The top level of the application base directory. |
| `FromBaseDirectory()` | The same, explicitly. |
| `FromDirectory(path, recursive: false)` | One directory, optionally with its subdirectories. |
| `FromDirectories(params paths)` | Several directories, top level only. |
| `FromFiles(params paths)` | Specific files. |
| `FromLoadedAssemblies()` | What the process has loaded so far. |
| `FromAssemblies(params assemblies)` / `FromAssemblyOf<T>()` | Specific loaded assemblies. |
| `FromDependencyClosure(root = null, includeFrameworkAssemblies: false)` | An assembly (the entry assembly by default) and everything it references, directly or indirectly. |

Sources can be combined. `FromDependencyClosure` follows references through metadata, so it also finds assemblies your application references but the runtime has not loaded yet, the ones `AppDomain.GetAssemblies()` misses. A directory or file that does not exist produces a `SourceNotFound` diagnostic.

### Name patterns

```c#
Assemblies.Scan("Contoso.*", "!*.Tests");                   // shortcut for Scan().Include(...)

Assemblies.Scan()
    .Include("Contoso.*", "Fabrikam.Core")                  // globs; a bare name matches Fabrikam.Core.dll
    .Exclude("*.Tests")                                      // exclusions always win, in any order
    .IncludeRegex(@"^Acme\.(Api|Web)\.dll$")
    .Include("plugins/**/*.dll");                           // a glob with a slash matches the relative path
```

Patterns are case-insensitive and are matched against the **file name**, not the assembly name in the metadata. That is what lets them run before a file is opened.

| Syntax | Matches | Example |
| --- | --- | --- |
| Glob | The whole file name, with or without `.dll`. `*` is any run of characters except `/`, `?` one character, `**` any number of directories. | `Contoso.*`, `plugins/**/*.dll` |
| Regex | Anywhere in the file name. | `\.Tests\.dll$` |
| Exact | The file name, with or without `.dll`. | `Contoso.Core` |

A pattern in text form may start with `!` (exclusion) and a `glob:`, `regex:` or `exact:` prefix, for example `"!regex:\.Tests\.dll$"`. To build patterns in code use `NamePattern.Glob`, `NamePattern.Regex`, `NamePattern.Exact` or `NamePattern.Parse`, mark one with `AsExclusion()`, and pass them to `Matching(...)`. With no inclusion pattern every file name is included.

### Filters

Every filter reads metadata only. An assembly must pass all of them.

| Method | Keeps assemblies that... |
| --- | --- |
| `WithVersionRange(min, max)`, `WithMinimumVersion("2.0.0.0")`, `WithMaximumVersion(...)` | have a version in the range (inclusive). |
| `TargetingFramework("net8.0")` | target a framework: a moniker such as `net8.0`, `netstandard2.0`, `net48`, or part of the name such as `.NETCoreApp`. |
| `Referencing("MediatR")`, `Referencing<T>()`, `Referencing(assembly)` | reference at least one of the named assemblies. |
| `WithAssemblyAttribute<T>()`, `WithAssemblyAttribute("Full.Name")` | carry an assembly-level attribute. |
| `WithMetadata("Role", "Plugin")` | carry `[assembly: AssemblyMetadata("Role", "Plugin")]`; leave out the value to match the key only. |
| `ContainingImplementationsOf<T>()`, `ContainingImplementationsOf(typeof(IHandler<>))` | declare a concrete class that implements or derives from the type. |
| `ContainingTypesWithAttribute<T>()` | declare a type with the attribute. |
| `ContainingTypes(t => ..., "description")` | declare a type matching a predicate over a [`TypeDescriptor`](#typedescriptor). |
| `WithResource("*.sql")` | embed a resource whose name matches the glob. |
| `WithPublicKeyToken(params tokens)` | have one of the public key tokens. |
| `RequireStrongName()` | carry a public key. |
| `RequireHash(params sha256Hashes)` | have a file hash on the allow-list. See [Trust](#trust). |
| `RequireCompatibility(configure = null)` | would run against what the host provides. See [Compatibility check](#compatibility-check). |
| `ExcludeFrameworkAssemblies()` | are not part of the .NET base class library (`System.*`, `mscorlib`, `netstandard`, the shared frameworks). |
| `ExcludeMicrosoftAssemblies()` | are neither framework assemblies nor Microsoft-signed `Microsoft.*` assemblies. |
| `ExcludeSatelliteAssemblies()` | are not culture-specific resource assemblies. |
| `ExcludeReferenceAssemblies()` | are not reference assemblies, which cannot be loaded for execution. |
| `Where(a => ..., "description")` | pass any predicate over an [`AssemblyDescriptor`](#assemblydescriptor). |

Give `Where` and `ContainingTypes` a description: it is what the diagnostic says when the filter rejects a file (`Rejected by filter: smaller than 5 MB.`). Predicates may run on several threads at once.

```c#
Assemblies.Scan()
    .WithMinimumVersion("2.0.0.0")
    .TargetingFramework("net8.0")
    .Referencing("MediatR")
    .ContainingImplementationsOf(typeof(IHandler<>))
    .ExcludeFrameworkAssemblies()
    .Where(a => a.FileSize < 5_000_000, "smaller than 5 MB");
```

### Running a scan

| Method | Does |
| --- | --- |
| `Scan(cancellationToken)` | Runs the scan and returns a `ScanResult`. |
| `ScanAsync(cancellationToken)` | The same, on the thread pool. |
| `EnumerateAsync(cancellationToken)` | Streams matching assemblies as they are found, in discovery order. |
| `Load()` | `Scan().Load()`: scans and loads into the default context. |
| `Types()` | `Scan().Types()`: scans, loads and starts a `TypeQuery`. |
| `Watch(debounce = 500 ms)` | Starts an [`AssemblyWatcher`](#hot-reload) over the scanned directories. |
| `WithParallelism(n)` | How many files are inspected at once (default: processor count; `1` for the calling thread only). |
| `WithoutCache()` | Reads every file again instead of using the [metadata cache](#performance-and-caching). |

## The scan result

### `ScanResult`

A `ScanResult` is an `IReadOnlyList<AssemblyDescriptor>` in discovery order.

| Member | |
| --- | --- |
| `Assemblies`, `Count`, indexer | The matching assemblies. |
| `Diagnostics` | Everything examined and left out, with the reason. |
| `Duration`, `FilesExamined` | How long it took and how much it looked at. |
| `Find(nameOrFileName)` | A matching assembly by simple name or file name, ignoring case. |
| `Explain(nameOrFileName)` | One sentence on what happened to an assembly: selected, or left out and why. |
| `Load()`, `TryLoad(out failures)`, `Types()` | [Load into the default context](#into-the-default-context). |
| `LoadIsolated(configure)` | [Load into isolated contexts](#isolated-and-unloadable). |
| `LoadPlugins<T>(configure)` | [Create plugin instances](#plugins). |
| `CheckCompatibility(configure)` | [Predict run-time failures](#compatibility-check). |
| `BuildDependencyGraph()` | [The reference graph](#dependency-graph). |
| `DiffFrom(previous)`, `Fingerprint`, `FindResources(glob)`, `ToJson(includeHashes)`, `ToMarkdown()` | [Reports](#reports). |

### `AssemblyDescriptor`

Everything known about one assembly from its metadata. Creating one never loads the assembly.

| Property or method | |
| --- | --- |
| `Name`, `Version`, `Culture`, `PublicKeyToken`, `FullName` | The identity. |
| `Mvid` | The module version id; it changes with every non-deterministic build. |
| `Path`, `FileName`, `RelativePath`, `Source` | Where it was found (`File`, `Loaded` or `DependencyClosure`). `Path` is `null` for an assembly that only exists in memory. |
| `FileSize`, `LastWriteTimeUtc`, `Sha256` | The file. The hash is computed on first access. |
| `TargetFramework`, `InformationalVersion`, `FileVersion`, `Company`, `Product`, `Title`, `Description`, `Configuration` | The usual assembly attributes. |
| `Metadata` | The `[assembly: AssemblyMetadata(key, value)]` pairs. |
| `References` | The referenced assemblies (`AssemblyReferenceInfo`: name, version, culture, public key token). |
| `AssemblyAttributes`, `HasAssemblyAttribute(fullName)` | Assembly-level attribute type names. |
| `ResourceNames`, `OpenResource(name)` | Embedded resources; opening one loads the assembly. |
| `Types` | The declared types as [`TypeDescriptor`](#typedescriptor)s, read on first access. |
| `IsReferencing(name)`, `DeclaresImplementationOf(fullName)` | Quick checks over references and types. |
| `IsFrameworkAssembly`, `IsReferenceAssembly`, `IsSatellite`, `IsStrongNamed` | Classification. |
| `Load()`, `Load(AssemblyLoadContext)`, `IsLoaded`, `LoadedAssembly` | Loading. `Load()` asks the default context by name first, so the host's own copy wins. |

### `TypeDescriptor`

A type read from metadata, without loading it: `FullName` (nested types use `+`), `Namespace`, `Name`, `BaseType`, `Interfaces`, `Attributes`, `GenericParameterCount`, and `IsPublic`, `IsNested`, `IsInterface`, `IsAbstract`, `IsSealed`, `IsEnum`, `IsValueType`, `IsClass`, `IsConcreteClass`, `IsStatic`, with `HasAttribute<T>()` and `Declares(fullName)` (implements or derives from it directly). Names of constructed generics are reported as their definition, for example ``IHandler`1``.

### Diagnostics

Nothing found is silently dropped. Each left-out file or assembly becomes a `ScanDiagnostic` with a `Path`, a `Reason`, a `Message` and, where there was one, the `Exception`.

| `SkipReason` | Meaning |
| --- | --- |
| `NotManaged` | A native image, or not a valid PE file. |
| `NotAnAssembly` | .NET metadata but no assembly manifest, for example a netmodule. |
| `Unreadable` | The file could not be opened or read. |
| `SourceNotFound` | A directory or file given as a source does not exist. |
| `ExcludedByPattern` | The file name matched an exclusion, or no inclusion. |
| `ExcludedByFilter` | A filter rejected it; the message names the filter. |
| `Duplicate` | The same build of the same assembly was already found elsewhere. |
| `Incompatible` | `RequireCompatibility` found that it would fail at run time; the message names the first problem. |
| `LoadFailed` | Selected, but loading failed (from `TryLoad`, isolated loading and plugins). |
| `ActivationFailed` | A plugin type was found but could not be constructed. |

```c#
Console.WriteLine(scan.Explain("Contoso.Legacy.dll"));
// Left out (ExcludedByFilter): C:\app\Contoso.Legacy.dll - Rejected by filter: targeting net8.0.
```

## Loading

### Into the default context

```c#
Assembly[] assemblies = scan.Load();                        // throws if one fails
Assembly[] some = scan.TryLoad(out var failures);           // reports failures instead
TypeQuery types = scan.Types();                             // loads, then queries
```

`Load` asks the default context for the assembly by name first, so the application's own copy wins when it has one. The scanned file is loaded by path only when the host cannot resolve the name. Assemblies in the default context stay loaded for the life of the process.

### Isolated and unloadable

```c#
using IsolatedAssemblySet set = scan.LoadIsolated(options =>
{
    options.PerAssembly = true;          // one load context per assembly
    options.LoadInMemory = true;         // do not lock the files on disk
    options.Share<IJob>();               // always take the contract from the host
});

foreach (Type type in set.Types().AssignableTo<IJob>())
    Console.WriteLine(type);
```

| `IsolationOptions` | Default | |
| --- | --- | --- |
| `Collectible` | `true` | Whether the contexts can be unloaded. |
| `PerAssembly` | `false` | One context per assembly, so each can carry its own dependency versions; otherwise one for the whole set. |
| `PreferSharedTypes` | `true` | Take any assembly the host can provide from the host, so contract types are identical on both sides. Turn it off to let plugins use private copies, and list the contracts in `SharedAssemblies` instead. |
| `SharedAssemblies`, `Share<T>()`, `Share(assembly)` | empty | Assemblies always taken from the host. |
| `LoadInMemory` | `false` | Load from a copy in memory, so the files are not locked and can be replaced. |
| `ThrowOnError` | `true` | Throw on the first assembly that fails, or record it in `Failures` and go on. |

A context resolves a dependency in this order: an explicitly shared assembly, an assembly of the scanned set, the host's copy (with `PreferSharedTypes`), the plugin's `.deps.json` through `AssemblyDependencyResolver` (native libraries included), then the plugin's folder.

Disposing the set unloads its contexts. Unloading is cooperative: the runtime releases a context only after every reference to its assemblies, types and instances is gone. See [Troubleshooting](#troubleshooting).

## Plugins

```c#
using PluginSet<IPlugin> plugins = Assemblies.Scan()
    .FromDirectory("plugins", recursive: true)
    .Referencing<IPlugin>()                  // do not even look at unrelated DLLs
    .RequireCompatibility()                  // nor at plugins built against an older contract
    .Scan()
    .LoadPlugins<IPlugin>(options =>
    {
        options.Services = serviceProvider;  // constructor injection
        options.Isolation.Share<IPlugin>();
    });

foreach (IPlugin plugin in plugins)
    plugin.Start();

foreach (ScanDiagnostic failure in plugins.Failures)
    logger.LogWarning("{Plugin}: {Message}", failure.Path, failure.Message);
```

`LoadPlugins<T>` creates one instance of every concrete class that implements `T`.

| `PluginOptions` | Default | |
| --- | --- | --- |
| `Isolated` | `true` | Load into unloadable contexts. When `false`, plugins go into the default context for good. |
| `Isolation` | `PerAssembly = true` | The [isolation options](#isolated-and-unloadable); each plugin assembly gets its own context by default. |
| `Services` | `null` | Satisfies constructor parameters. Without it, plugins need a parameterless constructor. |
| `ThrowOnError` | `false` | A plugin that fails to load or construct is recorded in `Failures` and the others still load. |

`PluginSet<T>` is an `IReadOnlyList<T>` with `PluginTypes`, `Failures` and `IsIsolated`. Disposing it disposes the plugins that implement `IDisposable` and unloads their contexts. If a plugin implements a second copy of the contract (same name, different assembly), the failure says so and suggests sharing the contract with the host.

## Type discovery

```c#
TypeQuery types = Assemblies.Scan("Contoso.*").Types();     // or Assemblies.TypesIn(assembly), or new TypeQuery(assemblies)

var handlers    = types.AssignableTo(typeof(IHandler<>)).Concrete();
var controllers = types.InNamespace("Contoso.Web").NameEndsWith("Controller").PublicOnly();
var jobs        = types.WithAttribute<JobAttribute>(job => job.Enabled);

foreach (ClosedGenericMatch match in types.ClosedImplementationsOf(typeof(IHandler<>)))
    Console.WriteLine($"{match.ServiceType} -> {match.ImplementationType}");

foreach (var (method, route) in types.MethodsWithAttribute<RouteAttribute>())
    Console.WriteLine($"{route.Template} -> {method.DeclaringType}.{method.Name}");

IReadOnlyList<IModule> modules = types.CreateInstances<IModule>(serviceProvider);
```

| Filter | Keeps types that... |
| --- | --- |
| `AssignableTo<T>()`, `AssignableTo(type)`, `AssignableToAny(params types)` | implement or derive from the type. Open generics such as `IHandler<>` match every closed form. |
| `WithAttribute<T>(inherit)`, `WithAttribute<T>(predicate)`, `WithAttribute(type)`, `WithoutAttribute<T>()` | carry, or do not carry, an attribute. |
| `InNamespace(ns)`, `InNamespaceOf<T>()`, `NotInNamespace(ns)` | are in a namespace or below it. |
| `NameStartsWith`, `NameEndsWith`, `NameMatches(glob)` | have a matching simple name. |
| `Concrete()` | can be instantiated: not abstract, not static, not delegates (open generic definitions are kept). |
| `Classes()`, `Interfaces()`, `PublicOnly()` | are classes (abstract ones included), interfaces, or visible from outside. |
| `Where(predicate)` | pass any predicate. |

| Result | |
| --- | --- |
| enumeration | The matching `Type`s. |
| `ClosedImplementationsOf(openGeneric)` | Each (closed service type, implementation) pair. |
| `WithAttributeData<T>()` | Each type with its attribute instances. |
| `MethodsWithAttribute<T>()` | Each method with its attribute instance. |
| `CreateInstances<T>(services)` | One instance of every concrete class assignable to `T`. |
| `LoaderErrors` | Why some types could not be loaded. |

A query is immutable: every filter returns a new query, so a base query can be reused. It never throws because one assembly contains a type that cannot be loaded; those problems go to `LoaderErrors`.

## Dependency injection

```c#
services.AddFromAssemblies(
    scan => scan.Include("Contoso.*").Exclude("*.Tests"),
    rules => rules
        .AddClassesAssignableTo<IRepository>().AsImplementedInterfaces().WithScopedLifetime()
        .AddClasses(types => types.NameEndsWith("Service")).AsMatchingInterface().WithScopedLifetime()
        .AddClassesAssignableTo(typeof(IValidator<>)).AsImplementedInterfaces().OnDuplicate(DuplicateStrategy.Skip)
        .AddClosedTypesOf(typeof(IHandler<>)).WithTransientLifetime());

services.AddFromAssemblies([typeof(Program).Assembly], rules => rules.AddClasses().AsSelf());
```

A rule starts with one of these and the methods after it configure that rule:

| Start a rule | Selects concrete classes that... |
| --- | --- |
| `AddClasses(filter = null)` | match an optional `TypeQuery` filter. |
| `AddClassesAssignableTo<T>()`, `AddClassesAssignableTo(type)` | implement or derive from the type. |
| `AddClosedTypesOf(openGeneric, filter = null)` | close an open generic; each is registered as the closed type it implements. Open generic classes are registered only when the container could build them. |
| `AddAttributedClasses(filter = null)` | carry `[Service]` (see below). |

| Register as | |
| --- | --- |
| `AsImplementedInterfaces()` | Every interface it implements, except the runtime's own such as `IDisposable`. |
| `AsMatchingInterface()` | The interface named after it: `OrderService` as `IOrderService`. |
| `AsSelf()` | Itself. |
| `AsSelfWithInterfaces()` | Itself and its interfaces, all resolving to the same instance. |
| `As<T>()`, `As(type)` | That type. |
| *(nothing)* | Its interfaces, or itself when it has none. |

| Configure | |
| --- | --- |
| `WithTransientLifetime()`, `WithScopedLifetime()`, `WithSingletonLifetime()`, `WithLifetime(lifetime)` | The lifetime (transient by default). |
| `WithServiceKey(key)` | A keyed registration. |
| `OnDuplicate(strategy)` | What to do when the service is already registered: `Append` (default), `Skip`, `Replace` or `Throw`. A service under another key is a different service. |

Open generic classes such as `Repository<T> : IRepository<T>` are registered as open generics.

Classes can also say how they want to be registered:

```c#
[Service(ServiceLifetime.Singleton)]
public class MemoryCache : ICache { }

[Service(ServiceLifetime.Scoped, typeof(IMailer), Key = "smtp")]
public class SmtpMailer : IMailer { }

services.AddAttributedServices(scan => scan.Include("Contoso.*"));   // or AddAttributedServices(assembly)
```

`[Service]` takes a lifetime (transient by default), a service type (by default its interfaces, or itself) and an optional `Key`. The scan overload loads only assemblies whose metadata shows a `[Service]` class.

## Compatibility check

A plugin built against last month's contract, or a library whose dependency was upgraded underneath it, loads fine and then fails on first use with `MissingMethodException`, `TypeLoadException` or `FileLoadException`. `CheckCompatibility` finds those failures from metadata, before anything is loaded:

```c#
CompatibilityReport report = Assemblies.Scan().FromDirectory("plugins").Scan().CheckCompatibility();

foreach (CompatibilityIssue issue in report)
    Console.WriteLine($"{issue.Assembly.Name}: {issue.Message} Used by {string.Join(", ", issue.UsedBy)}.");

// Plugin.Orders: Calls Contoso.IHost.Log(System.String), which Contoso.Contracts 2.0.0.0 does not have.
//                It has: Log(System.String, System.Int32). Used by Plugin.Orders.Exporter.Run.

report.ThrowIfIncompatible();      // or File.WriteAllText("compat.md", report.ToMarkdown())
```

Every type, method and field an assembly uses from another assembly is looked up in the copy the host would actually provide, following type forwarders and inherited members, and compared by exact signature. Every class is checked against the abstract members of its base classes and interfaces. Each problem names the exception the runtime would throw and, read from the IL, the methods that would throw it.

| `CompatibilityIssueKind` | Problem | The runtime would throw |
| --- | --- | --- |
| `MissingAssembly` | A referenced assembly cannot be found | `FileNotFoundException` |
| `AssemblyVersionTooLow` | It is found in an older version than the one compiled against, including a `System.Runtime` older than the .NET the assembly was built for | `FileLoadException` |
| `MissingType` | A used type no longer exists | `TypeLoadException` |
| `TypeNotAccessible` | A used type is no longer public | `TypeAccessException` |
| `MissingMethod` | A method or constructor was removed or changed (parameters, return type, static/instance) | `MissingMethodException` |
| `MissingField` | A field was removed or retyped | `MissingFieldException` |
| `MemberNotAccessible` | A member was made internal or private | `MethodAccessException`, `FieldAccessException` |
| `MissingImplementation` | An interface gained a member, or a base class an abstract method, that the class does not implement | `TypeLoadException` |
| `InvalidInheritance` | A base class became sealed or an interface, or an interface became a class | `TypeLoadException` |

The check reproduces the runtime's rules, so it does not report what would still work: a method moved to a base class, a new overload, a default interface member, a non-public type that only appears in a signature, or internals shared through `[InternalsVisibleTo]`. Each .NET 8 and .NET 10 shared framework (ASP.NET Core and Windows Desktop included, over 700 assemblies), checked against its own files, comes out clean, in about 300 ms per framework.

**Where references come from.** A referenced assembly is looked for in the host application first (its trusted platform assemblies, its base directory and what the default context loaded), then among the scanned assemblies, then in the folder of the assembly that references it, then in the probing directories.

| `CompatibilityOptions` | Default | |
| --- | --- | --- |
| `PreferHostAssemblies` | `true` | Look in the host first, as the default context and isolated loading with `PreferSharedTypes` do. Set it to `false` for plugins that bring private copies of their dependencies. |
| `ProbingDirectories` | empty | More directories to look in, after the referencing assembly's folder. |
| `CheckFrameworkReferences` | `true` | Also check uses of the base class library. Turn it off to check only your own contracts. |
| `CheckFrameworkAssemblies` | `false` | Also check base class library assemblies that are part of the result. |

**The report.** `CompatibilityReport` is an `IReadOnlyList<CompatibilityIssue>` with `IsCompatible`, `CheckedAssemblies`, `CompatibleAssemblies`, `IncompatibleAssemblies`, `Skipped` (no file to read, or framework assemblies), `For(assembly)` and `For(nameOrFileName)`, `ThrowIfIncompatible()` and `ToMarkdown()`. Each `CompatibilityIssue` has `Assembly`, `Kind`, `TargetAssembly`, `Member`, `Message`, `ExpectedException` and `UsedBy`.

**As a filter.** `RequireCompatibility(configure)` leaves incompatible assemblies out of a scan with an `Incompatible` diagnostic that names the first problem. It looks in the host, the file's own folder and the probing directories, but not at the other files of the same scan unless they sit there.

## Dependency graph

```c#
DependencyGraph graph = scan.BuildDependencyGraph();

graph.GetLoadOrder();                    // dependencies first
graph.GetDependencies(assembly);         // what it references, within the set
graph.GetDependents(assembly);           // who breaks if this one changes
graph.GetTransitiveDependencies(assembly);
graph.MissingReferences;                 // references neither the set nor the host can satisfy
graph.VersionConflicts;                  // asked for in several versions, or newer than what is present
graph.Cycles;

File.WriteAllText("dependencies.md", "```mermaid\n" + graph.ToMermaid() + "```");   // or ToDot()
```

A `VersionConflict` lists every `ReferenceRequest` (who asks for which version) and `IsDowngrade` tells whether somebody asks for a newer version than the one present, which fails at run time. The graph works on assembly names and versions; the [compatibility check](#compatibility-check) goes down to types and members.

## Hot reload

```c#
using AssemblyWatcher watcher = Assemblies.Scan()
    .FromDirectory("plugins", recursive: true)
    .Watch(TimeSpan.FromSeconds(1));

watcher.Changed += (_, change) =>
{
    Console.WriteLine($"+{change.Diff.Added.Count} -{change.Diff.Removed.Count} ~{change.Diff.Changed.Count}");
    // dispose the old PluginSet, then:
    // plugins = change.Current.LoadPlugins<IPlugin>(o => o.Isolation.LoadInMemory = true);
};
watcher.Error += (_, error) => logger.LogError(error.GetException(), "Watching plugins failed");
```

The watcher rescans the scanner's directories when files change. Events are debounced (500 ms by default), so a build that writes many files raises one event. `Changed` carries the `Previous` and `Current` results and their `Diff`; `Current` always holds the latest result and `Refresh()` rescans on demand. Load with `LoadInMemory` when the files must stay replaceable while they are in use.

## Trust

```c#
Assemblies.Scan()
    .FromDirectory("plugins")
    .RequireHash(allowedSha256Hashes)          // tamper check: any change to a file breaks the match
    .WithPublicKeyToken("b03f5f7f11d50a3a");   // identity check
```

.NET does not validate strong-name signatures, so a public key token identifies an assembly but does not prove where it came from. Use `RequireHash` when that matters; `scan.ToJson(includeHashes: true)` produces the list to approve.

## Reports

```c#
string json     = scan.ToJson(includeHashes: true);   // inventory for tooling
string markdown = scan.ToMarkdown();                   // for a build summary or a wiki
string id       = scan.Fingerprint;                    // same builds of the same assemblies => same value

IReadOnlyList<ResourceMatch> sql = scan.FindResources("*.sql");   // embedded resources, without loading
using Stream? first = sql[0].Open();                               // this one loads the assembly

ScanDiff diff = scan.DiffFrom(previousScan);           // Added, Removed, Changed (same path, new build)
```

## Performance and caching

- Files are inspected in parallel once there are at least 8 of them; `WithParallelism(n)` changes the degree, `1` keeps everything on the calling thread.
- File names are matched before a file is opened, so `Include` and `Exclude` are the cheapest filters.
- Metadata is cached process-wide per file until its size or last-write time changes, so scanning an unchanged folder again does not reopen any file. `WithoutCache()` skips the cache for one scan and `ScanCache.Clear()` empties it; `ScanCache.Count` shows its size. The compatibility check keeps the metadata of the assemblies it resolves into in the same way.
- A descriptor reads its types only when `Types` is first used, and its hash only when `Sha256` is.
- `ScanAsync` runs on the thread pool and `EnumerateAsync` streams matches as they are found; both accept a `CancellationToken`.

## Recipes

**A test that fails the build when a plugin no longer fits the host**

```c#
[Fact]
public void Plugins_AreCompatibleWithThisBuildOfTheHost()
{
    Assemblies.Scan()
        .FromDirectory(Path.Combine(AppContext.BaseDirectory, "plugins"), recursive: true)
        .Scan()
        .CheckCompatibility(o => o.CheckFrameworkReferences = false)
        .ThrowIfIncompatible();
}
```

**ASP.NET Core modules from a folder**

```c#
string modules = Path.Combine(AppContext.BaseDirectory, "modules");

builder.Services.AddFromAssemblies(
    scan => scan.FromDirectory(modules).Include("Contoso.Modules.*").RequireCompatibility(),
    rules => rules.AddClassesAssignableTo<IModule>().As<IModule>().WithSingletonLifetime());
```

**Find every assembly that still references a vulnerable package version**

```c#
var affected = Assemblies.Scan().FromDirectory(publishFolder, recursive: true).Scan()
    .Where(a => a.References.Any(r => r.Name == "Newtonsoft.Json" && r.Version < new Version(13, 0, 0, 0)));
```

**Swap plugins when their files change**

```c#
PluginSet<IPlugin> plugins = Load(Assemblies.Scan().FromDirectory("plugins").Scan());
using AssemblyWatcher watcher = Assemblies.Scan().FromDirectory("plugins").Watch();
watcher.Changed += (_, change) =>
{
    plugins.Dispose();
    plugins = Load(change.Current);
};

static PluginSet<IPlugin> Load(ScanResult scan)
    => scan.LoadPlugins<IPlugin>(o => o.Isolation.LoadInMemory = true);
```

**An inventory of what was deployed**

```c#
ScanResult scan = Assemblies.Scan().FromBaseDirectory().ExcludeFrameworkAssemblies().Scan();
File.WriteAllText("inventory.json", scan.ToJson(includeHashes: true));
Console.WriteLine($"Deployment fingerprint: {scan.Fingerprint}");
```

## The version 1 API

`Assemblies.Get` is still there and behaves exactly as before:

```c#
Assembly[] assemblies = Assemblies.Get("Masterly.*.dll", "!.UnitTests.dll$");
```

It treats patterns as **regular expressions**, loads **every** DLL in the application folder before filtering, throws on native DLLs and may return an assembly twice. New code should use `Assemblies.Scan`:

| Version 1 | Version 2 |
| --- | --- |
| `Assemblies.Get("Masterly.*.dll")` | `Assemblies.Scan("Masterly.*").Load()` |
| `Assemblies.Get("Masterly.*.dll", "!.UnitTests.dll$")` | `Assemblies.Scan("Masterly.*", "!*.UnitTests").Load()` |
| `Assemblies.Get(@"^Acme\.\w+\.dll$")` | `Assemblies.Scan().IncludeRegex(@"^Acme\.\w+\.dll$").Load()` |

### Upgrading from 1.x

- The package now targets .NET 8 and .NET 10 only.
- It depends on `Microsoft.Extensions.DependencyInjection.Abstractions` 11.0.0. On .NET 8, set `SuppressTfmSupportBuildWarnings` to `true` to silence that package's build warning.
- Existing calls to `Assemblies.Get` need no change.

## Troubleshooting

**"My DLL is not picked up."** Ask the result: `scan.Explain("MyPlugin.dll")` says whether it was seen, which pattern or filter left it out, or that no source contains it. Patterns match file names, not assembly names.

**"`NotManaged` for a DLL that is fine."** It is a native library (for example `e_sqlite3.dll`), which is expected and harmless. `NotAnAssembly` means a netmodule or another file with metadata but no manifest.

**"My plugin type does not implement my interface."** The plugin got its own copy of the contract assembly, so there are two `IPlugin` types. Share the contract with the host: `options.Isolation.Share<IPlugin>()`, or keep `PreferSharedTypes` on and make sure the host references the contract. `LoadPlugins` reports this case explicitly in `Failures`.

**"`MissingMethodException` after an upgrade."** Run `CheckCompatibility()` on the folder: it names the method, what exists instead, and every method that calls it.

**"A disposed plugin set does not unload."** The runtime only unloads a context once nothing references its assemblies, types or instances: no static field, event subscription, cached delegate, thread or timer anywhere in the process. Some libraries hold on by themselves; a plugin that serializes with Newtonsoft.Json 13.0.3 stays loaded even in a plain collectible `AssemblyLoadContext` without this library.

**"Type filters on the scanner miss a class."** `ContainingImplementationsOf` follows base classes only inside the same assembly, because it reads metadata. Load and use `Types()` for exact answers.

**Trimming and Native AOT.** The library is reflection-based and is not annotated for trimming or Native AOT.

## Building from source

```bash
dotnet restore
dotnet build --configuration Release --no-restore
dotnet test                                    # both target frameworks; needs the .NET 8 and .NET 10 SDKs
dotnet test -f net10.0                         # one target framework
dotnet test --filter-class "*CompatibilityTests"
dotnet test -f net10.0 --coverage              # code coverage
```

Tests use xunit.v3 on the Microsoft Testing Platform (selected in `global.json`) and Shouldly. Test assemblies are compiled at run time with Roslyn, so a test controls versions, references and attributes exactly.

**Releasing.** Change `<Version>` in `src/Masterly.AssembliesScanner/Masterly.AssembliesScanner.csproj`, add the version to `CHANGELOG.md`, and push to `master`. The Build workflow tests and packs; the Publish workflow then pushes that exact package to nuget.org with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (no stored API key), tags the commit `v<version>`, and creates a GitHub release with the changelog. A version that is already on nuget.org is skipped.

## License

MIT
