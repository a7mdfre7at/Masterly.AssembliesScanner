using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Masterly.AssembliesScanner.UnitTests;

public class CompatibilityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Builds a plugin against version 1 of a contract, then puts version 2 next to it, which is what happens when a
    /// host upgrades its contract and leaves old plugins in place.
    /// </summary>
    private Upgrade BuildUpgrade(string contractV1, string contractV2, string plugin)
    {
        string contract = Fixture.UniqueName("Contract");
        string pluginName = Fixture.UniqueName("Plugin");
        string build = _temp.Sub(Fixture.UniqueName("build"));
        string run = _temp.Sub(Fixture.UniqueName("run"));

        string v1 = Fixture.Compile(build, contract, contractV1);
        Fixture.Compile(run, pluginName, plugin, [v1]);
        Fixture.Compile(run, contract, contractV2);
        return new Upgrade(run, contract, pluginName);
    }

    private sealed record Upgrade(string Directory, string Contract, string Plugin)
    {
        public ScanResult Scan() => Assemblies.Scan().FromDirectory(Directory).Scan();

        public CompatibilityReport Check(Action<CompatibilityOptions>? configure = null) => Scan().CheckCompatibility(configure);

        public IReadOnlyList<CompatibilityIssue> PluginIssues(Action<CompatibilityOptions>? configure = null) => Check(configure).For(Plugin);
    }

    // ---------------------------------------------------------------- nothing changed

    [Fact]
    public void UnchangedContract_IsCompatible_EvenWithGenericsOverridesAndExplicitImplementations()
    {
        const string contract = """
            namespace Contract;
            public interface IHandler<T> { void Handle(T item); }
            public interface IGreeter { string Greet(string name); }
            public interface IParse<TSelf> where TSelf : IParse<TSelf> { static abstract TSelf Parse(string text); }
            public abstract class Repository<T> { public abstract T Find(int id); public abstract void Save(T item); public virtual int Count => 0; }
            public abstract class Middle<T> : Repository<T> { public override T Find(int id) => default!; }
            public class Box<T> { public T Value = default!; public T Get() => Value; public TOut Map<TOut>(System.Func<T, TOut> map) => map(Value); }
            public class Outer { public class Inner { public static int Depth = 2; } }
            public class Events { public event System.EventHandler? Changed; public void Raise() => Changed?.Invoke(this, System.EventArgs.Empty); }
            public class Settings { public string Name { get; init; } = ""; }
            """;
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Order { }
            public class OrderHandler : IHandler<Order> { public void Handle(Order item) { } }
            public class ExplicitHandler : IHandler<Order>, IGreeter
            {
                void IHandler<Order>.Handle(Order item) { }
                string IGreeter.Greet(string name) => name;
            }
            public class GenericHandler<T> : IHandler<T> { public void Handle(T item) { } }
            public struct Number : IParse<Number> { public static Number Parse(string text) => default; }
            public struct OtherNumber : IParse<OtherNumber> { static OtherNumber IParse<OtherNumber>.Parse(string text) => default; }
            public class OrderRepository : Repository<Order> { public override Order Find(int id) => new(); public override void Save(Order item) { } }
            public class Finished : Middle<Order> { public override void Save(Order item) { } public override int Count => 1; }
            public class User
            {
                public int Use(Box<int> box) => box.Get() + box.Value + box.Map(v => v * 2) + Outer.Inner.Depth;
                public Settings Make() => new Settings { Name = "x" };
                public void Listen(Events events) { events.Changed += (_, _) => { }; events.Raise(); }
            }
            """;
        Upgrade upgrade = BuildUpgrade(contract, contract, plugin);

        CompatibilityReport report = upgrade.Check();

        report.IsCompatible.ShouldBeTrue(string.Join(Environment.NewLine, report));
        report.Count.ShouldBe(0);
        report.CheckedAssemblies.Select(a => a.Name).ShouldBe([upgrade.Contract, upgrade.Plugin], ignoreOrder: true);
        report.Skipped.ShouldBeEmpty();
        report.IncompatibleAssemblies.ShouldBeEmpty();
        report.CompatibleAssemblies.Count.ShouldBe(2);
        Should.NotThrow(report.ThrowIfIncompatible);
        report.ToMarkdown().ShouldContain("2 checked, 0 with problems, 0 problems.");
        report.ToMarkdown().ShouldNotContain("| Assembly |");
    }

    [Fact]
    public void ChangesThatKeepBinaryCompatibility_AreNotReported()
    {
        // A method moved to a base class, a default interface member added, a new overload added.
        const string v1 = """
            namespace Contract;
            public class Animal { }
            public class Dog : Animal { public string Bark() => "woof"; }
            public interface IGreeter { string Greet(string name); }
            public static class Api { public static int Add(int a, int b) => a + b; }
            """;
        const string v2 = """
            namespace Contract;
            public class Animal { public string Bark() => "woof"; }
            public class Dog : Animal { }
            public interface IGreeter { string Greet(string name); string Shout(string name) => Greet(name).ToUpperInvariant(); }
            public static class Api { public static int Add(int a, int b) => a + b; public static long Add(long a, long b) => a + b; }
            """;
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Greeter : IGreeter { public string Greet(string name) => name; }
            public class User { public string Use() => new Dog().Bark() + Api.Add(1, 2); }
            """;

        Upgrade upgrade = BuildUpgrade(v1, v2, plugin);

        upgrade.PluginIssues().ShouldBeEmpty();
        upgrade.PluginIssues(o => o.CheckFrameworkReferences = false).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- members

    [Fact]
    public void RemovedOverload_IsAMissingMethod_ThatNamesItsCallersAndWhatIsThereInstead()
    {
        const string v1 = "namespace Contract; public interface IHost { void Log(string message); }";
        const string v2 = "namespace Contract; public interface IHost { void Log(string message, int level); void Log(System.Exception error); }";
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Runner
            {
                public void Run(IHost host) => host.Log("started");
                public void Safe(IHost host) { }
            }
            public static class Twice { public static void Run(IHost host) { host.Log("a"); host.Log("b"); } }
            """;

        CompatibilityIssue issue = BuildUpgrade(v1, v2, plugin).PluginIssues().ShouldHaveSingleItem();

        issue.Kind.ShouldBe(CompatibilityIssueKind.MissingMethod);
        issue.Member.ShouldBe("Contract.IHost.Log(System.String)");
        issue.ExpectedException.ShouldBe(typeof(MissingMethodException));
        issue.Message.ShouldContain("It has: Log(System.String, System.Int32); Log(System.Exception).", Case.Sensitive);
        issue.UsedBy.ShouldBe(["PluginCode.Runner.Run", "PluginCode.Twice.Run"]);
        issue.TargetAssembly.ShouldStartWith("Contract_");
        issue.ToString().ShouldStartWith(issue.Assembly.Name + ": MissingMethod Contract.IHost.Log(System.String) - Calls", Case.Sensitive);
    }

    [Fact]
    public void ChangingStaticToInstance_OrRemovingAConstructor_IsAMissingMethod()
    {
        const string v1 = """
            namespace Contract;
            public class Clock { public int Now() => 1; }
            public class Shape { public Shape(int size) { } }
            public class Widget : Shape { public Widget(int size) : base(size) { } }
            """;
        const string v2 = """
            namespace Contract;
            public class Clock { public static int Now() => 1; }
            public class Shape { public Shape(int size) { } }
            public class Widget : Shape { public Widget() : base(1) { } }
            """;
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class User { public int Use() => new Clock().Now() + (new Widget(3) is null ? 0 : 1); }
            """;

        IReadOnlyList<CompatibilityIssue> issues = BuildUpgrade(v1, v2, plugin).PluginIssues();

        issues.Select(i => i.Member).ShouldBe(["Contract.Clock.Now()", "Contract.Widget..ctor(System.Int32)"], ignoreOrder: true);
        issues.Single(i => i.Member == "Contract.Clock.Now()").Message.ShouldContain("It has: static Now().", Case.Sensitive);

        // Constructors are not inherited: Shape(int) does not satisfy Widget(int).
        issues.Single(i => i.Member.StartsWith("Contract.Widget")).Message.ShouldContain("It has: .ctor().", Case.Sensitive);
    }

    [Fact]
    public void ChangedOrRemovedField_IsAMissingField()
    {
        const string v1 = "namespace Contract; public class Settings { public int Retries; public string Name = \"\"; }";
        const string v2 = "namespace Contract; public class Settings { public long Retries; }";
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class User { public string Use(Settings s) => s.Retries + s.Name; }
            """;

        IReadOnlyList<CompatibilityIssue> issues = BuildUpgrade(v1, v2, plugin).PluginIssues();

        issues.ShouldAllBe(i => i.Kind == CompatibilityIssueKind.MissingField && i.ExpectedException == typeof(MissingFieldException));
        issues.Single(i => i.Member == "Contract.Settings.Retries").Message.ShouldContain("as System.Int32, but in", Case.Sensitive);
        issues.Single(i => i.Member == "Contract.Settings.Retries").Message.ShouldEndWith("it is System.Int64.", Case.Sensitive);
        issues.Single(i => i.Member == "Contract.Settings.Name").UsedBy.ShouldBe(["PluginCode.User.Use"]);
    }

    [Fact]
    public void MembersOfGenericTypes_AreComparedInTheirOpenForm()
    {
        const string v1 = "namespace Contract; public class Box<T> { public T Value = default!; public T Get() => Value; }";
        const string v2 = "namespace Contract; public class Box<T> { public T Value = default!; public T Take() => Value; }";
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Reader { public int Read(Box<int> box) => box.Get() + box.Value; }
            """;

        CompatibilityIssue issue = BuildUpgrade(v1, v2, plugin).PluginIssues().ShouldHaveSingleItem();

        issue.Member.ShouldBe("Contract.Box`1<System.Int32>.Get()");
        issue.UsedBy.ShouldBe(["PluginCode.Reader.Read"]);
    }

    // ---------------------------------------------------------------- types

    [Fact]
    public void RemovedTypes_AreMissingTypes_UsedByEveryTypeAndMethodThatTouchesThem()
    {
        const string v1 = """
            namespace Contract;
            public class Gone { public void Work() { } }
            public class Outer { public class Inner { } }
            """;
        const string v2 = "namespace Contract; public class Outer { }";
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Derived : Gone { }
            public class User { public void Use(Gone gone) => gone.Work(); public object Nested() => new Outer.Inner(); }
            """;

        IReadOnlyList<CompatibilityIssue> issues = BuildUpgrade(v1, v2, plugin).PluginIssues();

        issues.ShouldAllBe(i => i.Kind == CompatibilityIssueKind.MissingType && i.ExpectedException == typeof(TypeLoadException));
        issues.Single(i => i.Member == "Contract.Gone").UsedBy.ShouldBe(["PluginCode.Derived", "PluginCode.Derived..ctor", "PluginCode.User.Use"]);
        issues.Single(i => i.Member == "Contract.Outer+Inner").UsedBy.ShouldBe(["PluginCode.User.Nested"]);
    }

    [Fact]
    public void InterfaceThatGainsAMember_LeavesOldImplementationsIncomplete()
    {
        const string v1 = """
            namespace Contract;
            public interface IPlugin { string Name { get; } }
            public interface IHandler<T> { void Handle(T item); }
            """;
        const string v2 = """
            namespace Contract;
            public interface IPlugin { string Name { get; } void Start(); }
            public interface IHandler<T> { void Handle(T item); void Reset(); }
            """;
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Order { }
            public class MyPlugin : IPlugin { public string Name => "mine"; }
            public abstract class AbstractPlugin : IPlugin { public string Name => "abstract"; }
            public class OrderHandler : IHandler<Order> { public void Handle(Order item) { } }
            """;

        IReadOnlyList<CompatibilityIssue> issues = BuildUpgrade(v1, v2, plugin).PluginIssues();

        // An abstract class must still provide (possibly abstract) implementations of interface members.
        issues.Select(i => i.Member).ShouldBe([
            "PluginCode.MyPlugin : Contract.IPlugin.Start()",
            "PluginCode.AbstractPlugin : Contract.IPlugin.Start()",
            "PluginCode.OrderHandler : Contract.IHandler`1<PluginCode.Order>.Reset()"], ignoreOrder: true);
        issues.ShouldAllBe(i => i.Kind == CompatibilityIssueKind.MissingImplementation && i.ExpectedException == typeof(TypeLoadException));
        issues.Single(i => i.Member.StartsWith("PluginCode.MyPlugin")).UsedBy.ShouldBe(["PluginCode.MyPlugin"]);
    }

    [Fact]
    public void AbstractBaseClassThatGainsAMethod_LeavesConcreteSubclassesIncomplete()
    {
        const string v1 = "namespace Contract; public abstract class PluginBase { public abstract string Name { get; } }";
        const string v2 = "namespace Contract; public abstract class PluginBase { public abstract string Name { get; } public abstract void Start(); }";
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class MyPlugin : PluginBase { public override string Name => "mine"; }
            public abstract class StillAbstract : PluginBase { }
            """;

        CompatibilityIssue issue = BuildUpgrade(v1, v2, plugin).PluginIssues().ShouldHaveSingleItem();

        issue.Kind.ShouldBe(CompatibilityIssueKind.MissingImplementation);
        issue.Member.ShouldBe("PluginCode.MyPlugin : Contract.PluginBase.Start()");
        issue.Message.ShouldContain("declares abstract");
    }

    [Fact]
    public void BaseTypeThatBecameSealedOrAnInterface_AndInterfaceThatBecameAClass_AreInvalidInheritance()
    {
        const string v1 = "namespace Contract; public class Base { } public class Shape { } public interface IMarker { }";
        const string v2 = "namespace Contract; public sealed class Base { } public interface Shape { } public class IMarker { }";
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class Derived : Base { }
            public class Square : Shape { }
            public class Marked : IMarker { }
            """;

        IReadOnlyList<CompatibilityIssue> issues = BuildUpgrade(v1, v2, plugin).PluginIssues();

        // Square's constructor also calls Shape's, which an interface does not have.
        issues.Where(i => i.Kind != CompatibilityIssueKind.InvalidInheritance).ShouldHaveSingleItem().Member.ShouldBe("Contract.Shape..ctor()");
        issues.Where(i => i.Kind == CompatibilityIssueKind.InvalidInheritance).ShouldAllBe(i => i.ExpectedException == typeof(TypeLoadException));
        issues.Single(i => i.Member == "PluginCode.Derived : Contract.Base").Message.ShouldContain("which is sealed in");
        issues.Single(i => i.Member == "PluginCode.Square : Contract.Shape").Message.ShouldContain("which is an interface in");
        issues.Single(i => i.Member == "PluginCode.Marked : Contract.IMarker").Message.ShouldContain("no longer an interface");
    }

    // ---------------------------------------------------------------- accessibility

    private const string PublicTools = """
        namespace Contract;
        public class Tools { public static int Count() => 1; public static int Limit = 3; public static int Hidden() => 2; }
        public class Secret { }
        public class Outer { public class Nested { } }
        """;

    private const string InternalTools = """
        namespace Contract;
        public class Tools { internal static int Count() => 1; internal static int Limit = 3; private static int Hidden() => 2; }
        internal class Secret { }
        public class Outer { private class Nested { } }
        """;

    [Fact]
    public void MembersAndTypesThatAreNoLongerPublic_FailWhereTheRuntimeChecksAccess()
    {
        const string plugin = """
            using Contract;
            namespace PluginCode;
            public class User
            {
                public int Use() => Tools.Count() + Tools.Limit + Tools.Hidden();
                public object Make() => new Secret();
                public object MakeNested() => new Outer.Nested();
            }
            """;

        IReadOnlyList<CompatibilityIssue> issues = BuildUpgrade(PublicTools, InternalTools, plugin).PluginIssues();

        issues.Select(i => (i.Kind, i.Member, i.ExpectedException.Name)).ShouldBe([
            (CompatibilityIssueKind.MemberNotAccessible, "Contract.Tools.Count()", nameof(MethodAccessException)),
            (CompatibilityIssueKind.MemberNotAccessible, "Contract.Tools.Limit", nameof(FieldAccessException)),
            (CompatibilityIssueKind.MemberNotAccessible, "Contract.Tools.Hidden()", nameof(MethodAccessException)),
            (CompatibilityIssueKind.TypeNotAccessible, "Contract.Secret", nameof(TypeAccessException)),
            (CompatibilityIssueKind.TypeNotAccessible, "Contract.Outer+Nested", nameof(TypeAccessException))], ignoreOrder: true);
        issues.Single(i => i.Member == "Contract.Secret").UsedBy.ShouldBe(["PluginCode.User.Make"]);
    }

    [Fact]
    public void HiddenTypeThatOnlyAppearsInASignature_IsNotReported()
    {
        // The runtime does not check access for parameter types, so this method still runs.
        const string plugin = "namespace PluginCode; public class User { public void Accept(Contract.Secret secret) { } }";

        BuildUpgrade(PublicTools, InternalTools, plugin).PluginIssues().ShouldBeEmpty();
    }

    [Fact]
    public void InternalsVisibleTo_KeepsInternalMembersAccessible()
    {
        string contract = Fixture.UniqueName("Contract");
        string pluginName = Fixture.UniqueName("Plugin");
        string build = _temp.Sub("build");
        string v1 = Fixture.Compile(build, contract, PublicTools);
        Fixture.Compile(_temp.Path, pluginName, "using Contract; public class User { public int Use() => Tools.Count() + Tools.Limit; public object Make() => new Secret(); }", [v1]);
        Fixture.Compile(_temp.Path, contract, $"""[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("{pluginName}")]""" + InternalTools);

        Assemblies.Scan().FromDirectory(_temp.Path).Scan().CheckCompatibility().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- assemblies

    [Fact]
    public void ForwardedType_IsFollowedToItsNewAssembly_AndAMissingTargetIsReported()
    {
        string newHome = Fixture.UniqueName("NewHome");
        string contract = Fixture.UniqueName("Contract");
        string pluginName = Fixture.UniqueName("Plugin");
        string build = _temp.Sub("build");

        string v1 = Fixture.Compile(build, contract, "namespace Contract; public class Moved { public int Value => 1; }");
        Fixture.Compile(_temp.Path, pluginName, "public class User { public int Use() => new Contract.Moved().Value; }", [v1]);
        string home = Fixture.Compile(_temp.Path, newHome, "namespace Contract; public class Moved { public int Value => 1; }");
        Fixture.Compile(_temp.Path, contract, "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Contract.Moved))]", [home]);

        Assemblies.Scan().FromDirectory(_temp.Path).Scan().CheckCompatibility().For(pluginName).ShouldBeEmpty();

        File.Delete(home);
        CompatibilityIssue issue = Assemblies.Scan().FromDirectory(_temp.Path).Scan().CheckCompatibility().For(pluginName).ShouldHaveSingleItem();
        issue.Kind.ShouldBe(CompatibilityIssueKind.MissingAssembly);
        issue.TargetAssembly.ShouldBe(newHome);
        issue.Message.ShouldBe($"Contract.Moved is forwarded to {newHome}, which cannot be found.");
        issue.UsedBy.ShouldBe(["User.Use"]);
    }

    [Fact]
    public void MissingAssembly_IsReportedOnce_WithEveryUser()
    {
        string contract = Fixture.UniqueName("Contract");
        string pluginName = Fixture.UniqueName("Plugin");
        string v1 = Fixture.Compile(_temp.Sub("build"), contract, "namespace Contract; public class Api { public static int One() => 1; public static int Two() => 2; }");
        Fixture.Compile(_temp.Path, pluginName, "public class User { public int Use() => Contract.Api.One() + Contract.Api.Two(); }", [v1]);

        // A file with the right name that is not an assembly does not count.
        File.WriteAllText(Path.Join(_temp.Path, contract + ".dll"), "not an assembly");

        CompatibilityIssue issue = Assemblies.Scan().FromDirectory(_temp.Path).Scan().CheckCompatibility().ShouldHaveSingleItem();

        issue.Kind.ShouldBe(CompatibilityIssueKind.MissingAssembly);
        issue.Member.ShouldBe($"{contract}, Version=0.0.0.0");
        issue.ExpectedException.ShouldBe(typeof(FileNotFoundException));
        issue.UsedBy.ShouldBe(["User.Use"]);
    }

    [Fact]
    public void OlderVersionOfAReference_IsTooLow()
    {
        const string v1 = """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] namespace Contract; public class Api { }""";
        const string v2 = """[assembly: System.Reflection.AssemblyVersion("1.5.0.0")] namespace Contract; public class Api { }""";
        Upgrade upgrade = BuildUpgrade(v1, v2, "public class User { public object Use() => new Contract.Api(); }");

        CompatibilityIssue issue = upgrade.PluginIssues().ShouldHaveSingleItem();

        issue.Kind.ShouldBe(CompatibilityIssueKind.AssemblyVersionTooLow);
        issue.ExpectedException.ShouldBe(typeof(FileLoadException));
        issue.Message.ShouldStartWith($"Was built against {upgrade.Contract} 2.0.0.0, but 1.5.0.0 would be loaded from ", Case.Sensitive);
    }

    // ---------------------------------------------------------------- options

    [Fact]
    public void PreferHostAssemblies_DecidesWhetherTheHostsCopyOrThePluginsCopyIsChecked()
    {
        // The host already loaded version 1 of the contract; the plugin was built against version 2 and ships it.
        string contract = Fixture.UniqueName("Contract");
        string pluginName = Fixture.UniqueName("Plugin");
        string hostCopy = Fixture.Compile(_temp.Sub("host"), contract, "namespace Contract; public class Api { }");
        string pluginCopy = Fixture.Compile(_temp.Sub("plugin"), contract, "namespace Contract; public class Api { public static int Added() => 1; }");
        Fixture.Compile(_temp.Path + "/plugin", pluginName, "public class User { public int Use() => Contract.Api.Added(); }", [pluginCopy]);
        AssemblyLoadContext.Default.LoadFromAssemblyPath(hostCopy);
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path + "/plugin").Include(pluginName).Scan();

        CompatibilityReport hostFirst = scan.CheckCompatibility();
        CompatibilityReport pluginFirst = scan.CheckCompatibility(o => o.PreferHostAssemblies = false);

        hostFirst.ShouldHaveSingleItem().Member.ShouldBe("Contract.Api.Added()");
        hostFirst[0].Message.ShouldContain(contract);
        pluginFirst.IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void ProbingDirectories_AreSearchedForReferences()
    {
        string contract = Fixture.UniqueName("Contract");
        string pluginName = Fixture.UniqueName("Plugin");
        string libraries = _temp.Sub("lib");
        string plugins = _temp.Sub("plugins");
        string library = Fixture.Compile(libraries, contract, "namespace Contract; public class Api { }");
        Fixture.Compile(plugins, pluginName, "public class User { public object Use() => new Contract.Api(); }", [library]);
        ScanResult scan = Assemblies.Scan().FromDirectory(plugins).Scan();

        scan.CheckCompatibility().ShouldHaveSingleItem().Kind.ShouldBe(CompatibilityIssueKind.MissingAssembly);
        scan.CheckCompatibility(o => o.ProbingDirectories.Add(libraries)).IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void AnAssemblyBuiltForANewerRuntime_IsCaught_UnlessFrameworkReferencesAreSkipped()
    {
        Fixture.WriteAssemblyForRuntime(_temp.Path, Fixture.UniqueName("Future"), new Version(99, 0, 0, 0));
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        CompatibilityIssue issue = scan.CheckCompatibility().ShouldHaveSingleItem();
        issue.Kind.ShouldBe(CompatibilityIssueKind.AssemblyVersionTooLow);
        issue.Member.ShouldBe("System.Runtime, Version=99.0.0.0");
        issue.UsedBy.ShouldBe(["Future.Thing"]);

        scan.CheckCompatibility(o => o.CheckFrameworkReferences = false).IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void FrameworkAssemblies_AreSkippedUnlessAskedFor_AndAssembliesWithoutAFileCannotBeChecked()
    {
        string path = Fixture.Compile(_temp.Path, Fixture.UniqueName("InMemory"), "public class A { }");
        ScanResult scan = Assemblies.Scan().FromAssemblies(typeof(object).Assembly, Fixture.LoadFromMemory(path)).Scan();

        CompatibilityReport skipped = scan.CheckCompatibility();
        CompatibilityReport framework = scan.CheckCompatibility(o => o.CheckFrameworkAssemblies = true);

        skipped.CheckedAssemblies.ShouldBeEmpty();
        skipped.Skipped.Count.ShouldBe(2);
        framework.CheckedAssemblies.ShouldHaveSingleItem().Name.ShouldBe("System.Private.CoreLib");
        framework.IsCompatible.ShouldBeTrue();
    }

    [Fact]
    public void AFileThatDisappearedAfterTheScan_IsSkipped()
    {
        string path = Fixture.Compile(_temp.Path, Fixture.UniqueName("Vanishing"), "public class A { }");
        ScanResult scan = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        File.Delete(path);

        scan.CheckCompatibility().Skipped.ShouldHaveSingleItem();
    }

    [Fact]
    public void OddMetadata_DoesNotStopTheCheck()
    {
        Fixture.WriteAssemblyWithOddMetadata(_temp.Path, Fixture.UniqueName("Odd"));

        Should.NotThrow(() => Assemblies.Scan().FromDirectory(_temp.Path).Scan().CheckCompatibility());
    }

    // ---------------------------------------------------------------- scanner filter

    [Fact]
    public void RequireCompatibility_LeavesOutIncompatibleAssemblies_WithTheFirstProblemAsTheReason()
    {
        const string v1 = "namespace Contract; public static class Api { public static int One() => 1; public static int Two() => 2; }";
        const string v2 = "namespace Contract; public static class Api { }";
        Upgrade broken = BuildUpgrade(v1, v2, "public class User { public int Use() => Contract.Api.One() + Contract.Api.Two(); }");
        Upgrade once = BuildUpgrade(v1, "namespace Contract; public static class Api { public static int Two() => 2; }",
            "public class User { public int Use() => Contract.Api.One() + Contract.Api.Two(); }");

        ScanResult result = Assemblies.Scan().FromDirectories(broken.Directory, once.Directory).RequireCompatibility().Scan();

        result.Select(a => a.Name).ShouldBe([broken.Contract, once.Contract], ignoreOrder: true);
        ScanDiagnostic two = result.Diagnostics.Single(d => d.Path.Contains(broken.Plugin));
        two.Reason.ShouldBe(SkipReason.Incompatible);
        two.Message.ShouldStartWith("Not compatible: MissingMethod Contract.Api.", Case.Sensitive);
        two.Message.ShouldEndWith("(1 more problem)", Case.Sensitive);
        result.Diagnostics.Single(d => d.Path.Contains(once.Plugin)).Message.ShouldEndWith("does not have.", Case.Sensitive);
    }

    [Fact]
    public void RequireCompatibility_KeepsWhatItCannotOrNeedNotCheck()
    {
        string path = Fixture.Compile(_temp.Path, Fixture.UniqueName("InMemory"), "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromAssemblies(typeof(object).Assembly, Fixture.LoadFromMemory(path))
            .RequireCompatibility(o => o.CheckFrameworkReferences = false)
            .Scan();

        result.Count.ShouldBe(2);
        result.Diagnostics.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- the report

    [Fact]
    public void Report_FiltersByAssembly_RendersMarkdown_AndThrowsWithEveryProblem()
    {
        const string v1 = "namespace Contract; public class Box<T> { public T Get() => default!; } public class Api { public static int One() => 1; }";
        const string v2 = "namespace Contract; public class Box<T> { } public class Api { }";
        Upgrade upgrade = BuildUpgrade(v1, v2, "public class User { public int Use(Contract.Box<int> box) => box.Get() + Contract.Api.One(); }");
        ScanResult scan = upgrade.Scan();

        CompatibilityReport report = scan.CheckCompatibility();

        report.Count.ShouldBe(2);
        report[0].ShouldBeSameAs(report.First());
        Fixture.Enumerate(report).Count.ShouldBe(2);
        report.For(scan.Find(upgrade.Plugin)!).Count.ShouldBe(2);
        report.For(upgrade.Plugin + ".dll").Count.ShouldBe(2);
        report.For(scan.Find(upgrade.Contract)!).ShouldBeEmpty();
        report.IncompatibleAssemblies.ShouldHaveSingleItem().Name.ShouldBe(upgrade.Plugin);
        report.CompatibleAssemblies.ShouldHaveSingleItem().Name.ShouldBe(upgrade.Contract);
        Should.Throw<ArgumentNullException>(() => report.For((AssemblyDescriptor)null!));
        Should.Throw<ArgumentException>(() => report.For(""));

        string markdown = report.ToMarkdown();
        markdown.ShouldContain("2 checked, 1 with problems, 2 problems.");
        markdown.ShouldContain($"| {upgrade.Plugin} | MissingMethod | Contract.Box`1&lt;System.Int32&gt;.Get() | MissingMethodException | User.Use |");

        string message = Should.Throw<InvalidOperationException>(report.ThrowIfIncompatible).Message;
        message.ShouldStartWith("1 assembly is not compatible with what the host provides:", Case.Sensitive);
        message.ShouldContain("Contract.Api.One()");
        message.ShouldContain("Contract.Box`1<System.Int32>.Get()");
    }

    [Fact]
    public void Report_CountsSeveralIncompatibleAssemblies_AndShowsMethodsWithoutUsers()
    {
        string contract = Fixture.UniqueName("Contract");
        string v1 = Fixture.Compile(_temp.Sub("build"), contract, "namespace Contract; public class Api { }");
        Fixture.Compile(_temp.Path, Fixture.UniqueName("First"), "public class A : Contract.Api { }", [v1]);
        Fixture.Compile(_temp.Path, Fixture.UniqueName("Second"), "public class B { public Contract.Api? Field; }", [v1]);

        CompatibilityReport report = Assemblies.Scan().FromDirectory(_temp.Path).Scan().CheckCompatibility();

        Should.Throw<InvalidOperationException>(report.ThrowIfIncompatible).Message.ShouldStartWith("2 assemblies are not", Case.Sensitive);
        report.Single(i => i.UsedBy.Count == 0).Assembly.Name.ShouldStartWith("Second");
        report.ToMarkdown().ShouldContain("| FileNotFoundException | - |");
    }

    // ---------------------------------------------------------------- against the real runtime

    [Fact]
    public void Predictions_MatchWhatTheRuntimeThrows()
    {
        const string v1 = """
            namespace Contract;
            public static class Api { public static string Hello() => "hi"; }
            public interface IPlugin { string Name { get; } }
            """;
        const string v2 = """
            namespace Contract;
            public static class Api { public static string Hello(string name) => name; }
            public interface IPlugin { string Name { get; } void Start(); }
            """;
        const string plugin = """
            namespace PluginCode;
            public class Runner { public string Run() => Contract.Api.Hello(); }
            public class MyPlugin : Contract.IPlugin { public string Name => "mine"; }
            """;
        Upgrade upgrade = BuildUpgrade(v1, v2, plugin);
        IReadOnlyList<CompatibilityIssue> predicted = upgrade.PluginIssues();

        using IsolatedAssemblySet set = upgrade.Scan().LoadIsolated();
        Assembly loaded = set.Assemblies.Single(a => a.GetName().Name == upgrade.Plugin);
        object runner = Fixture.Create(loaded, "PluginCode.Runner");
        Exception call = Should.Throw<TargetInvocationException>(() => Fixture.Call(runner, "Run")).InnerException!;
        Exception load = Should.Throw<TypeLoadException>(() => loaded.GetType("PluginCode.MyPlugin", throwOnError: true));

        predicted.Single(i => i.Kind == CompatibilityIssueKind.MissingMethod).ExpectedException.ShouldBe(call.GetType());
        predicted.Single(i => i.Kind == CompatibilityIssueKind.MissingImplementation).ExpectedException.ShouldBe(load.GetType());
        predicted.Count.ShouldBe(2);
    }

    [Fact]
    public void TheTestOutputFolder_OurOwnAssembliesAreCompatible_AndTheKnownDiffEngineBreakIsFound()
    {
        CompatibilityReport report = Assemblies.Scan().FromBaseDirectory().Scan().CheckCompatibility();

        report.For("Masterly.AssembliesScanner").ShouldBeEmpty();
        report.For("Masterly.AssembliesScanner.UnitTests").ShouldBeEmpty();

        // The test project pins DiffEngine past the version Shouldly 4.3.0 was built against (see the csproj).
        CompatibilityIssue diffEngine = report.For("Shouldly").ShouldHaveSingleItem();
        diffEngine.Kind.ShouldBe(CompatibilityIssueKind.MissingMethod);
        diffEngine.Member.ShouldBe("DiffEngine.DiffRunner.Launch(System.String, System.String)");
        diffEngine.UsedBy.ShouldContain("Shouldly.ShouldMatchApprovedTestExtensions.ShouldMatchApproved");
    }

    [Fact]
    public void ReadingIL_CopesWithEveryOperandSize()
    {
        // Switch tables, 8-byte constants and more than 256 locals all have operands of their own sizes.
        var source = new StringBuilder("""
            namespace PluginCode;
            public class Busy
            {
                public double Work(int pick)
                {
                    long big = 1234567890123L; double real = 2.5; float small = 1.5f;
                    switch (pick) { case 0: big++; break; case 1: big--; break; case 2: big *= 2; break; case 3: big /= 2; break; }

            """);
        for (int i = 0; i < 300; i++)
            source.AppendLine($"        int v{i} = pick + {i};");

        source.AppendLine("        return big + real + small + " + string.Join(" + ", Enumerable.Range(0, 300).Select(i => "v" + i)) + " + Contract.Api.Gone();");
        source.AppendLine("    }").AppendLine("}");

        Upgrade upgrade = BuildUpgrade(
            "namespace Contract; public static class Api { public static int Gone() => 1; }",
            "namespace Contract; public static class Api { }",
            source.ToString());

        upgrade.PluginIssues().ShouldHaveSingleItem().UsedBy.ShouldBe(["PluginCode.Busy.Work"]);
    }
}
