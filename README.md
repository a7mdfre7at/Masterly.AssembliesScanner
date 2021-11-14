# Masterly.AssembliesScanner
A helper methods scan and returns all assemblies in the current app domain according to name patterns.

<img src="https://raw.githubusercontent.com/a7mdfre7at/Masterly.AssembliesScanner/master/repo_image.png" width="200" height="180">

[![Nuget](https://img.shields.io/nuget/v/Masterly.AssembliesScanner?style=flat-square)](https://www.nuget.org/packages/Masterly.AssembliesScanner) ![Nuget](https://img.shields.io/nuget/dt/Masterly.AssembliesScanner?label=nuget%20downloads&style=flat-square) ![GitHub last commit](https://img.shields.io/github/last-commit/a7mdfre7at/Masterly.AssembliesScanner?style=flat-square) ![GitHub](https://img.shields.io/github/license/a7mdfre7at/Masterly.AssembliesScanner?style=flat-square)

## Give a Star! :star:

If you like or are using this project please give it a star. Thanks!

## Usage

```c#
// Scan all assemblies that thier names starts with "Masterly." and ends with ".dll"
string[] assemblyPatterns = new string[] { "Masterly.*.dll" };
Assembly[] asslemblyArray = Assemblies.Get(assemblyPatterns);

// To execlud assemblies, add '!' (Exclamation mark) before name pattern as below

// Scan all assemblies that thier names starts with "Masterly." and ends with ".dll" excluding all assemblies that ends with ".UnitTests.dll" 
string[] assemblyPatterns = new string[] { "Masterly.*.dll", "!.UnitTests.dll$" };
Assembly[] asslemblyArray = Assemblies.Get(assemblyPatterns);

```

## License

MIT
**Free Software, Hell Yeah!**