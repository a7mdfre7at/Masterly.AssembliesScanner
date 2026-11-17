using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit.Sdk;
using Xunit.v3;

// The tests share process-wide state (the metadata cache and the default load context),
// so they run one class at a time.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace Masterly.AssembliesScanner.UnitTests;

/// <summary>A scratch directory that is removed, as far as file locks allow, when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "masterly-scanner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        string path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Assemblies loaded from here stay locked until the process exits.
        }
    }
}

/// <summary>Compiles small assemblies on the fly so tests control versions, references and attributes.</summary>
internal static class Fixture
{
    private static readonly List<MetadataReference> PlatformReferences = BuildPlatformReferences();

    /// <summary>A name no other test uses, so loading it into the default context never collides.</summary>
    public static string UniqueName(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Compiles <paramref name="source"/> into <c>assemblyName.dll</c> and returns the path of the file.</summary>
    /// <param name="kind">Pass <see cref="OutputKind.NetModule"/> for a file with metadata but no assembly manifest.</param>
    /// <param name="withSymbols">Also writes <c>assemblyName.pdb</c>, with <c>assemblyName.cs</c> as the source file name.</param>
    public static string Compile(
        string directory,
        string assemblyName,
        string source,
        IEnumerable<string>? references = null,
        IEnumerable<string>? resourceNames = null,
        OutputKind kind = OutputKind.DynamicallyLinkedLibrary,
        bool withSymbols = false)
    {
        Directory.CreateDirectory(directory);

        IEnumerable<MetadataReference> allReferences = PlatformReferences
            .Concat((references ?? []).Select(r => (MetadataReference)MetadataReference.CreateFromFile(r)));

        // Symbols need a source text with an encoding and a file name.
        SyntaxTree tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: assemblyName + ".cs");
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, allReferences, new CSharpCompilationOptions(kind));

        IEnumerable<ResourceDescription> resources = (resourceNames ?? [])
            .Select(name => new ResourceDescription(name, () => new MemoryStream([1, 2, 3]), isPublic: true));

        // Emit to memory first so the file appears on disk complete, in one write.
        using var image = new MemoryStream();
        using var symbols = new MemoryStream();
        EmitResult result = compilation.Emit(
            image,
            withSymbols ? symbols : null,
            manifestResources: resources,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
            throw new InvalidOperationException("Fixture did not compile:" + Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics));

        string path = Path.Combine(directory, assemblyName + ".dll");
        if (withSymbols)
            File.WriteAllBytes(Path.ChangeExtension(path, ".pdb"), symbols.ToArray());

        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    /// <summary>Writes a well-formed native image: a PE file that carries no .NET metadata.</summary>
    public static string WriteNativeImage(string path)
    {
        var image = new BlobBuilder();
        new NativeImageBuilder().Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    /// <summary>Loads a file from a stream into a load context of its own, so the assembly has no location.</summary>
    public static Assembly LoadFromMemory(string path)
    {
        var context = new AssemblyLoadContext(UniqueName("Memory"), isCollectible: true);
        using var image = new MemoryStream(File.ReadAllBytes(path));
        return context.LoadFromStream(image);
    }

    /// <summary>
    /// Writes an assembly no compiler would produce: attribute values without a prolog or cut short,
    /// attributes declared on something that has no type name, and a reference that carries a full
    /// public key instead of a token.
    /// </summary>
    public static string WriteAssemblyWithOddMetadata(string directory, string assemblyName)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(assemblyName + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        // The ECMA public key; its token is b77a5c561934e089.
        byte[] ecmaKey = [0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0];
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"), new Version(8, 0, 0, 0), default, metadata.GetOrAddBlob(ecmaKey), AssemblyFlags.PublicKey, default);

        var signature = new BlobBuilder();
        new BlobEncoder(signature).MethodSignature(isInstanceMethod: true)
            .Parameters(1, returnType => returnType.Void(), parameters => parameters.AddParameter().Type().String());
        BlobHandle stringConstructor = metadata.GetOrAddBlob(signature);

        EntityHandle AttributeType(string name)
            => metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Reflection"), metadata.GetOrAddString(name));
        void Apply(EntityHandle declaringType, params byte[] value)
            => metadata.AddCustomAttribute(
                EntityHandle.AssemblyDefinition,
                metadata.AddMemberReference(declaringType, metadata.GetOrAddString(".ctor"), stringConstructor),
                metadata.GetOrAddBlob(value));

        Apply(AttributeType("AssemblyTitleAttribute"), 0xFF);                               // no prolog
        Apply(AttributeType("AssemblyCompanyAttribute"), 1, 0, 32, (byte)'a');              // string cut short
        Apply(AttributeType("AssemblyProductAttribute"), 1, 0, 2, (byte)'O', (byte)'k', 0, 0);
        Apply(metadata.AddTypeSpecification(metadata.GetOrAddBlob(new byte[] { 0x1D, 0x08 })), 1, 0, 0, 0);   // declared on int[]
        Apply(metadata.AddModuleReference(metadata.GetOrAddString("native")), 1, 0, 0, 0);                    // declared on a module

        var image = new BlobBuilder();
        new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder()).Serialize(image);

        string path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    /// <summary>
    /// Writes an assembly whose one class derives from <c>System.Object</c> taken from <c>System.Runtime</c> in
    /// <paramref name="runtimeVersion"/>, as if it had been built for that version of .NET.
    /// </summary>
    public static string WriteAssemblyForRuntime(string directory, string assemblyName, Version runtimeVersion)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(assemblyName + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(assemblyName), new Version(1, 0, 0, 0), default, default, 0, AssemblyHashAlgorithm.None);

        // System.Runtime's public key token.
        AssemblyReferenceHandle runtime = metadata.AddAssemblyReference(
            metadata.GetOrAddString("System.Runtime"), runtimeVersion, default,
            metadata.GetOrAddBlob(new byte[] { 0xB0, 0x3F, 0x5F, 0x7F, 0x11, 0xD5, 0x0A, 0x3A }), default, default);
        TypeReferenceHandle objectType = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));

        metadata.AddTypeDefinition(default, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddTypeDefinition(
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class,
            metadata.GetOrAddString("Future"), metadata.GetOrAddString("Thing"), objectType,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var image = new BlobBuilder();
        new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            ilStream: new BlobBuilder()).Serialize(image);

        string path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, image.ToArray());
        return path;
    }

    /// <summary>Creates an instance of a type that a fixture assembly declares.</summary>
    public static object Create(Assembly assembly, string typeName)
        => Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!)!;

    /// <summary>Calls a public method without parameters by name and returns what it returns.</summary>
    public static object? Call(object target, string methodName)
        => target.GetType().GetMethod(methodName)!.Invoke(target, null);

    /// <summary>Walks a sequence through the non-generic <see cref="IEnumerable"/>, as old-style code would.</summary>
    public static List<object> Enumerate(IEnumerable sequence)
    {
        var items = new List<object>();
        foreach (object item in sequence)
            items.Add(item);

        return items;
    }

    private static List<MetadataReference> BuildPlatformReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            paths.Add(path);

        paths.Add(typeof(Fixture).Assembly.Location);
        paths.Add(typeof(Assemblies).Assembly.Location);
        return [.. paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
    }

    /// <summary>The smallest PE file the reader accepts: one code section and no CLR header.</summary>
    private sealed class NativeImageBuilder : PEBuilder
    {
        public NativeImageBuilder()
            : base(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll), deterministicIdProvider: null)
        {
        }

        protected override ImmutableArray<Section> CreateSections()
            => [new Section(".text", SectionCharacteristics.MemRead | SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode)];

        protected override BlobBuilder SerializeSection(string name, SectionLocation location)
        {
            var code = new BlobBuilder();
            code.WriteBytes(0xC3, 16);
            return code;
        }

        protected override PEDirectoriesBuilder GetDirectories() => new();
    }
}
