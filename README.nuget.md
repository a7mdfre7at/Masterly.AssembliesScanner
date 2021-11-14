# Masterly.AssembliesScanner
A helper methods scan and returns all assemblies in the current app domain according to name patterns.

## Give a Star om Github! :star:

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