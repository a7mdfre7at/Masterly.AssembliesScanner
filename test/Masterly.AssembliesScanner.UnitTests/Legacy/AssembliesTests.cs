using System.Reflection;

namespace Masterly.AssembliesScanner.UnitTests;

public class AssembliesTests
{
    [Fact]
    public void Scan_ShouldReturn_AllAssembliesStartsWithMasterly()
    {
        // Arrange
        string[] assemblyPatterns = ["Masterly.*.dll"];

        // Act
        Assembly[] result = Assemblies.Get(assemblyPatterns);

        // Assert
        result.Length.ShouldBe(2);
        result.All(a => a.ManifestModule.Name.StartsWith("Masterly.AssembliesScanner")).ShouldBeTrue();
    }

    [Fact]
    public void Scan_ShouldNotReturn_AssebmluEndsWithUnitTestsDll()
    {
        // Arrange
        string[] assemblyPatterns = ["Masterly.*.dll", "!.UnitTests.dll$"];

        // Act
        Assembly[] result = Assemblies.Get(assemblyPatterns);

        // Assert
        result.Length.ShouldBe(1);
        result.First().ManifestModule.Name.ShouldBe("Masterly.AssembliesScanner.dll");
    }

    [Fact]
    public void Get_WithoutPatterns_ReturnsNothing()
    {
        Assemblies.Get().ShouldBeEmpty();
        Assemblies.Get(new List<string>()).ShouldBeEmpty();
    }

    [Fact]
    public void Get_TakesAnyEnumerableOfPatterns()
    {
        var patterns = new List<string> { "Masterly.*.dll", "!.UnitTests.dll$" };

        Assembly[] result = Assemblies.Get(patterns);

        result.ShouldHaveSingleItem().ManifestModule.Name.ShouldBe("Masterly.AssembliesScanner.dll");
    }

    [Fact]
    public void Get_AppliesExclusions_WhereverTheyAppear()
    {
        Assembly[] result = Assemblies.Get("!.UnitTests.dll$", "Masterly.*.dll");

        result.ShouldHaveSingleItem().ManifestModule.Name.ShouldBe("Masterly.AssembliesScanner.dll");
    }

    [Fact]
    public void Get_IgnoresCase()
    {
        Assembly[] result = Assemblies.Get("MASTERLY.ASSEMBLIESSCANNER.DLL");

        result.ShouldHaveSingleItem().ManifestModule.Name.ShouldBe("Masterly.AssembliesScanner.dll");
    }

    [Fact]
    public void Get_ReturnsAnAssemblyOncePerPatternThatMatchesIt()
    {
        Assembly[] result = Assemblies.Get("Masterly.AssembliesScanner.dll", "^masterly.assembliesscanner.dll$");

        result.Length.ShouldBe(2);
        result[0].ShouldBeSameAs(result[1]);
    }

    [Fact]
    public void Get_TreatsPatternsAsUnanchoredRegularExpressions()
    {
        // "Scanner.dll" is found in the middle of the library's file name, and the dot matches any character.
        Assemblies.Get("Scanner.dll").ShouldHaveSingleItem().ManifestModule.Name.ShouldBe("Masterly.AssembliesScanner.dll");
        Assemblies.Get("ScannerXdll").ShouldBeEmpty();
        Assemblies.Get("Sc.nner.dll").ShouldHaveSingleItem();
    }
}
