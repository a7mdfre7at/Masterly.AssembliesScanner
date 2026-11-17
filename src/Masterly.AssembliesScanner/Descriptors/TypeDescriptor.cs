namespace Masterly.AssembliesScanner;

/// <summary>
/// A type as declared in an assembly's metadata, read without loading the assembly.
/// </summary>
/// <remarks>
/// Names use the reflection format: <c>Namespace.Outer+Nested</c>, with a <c>`N</c> arity suffix on
/// generic types, so they compare directly against <see cref="Type.FullName"/> of a type definition.
/// </remarks>
public sealed class TypeDescriptor
{
    internal TypeDescriptor(
        string fullName,
        string @namespace,
        string name,
        bool isPublic,
        bool isNested,
        bool isInterface,
        bool isAbstract,
        bool isSealed,
        int genericParameterCount,
        string? baseType,
        IReadOnlyList<string> interfaces,
        IReadOnlyList<string> attributes)
    {
        FullName = fullName;
        Namespace = @namespace;
        Name = name;
        IsPublic = isPublic;
        IsNested = isNested;
        IsInterface = isInterface;
        IsAbstract = isAbstract;
        IsSealed = isSealed;
        GenericParameterCount = genericParameterCount;
        BaseType = baseType;
        Interfaces = interfaces;
        Attributes = attributes;
    }

    /// <summary>The namespace-qualified name.</summary>
    public string FullName { get; }

    /// <summary>The namespace, or an empty string. A nested type reports the namespace of its outermost type.</summary>
    public string Namespace { get; }

    /// <summary>The simple name.</summary>
    public string Name { get; }

    /// <summary>Whether the type is declared <c>public</c> (for a nested type, nested public).</summary>
    public bool IsPublic { get; }

    /// <summary>Whether the type is declared inside another type.</summary>
    public bool IsNested { get; }

    /// <summary>Whether the type is an interface.</summary>
    public bool IsInterface { get; }

    /// <summary>Whether the type is abstract. Static classes are abstract and sealed.</summary>
    public bool IsAbstract { get; }

    /// <summary>Whether the type is sealed.</summary>
    public bool IsSealed { get; }

    /// <summary>The number of generic parameters the type declares.</summary>
    public int GenericParameterCount { get; }

    /// <summary>The full name of the base type, or <c>null</c> for interfaces and <see cref="object"/>.</summary>
    public string? BaseType { get; }

    /// <summary>The full names of the interfaces the type declares. Generic interfaces appear as their definition, for example <c>Contoso.IHandler`1</c>.</summary>
    public IReadOnlyList<string> Interfaces { get; }

    /// <summary>The full names of the attribute types applied to the type.</summary>
    public IReadOnlyList<string> Attributes { get; }

    /// <summary>Whether the type is an enum.</summary>
    public bool IsEnum => BaseType == "System.Enum";

    /// <summary>Whether the type is a struct or an enum.</summary>
    public bool IsValueType => BaseType == "System.ValueType" || IsEnum;

    /// <summary>Whether the type is a class, including abstract and static classes and delegates.</summary>
    public bool IsClass => !IsInterface && !IsValueType;

    /// <summary>Whether the type is a class that can be instantiated.</summary>
    public bool IsConcreteClass => IsClass && !IsAbstract;

    /// <summary>Whether the type is a static class.</summary>
    public bool IsStatic => IsAbstract && IsSealed;

    /// <summary>Whether an attribute with the given full type name is applied to the type.</summary>
    public bool HasAttribute(string attributeFullName) => Attributes.Contains(attributeFullName, StringComparer.Ordinal);

    /// <summary>Whether an attribute of type <typeparamref name="TAttribute"/> is applied to the type.</summary>
    public bool HasAttribute<TAttribute>() where TAttribute : Attribute => HasAttribute(typeof(TAttribute).FullName!);

    /// <summary>Whether the type itself declares the interface or base type with the given full name.</summary>
    public bool Declares(string typeFullName)
        => BaseType == typeFullName || Interfaces.Contains(typeFullName, StringComparer.Ordinal);

    /// <inheritdoc />
    public override string ToString() => FullName;
}
