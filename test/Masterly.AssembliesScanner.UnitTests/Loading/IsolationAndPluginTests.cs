using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Masterly.AssembliesScanner.UnitTests.Samples;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.AssembliesScanner.UnitTests;

public class IsolationAndPluginTests : IDisposable
{
    private const string HelloPlugin = """
        using Masterly.AssembliesScanner.UnitTests.Samples;
        public class HelloGreeter : IGreeter { public string Greet(string name) => "Hello " + name; }
        """;

    private const string HowdyPlugin = """
        using Masterly.AssembliesScanner.UnitTests.Samples;
        public class HowdyGreeter : IGreeter { public string Greet(string name) => "Howdy " + name; }
        """;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static void CollectUntilDead(WeakReference reference)
    {
        for (int i = 0; reference.IsAlive && i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public void LoadIsolated_KeepsAssembliesOutOfTheDefaultContext()
    {
        string name = Fixture.UniqueName("Isolated");
        Fixture.Compile(_temp.Path, name, "public class Thing { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        using IsolatedAssemblySet first = scan.LoadIsolated();
        using IsolatedAssemblySet second = scan.LoadIsolated();

        Assembly assembly = first.Assemblies.ShouldHaveSingleItem();
        AssemblyLoadContext.GetLoadContext(assembly).ShouldBeOfType<IsolatedLoadContext>().ShouldBeSameAs(first.Contexts[0]);
        AssemblyLoadContext.Default.Assemblies.ShouldNotContain(a => a.GetName().Name == name);
        first.Assemblies[0].GetType("Thing").ShouldNotBeSameAs(second.Assemblies[0].GetType("Thing"));
        scan[0].IsLoaded.ShouldBeFalse();
    }

    [Fact]
    public void LoadIsolated_PerAssembly_GivesEachAssemblyItsOwnContext()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("SoloA"), "public class A { }");
        Fixture.Compile(_temp.Path, Fixture.UniqueName("SoloB"), "public class B { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        using IsolatedAssemblySet shared = scan.LoadIsolated();
        using IsolatedAssemblySet separate = scan.LoadIsolated(o => o.PerAssembly = true);

        shared.Contexts.ShouldHaveSingleItem();
        separate.Contexts.Count().ShouldBe(2);
        separate.Assemblies.Select(a => AssemblyLoadContext.GetLoadContext(a)).ShouldBeUnique();
    }

    [Fact]
    public void LoadIsolated_ResolvesPrivateDependencies_FromThePluginFolder()
    {
        string dependencyName = Fixture.UniqueName("PrivateDependency");
        string mainName = Fixture.UniqueName("NeedsDependency");
        string dependency = Fixture.Compile(_temp.Path, dependencyName, """public static class Secret { public static string Value => "from dependency"; }""");
        Fixture.Compile(_temp.Path, mainName, "public class Reader { public string Read() => Secret.Value; }", [dependency]);

        // Only the main assembly is scanned; its dependency has to be found by the load context.
        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Include(mainName).Scan().LoadIsolated();
        object reader = Fixture.Create(set.Assemblies.Single(), "Reader");

        Fixture.Call(reader, "Read").ShouldBe("from dependency");
        AssemblyLoadContext.Default.Assemblies.ShouldNotContain(a => a.GetName().Name == dependencyName);
    }

    [Fact]
    public void LoadIsolated_InMemory_LeavesTheFileUnlocked()
    {
        string path = Fixture.Compile(_temp.Path, Fixture.UniqueName("InMemory"), "public class Thing { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        using IsolatedAssemblySet set = scan.LoadIsolated(o => o.LoadInMemory = true);

        set.Assemblies.Single().Location.ShouldBeEmpty();
        File.Delete(path);
        File.Exists(path).ShouldBeFalse();
        set.Assemblies.Single().GetType("Thing").ShouldNotBeNull();
    }

    [Fact]
    public void LoadIsolated_UnloadsWhenDisposed()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("Unloadable"), "public class Thing { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        WeakReference context = LoadUseAndDispose(scan);
        CollectUntilDead(context);

        context.IsAlive.ShouldBeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadUseAndDispose(ScanResult scan)
    {
        IsolatedAssemblySet set = scan.LoadIsolated();
        var reference = new WeakReference(set.Contexts[0]);
        Fixture.Create(set.Assemblies[0], "Thing");

        set.Dispose();

        set.IsDisposed.ShouldBeTrue();
        set.Assemblies.ShouldBeEmpty();
        return reference;
    }

    [Fact]
    public void LoadIsolated_CollectsLoadFailures_WhenAskedNotToThrow()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("Fine"), "public class A { }");
        string broken = Fixture.Compile(_temp.Path, Fixture.UniqueName("Gone"), "public class A { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        File.Delete(broken);

        using IsolatedAssemblySet tolerant = scan.LoadIsolated(o => o.ThrowOnError = false);
        Action strict = () => scan.LoadIsolated();

        tolerant.Assemblies.ShouldHaveSingleItem();
        tolerant.Failures.ShouldContain(f => f.Reason == SkipReason.LoadFailed && f.Path == broken, 1);
        Should.Throw<IOException>(strict);
    }

    [Fact]
    public void IsolatedTypes_SurviveATypeThatCannotLoad()
    {
        string dependency = Fixture.Compile(_temp.Sub("elsewhere"), Fixture.UniqueName("AbsentBase"), "public class AbsentBase { }");
        Fixture.Compile(_temp.Sub("app"), Fixture.UniqueName("Partial"),
            "public class Broken : AbsentBase { } public class Intact { }", [dependency]);

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(Path.Combine(_temp.Path, "app")).Scan().LoadIsolated();
        TypeQuery types = set.Types();

        types.Select(t => t.Name).ShouldBe(["Intact"]);
        types.LoaderErrors.ShouldNotBeEmpty();
    }

    [Fact]
    public void LoadPlugins_CreatesInstances_ThatShareTheHostsContract()
    {
        Fixture.Compile(_temp.Sub("hello"), Fixture.UniqueName("HelloPlugin"), HelloPlugin);
        Fixture.Compile(_temp.Sub("howdy"), Fixture.UniqueName("HowdyPlugin"), HowdyPlugin);
        Fixture.Compile(_temp.Sub("unrelated"), Fixture.UniqueName("NotAPlugin"), "public class A { }");

        using PluginSet<IGreeter> plugins = Assemblies.Scan()
            .FromDirectory(_temp.Path, recursive: true)
            .Referencing<IGreeter>()
            .Scan()
            .LoadPlugins<IGreeter>();

        plugins.Select(p => p.Greet("Ada")).ShouldBe(["Hello Ada", "Howdy Ada"], ignoreOrder: true);
        plugins.PluginTypes.Count().ShouldBe(2);
        plugins.Failures.ShouldBeEmpty();
        plugins.IsIsolated.ShouldBeTrue();
        List<AssemblyLoadContext?> contexts = [.. plugins.PluginTypes.Select(t => AssemblyLoadContext.GetLoadContext(t.Assembly))];
        contexts.ShouldBeUnique();
        contexts.ShouldNotContain(AssemblyLoadContext.Default);
    }

    [Fact]
    public void LoadPlugins_InjectsConstructorParameters_FromAServiceProvider()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("ConfiguredPlugin"), """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            public class ConfiguredGreeter : IGreeter
            {
                private readonly GreetingOptions _options;
                public ConfiguredGreeter(GreetingOptions options) { _options = options; }
                public string Greet(string name) => _options.Prefix + " " + name;
            }
            """);
        ServiceProvider services = new ServiceCollection().AddSingleton(new GreetingOptions { Prefix = "Welcome" }).BuildServiceProvider();

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan()
            .LoadPlugins<IGreeter>(o => o.Services = services);

        plugins.ShouldHaveSingleItem().Greet("Ada").ShouldBe("Welcome Ada");
    }

    [Fact]
    public void LoadPlugins_ReportsABrokenPlugin_AndStillLoadsTheOthers()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("GoodPlugin"), HelloPlugin);
        Fixture.Compile(_temp.Path, Fixture.UniqueName("BadPlugin"), """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            public class ExplodingGreeter : IGreeter
            {
                public ExplodingGreeter() { throw new System.InvalidOperationException("boom"); }
                public string Greet(string name) => name;
            }
            """);
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        using PluginSet<IGreeter> plugins = scan.LoadPlugins<IGreeter>();
        Action strict = () => scan.LoadPlugins<IGreeter>(o => o.ThrowOnError = true);

        plugins.ShouldHaveSingleItem().Greet("Ada").ShouldBe("Hello Ada");
        ScanDiagnostic failure = plugins.Failures.ShouldHaveSingleItem();
        failure.Reason.ShouldBe(SkipReason.ActivationFailed);
        failure.Path.ShouldBe("ExplodingGreeter");
        failure.Message.ShouldBe("boom");
        Should.Throw<TargetInvocationException>(strict);
    }

    [Fact]
    public void LoadPlugins_NotIsolated_LoadsIntoTheDefaultContext()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("HostedPlugin"), HelloPlugin);

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan()
            .LoadPlugins<IGreeter>(o => o.Isolated = false);

        plugins.IsIsolated.ShouldBeFalse();
        AssemblyLoadContext.GetLoadContext(plugins.PluginTypes.Single().Assembly).ShouldBeSameAs(AssemblyLoadContext.Default);
    }

    [Fact]
    public void LoadPlugins_DisposesPluginsAndUnloads_WhenTheSetIsDisposed()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("DisposablePlugin"), """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            public class DisposableGreeter : IGreeter, System.IDisposable
            {
                public static int Disposed;
                public string Greet(string name) => name;
                public void Dispose() { Disposed++; }
            }
            """);
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        WeakReference context = LoadPluginsAndDispose(scan, out int disposed);
        CollectUntilDead(context);

        disposed.ShouldBe(1);
        context.IsAlive.ShouldBeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadPluginsAndDispose(ScanResult scan, out int disposed)
    {
        PluginSet<IGreeter> plugins = scan.LoadPlugins<IGreeter>();
        Type type = plugins.PluginTypes.Single();
        var reference = new WeakReference(AssemblyLoadContext.GetLoadContext(type.Assembly));

        plugins.Dispose();

        disposed = (int)type.GetField("Disposed")!.GetValue(null)!;
        plugins.ShouldBeEmpty();
        return reference;
    }

    [Theory]
    [InlineData("public interface IContract { }")]
    [InlineData("public abstract class IContract { }")]
    public void LoadPlugins_ExplainsAContractLoadedTwice(string contractSource)
    {
        // The plugin folder carries its own copy of the contract assembly and is told not to share it,
        // so its IContract is a different type from the host's.
        string contractName = Fixture.UniqueName("Contract");
        string hostCopy = Fixture.Compile(_temp.Sub("host"), contractName, contractSource);
        string pluginFolder = _temp.Sub("plugin");
        string pluginName = Fixture.UniqueName("Mismatched");
        File.Copy(hostCopy, Path.Combine(pluginFolder, contractName + ".dll"));
        Fixture.Compile(pluginFolder, pluginName, "public class Implementation : IContract { }", [hostCopy]);

        Type contract = AssemblyLoadContext.Default.LoadFromAssemblyPath(hostCopy).GetType("IContract")!;
        ScanResult scan = Assemblies.Scan().FromDirectory(pluginFolder).Include(pluginName).Scan();

        Action<PluginOptions> configure = o => o.Isolation.PreferSharedTypes = false;
        object plugins = typeof(ScanResult).GetMethod(nameof(ScanResult.LoadPlugins))!.MakeGenericMethod(contract).Invoke(scan, [configure])!;
        var failures = (IReadOnlyList<ScanDiagnostic>)plugins.GetType().GetProperty("Failures")!.GetValue(plugins)!;
        int count = (int)plugins.GetType().GetProperty("Count")!.GetValue(plugins)!;
        ((IDisposable)plugins).Dispose();

        count.ShouldBe(0);
        failures.ShouldHaveSingleItem().Message.ShouldContain("second copy of the contract assembly", Case.Sensitive);
    }

    [Fact]
    public void Share_KeepsTheContractShared_EvenWithoutPreferSharedTypes()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("ExplicitShare"), HelloPlugin);

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan()
            .LoadPlugins<IGreeter>(o =>
            {
                o.Isolation.PreferSharedTypes = false;
                o.Isolation.Share<IGreeter>();
            });

        plugins.ShouldHaveSingleItem().Greet("Ada").ShouldBe("Hello Ada");
    }

    [Fact]
    public void LoadIsolated_NeedsAFileOnDisk()
    {
        string name = Fixture.UniqueName("MemoryOnly");
        Assembly assembly = Fixture.LoadFromMemory(Fixture.Compile(_temp.Path, name, "public class A { }"));
        ScanResult scan = Assemblies.Scan().FromAssemblies(assembly).Scan();

        Action strict = () => scan.LoadIsolated();
        using IsolatedAssemblySet tolerant = scan.LoadIsolated(o => o.ThrowOnError = false);

        Should.Throw<InvalidOperationException>(strict).Message.ShouldContain("has no file path");
        tolerant.Assemblies.ShouldBeEmpty();
        tolerant.Contexts.ShouldBeEmpty();
        ScanDiagnostic failure = tolerant.Failures.ShouldHaveSingleItem();
        failure.Reason.ShouldBe(SkipReason.LoadFailed);
        failure.Path.ShouldBe(name + ".dll");
    }

    [Fact]
    public void LoadIsolated_InMemory_LoadsTheSymbolsNextToTheFile()
    {
        string name = Fixture.UniqueName("WithSymbols");
        Fixture.Compile(
            _temp.Path,
            name,
            """public class Thrower { public void Throw() { throw new System.InvalidOperationException("from plugin"); } }""",
            withSymbols: true);

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated(o => o.LoadInMemory = true);
        object thrower = Fixture.Create(set.Assemblies.Single(), "Thrower");
        Action act = () => Fixture.Call(thrower, "Throw");

        Exception thrown = Should.Throw<TargetInvocationException>(act).InnerException!;
        new System.Diagnostics.StackTrace(thrown, fNeedFileInfo: true).GetFrame(0)!.GetFileName().ShouldBe(name + ".cs");
    }

    [Fact]
    public void LoadIsolated_InMemory_LoadsEachImageOnce()
    {
        string path = Fixture.Compile(_temp.Path, Fixture.UniqueName("OneImage"), "public class Thing { }");

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated(o => o.LoadInMemory = true);

        // Loading the same image from a stream a second time would be an error.
        set.Contexts[0].LoadFile(path).ShouldBeSameAs(set.Assemblies[0]);
    }

    [Fact]
    public void LoadIsolated_LetsScannedAssembliesUseEachOther()
    {
        string libraryName = Fixture.UniqueName("ScannedLibrary");
        string library = Fixture.Compile(_temp.Path, libraryName, """public static class Secret { public static string Value => "from the scanned copy"; }""");
        Fixture.Compile(_temp.Path, Fixture.UniqueName("ScannedConsumer"), "public class Reader { public string Read() => Secret.Value; }", [library]);

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated(o => o.LoadInMemory = true);
        Assembly consumer = set.Assemblies.Single(a => a.GetType("Reader") is not null);
        object reader = Fixture.Create(consumer, "Reader");

        Fixture.Call(reader, "Read").ShouldBe("from the scanned copy");
        set.Contexts.ShouldHaveSingleItem().Assemblies.Count(a => a.GetName().Name == libraryName).ShouldBe(1);
    }

    [Fact]
    public void LoadIsolated_PicksUpAScannedFileLater_WhenItWasLockedAtLoadTime()
    {
        string name = Fixture.UniqueName("LockedAtFirst");
        string path = Fixture.Compile(_temp.Path, name, "public class Thing { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        IsolatedAssemblySet set;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            set = scan.LoadIsolated(o => { o.ThrowOnError = false; o.LoadInMemory = true; });

        using (set)
        {
            set.Assemblies.ShouldBeEmpty();
            set.Failures.ShouldHaveSingleItem().Reason.ShouldBe(SkipReason.LoadFailed);

            Assembly later = set.Contexts[0].LoadFromAssemblyName(new AssemblyName(name));

            later.GetType("Thing").ShouldNotBeNull();
            AssemblyLoadContext.GetLoadContext(later).ShouldBeSameAs(set.Contexts[0]);
        }
    }

    [Fact]
    public void LoadIsolated_FindsADependencyNextToThePlugin_ThatItsDepsFileDoesNotList()
    {
        string dependencyName = Fixture.UniqueName("Unlisted");
        string mainName = Fixture.UniqueName("HasDepsFile");
        string dependency = Fixture.Compile(_temp.Path, dependencyName, """public static class Secret { public static string Value => "found by probing"; }""");
        Fixture.Compile(_temp.Path, mainName, "public class Reader { public string Read() => Secret.Value; }", [dependency]);
        File.WriteAllText(
            Path.Combine(_temp.Path, mainName + ".deps.json"),
            """{"runtimeTarget":{"name":".NETCoreApp,Version=v8.0"},"targets":{".NETCoreApp,Version=v8.0":{}},"libraries":{}}""");

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Include(mainName).Scan().LoadIsolated();
        object reader = Fixture.Create(set.Assemblies.Single(), "Reader");

        Fixture.Call(reader, "Read").ShouldBe("found by probing");
    }

    [Fact]
    public void LoadIsolated_LeavesAMissingNativeLibraryToTheRuntime()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("CallsNative"), """
            using System.Runtime.InteropServices;
            public class Native
            {
                [DllImport("masterly_scanner_missing_native_library")]
                private static extern int Missing();
                public int Call() => Missing();
            }
            """);

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated();
        object native = Fixture.Create(set.Assemblies.Single(), "Native");
        Action act = () => Fixture.Call(native, "Call");

        Should.Throw<TargetInvocationException>(act).InnerException.ShouldBeOfType<DllNotFoundException>();
    }

    [Fact]
    public void LoadIsolated_Dispose_CanBeRepeated()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("DisposedTwice"), "public class A { }");
        IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated();

        set.Dispose();
        set.Dispose();

        set.IsDisposed.ShouldBeTrue();
        set.Contexts.ShouldBeEmpty();
        set.Types().ShouldBeEmpty();
    }

    [Fact]
    public void LoadIsolated_NotCollectible_KeepsTheContextAlive()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("Permanent"), "public class A { }");

        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated(o => o.Collectible = false);
        IsolatedLoadContext context = set.Contexts.ShouldHaveSingleItem();
        set.Dispose();

        context.IsCollectible.ShouldBeFalse();
        context.Assemblies.ShouldHaveSingleItem();
    }

    [Fact]
    public void Types_SkipATypeWhoseAttributeCannotBeLoaded()
    {
        string attributeLibrary = Fixture.Compile(_temp.Sub("build"), Fixture.UniqueName("AttributeLibrary"), "public class GoneAttribute : System.Attribute { }");
        Fixture.Compile(_temp.Path, Fixture.UniqueName("Decorated"), "[Gone] public class Decorated { } [System.Obsolete] public class Plain { }", [attributeLibrary]);

        // The attribute's assembly is not next to the scanned file, so reading Decorated's attributes fails.
        using IsolatedAssemblySet set = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadIsolated();

        set.Types().Select(t => t.Name).ShouldBe(["Decorated", "Plain"], ignoreOrder: true);
        set.Types().WithAttribute<ObsoleteAttribute>().Select(t => t.Name).ShouldBe(["Plain"]);
        set.Types().InNamespace("Shapes").ShouldBeEmpty();
    }

    [Fact]
    public void PluginSet_IsAReadOnlyList()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("ListedPlugin"), HelloPlugin);

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadPlugins<IGreeter>();

        plugins.Count.ShouldBe(1);
        plugins[0].Greet("Ada").ShouldBe("Hello Ada");
        Fixture.Enumerate(plugins).ShouldBe(plugins);
    }

    [Fact]
    public void PluginSet_Dispose_SurvivesAPluginThatFailsToDispose_AndCanBeRepeated()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("FailsToDispose"), """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            public class StubbornGreeter : IGreeter, System.IDisposable
            {
                public string Greet(string name) => name;
                public void Dispose() { throw new System.InvalidOperationException("will not go"); }
            }
            """);
        Fixture.Compile(_temp.Path, Fixture.UniqueName("DisposesFine"), HelloPlugin);
        PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadPlugins<IGreeter>();
        plugins.Count.ShouldBe(2);

        plugins.Dispose();
        plugins.Dispose();

        plugins.ShouldBeEmpty();
        plugins.PluginTypes.ShouldBeEmpty();
    }

    [Fact]
    public void LoadPlugins_NotIsolatedAndStrict_LoadsIntoTheDefaultContext()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("StrictHostedPlugin"), HelloPlugin);

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan()
            .LoadPlugins<IGreeter>(o =>
            {
                o.Isolated = false;
                o.ThrowOnError = true;
            });

        plugins.ShouldHaveSingleItem().Greet("Ada").ShouldBe("Hello Ada");
        plugins.IsIsolated.ShouldBeFalse();
    }

    [Fact]
    public void LoadPlugins_ReportsAPluginWhoseDependenciesAreNotRegistered()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("NeedsOptions"), """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            public class NeedyGreeter : IGreeter
            {
                public NeedyGreeter(GreetingOptions options) { }
                public string Greet(string name) => name;
            }
            """);
        ServiceProvider empty = new ServiceCollection().BuildServiceProvider();

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan()
            .LoadPlugins<IGreeter>(o => o.Services = empty);

        plugins.ShouldBeEmpty();
        ScanDiagnostic failure = plugins.Failures.ShouldHaveSingleItem();
        failure.Reason.ShouldBe(SkipReason.ActivationFailed);
        failure.Path.ShouldBe("NeedyGreeter");
        failure.Exception.ShouldBeOfType<InvalidOperationException>();
        failure.Message.ShouldContain("GreetingOptions");
    }

    [Fact]
    public void LoadPlugins_FindsNothing_InAnAssemblyWithoutPlugins()
    {
        Fixture.Compile(_temp.Path, Fixture.UniqueName("NoPluginsHere"), "public class Base { } public class Derived : Base { }");

        using PluginSet<IGreeter> plugins = Assemblies.Scan().FromDirectory(_temp.Path).Scan().LoadPlugins<IGreeter>();

        plugins.ShouldBeEmpty();
        plugins.PluginTypes.ShouldBeEmpty();
        plugins.Failures.ShouldBeEmpty();
    }
}
