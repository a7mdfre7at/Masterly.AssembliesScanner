using System.Runtime.Loader;
using System.Security.Cryptography;
using Masterly.AssembliesScanner.UnitTests.Samples;

namespace Masterly.AssembliesScanner.UnitTests;

public class AssemblyScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static bool IsLoadedAnywhere(string assemblyName)
        => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assemblyName);

    [Fact]
    public void Scan_ReadsMetadata_WithoutLoadingTheAssembly()
    {
        string name = Fixture.UniqueName("NotLoaded");
        Fixture.Compile(_temp.Path, name, """
            using System.Reflection;
            [assembly: AssemblyVersion("3.4.5.6")]
            [assembly: AssemblyInformationalVersion("3.4.5-beta+abc")]
            [assembly: AssemblyCompany("Contoso")]
            [assembly: AssemblyProduct("Widgets")]
            [assembly: AssemblyMetadata("Plugin", "true")]
            [assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v8.0")]
            public class Widget { }
            """);

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        AssemblyDescriptor descriptor = result.ShouldHaveSingleItem();
        descriptor.Name.ShouldBe(name);
        descriptor.Version.ShouldBe(new Version(3, 4, 5, 6));
        descriptor.InformationalVersion.ShouldBe("3.4.5-beta+abc");
        descriptor.Company.ShouldBe("Contoso");
        descriptor.Product.ShouldBe("Widgets");
        descriptor.Metadata["Plugin"].ShouldBe("true");
        descriptor.TargetFramework.ShouldBe(".NETCoreApp,Version=v8.0");
        descriptor.FullName.ShouldBe(name + ", Version=3.4.5.6, Culture=neutral, PublicKeyToken=null");
        descriptor.FileName.ShouldBe(name + ".dll");
        descriptor.FileSize.ShouldBeGreaterThan(0);
        descriptor.Mvid.ShouldNotBe(Guid.Empty);
        descriptor.Types.Select(t => t.FullName).ShouldContain("Widget");
        descriptor.IsLoaded.ShouldBeFalse();
        IsLoadedAnywhere(name).ShouldBeFalse();
    }

    [Fact]
    public void Scan_SkipsNativeAndBrokenFiles_AndSaysWhy()
    {
        string name = Fixture.UniqueName("Good");
        Fixture.Compile(_temp.Path, name, "public class A { }");
        File.WriteAllText(Path.Join(_temp.Path, "native.dll"), "this is not a portable executable");
        File.WriteAllBytes(Path.Join(_temp.Path, "empty.dll"), []);

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        result.Select(a => a.Name).ShouldBe([name]);
        result.FilesExamined.ShouldBe(3);
        result.Diagnostics.Count.ShouldBe(2);
        result.Diagnostics.ShouldAllBe(d => d.Reason == SkipReason.NotManaged);
        result.Diagnostics.Select(d => Path.GetFileName(d.Path)).ShouldBe(["native.dll", "empty.dll"], ignoreOrder: true);
    }

    [Fact]
    public void Scan_ReportsMissingDirectory_InsteadOfThrowing()
    {
        string missing = Path.Join(_temp.Path, "does-not-exist");

        ScanResult result = Assemblies.Scan().FromDirectory(missing).Scan();

        result.ShouldBeEmpty();
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.SourceNotFound && d.Path == missing, 1);
    }

    [Fact]
    public void IncludeAndExclude_SelectByGlob_AndExclusionsWin()
    {
        Fixture.Compile(_temp.Path, "Acme.Core", "public class A { }");
        Fixture.Compile(_temp.Path, "Acme.Core.Tests", "public class A { }");
        Fixture.Compile(_temp.Path, "Other.Lib", "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .Include("Acme.*", "!*.Tests")
            .Scan();

        result.Select(a => a.Name).ShouldBe(["Acme.Core"]);
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.ExcludedByPattern && d.Path.EndsWith("Acme.Core.Tests.dll"));
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.ExcludedByPattern && d.Path.EndsWith("Other.Lib.dll"));
    }

    [Fact]
    public void IncludeRegexAndExcludeRegex_SelectByRegularExpression()
    {
        Fixture.Compile(_temp.Path, "Acme.Core", "public class A { }");
        Fixture.Compile(_temp.Path, "Acme.Core.Tests", "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .IncludeRegex("^acme")
            .ExcludeRegex(@"\.Tests\.dll$")
            .Scan();

        result.Select(a => a.Name).ShouldBe(["Acme.Core"]);
    }

    [Fact]
    public void FromDirectory_Recursive_FindsNestedFiles_AndPathGlobsApply()
    {
        Fixture.Compile(_temp.Path, "Top", "public class A { }");
        Fixture.Compile(_temp.Sub("plugins/one"), "PluginOne", "public class A { }");
        Fixture.Compile(_temp.Sub("other"), "Elsewhere", "public class A { }");

        ScanResult flat = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        ScanResult deep = Assemblies.Scan().FromDirectory(_temp.Path, recursive: true).Scan();
        ScanResult pluginsOnly = Assemblies.Scan().FromDirectory(_temp.Path, recursive: true).Include("plugins/**").Scan();

        flat.Select(a => a.Name).ShouldBe(["Top"]);
        deep.Select(a => a.Name).ShouldBe(["Top", "PluginOne", "Elsewhere"], ignoreOrder: true);
        pluginsOnly.Select(a => a.Name).ShouldBe(["PluginOne"]);
        pluginsOnly[0].RelativePath.ShouldBe("plugins/one/PluginOne.dll");
    }

    [Fact]
    public void FromFiles_ScansExactlyTheGivenFiles()
    {
        string wanted = Fixture.Compile(_temp.Path, "Wanted", "public class A { }");
        Fixture.Compile(_temp.Path, "Unwanted", "public class A { }");

        ScanResult result = Assemblies.Scan().FromFiles(wanted, Path.Join(_temp.Path, "missing.dll")).Scan();

        result.Select(a => a.Name).ShouldBe(["Wanted"]);
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.SourceNotFound, 1);
    }

    [Fact]
    public void WithVersionRange_KeepsVersionsInsideTheRange()
    {
        Fixture.Compile(_temp.Path, "V1", """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class A { }""");
        Fixture.Compile(_temp.Path, "V2", """[assembly: System.Reflection.AssemblyVersion("2.5.0.0")] public class A { }""");
        Fixture.Compile(_temp.Path, "V3", """[assembly: System.Reflection.AssemblyVersion("3.0.0.0")] public class A { }""");

        Assemblies.Scan().FromDirectory(_temp.Path).WithMinimumVersion("2.0.0.0").Scan()
            .Select(a => a.Name).ShouldBe(["V2", "V3"]);

        Assemblies.Scan().FromDirectory(_temp.Path).WithMinimumVersion("2.0.0.0").WithMaximumVersion("2.9.0.0").Scan()
            .Select(a => a.Name).ShouldBe(["V2"]);

        Assemblies.Scan().FromDirectory(_temp.Path).WithVersionRange(null, new Version(1, 0, 0, 0)).Scan()
            .Select(a => a.Name).ShouldBe(["V1"]);

        Assemblies.Scan().FromDirectory(_temp.Path).WithVersionRange(new Version(1, 5, 0, 0), new Version(3, 0, 0, 0)).Scan()
            .Select(a => a.Name).ShouldBe(["V2", "V3"]);
    }

    [Fact]
    public void Referencing_KeepsAssembliesThatReferenceTheGivenOne()
    {
        string library = Fixture.Compile(_temp.Path, "Shared.Library", "public class Shared { }");
        Fixture.Compile(_temp.Path, "Uses.Library", "public class A : Shared { }", [library]);
        Fixture.Compile(_temp.Path, "Uses.Nothing", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Referencing("Shared.Library").Scan();

        result.Select(a => a.Name).ShouldBe(["Uses.Library"]);
        result[0].IsReferencing("shared.library").ShouldBeTrue();
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.ExcludedByFilter && d.Message.Contains("referencing Shared.Library"));
    }

    [Fact]
    public void WithAssemblyAttributeAndWithMetadata_FilterOnAssemblyLevelMarkers()
    {
        Fixture.Compile(_temp.Path, "Marked", """
            [assembly: System.Reflection.AssemblyMetadata("Role", "Plugin")]
            [assembly: System.CLSCompliant(true)]
            public class A { }
            """);
        Fixture.Compile(_temp.Path, "Plain", "public class A { }");

        Assemblies.Scan().FromDirectory(_temp.Path).WithMetadata("Role").Scan()
            .Select(a => a.Name).ShouldBe(["Marked"]);
        Assemblies.Scan().FromDirectory(_temp.Path).WithMetadata("Role", "plugin").Scan()
            .Select(a => a.Name).ShouldBe(["Marked"]);
        Assemblies.Scan().FromDirectory(_temp.Path).WithMetadata("Role", "Host").Scan()
            .ShouldBeEmpty();
        Assemblies.Scan().FromDirectory(_temp.Path).WithAssemblyAttribute<CLSCompliantAttribute>().Scan()
            .Select(a => a.Name).ShouldBe(["Marked"]);
    }

    [Fact]
    public void ContainingImplementationsOf_FindsImplementersFromMetadataAlone()
    {
        string implementer = Fixture.UniqueName("Implementer");
        Fixture.Compile(_temp.Path, implementer, """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            public abstract class GreeterBase : IGreeter { public abstract string Greet(string name); }
            public class Polite : GreeterBase { public override string Greet(string name) => "Good day " + name; }
            public class Handles : IHandler<Order> { }
            """);
        Fixture.Compile(_temp.Path, "Bystander", "public interface IGreeter { } public class A : IGreeter { }");

        ScanResult greeters = Assemblies.Scan().FromDirectory(_temp.Path).ContainingImplementationsOf<IGreeter>().Scan();
        ScanResult handlers = Assemblies.Scan().FromDirectory(_temp.Path).ContainingImplementationsOf(typeof(IHandler<>)).Scan();

        greeters.Select(a => a.Name).ShouldBe([implementer]);
        handlers.Select(a => a.Name).ShouldBe([implementer]);
        IsLoadedAnywhere(implementer).ShouldBeFalse();
    }

    [Fact]
    public void ContainingTypes_FiltersOnTypeMetadata()
    {
        Fixture.Compile(_temp.Path, "HasController", """
            namespace Web { public class HomeController { } internal class Helper { } public interface IThing { } }
            """);
        Fixture.Compile(_temp.Path, "HasNone", "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .ContainingTypes(t => t.IsPublic && t.IsConcreteClass && t.Name.EndsWith("Controller"))
            .Scan();

        result.Select(a => a.Name).ShouldBe(["HasController"]);
        TypeDescriptor controller = result[0].Types.Single(t => t.Name == "HomeController");
        controller.FullName.ShouldBe("Web.HomeController");
        controller.Namespace.ShouldBe("Web");
        controller.BaseType.ShouldBe("System.Object");
        result[0].Types.Single(t => t.Name == "Helper").IsPublic.ShouldBeFalse();
        result[0].Types.Single(t => t.Name == "IThing").IsInterface.ShouldBeTrue();
    }

    [Fact]
    public void ContainingTypesWithAttribute_FindsAttributedTypes()
    {
        Fixture.Compile(_temp.Path, "HasMarked", """
            using Masterly.AssembliesScanner.UnitTests.Samples;
            [Marker("x")] public class A { }
            """);
        Fixture.Compile(_temp.Path, "HasUnmarked", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).ContainingTypesWithAttribute<MarkerAttribute>().Scan();

        result.Select(a => a.Name).ShouldBe(["HasMarked"]);
    }

    [Fact]
    public void TargetingFramework_AcceptsMonikers()
    {
        string current = "net" + Environment.Version.Major + ".0";

        Assemblies.Scan().FromAssemblyOf<AssemblyScanner>().TargetingFramework(current).Scan().ShouldHaveSingleItem();
        Assemblies.Scan().FromAssemblyOf<AssemblyScanner>().TargetingFramework(".NETCoreApp").Scan().ShouldHaveSingleItem();
        Assemblies.Scan().FromAssemblyOf<AssemblyScanner>().TargetingFramework("netstandard2.0").Scan().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("net8.0", ".NETCoreApp,Version=v8.0")]
    [InlineData("net10.0-windows", ".NETCoreApp,Version=v10.0")]
    [InlineData("netcoreapp3.1", ".NETCoreApp,Version=v3.1")]
    [InlineData("netstandard2.0", ".NETStandard,Version=v2.0")]
    [InlineData("net48", ".NETFramework,Version=v4.8")]
    [InlineData("net472", ".NETFramework,Version=v4.7.2")]
    [InlineData(".NETCoreApp", ".NETCoreApp")]
    public void NormalizeFramework_TranslatesMonikersToFrameworkNames(string moniker, string expected)
    {
        AssemblyScanner.NormalizeFramework(moniker).ShouldBe(expected);
    }

    [Fact]
    public void PublicKeyTokenFilters_UseTheTokenFromMetadata()
    {
        System.Reflection.Assembly json = typeof(System.Text.Json.JsonSerializer).Assembly;
        string token = Convert.ToHexString(json.GetName().GetPublicKeyToken()!).ToLowerInvariant();
        string unsigned = Fixture.Compile(_temp.Path, "Unsigned", "public class A { }");

        ScanResult all = Assemblies.Scan().FromFiles(json.Location, unsigned).Scan();
        ScanResult signed = Assemblies.Scan().FromFiles(json.Location, unsigned).RequireStrongName().Scan();
        ScanResult trusted = Assemblies.Scan().FromFiles(json.Location, unsigned).WithPublicKeyToken(token.ToUpperInvariant()).Scan();

        all.Count().ShouldBe(2);
        all.Find("System.Text.Json")!.PublicKeyToken.ShouldBe(token);
        all.Find("System.Text.Json")!.IsFrameworkAssembly.ShouldBeTrue();
        all.Find("Unsigned")!.PublicKeyToken.ShouldBeNull();
        signed.Select(a => a.Name).ShouldBe(["System.Text.Json"]);
        trusted.Select(a => a.Name).ShouldBe(["System.Text.Json"]);
    }

    [Fact]
    public void RequireHash_KeepsOnlyFilesOnTheAllowList()
    {
        string trustedPath = Fixture.Compile(_temp.Path, "Trusted", "public class A { }");
        Fixture.Compile(_temp.Path, "Tampered", "public class B { }");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(trustedPath)));

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).RequireHash(hash).Scan();

        result.Select(a => a.Name).ShouldBe(["Trusted"]);
        result[0].Sha256.ShouldBe(hash.ToLowerInvariant());
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.ExcludedByFilter && d.Path.EndsWith("Tampered.dll"), 1);
    }

    [Fact]
    public void WithResource_FiltersOnEmbeddedResourceNames()
    {
        Fixture.Compile(_temp.Path, "HasResource", "public class A { }", resourceNames: ["App.settings.json", "App.logo.png"]);
        Fixture.Compile(_temp.Path, "NoResource", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).WithResource("*.json").Scan();

        result.Select(a => a.Name).ShouldBe(["HasResource"]);
        result[0].ResourceNames.ShouldBe(["App.settings.json", "App.logo.png"], ignoreOrder: true);
    }

    [Fact]
    public void Where_AppliesACustomPredicate_AndNamesItInDiagnostics()
    {
        Fixture.Compile(_temp.Path, "Small", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Where(a => a.FileSize > 10_000_000, "larger than 10 MB").Scan();

        result.ShouldBeEmpty();
        result.Diagnostics.ShouldContain(d => d.Message.Contains("larger than 10 MB"), 1);
    }

    [Fact]
    public void FromLoadedAssemblies_SeesWhatTheProcessLoaded_AndFrameworkCanBeExcluded()
    {
        ScanResult all = Assemblies.Scan().FromLoadedAssemblies().Scan();
        ScanResult own = Assemblies.Scan().FromLoadedAssemblies().ExcludeFrameworkAssemblies().Scan();

        new[] { "System.Private.CoreLib", "Masterly.AssembliesScanner" }.ShouldBeSubsetOf(all.Select(a => a.Name));
        all.ShouldAllBe(a => a.Source == AssemblySource.Loaded && a.IsLoaded);
        own.Select(a => a.Name).ShouldContain("Masterly.AssembliesScanner");
        own.Select(a => a.Name).ShouldNotContain("System.Private.CoreLib");
        own.ShouldNotContain(a => a.IsFrameworkAssembly);
    }

    [Fact]
    public void ExcludeMicrosoftAssemblies_AlsoRemovesMicrosoftPackages()
    {
        ScanResult result = Assemblies.Scan().FromLoadedAssemblies().ExcludeMicrosoftAssemblies().Scan();

        // Packages that only look like the framework by name, such as System.ClientModel, are not Microsoft-signed framework code and stay.
        result.Select(a => a.Name).ShouldContain("Masterly.AssembliesScanner");
        result.Select(a => a.Name).ShouldNotContain("System.Private.CoreLib");
        result.Where(a => a.Name.StartsWith("Microsoft.") || a.IsFrameworkAssembly).Select(a => a.Name).ShouldBeEmpty();
    }

    [Fact]
    public void SatelliteAndReferenceAssemblies_CanBeExcluded()
    {
        Fixture.Compile(_temp.Path, "Ordinary", "public class A { }");
        Fixture.Compile(_temp.Path, "Ordinary.resources", """[assembly: System.Reflection.AssemblyCulture("fr")]""");
        Fixture.Compile(_temp.Path, "Ordinary.Reference", "[assembly: System.Runtime.CompilerServices.ReferenceAssembly] public class A { }");

        ScanResult all = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        ScanResult filtered = Assemblies.Scan().FromDirectory(_temp.Path).ExcludeSatelliteAssemblies().ExcludeReferenceAssemblies().Scan();

        all.Count().ShouldBe(3);
        all.Find("Ordinary.resources")!.IsSatellite.ShouldBeTrue();
        all.Find("Ordinary.resources")!.Culture.ShouldBe("fr");
        all.Find("Ordinary.resources")!.FullName.ShouldContain("Culture=fr", Case.Sensitive);
        all.Find("Ordinary.Reference")!.IsReferenceAssembly.ShouldBeTrue();
        filtered.Select(a => a.Name).ShouldBe(["Ordinary"]);
    }

    [Fact]
    public void FromDependencyClosure_FollowsReferences_WithoutLoading()
    {
        System.Reflection.Assembly root = typeof(AssemblyScannerTests).Assembly;

        ScanResult result = Assemblies.Scan().FromDependencyClosure(root).Scan();
        ScanResult withFramework = Assemblies.Scan().FromDependencyClosure(root, includeFrameworkAssemblies: true).Scan();

        new[]
        {
            "Masterly.AssembliesScanner.UnitTests", "Masterly.AssembliesScanner", "Shouldly", "Microsoft.CodeAnalysis.CSharp"
        }.ShouldBeSubsetOf(result.Select(a => a.Name));
        result.ShouldNotContain(a => a.Name == "System.Runtime");
        result.ShouldAllBe(a => a.Source == AssemblySource.DependencyClosure);
        withFramework.Select(a => a.Name).ShouldContain("System.Runtime");
        withFramework.Count.ShouldBeGreaterThan(result.Count);
    }

    [Fact]
    public void Scan_KeepsOneCopy_WhenTheSameBuildIsFoundTwice()
    {
        string original = Fixture.Compile(_temp.Sub("a"), "Twin", "public class A { }");
        File.Copy(original, Path.Join(_temp.Sub("b"), "Twin.dll"));

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path, recursive: true).Scan();

        result.ShouldHaveSingleItem().Path.ShouldBe(original);
        result.Diagnostics.ShouldContain(d => d.Reason == SkipReason.Duplicate, 1);
    }

    [Fact]
    public void Scan_GivesTheSameAnswer_InParallelAndOnOneThread()
    {
        for (int i = 0; i < 20; i++)
            Fixture.Compile(_temp.Path, $"Many{i:00}", $"public class C{i} {{ }}");

        ScanResult sequential = Assemblies.Scan().FromDirectory(_temp.Path).WithParallelism(1).WithoutCache().Scan();
        ScanResult parallel = Assemblies.Scan().FromDirectory(_temp.Path).WithParallelism(8).WithoutCache().Scan();

        sequential.Count().ShouldBe(20);
        parallel.Select(a => a.FullName).ShouldBe(sequential.Select(a => a.FullName));
        parallel.Fingerprint.ShouldBe(sequential.Fingerprint);
    }

    [Fact]
    public void Cache_IsReused_AndInvalidatedWhenTheFileChanges()
    {
        ScanCache.Clear();
        string path = Fixture.Compile(_temp.Path, "Changing", """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class A { }""");

        ScanResult first = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        ScanCache.Count.ShouldBe(1);

        Fixture.Compile(_temp.Path, "Changing", """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] public class A { } public class B { }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        ScanResult second = Assemblies.Scan().FromDirectory(_temp.Path).Scan();

        first[0].Version.ShouldBe(new Version(1, 0, 0, 0));
        second[0].Version.ShouldBe(new Version(2, 0, 0, 0));
        ScanCache.Count.ShouldBe(1);

        ScanCache.Clear();
        ScanCache.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ScanAsyncAndEnumerateAsync_ProduceTheSameAssemblies()
    {
        Fixture.Compile(_temp.Path, "AsyncOne", "public class A { }");
        Fixture.Compile(_temp.Path, "AsyncTwo", "public class A { }");
        AssemblyScanner scanner = Assemblies.Scan().FromDirectory(_temp.Path);

        ScanResult result = await scanner.ScanAsync();
        var streamed = new List<string>();
        await foreach (AssemblyDescriptor descriptor in scanner.EnumerateAsync())
            streamed.Add(descriptor.Name);

        result.Select(a => a.Name).ShouldBe(["AsyncOne", "AsyncTwo"]);
        streamed.ShouldBe(["AsyncOne", "AsyncTwo"]);
    }

    [Fact]
    public void Scan_HonoursCancellation()
    {
        Fixture.Compile(_temp.Path, "Cancelled", "public class A { }");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action act = () => Assemblies.Scan().FromDirectory(_temp.Path).Scan(cancellation.Token);

        Should.Throw<OperationCanceledException>(act);
    }

    [Fact]
    public void Scan_WithNoSource_ReadsTheBaseDirectory_LikeTheOriginalApi()
    {
        ScanResult result = Assemblies.Scan("Masterly.*", "!*.UnitTests").Scan();

        result.Select(a => a.FileName).ShouldBe(["Masterly.AssembliesScanner.dll"]);
    }

    [Fact]
    public void Load_ReturnsTheHostsCopy_WhenTheHostAlreadyHasTheAssembly()
    {
        System.Reflection.Assembly[] loaded = Assemblies.Scan("Masterly.AssembliesScanner").Load();

        loaded.ShouldHaveSingleItem().ShouldBeSameAs(typeof(Assemblies).Assembly);
    }

    [Fact]
    public void Load_LoadsAScannedFileIntoTheDefaultContext()
    {
        string name = Fixture.UniqueName("LoadMe");
        Fixture.Compile(_temp.Path, name, """public class Loaded { public string Hello() => "hello"; }""");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        System.Reflection.Assembly assembly = result.Load().Single();

        AssemblyLoadContext.GetLoadContext(assembly).ShouldBeSameAs(AssemblyLoadContext.Default);
        result[0].IsLoaded.ShouldBeTrue();
        result[0].LoadedAssembly.ShouldBeSameAs(assembly);
        object instance = Fixture.Create(assembly, "Loaded");
        Fixture.Call(instance, "Hello").ShouldBe("hello");
    }

    [Fact]
    public void Scan_ReadsTheDescriptiveAssemblyAttributes()
    {
        Fixture.Compile(_temp.Path, "Described", """
            using System.Reflection;
            [assembly: AssemblyFileVersion("1.2.3.4")]
            [assembly: AssemblyTitle("A title")]
            [assembly: AssemblyDescription("A description")]
            [assembly: AssemblyConfiguration("Release")]
            public class A { }
            """);

        AssemblyDescriptor descriptor = Assemblies.Scan().FromDirectory(_temp.Path).Scan().ShouldHaveSingleItem();

        descriptor.FileVersion.ShouldBe("1.2.3.4");
        descriptor.Title.ShouldBe("A title");
        descriptor.Description.ShouldBe("A description");
        descriptor.Configuration.ShouldBe("Release");
        descriptor.TargetFramework.ShouldBeNull();
        descriptor.ToString().ShouldBe(descriptor.FullName);
        AssemblyReferenceInfo reference = descriptor.References.First();
        reference.ToString().ShouldBe($"{reference.Name}, Version={reference.Version}");
    }

    [Fact]
    public void Scan_TellsNativeImagesModulesAndLockedFilesApart()
    {
        Fixture.WriteNativeImage(Path.Join(_temp.Path, "Native.dll"));
        Fixture.Compile(_temp.Path, "Module", "public class A { }", kind: Microsoft.CodeAnalysis.OutputKind.NetModule);
        string locked = Fixture.Compile(_temp.Path, "Locked", "public class A { }");
        AssemblyScanner scanner = Assemblies.Scan().FromDirectory(_temp.Path);

        ScanResult whileLocked;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            whileLocked = scanner.Scan();
        ScanResult afterwards = scanner.Scan();

        ScanDiagnostic For(string file) => whileLocked.Diagnostics.Single(d => Path.GetFileName(d.Path) == file);
        whileLocked.ShouldBeEmpty();
        For("Native.dll").Reason.ShouldBe(SkipReason.NotManaged);
        For("Native.dll").Message.ShouldBe("The file is a native image without .NET metadata.");
        For("Module.dll").Reason.ShouldBe(SkipReason.NotAnAssembly);
        For("Locked.dll").Reason.ShouldBe(SkipReason.Unreadable);
        For("Locked.dll").Exception.ShouldBeAssignableTo<IOException>();
        For("Locked.dll").ToString().ShouldStartWith($"Unreadable: {locked} - The file could not be read: ", Case.Sensitive);

        // A file that could not be read is looked at again next time.
        afterwards.Select(a => a.Name).ShouldBe(["Locked"]);
    }

    [Fact]
    public void FromDirectoriesAndFromBaseDirectory_AddSeveralSources()
    {
        Fixture.Compile(_temp.Sub("one"), "DirOne", "public class A { }");
        Fixture.Compile(_temp.Sub("two"), "DirTwo", "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromDirectories(Path.Join(_temp.Path, "one"), Path.Join(_temp.Path, "two"))
            .FromBaseDirectory()
            .Include("Dir*", "Masterly.AssembliesScanner")
            .Scan();

        result.Select(a => a.Name).ShouldBe(["DirOne", "DirTwo", "Masterly.AssembliesScanner"]);
    }

    [Fact]
    public void Matching_TakesPatternsBuiltInCode()
    {
        Fixture.Compile(_temp.Path, "Acme.Api", "public class A { }");
        Fixture.Compile(_temp.Path, "Acme.Api.Tests", "public class A { }");
        Fixture.Compile(_temp.Path, "Other.Api", "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .Matching(NamePattern.Glob("Acme.*"), NamePattern.Regex(@"\.Tests\.dll$").AsExclusion())
            .Scan();

        result.Select(a => a.Name).ShouldBe(["Acme.Api"]);
        result.Diagnostics.Count.ShouldBe(2);
        result.Diagnostics.ShouldAllBe(d => d.Reason == SkipReason.ExcludedByPattern);
    }

    [Fact]
    public void Where_WithoutADescription_IsReportedAsACustomPredicate()
    {
        Fixture.Compile(_temp.Path, "Unnamed", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Where(a => false).Scan();

        result.ShouldBeEmpty();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe("Rejected by filter: custom predicate.");
    }

    [Fact]
    public void AFilterThatCannotReadTheFile_SkipsIt()
    {
        Fixture.Compile(_temp.Path, "Guarded", "public class A { }");

        ScanResult result = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .Where(a => throw new IOException("disk gone"), "disk check")
            .Scan();

        result.ShouldBeEmpty();
        ScanDiagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Reason.ShouldBe(SkipReason.Unreadable);
        diagnostic.Message.ShouldBe("Filter 'disk check' could not read the file: disk gone");
        diagnostic.Exception.ShouldBeOfType<IOException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFilterThatFails_SurfacesItsOwnException(bool onOneThread)
    {
        // Twelve candidates, so the default scan evaluates them in parallel.
        for (int i = 0; i < 12; i++)
            Fixture.Compile(_temp.Path, $"Bulk{i:00}", "public class A { }");

        AssemblyScanner scanner = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .Where(a =>
            {
                if (a.Name == "Bulk07")
                    throw new InvalidOperationException("boom");

                return true;
            }, "explosive");
        if (onOneThread)
            scanner.WithParallelism(1);

        Action act = () => scanner.Scan();

        Should.Throw<InvalidOperationException>(act).Message.ShouldBe("boom");
    }

    [Fact]
    public void WithParallelism_RejectsLessThanOne()
    {
        Action act = () => Assemblies.Scan().WithParallelism(0);

        Should.Throw<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void AssembliesLoadedFromMemory_AreDescribedWithoutAPath()
    {
        string name = Fixture.UniqueName("FromMemory");
        System.Reflection.Assembly assembly = Fixture.LoadFromMemory(Fixture.Compile(_temp.Path, name, "public class A { }"));

        ScanResult result = Assemblies.Scan().FromAssemblies(assembly, assembly).Scan();

        AssemblyDescriptor descriptor = result.ShouldHaveSingleItem();
        descriptor.Path.ShouldBeNull();
        descriptor.FileName.ShouldBe(name + ".dll");
        descriptor.Sha256.ShouldBeNull();
        descriptor.Source.ShouldBe(AssemblySource.Loaded);
        descriptor.Load().ShouldBeSameAs(assembly);
        descriptor.Types.Select(t => t.FullName).ShouldContain("A");
        ScanDiagnostic duplicate = result.Diagnostics.ShouldHaveSingleItem();
        duplicate.Reason.ShouldBe(SkipReason.Duplicate);
        duplicate.Path.ShouldBe(name + ".dll");
        result.Explain(name).ShouldBe($"Selected: {descriptor.FullName} from memory.");
    }

    [Fact]
    public void RequireHash_RejectsAnAssemblyThatHasNoFile()
    {
        string name = Fixture.UniqueName("Hashless");
        System.Reflection.Assembly assembly = Fixture.LoadFromMemory(Fixture.Compile(_temp.Path, name, "public class A { }"));

        ScanResult result = Assemblies.Scan().FromAssemblies(assembly).RequireHash(new string('0', 64)).Scan();

        result.ShouldBeEmpty();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe("Rejected by filter: SHA-256 allow-list.");
    }

    [Fact]
    public void FromAssemblies_ListsAnAssemblyOnce_AndReadsItsTypesFromMemory()
    {
        ScanResult result = Assemblies.Scan()
            .FromAssemblyOf<AssemblyScanner>()
            .FromAssemblies(typeof(AssemblyScanner).Assembly)
            .Scan();

        AssemblyDescriptor descriptor = result.ShouldHaveSingleItem();
        result.Diagnostics.ShouldBeEmpty();
        descriptor.IsLoaded.ShouldBeTrue();
        descriptor.Types.Select(t => t.FullName).ShouldContain(typeof(AssemblyScanner).FullName);
    }

    [Fact]
    public void Load_ReturnsTheSameAssembly_EveryTime()
    {
        string name = Fixture.UniqueName("LoadedTwice");
        Fixture.Compile(_temp.Path, name, "public class A { }");
        AssemblyDescriptor descriptor = Assemblies.Scan().FromDirectory(_temp.Path).Scan().ShouldHaveSingleItem();

        System.Reflection.Assembly first = descriptor.Load();

        descriptor.Load().ShouldBeSameAs(first);
        descriptor.LoadedAssembly.ShouldBeSameAs(first);
    }

    [Fact]
    public void Load_IntoAGivenContext_LoadsTheScannedFileThere()
    {
        string name = Fixture.UniqueName("Elsewhere");
        Fixture.Compile(_temp.Path, name, "public class A { }");
        AssemblyDescriptor descriptor = Assemblies.Scan().FromDirectory(_temp.Path).Scan().ShouldHaveSingleItem();
        var context = new AssemblyLoadContext(Fixture.UniqueName("Context"), isCollectible: true);

        System.Reflection.Assembly loaded = descriptor.Load(context);
        Action withoutContext = () => descriptor.Load(null!);

        AssemblyLoadContext.GetLoadContext(loaded).ShouldBeSameAs(context);
        AssemblyLoadContext.Default.Assemblies.ShouldNotContain(a => a.GetName().Name == name);
        descriptor.IsLoaded.ShouldBeFalse();
        Should.Throw<ArgumentNullException>(withoutContext);
        context.Unload();
    }

    [Fact]
    public void Load_IntoAGivenContext_NeedsAFile()
    {
        string name = Fixture.UniqueName("NoFile");
        System.Reflection.Assembly assembly = Fixture.LoadFromMemory(Fixture.Compile(_temp.Path, name, "public class A { }"));
        AssemblyDescriptor descriptor = Assemblies.Scan().FromAssemblies(assembly).Scan().ShouldHaveSingleItem();
        var context = new AssemblyLoadContext(Fixture.UniqueName("Context"), isCollectible: true);

        Action act = () => descriptor.Load(context);

        Should.Throw<InvalidOperationException>(act).Message.ShouldContain("has no file path");
        context.Unload();
    }

    [Fact]
    public void FromDependencyClosure_StartsAtTheEntryAssembly_WhenNoRootIsGiven()
    {
        string? entry = System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name;

        ScanResult result = Assemblies.Scan().FromDependencyClosure().Scan();

        result[0].Name.ShouldBe(entry);
        result[0].Source.ShouldBe(AssemblySource.DependencyClosure);
        result.Select(a => a.Name).ShouldContain("Masterly.AssembliesScanner");
    }

    [Fact]
    public void FromDependencyClosure_OfADynamicAssembly_ReportsThatItHasNoMetadata()
    {
        System.Reflection.Assembly dynamic = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName(Fixture.UniqueName("Dynamic")),
            System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);

        ScanResult result = Assemblies.Scan().FromDependencyClosure(dynamic).Scan();

        result.ShouldBeEmpty();
        ScanDiagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Reason.ShouldBe(SkipReason.NotManaged);
        diagnostic.Message.ShouldBe("Dynamic assemblies have no readable metadata.");
    }

    [Fact]
    public void TargetingFramework_SkipsAnAssemblyThatDoesNotSayWhatItTargets()
    {
        Fixture.Compile(_temp.Path, "Untargeted", "public class A { }");

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).TargetingFramework("net8.0").Scan();

        result.ShouldBeEmpty();
        result.Diagnostics.ShouldHaveSingleItem().Message.ShouldBe("Rejected by filter: targeting net8.0.");
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("net")]
    [InlineData("netx")]
    public void NormalizeFramework_LeavesWhatItDoesNotRecognizeAlone(string text)
    {
        AssemblyScanner.NormalizeFramework(text).ShouldBe(text);
    }

    [Fact]
    public void ContainingImplementationsOf_RejectsATypeWithoutAName()
    {
        Type typeParameter = typeof(List<>).GetGenericArguments()[0];

        Action act = () => Assemblies.Scan().ContainingImplementationsOf(typeParameter);

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void Types_NameNestedBaseTypesAndInterfaces_WithAPlusSign()
    {
        string library = Fixture.Compile(_temp.Path, "Nesting.Library", "public class Outer { public interface IInner { } public class Base { } }");
        Fixture.Compile(_temp.Path, "Nesting.User", "public class Implementation : Outer.Base, Outer.IInner { }", [library]);

        ScanResult result = Assemblies.Scan().FromDirectory(_temp.Path).Scan();
        TypeDescriptor implementation = result.Find("Nesting.User")!.Types.Single(t => t.Name == "Implementation");

        implementation.BaseType.ShouldBe("Outer+Base");
        implementation.Interfaces.ShouldBe(["Outer+IInner"]);
        result.Find("Nesting.Library")!.Types.Select(t => t.FullName).ShouldBe(["Outer", "Outer+IInner", "Outer+Base"], ignoreOrder: true);
    }

    [Fact]
    public void Types_DescribeEveryKindOfType()
    {
        Fixture.Compile(_temp.Path, "Kinds", """
            namespace Shapes
            {
                public struct Point { }
                public enum Colour { Red }
                public static class Helper { }
                public interface IShape { }
                public abstract class Base { }
                [System.Obsolete] public sealed class Square : Base, IShape { public class Corner { } }
                internal class Box<T> { }
            }
            """);

        AssemblyDescriptor descriptor = Assemblies.Scan().FromDirectory(_temp.Path).Scan().ShouldHaveSingleItem();
        TypeDescriptor Type(string name) => descriptor.Types.Single(t => t.Name == name);

        Type("Point").IsValueType.ShouldBeTrue();
        Type("Point").IsEnum.ShouldBeFalse();
        Type("Point").IsClass.ShouldBeFalse();
        Type("Colour").IsEnum.ShouldBeTrue();
        Type("Colour").IsValueType.ShouldBeTrue();
        Type("Helper").IsStatic.ShouldBeTrue();
        Type("Helper").IsConcreteClass.ShouldBeFalse();
        Type("IShape").IsInterface.ShouldBeTrue();
        Type("IShape").IsClass.ShouldBeFalse();
        Type("Base").IsAbstract.ShouldBeTrue();
        Type("Base").IsStatic.ShouldBeFalse();

        TypeDescriptor square = Type("Square");
        square.IsSealed.ShouldBeTrue();
        square.IsConcreteClass.ShouldBeTrue();
        square.IsPublic.ShouldBeTrue();
        square.Namespace.ShouldBe("Shapes");
        square.HasAttribute<ObsoleteAttribute>().ShouldBeTrue();
        square.HasAttribute("System.FlagsAttribute").ShouldBeFalse();
        square.Declares("Shapes.Base").ShouldBeTrue();
        square.Declares("Shapes.IShape").ShouldBeTrue();
        square.Declares("Shapes.Point").ShouldBeFalse();
        square.ToString().ShouldBe("Shapes.Square");

        Type("Corner").IsNested.ShouldBeTrue();
        Type("Corner").FullName.ShouldBe("Shapes.Square+Corner");
        Type("Box`1").GenericParameterCount.ShouldBe(1);
        Type("Box`1").IsPublic.ShouldBeFalse();
    }

    [Fact]
    public void Scan_SurvivesMetadataNoCompilerWouldWrite()
    {
        Fixture.WriteAssemblyWithOddMetadata(_temp.Path, "Odd");

        AssemblyDescriptor descriptor = Assemblies.Scan().FromDirectory(_temp.Path).Scan().ShouldHaveSingleItem();

        descriptor.Name.ShouldBe("Odd");
        descriptor.Title.ShouldBeNull();
        descriptor.Company.ShouldBeNull();
        descriptor.Product.ShouldBe("Ok");
        descriptor.AssemblyAttributes.ShouldBe([
            "System.Reflection.AssemblyTitleAttribute",
            "System.Reflection.AssemblyCompanyAttribute",
            "System.Reflection.AssemblyProductAttribute"
        ]);
        descriptor.References.ShouldHaveSingleItem().PublicKeyToken.ShouldBe("b77a5c561934e089");
        descriptor.Types.ShouldBeEmpty();
    }
}
