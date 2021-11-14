using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Masterly.AssembliesScanner.UnitTests
{
    public class AssembliesTests
    {
        [Fact]
        public void Scan_ShouldReturn_AllAssembliesStartsWithMasterly()
        {
            // Arrange
            string[] assemblyPatterns = new string[] { "Masterly.*.dll" };

            // Act
            Assembly[] result = Assemblies.Get(assemblyPatterns);

            // Assert
            result.Length.Should().Be(2);
            result.All(a => a.ManifestModule.Name.StartsWith("Masterly.AssembliesScanner")).Should().BeTrue();
        }

        [Fact]
        public void Scan_ShouldNotReturn_AssebmluEndsWithUnitTestsDll()
        {
            // Arrange
            string[] assemblyPatterns = new string[] { "Masterly.*.dll", "!.UnitTests.dll$" };

            // Act
            Assembly[] result = Assemblies.Get(assemblyPatterns);

            // Assert
            result.Length.Should().Be(1);
            result.FirstOrDefault().ManifestModule.Name.Should().Be("Masterly.AssembliesScanner.dll");
        }
    }
}