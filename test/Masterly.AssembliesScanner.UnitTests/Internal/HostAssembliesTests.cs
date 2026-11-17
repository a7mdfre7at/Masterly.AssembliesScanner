using System.Runtime.Loader;
using Masterly.AssembliesScanner.Internal;

namespace Masterly.AssembliesScanner.UnitTests;

public class HostAssembliesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ResolvePath_FindsWhatTheHostWouldLoad_AndNothingElse()
    {
        string? path = HostAssemblies.ResolvePath("System.Runtime");

        File.Exists(path).ShouldBeTrue();
        Path.GetFileName(path).ShouldBe("System.Runtime.dll");
        HostAssemblies.ResolvePath("Masterly.AssembliesScanner").ShouldBe(typeof(Assemblies).Assembly.Location, StringCompareShould.IgnoreCase);
        HostAssemblies.ResolvePath("Definitely.Not.There").ShouldBeNull();
    }

    [Fact]
    public void CanProvide_CoversPlatformAssemblies_AndWhatTheDefaultContextLoaded()
    {
        string name = Fixture.UniqueName("LoadedByHost");
        string path = Fixture.Compile(_temp.Path, name, "public class A { }");

        bool before = HostAssemblies.CanProvide(name);
        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

        before.ShouldBeFalse();
        HostAssemblies.CanProvide(name).ShouldBeTrue();
        HostAssemblies.CanProvide("System.Runtime").ShouldBeTrue();
    }

    [Theory]
    [InlineData("System.Text.Json", "cc7b13ffcd2ddd51", true, true)]
    [InlineData("mscorlib", "b77a5c561934e089", true, true)]
    [InlineData("Microsoft.Extensions.Logging", "adb9793829ddae60", false, true)]
    [InlineData("Microsoft.Identity.Client", "0a613f4dd989e8ae", false, true)]
    [InlineData("System.Lookalike", null, false, false)]
    [InlineData("Contoso.Core", "cc7b13ffcd2ddd51", false, false)]
    public void FrameworkHeuristics_NeedBothAMicrosoftKeyAndAFrameworkName(string name, string? token, bool framework, bool microsoft)
    {
        FrameworkAssemblies.IsFramework(name, token, null).ShouldBe(framework);
        FrameworkAssemblies.IsMicrosoft(name, token, null).ShouldBe(microsoft);
    }

    [Fact]
    public void FrameworkHeuristics_TrustAFileInTheSharedFramework_WhateverItIsCalled()
    {
        string runtimeFile = typeof(object).Assembly.Location;

        FrameworkAssemblies.IsFramework("Anything", null, runtimeFile).ShouldBeTrue();
        FrameworkAssemblies.IsFramework("Anything", null, Path.Combine(_temp.Path, "Anything.dll")).ShouldBeFalse();
    }
}
