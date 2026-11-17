namespace Masterly.AssembliesScanner.UnitTests;

public class DependencyGraphTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private DependencyGraph ScanGraph(string directory, out ScanResult result)
    {
        result = Assemblies.Scan().FromDirectory(directory).Scan();
        return result.BuildDependencyGraph();
    }

    [Fact]
    public void LoadOrder_PutsDependenciesFirst()
    {
        string bottom = Fixture.Compile(_temp.Path, "Chain.Bottom", "public class Bottom { }");
        string middle = Fixture.Compile(_temp.Path, "Chain.Middle", "public class Middle : Bottom { }", [bottom]);
        Fixture.Compile(_temp.Path, "Chain.Top", "public class Top : Middle { public Bottom Direct; }", [bottom, middle]);

        DependencyGraph graph = ScanGraph(_temp.Path, out ScanResult result);
        AssemblyDescriptor top = result.Find("Chain.Top")!;
        AssemblyDescriptor mid = result.Find("Chain.Middle")!;
        AssemblyDescriptor bot = result.Find("Chain.Bottom")!;

        graph.GetLoadOrder().Select(a => a.Name).ShouldBe(["Chain.Bottom", "Chain.Middle", "Chain.Top"]);
        graph.GetDependencies(mid).ShouldBe([bot]);
        graph.GetDependents(mid).ShouldBe([top]);
        graph.GetDependents(bot).ShouldBe([mid, top], ignoreOrder: true);
        graph.GetTransitiveDependencies(top).ShouldBe([mid, bot], ignoreOrder: true);
        graph.Cycles.ShouldBeEmpty();
        graph.MissingReferences.ShouldBeEmpty();
        graph.VersionConflicts.ShouldBeEmpty();
    }

    [Fact]
    public void MissingReferences_ListsWhatNobodyCanSupply()
    {
        string ghost = Fixture.Compile(_temp.Sub("elsewhere"), "Ghost.Library", "public class Ghost { }");
        Fixture.Compile(_temp.Sub("app"), "Haunted", "public class Haunted : Ghost { }", [ghost]);

        DependencyGraph graph = ScanGraph(Path.Combine(_temp.Path, "app"), out _);

        ReferenceRequest missing = graph.MissingReferences.ShouldHaveSingleItem();
        missing.Referrer.Name.ShouldBe("Haunted");
        missing.Reference.Name.ShouldBe("Ghost.Library");
    }

    [Fact]
    public void VersionConflicts_FlagsAnOlderVersionThanRequested()
    {
        string newer = Fixture.Compile(_temp.Sub("build"), "Versioned.Library",
            """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] public class Thing { }""");
        string app = _temp.Sub("app");
        Fixture.Compile(app, "Consumer", "public class Consumer : Thing { }", [newer]);
        Fixture.Compile(app, "Versioned.Library",
            """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class Thing { }""");

        DependencyGraph graph = ScanGraph(app, out _);

        VersionConflict conflict = graph.VersionConflicts.ShouldHaveSingleItem();
        conflict.AssemblyName.ShouldBe("Versioned.Library");
        conflict.AvailableVersion.ShouldBe(new Version(1, 0, 0, 0));
        conflict.IsDowngrade.ShouldBeTrue();
        conflict.ToString().ShouldBe("Versioned.Library: available 1.0.0.0, requested 2.0.0.0");
        conflict.Requests.ShouldContain(r => r.Referrer.Name == "Consumer" && r.Reference.Version == new Version(2, 0, 0, 0), 1);
    }

    [Fact]
    public void VersionConflicts_FlagsTwoReferrersAskingForDifferentVersions()
    {
        string v1 = Fixture.Compile(_temp.Sub("v1"), "Split.Library",
            """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class Thing { }""");
        string v2 = Fixture.Compile(_temp.Sub("v2"), "Split.Library",
            """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] public class Thing { }""");
        string app = _temp.Sub("app");
        Fixture.Compile(app, "WantsOne", "public class A : Thing { }", [v1]);
        Fixture.Compile(app, "WantsTwo", "public class B : Thing { }", [v2]);
        File.Copy(v2, Path.Combine(app, "Split.Library.dll"));

        DependencyGraph graph = ScanGraph(app, out _);

        VersionConflict conflict = graph.VersionConflicts.ShouldHaveSingleItem();
        conflict.IsDowngrade.ShouldBeFalse();
        conflict.Requests.Select(r => r.Reference.Version.Major).ShouldBe([1, 2], ignoreOrder: true);
    }

    [Fact]
    public void Cycles_AreReported_AndLoadOrderStillCoversEverything()
    {
        // Build A, then B against A, then A again against B: a circle no project system would allow.
        string first = Fixture.Compile(_temp.Sub("stage"), "Loop.A", "public class A { }");
        string b = Fixture.Compile(_temp.Path, "Loop.B", "public class B : A { }", [first]);
        Fixture.Compile(_temp.Path, "Loop.A", "public class A { } public class UsesB { public B Value; }", [b]);

        DependencyGraph graph = ScanGraph(_temp.Path, out _);

        graph.Cycles.ShouldHaveSingleItem().Select(a => a.Name).ShouldBe(["Loop.A", "Loop.B"], ignoreOrder: true);
        graph.GetLoadOrder().Select(a => a.Name).ShouldBe(["Loop.A", "Loop.B"], ignoreOrder: true);
    }

    [Fact]
    public void ToMermaidAndToDot_RenderTheEdges()
    {
        string library = Fixture.Compile(_temp.Path, "Draw.Library", "public class L { }");
        Fixture.Compile(_temp.Path, "Draw.App", "public class App : L { }", [library]);

        DependencyGraph graph = ScanGraph(_temp.Path, out _);

        string mermaid = graph.ToMermaid();
        mermaid.ShouldStartWith("graph TD", Case.Sensitive);
        mermaid.ShouldContain("""n0["Draw.App 0.0.0.0"]""", Case.Sensitive);
        mermaid.ShouldContain("n0 --> n1", Case.Sensitive);

        string dot = graph.ToDot();
        dot.ShouldStartWith("digraph assemblies {", Case.Sensitive);
        dot.ShouldContain("\"Draw.App\" -> \"Draw.Library\";", Case.Sensitive);
    }

    [Fact]
    public void GetDependencies_RejectsAnAssemblyFromAnotherGraph()
    {
        Fixture.Compile(_temp.Sub("one"), "Graph.One", "public class A { }");
        Fixture.Compile(_temp.Sub("two"), "Graph.Two", "public class A { }");
        DependencyGraph graph = ScanGraph(Path.Combine(_temp.Path, "one"), out _);
        ScanGraph(Path.Combine(_temp.Path, "two"), out ScanResult other);

        Action act = () => graph.GetDependencies(other[0]);

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void MissingReferencesAndConflicts_DescribeThemselves()
    {
        string v1 = Fixture.Compile(_temp.Sub("v1"), "Absent.Library",
            """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class Thing { }""");
        string v2 = Fixture.Compile(_temp.Sub("v2"), "Absent.Library",
            """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] public class Thing { }""");
        string app = _temp.Sub("app");
        Fixture.Compile(app, "NeedsOne", "public class A : Thing { }", [v1]);
        Fixture.Compile(app, "NeedsTwo", "public class B : Thing { }", [v2]);

        DependencyGraph graph = ScanGraph(app, out _);

        graph.MissingReferences.Select(r => r.ToString()).ShouldBe(
            ["NeedsOne -> Absent.Library, Version=1.0.0.0", "NeedsTwo -> Absent.Library, Version=2.0.0.0"],
            ignoreOrder: true);
        VersionConflict conflict = graph.VersionConflicts.ShouldHaveSingleItem();
        conflict.AvailableVersion.ShouldBeNull();
        conflict.IsDowngrade.ShouldBeFalse();
        conflict.ToString().ShouldStartWith("Absent.Library: available none, requested ", Case.Sensitive);
        conflict.ToString().ShouldContain("1.0.0.0", Case.Sensitive);
        conflict.ToString().ShouldContain("2.0.0.0", Case.Sensitive);
    }

    [Fact]
    public void AGraphOfOneAssembly_HasNoEdges()
    {
        Fixture.Compile(_temp.Path, "Alone", "public class A { }");

        DependencyGraph graph = ScanGraph(_temp.Path, out ScanResult result);

        graph.Assemblies.ShouldBe(result.Assemblies);
        graph.GetDependencies(result[0]).ShouldBeEmpty();
        graph.GetDependents(result[0]).ShouldBeEmpty();
        graph.GetTransitiveDependencies(result[0]).ShouldBeEmpty();
        graph.GetLoadOrder().ShouldBe(result.Assemblies);
        graph.MissingReferences.ShouldBeEmpty();
        graph.VersionConflicts.ShouldBeEmpty();
        graph.Cycles.ShouldBeEmpty();
    }
}
