namespace Masterly.AssembliesScanner.UnitTests;

public class NamePatternTests
{
    [Theory]
    [InlineData("Contoso.*", "Contoso.Core.dll", true)]
    [InlineData("Contoso.*.dll", "Contoso.Core.dll", true)]
    [InlineData("Contoso.Core", "Contoso.Core.dll", true)]
    [InlineData("contoso.core.DLL", "Contoso.Core.dll", true)]
    [InlineData("Contoso.Core", "Contoso.Core.Tests.dll", false)]
    [InlineData("*.Tests", "Contoso.Core.Tests.dll", true)]
    [InlineData("Contoso.?ore", "Contoso.Core.dll", true)]
    [InlineData("Contoso.?", "Contoso.Core.dll", false)]
    [InlineData("Fabrikam.*", "Contoso.Core.dll", false)]
    [InlineData("*", "anything.dll", true)]
    public void Glob_MatchesWholeName_IgnoringCaseAndExtension(string pattern, string fileName, bool expected)
    {
        NamePattern.Glob(pattern).IsMatch(fileName).ShouldBe(expected);
    }

    [Fact]
    public void Glob_TreatsDotsLiterally()
    {
        // As a regular expression "Contoso.Core" would also match "ContosoXCore".
        NamePattern.Glob("Contoso.Core").IsMatch("ContosoXCore.dll").ShouldBeFalse();
    }

    [Theory]
    [InlineData("plugins/**/*.dll", "plugins/a/b/Foo.dll", true)]
    [InlineData("plugins/**/*.dll", "plugins/Foo.dll", true)]
    [InlineData("plugins/*/Foo.dll", "plugins/a/b/Foo.dll", false)]
    [InlineData("plugins/*/Foo.dll", "plugins/a/Foo.dll", true)]
    [InlineData("plugins/**", "other/Foo.dll", false)]
    [InlineData(@"plugins\*\Foo.dll", "plugins/a/Foo.dll", true)]
    public void Glob_WithDirectorySeparator_MatchesRelativePath(string pattern, string relativePath, bool expected)
    {
        NamePattern.Glob(pattern).IsMatch("Foo.dll", relativePath).ShouldBe(expected);
    }

    [Fact]
    public void Regex_IsUnanchoredAndCaseInsensitive()
    {
        NamePattern pattern = NamePattern.Regex(@"\.tests\.dll$");

        pattern.IsMatch("Contoso.Core.Tests.dll").ShouldBeTrue();
        pattern.IsMatch("Contoso.Core.dll").ShouldBeFalse();
    }

    [Fact]
    public void Regex_KeepsTheMeaningOfUpperCaseEscapes()
    {
        // \D is "not a digit". The version 1 API lower-cased patterns, which turned it into \d.
        NamePattern.Regex(@"^\D+$").IsMatch("Contoso.dll").ShouldBeTrue();
        NamePattern.Regex(@"^\D+$").IsMatch("Contoso2.dll").ShouldBeFalse();
    }

    [Fact]
    public void Exact_MatchesFileNameOrAssemblyNameOnly()
    {
        NamePattern pattern = NamePattern.Exact("Contoso.Core");

        pattern.IsMatch("Contoso.Core.dll").ShouldBeTrue();
        pattern.IsMatch("Contoso.Core.Extra.dll").ShouldBeFalse();
    }

    [Theory]
    [InlineData("!Contoso.*", PatternSyntax.Glob, true, "Contoso.*")]
    [InlineData("regex:^Contoso", PatternSyntax.Regex, false, "^Contoso")]
    [InlineData("! regex:Tests$", PatternSyntax.Regex, true, "Tests$")]
    [InlineData("exact:Contoso.Core", PatternSyntax.Exact, false, "Contoso.Core")]
    [InlineData("  glob:Contoso.*  ", PatternSyntax.Glob, false, "Contoso.*")]
    public void Parse_ReadsExclusionMarkerAndSyntaxPrefix(string text, PatternSyntax syntax, bool exclusion, string expectedText)
    {
        NamePattern pattern = NamePattern.Parse(text);

        pattern.Syntax.ShouldBe(syntax);
        pattern.IsExclusion.ShouldBe(exclusion);
        pattern.Text.ShouldBe(expectedText);
    }

    [Fact]
    public void Parse_UsesTheDefaultSyntaxWhenThereIsNoPrefix()
    {
        NamePattern.Parse("Contoso", PatternSyntax.Regex).Syntax.ShouldBe(PatternSyntax.Regex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!")]
    [InlineData("regex:")]
    public void Parse_RejectsEmptyPatterns(string text)
    {
        Action act = () => NamePattern.Parse(text);

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void Regex_RejectsInvalidExpressions()
    {
        Action act = () => NamePattern.Regex("(unclosed");

        Should.Throw<ArgumentException>(act);
    }

    [Theory]
    [InlineData("plugins/*.dll", "a.dll", "plugins/a.dll", true)]
    [InlineData("plugins/*.dll", "a.dll", @"plugins\a.dll", true)]
    [InlineData("plugins/*.dll", "a.dll", "plugins/deep/a.dll", false)]
    [InlineData("plugins/**/*.dll", "a.dll", "plugins/deep/er/a.dll", true)]
    [InlineData("plugins/**/*.dll", "a.dll", "plugins/a.dll", true)]
    [InlineData(@"**\a.dll", "a.dll", "x/y/a.dll", true)]
    [InlineData(@"**\a.dll", "a.dll", "a.dll", true)]
    [InlineData("plugins/**", "a.dll", "plugins/deep/a.dll", true)]
    [InlineData("plugins/*.dll", "a.dll", null, false)]
    [InlineData("Acme**Core", "Acme.Data.Core.dll", null, true)]
    [InlineData("Acme**Core", "Acme.Data.Core.dll", "sub/Acme.Data.Core.dll", true)]
    public void Glob_UsesTheRelativePath_OnlyWhenThePatternHasASeparator(string pattern, string fileName, string? relativePath, bool expected)
    {
        NamePattern.Glob(pattern).IsMatch(fileName, relativePath).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Foo", "Foo.dll", true)]
    [InlineData("Foo.dll", "Foo.dll", true)]
    [InlineData("foo", "FOO.exe", true)]
    [InlineData("Foo", "Foo.Bar.dll", false)]
    [InlineData("Foo*", "Foo.dll", false)]
    public void Exact_ComparesTheWholeName_WithoutWildcards(string name, string fileName, bool expected)
    {
        NamePattern.Exact(name).IsMatch(fileName).ShouldBe(expected);
    }

    [Fact]
    public void AsExclusion_TurnsAPatternIntoAnExclusion_Once()
    {
        NamePattern include = NamePattern.Glob("*.Tests");

        NamePattern exclusion = include.AsExclusion();

        include.IsExclusion.ShouldBeFalse();
        exclusion.IsExclusion.ShouldBeTrue();
        exclusion.Text.ShouldBe("*.Tests");
        exclusion.Syntax.ShouldBe(PatternSyntax.Glob);
        exclusion.AsExclusion().ShouldBeSameAs(exclusion);
    }

    [Theory]
    [InlineData("*.Tests", "glob:*.Tests")]
    [InlineData("!*.Tests", "!glob:*.Tests")]
    [InlineData("regex:^Acme", "regex:^Acme")]
    [InlineData("!exact:Acme.Core", "!exact:Acme.Core")]
    public void ToString_RoundTripsThroughParse(string text, string expected)
    {
        NamePattern pattern = NamePattern.Parse(text);

        pattern.ToString().ShouldBe(expected);
        NamePattern.Parse(pattern.ToString()).ToString().ShouldBe(expected);
    }
}
