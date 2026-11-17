using System.Text.Json;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner.UnitTests;

public class ScanResultTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Explain_SaysWhatHappenedToAnAssembly()
    {
        Fixture.Compile(_temp.Path, "Chosen", "public class A { }");
        Fixture.Compile(_temp.Path, "Rejected.Tests", "public class A { }");
        File.WriteAllText(Path.Combine(_temp.Path, "native.dll"), "junk");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Exclude("*.Tests").Scan();

        result.Explain("Chosen").ShouldStartWith("Selected: Chosen, Version=", Case.Sensitive);
        string explanation = result.Explain("Rejected.Tests.dll");
        explanation.ShouldContain("ExcludedByPattern", Case.Sensitive);
        explanation.ShouldContain("*.Tests", Case.Sensitive);
        result.Explain("native").ShouldContain("NotManaged", Case.Sensitive);
        result.Explain("Unknown").ShouldStartWith("Not seen", Case.Sensitive);
    }

    [Fact]
    public void Find_LooksUpByAssemblyNameOrFileName()
    {
        Fixture.Compile(_temp.Path, "Findable", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        result.Find("findable").ShouldNotBeNull();
        result.Find("Findable.dll").ShouldBeSameAs(result.Find("Findable"));
        result.Find("Other").ShouldBeNull();
    }

    [Fact]
    public void Fingerprint_IsStableForTheSameFiles_AndChangesWithTheSet()
    {
        Fixture.Compile(_temp.Path, "One", "public class A { }");

        string first = Assemblies.Scan().FromDirectory(_temp.Path).Scan().Fingerprint;
        string again = Assemblies.Scan().FromDirectory(_temp.Path).WithoutCache().Scan().Fingerprint;
        Fixture.Compile(_temp.Path, "Two", "public class A { }");
        string grown = Assemblies.Scan().FromDirectory(_temp.Path).Scan().Fingerprint;

        first.Length.ShouldBe(64);
        first.ShouldBe(again);
        grown.ShouldNotBe(first);
    }

    [Fact]
    public void DiffFrom_ReportsAddedRemovedAndChanged()
    {
        Fixture.Compile(_temp.Path, "Stays", "public class A { }");
        Fixture.Compile(_temp.Path, "Changes", """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class A { }""");
        string leaving = Fixture.Compile(_temp.Path, "Leaves", "public class A { }");
        ScanResult before = Assemblies.Scan().FromDirectory(_temp.Path).WithoutCache().Scan();

        File.Delete(leaving);
        Fixture.Compile(_temp.Path, "Changes", """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] public class A { }""");
        Fixture.Compile(_temp.Path, "Arrives", "public class A { }");
        ScanResult after = Assemblies.Scan().FromDirectory(_temp.Path).WithoutCache().Scan();

        ScanDiff diff = after.DiffFrom(before);

        diff.HasChanges.ShouldBeTrue();
        diff.Added.Select(a => a.Name).ShouldBe(["Arrives"]);
        diff.Removed.Select(a => a.Name).ShouldBe(["Leaves"]);
        diff.Changed.Select(a => a.Name).ShouldBe(["Changes"]);
        diff.Changed[0].Version.ShouldBe(new Version(2, 0, 0, 0));
        after.DiffFrom(after).HasChanges.ShouldBeFalse();
    }

    [Fact]
    public void ToJson_WritesAParseableInventory()
    {
        Fixture.Compile(_temp.Path, "Inventory", """[assembly: System.Reflection.AssemblyVersion("1.2.3.4")] public class A { }""");
        File.WriteAllText(Path.Combine(_temp.Path, "native.dll"), "junk");
        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        using JsonDocument document = JsonDocument.Parse(result.ToJson(includeHashes: true));
        JsonElement root = document.RootElement;
        JsonElement assembly = root.GetProperty("assemblies").EnumerateArray().Single();

        root.GetProperty("filesExamined").GetInt32().ShouldBe(2);
        root.GetProperty("fingerprint").GetString().ShouldBe(result.Fingerprint);
        assembly.GetProperty("name").GetString().ShouldBe("Inventory");
        assembly.GetProperty("version").GetString().ShouldBe("1.2.3.4");
        assembly.GetProperty("sha256").GetString().ShouldBe(result[0].Sha256);
        assembly.GetProperty("references").GetArrayLength().ShouldBeGreaterThan(0);
        root.GetProperty("diagnostics").EnumerateArray().Single().GetProperty("reason").GetString().ShouldBe("NotManaged");

        JsonDocument.Parse(result.ToJson()).RootElement.GetProperty("assemblies")[0].TryGetProperty("sha256", out _).ShouldBeFalse();
    }

    [Fact]
    public void ToMarkdown_ListsAssembliesAndWhatWasLeftOut()
    {
        Fixture.Compile(_temp.Path, "Reported", "public class A { }");
        File.WriteAllText(Path.Combine(_temp.Path, "native.dll"), "junk");

        string markdown = Assemblies.Scan().FromDirectory(_temp.Path).Scan().ToMarkdown();

        markdown.ShouldContain("| Reported | 0.0.0.0 |", Case.Sensitive);
        markdown.ShouldContain("## Left out", Case.Sensitive);
        markdown.ShouldContain("| native.dll | NotManaged |", Case.Sensitive);
    }

    [Fact]
    public void FindResources_ListsEmbeddedResources_AndOpensThemOnDemand()
    {
        string name = Fixture.UniqueName("Resources");
        Fixture.Compile(_temp.Path, name, "public class A { }", resourceNames: ["data.json", "logo.png"]);
        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        ResourceMatch match = result.FindResources("*.json").ShouldHaveSingleItem();

        match.ResourceName.ShouldBe("data.json");
        match.Assembly.IsLoaded.ShouldBeFalse();
        using Stream stream = match.Open()!;
        stream.Length.ShouldBe(3);
        match.Assembly.IsLoaded.ShouldBeTrue();
    }

    [Fact]
    public void TryLoad_ReportsFailures_InsteadOfThrowing()
    {
        string good = Fixture.UniqueName("Loadable");
        Fixture.Compile(_temp.Path, good, "public class A { }");
        string broken = Fixture.Compile(_temp.Path, Fixture.UniqueName("Vanishing"), "public class A { }");
        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        File.Delete(broken);

        System.Reflection.Assembly[] loaded = result.TryLoad(out var failures);

        loaded.Select(a => a.GetName().Name).ShouldBe([good]);
        failures.ShouldContain(f => f.Reason == SkipReason.LoadFailed && f.Path == broken, 1);
    }

    [Fact]
    public void Result_IsAListOfItsAssemblies()
    {
        Fixture.Compile(_temp.Path, "Listed.One", "public class A { }");
        Fixture.Compile(_temp.Path, "Listed.Two", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        result.Count.ShouldBe(2);
        result.Assemblies.Select(a => a.Name).ShouldBe(["Listed.One", "Listed.Two"]);
        Fixture.Enumerate(result).ShouldBe(result.Assemblies);
        result[1].ShouldBeSameAs(result.Assemblies[1]);
        result.FilesExamined.ShouldBe(2);
    }

    [Fact]
    public void Cache_DoesNotRememberAFileItCouldNotRead()
    {
        string path = Path.Combine(_temp.Path, "Late.dll");

        InspectionResult cached = ScanCache.Inspect(path, useCache: true);
        InspectionResult direct = ScanCache.Inspect(path, useCache: false);
        Fixture.Compile(_temp.Path, "Late", "public class A { }");
        InspectionResult afterwards = ScanCache.Inspect(path, useCache: true);

        cached.Status.ShouldBe(InspectionStatus.Unreadable);
        cached.Exception.ShouldBeOfType<FileNotFoundException>();
        direct.Status.ShouldBe(InspectionStatus.Unreadable);
        afterwards.Status.ShouldBe(InspectionStatus.Ok);
        afterwards.Snapshot!.Name.ShouldBe("Late");
    }

    [Fact]
    public void Cache_CanBeCleared()
    {
        Fixture.Compile(_temp.Path, "Cached", "public class A { }");
        Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        ScanCache.Count.ShouldBeGreaterThan(0);
        ScanCache.Clear();

        ScanCache.Count.ShouldBe(0);
    }

    [Fact]
    public void Diagnostic_DescribesItself()
    {
        string missing = Path.Combine(_temp.Path, "nowhere");

        ScanDiagnostic diagnostic = Assemblies.Scan().FromDirectory(missing).Scan().Diagnostics.ShouldHaveSingleItem();

        diagnostic.ToString().ShouldBe($"{diagnostic.Reason}: {diagnostic.Path} - {diagnostic.Message}");
        diagnostic.Reason.ShouldBe(SkipReason.SourceNotFound);
        diagnostic.Exception.ShouldBeNull();
    }
}
