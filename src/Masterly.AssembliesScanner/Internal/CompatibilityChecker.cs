using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Masterly.AssembliesScanner.Internal;

/// <summary>
/// Checks, from metadata alone, that every type and member an assembly uses from other assemblies exists in the
/// copies the host would provide, with the same signature and accessible, and that its classes still implement
/// everything their base classes and interfaces declare abstract.
/// </summary>
internal sealed class CompatibilityChecker
{
    private const int MaxDepth = 64;

    private readonly CompatibilityOptions _options;
    private readonly Dictionary<string, string> _scanned = new(StringComparer.OrdinalIgnoreCase);

    public CompatibilityChecker(CompatibilityOptions options, IEnumerable<AssemblyDescriptor> scanned)
    {
        _options = options;
        foreach (AssemblyDescriptor assembly in scanned)
        {
            if (assembly.Path is not null)
                _scanned.TryAdd(assembly.Name, assembly.Path);
        }
    }

    /// <summary>Checks one assembly, or returns <c>null</c> when it has no file that can be read.</summary>
    public List<CompatibilityIssue>? Check(AssemblyDescriptor assembly)
    {
        if (assembly.Path is null)
            return null;

        using MetadataModule? module = MetadataModule.OpenWithBodies(assembly.Path);
        return module is null ? null : new Session(this, assembly, module).Run();
    }

    private enum LookupStatus
    {
        Found,
        AssemblyMissing,
        TypeMissing
    }

    private readonly record struct TypeLookup(LookupStatus Status, MetadataModule? Module, TypeDefinitionHandle Handle, string AssemblyName);

    /// <summary>A type definition together with what its generic parameters stand for, or <c>null</c> to keep them open.</summary>
    private sealed record Link(MetadataModule Module, TypeDefinitionHandle Handle, string[]? Arguments)
    {
        public TypeDefinition Definition => Module.Reader.GetTypeDefinition(Handle);

        public string Display
        {
            get
            {
                string name = MetadataInspector.GetTypeName(Module.Reader, Handle)!;
                return Arguments is null ? name : name + "<" + string.Join(",", Arguments) + ">";
            }
        }
    }

    /// <summary>A type and its base classes, and whether every base class could be found.</summary>
    private sealed record Chain(List<Link> Links, bool Complete);

    private sealed record Draft(CompatibilityIssueKind Kind, string Target, string Member, string Message, Type Exception)
    {
        public SortedSet<string> UsedBy { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Session(CompatibilityChecker owner, AssemblyDescriptor assembly, MetadataModule module)
    {
        private readonly MetadataReader _reader = module.Reader;
        private readonly Dictionary<string, MetadataModule?> _assemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(MetadataModule, TypeReferenceHandle), TypeLookup> _typeLookups = [];
        private readonly Dictionary<string, Draft> _issues = new(StringComparer.Ordinal);
        private readonly Dictionary<EntityHandle, Draft> _users = [];
        private readonly Dictionary<string, Draft> _missingAssemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Draft> _tooOld = new(StringComparer.OrdinalIgnoreCase);

        // Uses of an assembly that is too old: they name its users without hiding problems with the members themselves.
        private readonly Dictionary<EntityHandle, Draft> _softUsers = [];
        private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TypeReferenceHandle, TypeLookup> _hidden = [];
        private List<(string User, EntityHandle Token)>? _ilUses;
        private readonly SignatureFormatter _formatter = SignatureFormatter.Instance;

        public List<CompatibilityIssue> Run()
        {
            CheckAssemblyReferences();
            CheckTypeReferences();
            CheckMemberReferences();
            CheckTypeDefinitions();
            if (_hidden.Count > 0)
                CheckHiddenTypeUses();

            if (_users.Count > 0 || _softUsers.Count > 0)
                FindUsers();

            return [.. _issues.Values.Select(d => new CompatibilityIssue(assembly, d.Kind, d.Target, d.Member, d.Message, d.Exception, [.. d.UsedBy]))];
        }

        // ---------------------------------------------------------------- the four checks

        private void CheckAssemblyReferences()
        {
            // The runtime opens a referenced assembly only when a type from it is needed. A reference that no type
            // reference points into (a facade that only forwards types, for example) cannot fail on its own.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypeReferenceHandle typeHandle in _reader.TypeReferences)
            {
                if (ReferencedAssembly(_reader, typeHandle) is { } scope)
                    used.Add(scope);
            }

            foreach (AssemblyReferenceHandle handle in _reader.AssemblyReferences)
            {
                AssemblyReference reference = _reader.GetAssemblyReference(handle);
                string name = _reader.GetString(reference.Name);
                if (!used.Contains(name))
                    continue;

                if (!owner._options.CheckFrameworkReferences
                    && FrameworkAssemblies.IsFramework(name, MetadataInspector.GetPublicKeyToken(_reader, reference), null))
                {
                    _ignored.Add(name);
                    continue;
                }

                MetadataModule? target = ResolveAssembly(name, module.Directory);
                if (target is null)
                {
                    _missingAssemblies[name] = Report(CompatibilityIssueKind.MissingAssembly, name, $"{name}, Version={reference.Version}",
                        $"References {name} {reference.Version}, which neither the host, the scanned assemblies nor the folder of {assembly.FileName} provides.",
                        typeof(FileNotFoundException));
                }
                else if (target.Version < reference.Version)
                {
                    _tooOld[name] = Report(CompatibilityIssueKind.AssemblyVersionTooLow, name, $"{name}, Version={reference.Version}",
                        $"Was built against {name} {reference.Version}, but {target.Version} would be loaded from {target.Path}.",
                        typeof(FileLoadException));
                }
            }
        }

        private void CheckTypeReferences()
        {
            foreach (TypeReferenceHandle handle in _reader.TypeReferences)
            {
                string? scope = ReferencedAssembly(_reader, handle);
                if (scope is null || _ignored.Contains(scope))
                    continue;

                if (_tooOld.TryGetValue(scope, out Draft? tooOld))
                    _softUsers[handle] = tooOld;

                string fullName = MetadataInspector.GetTypeName(_reader, handle)!;
                TypeLookup lookup = ResolveTypeReference(module, handle);
                switch (lookup.Status)
                {
                    case LookupStatus.AssemblyMissing:
                        // Reported once for the assembly; a type forwarded to a missing assembly gets its own entry.
                        if (!_missingAssemblies.TryGetValue(lookup.AssemblyName, out Draft? missing))
                        {
                            _missingAssemblies[lookup.AssemblyName] = missing = Report(
                                CompatibilityIssueKind.MissingAssembly, lookup.AssemblyName, lookup.AssemblyName,
                                $"{fullName} is forwarded to {lookup.AssemblyName}, which cannot be found.",
                                typeof(FileNotFoundException));
                        }

                        _users[handle] = missing;
                        break;

                    case LookupStatus.TypeMissing:
                        _users[handle] = Report(CompatibilityIssueKind.MissingType, lookup.AssemblyName, fullName,
                            $"Uses {fullName}, which {lookup.AssemblyName} {lookup.Module!.Version} does not declare.",
                            typeof(TypeLoadException));
                        break;

                    default:
                        // Whether a hidden type is a problem depends on how it is used; see CheckHiddenTypeUses.
                        if (!IsTypeAccessible(lookup.Module!, lookup.Handle, lookup.Module!.GrantsInternalsTo(module.Name)))
                            _hidden[handle] = lookup;
                        break;
                }
            }
        }

        private void CheckMemberReferences()
        {
            foreach (MemberReferenceHandle handle in _reader.MemberReferences)
            {
                try
                {
                    CheckMemberReference(handle);
                }
                catch (BadImageFormatException)
                {
                    // A signature the reader cannot decode; the runtime would reject it on its own terms.
                }
            }
        }

        private void CheckMemberReference(MemberReferenceHandle handle)
        {
            MemberReference reference = _reader.GetMemberReference(handle);
            if (UnderlyingType(_reader, reference.Parent) is not { } parent)
                return;

            var parentType = (TypeReferenceHandle)parent;
            if (_softUsers.TryGetValue(parentType, out Draft? tooOld))
                _softUsers[handle] = tooOld;

            if (_users.TryGetValue(parentType, out Draft? parentIssue))
            {
                _users[handle] = parentIssue;
                return;
            }

            string? scope = ReferencedAssembly(_reader, parentType);
            if (scope is null || _ignored.Contains(scope))
                return;

            TypeLookup lookup = ResolveTypeReference(module, parentType);
            if (lookup.Status != LookupStatus.Found)
                return;

            string name = _reader.GetString(reference.Name);
            string ownerName = Render(_reader, reference.Parent, null);
            var start = new Link(lookup.Module!, lookup.Handle, null);
            bool friend;

            if (reference.GetKind() == MemberReferenceKind.Method)
            {
                MethodSignature<string> signature = reference.DecodeMethodSignature(_formatter, null);
                string wanted = SignatureFormatter.Format(signature);
                string member = ownerName + "." + SignatureFormatter.Describe(name, signature);

                // Constructors are not inherited; everything else may live in a base class.
                bool inherited = name is not (".ctor" or ".cctor");
                Chain chain = inherited ? BuildChain(start) : new Chain([start], true);
                var others = new List<string>();

                foreach (Link link in chain.Links)
                {
                    foreach (MethodDefinitionHandle methodHandle in link.Definition.GetMethods())
                    {
                        MethodDefinition method = link.Module.Reader.GetMethodDefinition(methodHandle);
                        if (!link.Module.Reader.StringComparer.Equals(method.Name, name))
                            continue;

                        MethodSignature<string> candidate = method.DecodeSignature(_formatter, link.Arguments);
                        if (SignatureFormatter.Format(candidate) == wanted)
                        {
                            friend = link.Module.GrantsInternalsTo(module.Name);
                            if (!IsMemberAccessible((int)(method.Attributes & MethodAttributes.MemberAccessMask), friend))
                            {
                                _users[handle] = Report(CompatibilityIssueKind.MemberNotAccessible, lookup.AssemblyName, member,
                                    $"Calls {member}, which {link.Module.Name} {link.Module.Version} does not make accessible to other assemblies.",
                                    typeof(MethodAccessException));
                            }

                            return;
                        }

                        others.Add((candidate.Header.IsInstance ? string.Empty : "static ") + SignatureFormatter.Describe(name, candidate));
                    }
                }

                if (chain.Complete)
                {
                    string instead = others.Count == 0 ? string.Empty : " It has: " + string.Join("; ", others.Distinct()) + ".";
                    _users[handle] = Report(CompatibilityIssueKind.MissingMethod, lookup.AssemblyName, member,
                        $"Calls {member}, which {lookup.AssemblyName} {lookup.Module!.Version} does not have.{instead}",
                        typeof(MissingMethodException));
                }
            }
            else if (reference.GetKind() == MemberReferenceKind.Field)
            {
                string wanted = reference.DecodeFieldSignature(_formatter, null);
                string member = ownerName + "." + name;
                Chain chain = BuildChain(start);

                foreach (Link link in chain.Links)
                {
                    foreach (FieldDefinitionHandle fieldHandle in link.Definition.GetFields())
                    {
                        FieldDefinition field = link.Module.Reader.GetFieldDefinition(fieldHandle);
                        if (!link.Module.Reader.StringComparer.Equals(field.Name, name))
                            continue;

                        string type = field.DecodeSignature(_formatter, link.Arguments);
                        if (type != wanted)
                        {
                            _users[handle] = Report(CompatibilityIssueKind.MissingField, lookup.AssemblyName, member,
                                $"Uses {member} as {wanted}, but in {link.Module.Name} {link.Module.Version} it is {type}.",
                                typeof(MissingFieldException));
                            return;
                        }

                        friend = link.Module.GrantsInternalsTo(module.Name);
                        if (!IsMemberAccessible((int)(field.Attributes & FieldAttributes.FieldAccessMask), friend))
                        {
                            _users[handle] = Report(CompatibilityIssueKind.MemberNotAccessible, lookup.AssemblyName, member,
                                $"Uses {member}, which {link.Module.Name} {link.Module.Version} does not make accessible to other assemblies.",
                                typeof(FieldAccessException));
                        }

                        return;
                    }
                }

                if (chain.Complete)
                {
                    _users[handle] = Report(CompatibilityIssueKind.MissingField, lookup.AssemblyName, member,
                        $"Uses {member}, which {lookup.AssemblyName} {lookup.Module!.Version} does not have.",
                        typeof(MissingFieldException));
                }
            }
        }

        private void CheckTypeDefinitions()
        {
            foreach (TypeDefinitionHandle handle in _reader.TypeDefinitions)
            {
                try
                {
                    CheckTypeDefinition(handle);
                }
                catch (BadImageFormatException)
                {
                    // As for member references: leave undecodable signatures to the runtime.
                }
            }
        }

        private void CheckTypeDefinition(TypeDefinitionHandle handle)
        {
            TypeDefinition definition = _reader.GetTypeDefinition(handle);
            if ((definition.Attributes & TypeAttributes.Interface) != 0 || definition.BaseType.IsNil)
                return;

            string typeName = MetadataInspector.GetTypeName(_reader, handle)!;
            Chain chain = BuildChain(new Link(module, handle, null));

            if (chain.Links.Count > 1 && !ReferenceEquals(chain.Links[1].Module, module))
            {
                Link baseLink = chain.Links[1];
                TypeAttributes baseAttributes = baseLink.Definition.Attributes;
                string problem = (baseAttributes & TypeAttributes.Interface) != 0 ? "an interface"
                    : (baseAttributes & TypeAttributes.Sealed) != 0 ? "sealed"
                    : string.Empty;

                if (problem.Length > 0)
                {
                    ReportFor(typeName, CompatibilityIssueKind.InvalidInheritance, baseLink.Module.Name, $"{typeName} : {baseLink.Display}",
                        $"{typeName} derives from {baseLink.Display}, which is {problem} in {baseLink.Module.Name} {baseLink.Module.Version}.");
                }
            }

            // Interfaces: every abstract member must have an implementation, explicit or by name and signature.
            var interfaces = new List<Link>();
            foreach (InterfaceImplementationHandle implementation in definition.GetInterfaceImplementations())
            {
                Link? contract = ResolveEntity(module, _reader.GetInterfaceImplementation(implementation).Interface, null);
                if (contract is not null)
                    interfaces.Add(contract);
            }

            foreach (Link contract in interfaces)
            {
                if (ReferenceEquals(contract.Module, module))
                    continue;

                if ((contract.Definition.Attributes & TypeAttributes.Interface) == 0)
                {
                    ReportFor(typeName, CompatibilityIssueKind.InvalidInheritance, contract.Module.Name, $"{typeName} : {contract.Display}",
                        $"{typeName} implements {contract.Display}, which is no longer an interface in {contract.Module.Name} {contract.Module.Version}.");
                    continue;
                }

                if (chain.Complete)
                    CheckImplemented(typeName, contract, chain.Links, interfaces);
            }

            // Abstract base classes: a concrete class must override every abstract method above it.
            if ((definition.Attributes & TypeAttributes.Abstract) != 0 || !chain.Complete)
                return;

            for (int i = 1; i < chain.Links.Count; i++)
            {
                Link declaring = chain.Links[i];
                if (!ReferenceEquals(declaring.Module, module) && (declaring.Definition.Attributes & TypeAttributes.Abstract) != 0)
                    CheckImplemented(typeName, declaring, chain.Links.GetRange(0, i), []);
            }
        }

        private void CheckImplemented(string typeName, Link declaring, List<Link> implementers, List<Link> interfaces)
        {
            MetadataReader reader = declaring.Module.Reader;
            foreach (MethodDefinitionHandle methodHandle in declaring.Definition.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.Abstract) == 0)
                    continue;

                string name = reader.GetString(method.Name);
                MethodSignature<string> closed = method.DecodeSignature(_formatter, declaring.Arguments);
                string closedText = SignatureFormatter.Format(closed);

                bool implemented = implementers.Any(link => HasMethod(link, name, closedText))
                    || implementers.Concat(interfaces).Any(link => HasExplicitImplementation(link, declaring, methodHandle));

                if (!implemented)
                {
                    string member = declaring.Display + "." + SignatureFormatter.Describe(name, closed);
                    ReportFor(typeName, CompatibilityIssueKind.MissingImplementation, declaring.Module.Name, $"{typeName} : {member}",
                        $"{typeName} does not implement {member}, which {declaring.Module.Name} {declaring.Module.Version} declares abstract.");
                }
            }
        }

        private bool HasMethod(Link link, string name, string signature)
        {
            MetadataReader reader = link.Module.Reader;
            foreach (MethodDefinitionHandle methodHandle in link.Definition.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if (reader.StringComparer.Equals(method.Name, name)
                    && SignatureFormatter.Format(method.DecodeSignature(_formatter, link.Arguments)) == signature)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether one of the type's explicit overrides (<c>.override</c> entries) targets the given method.</summary>
        private bool HasExplicitImplementation(Link link, Link declaring, MethodDefinitionHandle target)
        {
            MetadataReader reader = link.Module.Reader;
            foreach (MethodImplementationHandle implementationHandle in link.Definition.GetMethodImplementations())
            {
                EntityHandle declaration = reader.GetMethodImplementation(implementationHandle).MethodDeclaration;
                if (ResolveMethod(link.Module, declaration, link.Arguments) is not var (owner, method))
                    continue;

                // IFoo<int>.M and IFoo<string>.M are the same definition; the instantiation tells them apart.
                if (ReferenceEquals(owner.Module, declaring.Module)
                    && method == target
                    && (declaring.Arguments is null || owner.Arguments is null || owner.Display == declaring.Display))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the method a method definition or reference stands for. Like the runtime, a reference is looked up in
        /// its parent type and then in that type's base classes, so an override may name a class that only inherits
        /// the method.
        /// </summary>
        private (Link Owner, MethodDefinitionHandle Method)? ResolveMethod(MetadataModule from, EntityHandle handle, string[]? arguments)
        {
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var definition = (MethodDefinitionHandle)handle;
                return (new Link(from, from.Reader.GetMethodDefinition(definition).GetDeclaringType(), null), definition);
            }

            if (handle.Kind != HandleKind.MemberReference)
                return null;

            MemberReference reference = from.Reader.GetMemberReference((MemberReferenceHandle)handle);
            if (ResolveEntity(from, reference.Parent, arguments) is not { } parent)
                return null;

            string wanted = SignatureFormatter.Format(reference.DecodeMethodSignature(_formatter, parent.Arguments));
            string name = from.Reader.GetString(reference.Name);
            foreach (Link link in BuildChain(parent).Links)
            {
                MetadataReader reader = link.Module.Reader;
                foreach (MethodDefinitionHandle methodHandle in link.Definition.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    if (reader.StringComparer.Equals(method.Name, name)
                        && SignatureFormatter.Format(method.DecodeSignature(_formatter, link.Arguments)) == wanted)
                    {
                        return (link, methodHandle);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// A type that is not public only fails where the runtime checks access: in IL, as the owner of a called
        /// member, and as a base type or interface. A type that appears only inside signatures (in a required
        /// modifier, for example) is never checked, so it is not reported.
        /// </summary>
        private void CheckHiddenTypeUses()
        {
            // A custom attribute names its constructor too, but nothing checks access until code reads the attribute.
            var checkedUses = new HashSet<EntityHandle>();
            foreach ((_, EntityHandle token) in TypeLevelUses().Concat(ILUses()))
            {
                checkedUses.Add(token);
                if (token.Kind == HandleKind.MemberReference
                    && UnderlyingType(_reader, _reader.GetMemberReference((MemberReferenceHandle)token).Parent) is { } parent)
                {
                    checkedUses.Add(parent);
                }
            }

            foreach ((TypeReferenceHandle handle, TypeLookup lookup) in _hidden)
            {
                if (!checkedUses.Contains(handle))
                    continue;

                string fullName = MetadataInspector.GetTypeName(_reader, handle)!;
                _users[handle] = Report(CompatibilityIssueKind.TypeNotAccessible, lookup.AssemblyName, fullName,
                    $"Uses {fullName}, which {lookup.AssemblyName} {lookup.Module!.Version} does not make public.",
                    typeof(TypeAccessException));
            }

            // A call on a hidden type fails with it.
            foreach (MemberReferenceHandle handle in _reader.MemberReferences)
            {
                if (UnderlyingType(_reader, _reader.GetMemberReference(handle).Parent) is { } parent && _users.TryGetValue(parent, out Draft? draft))
                    _users.TryAdd(handle, draft);
            }
        }

        // ---------------------------------------------------------------- who uses what

        /// <summary>Records, for every problem, the types and methods of the checked assembly that run into it.</summary>
        private void FindUsers()
        {
            foreach ((string user, EntityHandle token) in TypeLevelUses().Concat(ILUses()))
            {
                if (_users.TryGetValue(token, out Draft? issue))
                    issue.UsedBy.Add(user);

                if (_softUsers.TryGetValue(token, out Draft? tooOld))
                    tooOld.UsedBy.Add(user);
            }
        }

        /// <summary>Each type with its base type and interfaces: a type that derives from or implements a broken type fails as a whole.</summary>
        private IEnumerable<(string User, EntityHandle Token)> TypeLevelUses()
        {
            foreach (TypeDefinitionHandle typeHandle in _reader.TypeDefinitions)
            {
                TypeDefinition type = _reader.GetTypeDefinition(typeHandle);
                string typeName = MetadataInspector.GetTypeName(_reader, typeHandle)!;

                if (Normalize(type.BaseType) is { } baseType)
                    yield return (typeName, baseType);

                foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
                {
                    if (Normalize(_reader.GetInterfaceImplementation(implementation).Interface) is { } contract)
                        yield return (typeName, contract);
                }
            }
        }

        /// <summary>Every type, method and field token in every method body, with the method it appears in. Read once.</summary>
        private List<(string User, EntityHandle Token)> ILUses()
        {
            if (_ilUses is not null)
                return _ilUses;

            _ilUses = [];
            foreach (TypeDefinitionHandle typeHandle in _reader.TypeDefinitions)
            {
                TypeDefinition type = _reader.GetTypeDefinition(typeHandle);
                string typeName = MetadataInspector.GetTypeName(_reader, typeHandle)!;
                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = _reader.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                        continue;

                    string user = typeName + "." + _reader.GetString(method.Name);
                    try
                    {
                        BlobReader il = module.Image.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                        foreach (int token in ILTokens.Read(il))
                        {
                            if (Normalize(MetadataTokens.EntityHandle(token)) is { } handle)
                                _ilUses.Add((user, handle));
                        }
                    }
                    catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
                    {
                        // A body that cannot be read, or a token outside the tables, names no users.
                    }
                }
            }

            return _ilUses;
        }

        /// <summary>Looks through method and generic type instantiations to the reference they instantiate.</summary>
        private EntityHandle? Normalize(EntityHandle handle)
        {
            if (handle.IsNil)
                return null;

            if (handle.Kind == HandleKind.MethodSpecification)
                return _reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;

            if (handle.Kind == HandleKind.TypeSpecification)
                return UnderlyingType(_reader, handle) ?? handle;

            return handle;
        }

        // ---------------------------------------------------------------- resolution

        private MetadataModule? ResolveAssembly(string name, string directory)
        {
            string key = directory + "|" + name;
            if (_assemblies.TryGetValue(key, out MetadataModule? cached))
                return cached;

            MetadataModule? found = null;
            foreach (string? path in Candidates(name, directory))
            {
                if (path is not null && (found = MetadataModule.OpenShared(path)) is not null)
                    break;
            }

            return _assemblies[key] = found;
        }

        private IEnumerable<string?> Candidates(string name, string directory)
        {
            string fileName = name + ".dll";
            if (owner._options.PreferHostAssemblies)
                yield return HostAssemblies.FindFile(name);

            yield return owner._scanned.TryGetValue(name, out string? scanned) ? scanned : null;
            yield return Existing(Path.Combine(directory, fileName));
            foreach (string probe in owner._options.ProbingDirectories)
                yield return Existing(Path.Combine(probe, fileName));

            if (!owner._options.PreferHostAssemblies)
                yield return HostAssemblies.FindFile(name);
        }

        private static string? Existing(string path) => File.Exists(path) ? path : null;

        private TypeLookup ResolveTypeReference(MetadataModule from, TypeReferenceHandle handle)
        {
            if (_typeLookups.TryGetValue((from, handle), out TypeLookup cached))
                return cached;

            string fullName = MetadataInspector.GetTypeName(from.Reader, handle)!;
            string? assemblyName = ReferencedAssembly(from.Reader, handle);
            TypeLookup lookup;
            if (assemblyName is null)
            {
                lookup = FindType(from, fullName, 0);
            }
            else
            {
                MetadataModule? target = ResolveAssembly(assemblyName, from.Directory);
                lookup = target is null
                    ? new TypeLookup(LookupStatus.AssemblyMissing, null, default, assemblyName)
                    : FindType(target, fullName, 0);
            }

            return _typeLookups[(from, handle)] = lookup;
        }

        private TypeLookup FindType(MetadataModule target, string fullName, int depth)
        {
            TypeDefinitionHandle handle = target.FindType(fullName);
            if (!handle.IsNil)
                return new TypeLookup(LookupStatus.Found, target, handle, target.Name);

            string? forwardedTo = target.FindForwarder(fullName);
            if (forwardedTo is null || depth >= MaxDepth)
                return new TypeLookup(LookupStatus.TypeMissing, target, default, target.Name);

            MetadataModule? next = ResolveAssembly(forwardedTo, target.Directory);
            return next is null
                ? new TypeLookup(LookupStatus.AssemblyMissing, null, default, forwardedTo)
                : FindType(next, fullName, depth + 1);
        }

        /// <summary>Resolves a type definition, reference or generic instantiation to its definition.</summary>
        private Link? ResolveEntity(MetadataModule from, EntityHandle handle, string[]? arguments)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return new Link(from, (TypeDefinitionHandle)handle, null);

                case HandleKind.TypeReference:
                    TypeLookup lookup = ResolveTypeReference(from, (TypeReferenceHandle)handle);
                    return lookup.Status == LookupStatus.Found ? new Link(lookup.Module!, lookup.Handle, null) : null;

                case HandleKind.TypeSpecification:
                    BlobReader blob = from.Reader.GetBlobReader(from.Reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                        return null;

                    blob.ReadByte(); // ELEMENT_TYPE_CLASS or ELEMENT_TYPE_VALUETYPE
                    EntityHandle generic = blob.ReadTypeHandle();
                    var decoder = new SignatureDecoder<string, string[]?>(_formatter, from.Reader, arguments);
                    var typeArguments = new string[blob.ReadCompressedInteger()];
                    for (int i = 0; i < typeArguments.Length; i++)
                        typeArguments[i] = decoder.DecodeType(ref blob);

                    return ResolveEntity(from, generic, null) is { } definition ? definition with { Arguments = typeArguments } : null;

                default:
                    return null;
            }
        }

        private Chain BuildChain(Link start)
        {
            var links = new List<Link> { start };
            Link current = start;
            for (int depth = 0; depth < MaxDepth; depth++)
            {
                EntityHandle baseType = current.Definition.BaseType;
                if (baseType.IsNil)
                    return new Chain(links, true);

                Link? next = ResolveEntity(current.Module, baseType, current.Arguments);
                if (next is null)
                    return new Chain(links, false);

                links.Add(current = next);
            }

            return new Chain(links, false);
        }

        // ---------------------------------------------------------------- helpers

        private Draft Report(CompatibilityIssueKind kind, string target, string member, string message, Type exception)
        {
            string key = Key(kind, member);
            if (!_issues.TryGetValue(key, out Draft? draft))
                _issues[key] = draft = new Draft(kind, target, member, message, exception);

            return draft;
        }

        private void ReportFor(string typeName, CompatibilityIssueKind kind, string target, string member, string message)
            => Report(kind, target, member, message, typeof(TypeLoadException)).UsedBy.Add(typeName);

        private static string Key(CompatibilityIssueKind kind, string member) => kind + "|" + member;

        private string Render(MetadataReader reader, EntityHandle handle, string[]? arguments)
            => handle.Kind == HandleKind.TypeSpecification
                ? reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_formatter, arguments)
                : MetadataInspector.GetTypeName(reader, handle) ?? string.Empty;

        /// <summary>The simple name of the assembly a type reference points into, or <c>null</c> for this module.</summary>
        private static string? ReferencedAssembly(MetadataReader reader, TypeReferenceHandle handle)
        {
            EntityHandle scope = reader.GetTypeReference(handle).ResolutionScope;
            for (int depth = 0; scope.Kind == HandleKind.TypeReference && depth < MaxDepth; depth++)
                scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;

            return scope.Kind == HandleKind.AssemblyReference
                ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
                : null;
        }

        /// <summary>The type reference behind a reference or generic instantiation, if there is one.</summary>
        private static EntityHandle? UnderlyingType(MetadataReader reader, EntityHandle handle)
        {
            if (handle.Kind == HandleKind.TypeReference)
                return handle;

            if (handle.Kind != HandleKind.TypeSpecification)
                return null;

            BlobReader blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                return null;

            blob.ReadByte();
            EntityHandle generic = blob.ReadTypeHandle();
            return generic.Kind == HandleKind.TypeReference ? generic : null;
        }

        private static bool IsTypeAccessible(MetadataModule target, TypeDefinitionHandle handle, bool friend)
        {
            for (int depth = 0; !handle.IsNil && depth < MaxDepth; depth++)
            {
                TypeDefinition definition = target.Reader.GetTypeDefinition(handle);
                bool accessible = (definition.Attributes & TypeAttributes.VisibilityMask) switch
                {
                    TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem => true,
                    TypeAttributes.NestedPrivate => false,
                    _ => friend // NotPublic, NestedAssembly, NestedFamANDAssem
                };

                if (!accessible)
                    return false;

                handle = definition.GetDeclaringType();
            }

            return true;
        }

        /// <summary>Method and field access levels share their values: 1 private, 2 family-and-assembly, 3 assembly, 4 family, 5 family-or-assembly, 6 public.</summary>
        private static bool IsMemberAccessible(int access, bool friend)
            => access switch
            {
                (int)MethodAttributes.Public or (int)MethodAttributes.Family or (int)MethodAttributes.FamORAssem => true,
                (int)MethodAttributes.Assembly or (int)MethodAttributes.FamANDAssem => friend,
                _ => false
            };
    }
}

/// <summary>Walks IL and yields the metadata tokens of the types, methods and fields it refers to.</summary>
internal static class ILTokens
{
    private static readonly OpCode?[] OneByte = new OpCode?[256];
    private static readonly OpCode?[] TwoByte = new OpCode?[256];

    static ILTokens()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode code)
                continue;

            ushort value = (ushort)code.Value;
            if (code.Size == 1)
                OneByte[value] = code;
            else
                TwoByte[value & 0xFF] = code;
        }
    }

    public static List<int> Read(BlobReader il)
    {
        var tokens = new List<int>();
        while (il.RemainingBytes > 0)
        {
            byte first = il.ReadByte();
            OpCode? code = first == 0xFE ? (il.RemainingBytes > 0 ? TwoByte[il.ReadByte()] : null) : OneByte[first];
            if (code is null)
                break;

            switch (code.Value.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.ReadByte();
                    break;
                case OperandType.InlineVar:
                    il.ReadInt16();
                    break;
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok or OperandType.InlineType:
                    tokens.Add(il.ReadInt32());
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.ReadInt64();
                    break;
                case OperandType.InlineSwitch:
                    int targets = il.ReadInt32();
                    il.Offset += 4 * targets;
                    break;
                default: // InlineI, InlineBrTarget, InlineSig, InlineString, ShortInlineR
                    il.ReadInt32();
                    break;
            }
        }

        return tokens;
    }
}
