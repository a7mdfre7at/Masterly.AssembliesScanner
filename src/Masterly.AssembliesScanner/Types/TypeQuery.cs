using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.AssembliesScanner;

/// <summary>A closed generic service and the class that implements it, for example <c>IHandler&lt;Order&gt;</c> and <c>OrderHandler</c>.</summary>
public sealed class ClosedGenericMatch
{
    internal ClosedGenericMatch(Type serviceType, Type implementationType)
    {
        ServiceType = serviceType;
        ImplementationType = implementationType;
    }

    /// <summary>The constructed generic interface or base class.</summary>
    public Type ServiceType { get; }

    /// <summary>The concrete class.</summary>
    public Type ImplementationType { get; }
}

/// <summary>
/// A fluent filter over the types of a set of loaded assemblies.
/// </summary>
/// <remarks>
/// <para>
/// The query never throws because one assembly has a type that cannot be loaded: the types that do load
/// are used, and the problems are available in <see cref="LoaderErrors"/>.
/// </para>
/// <para>
/// Public and non-public types are both included; call <see cref="PublicOnly"/> to restrict.
/// Compiler-generated types are always left out.
/// </para>
/// <para>
/// A query is immutable: every filter method returns a new query and leaves the original as it was, so
/// one query can be the starting point for several others. It is evaluated each time it is enumerated.
/// </para>
/// </remarks>
public sealed class TypeQuery : IEnumerable<Type>
{
    private readonly Lazy<(Type[] Types, List<Exception> Errors)> _source;
    private readonly Func<Type, bool>[] _predicates;

    /// <summary>Creates a query over the types of the given assemblies.</summary>
    public TypeQuery(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        Assembly[] snapshot = [.. assemblies.Distinct()];
        _source = new Lazy<(Type[], List<Exception>)>(() =>
        {
            var errors = new List<Exception>();
            Type[] types = [.. snapshot
                .SelectMany(a => GetLoadableTypes(a, errors))
                .Where(t => !t.Name.StartsWith('<') && !(t.DeclaringType?.Name.StartsWith('<') ?? false))];
            return (types, errors);
        });
        _predicates = [];
    }

    private TypeQuery(Lazy<(Type[] Types, List<Exception> Errors)> source, Func<Type, bool>[] predicates)
    {
        _source = source;
        _predicates = predicates;
    }

    /// <summary>The exceptions the runtime raised for types that could not be loaded.</summary>
    public IReadOnlyList<Exception> LoaderErrors => _source.Value.Errors;

    /// <summary>Keeps types for which the predicate returns <c>true</c>.</summary>
    public TypeQuery Where(Func<Type, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var predicates = new Func<Type, bool>[_predicates.Length + 1];
        _predicates.CopyTo(predicates, 0);
        predicates[_predicates.Length] = predicate;
        return new TypeQuery(_source, predicates);
    }

    /// <summary>
    /// Keeps types that implement or derive from <paramref name="type"/>, not counting the type itself.
    /// An open generic such as <c>typeof(IHandler&lt;&gt;)</c> matches every type that implements any
    /// construction of it.
    /// </summary>
    public TypeQuery AssignableTo(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Where(t => IsAssignableTo(t, type));
    }

    /// <summary>Keeps types that implement or derive from <typeparamref name="T"/>.</summary>
    public TypeQuery AssignableTo<T>() => AssignableTo(typeof(T));

    /// <summary>Keeps types that implement or derive from at least one of the given types.</summary>
    public TypeQuery AssignableToAny(params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        Type[] targets = [.. types];
        return Where(t => targets.Any(target => IsAssignableTo(t, target)));
    }

    /// <summary>Keeps types marked with the attribute.</summary>
    public TypeQuery WithAttribute(Type attributeType, bool inherit = true)
    {
        ArgumentNullException.ThrowIfNull(attributeType);
        return Where(t => t.IsDefined(attributeType, inherit));
    }

    /// <summary>Keeps types marked with <typeparamref name="TAttribute"/>.</summary>
    public TypeQuery WithAttribute<TAttribute>(bool inherit = true) where TAttribute : Attribute
        => WithAttribute(typeof(TAttribute), inherit);

    /// <summary>Keeps types marked with a <typeparamref name="TAttribute"/> that satisfies the predicate.</summary>
    public TypeQuery WithAttribute<TAttribute>(Func<TAttribute, bool> predicate) where TAttribute : Attribute
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Where(t => t.GetCustomAttributes<TAttribute>(true).Any(predicate));
    }

    /// <summary>Removes types marked with <typeparamref name="TAttribute"/>.</summary>
    public TypeQuery WithoutAttribute<TAttribute>(bool inherit = true) where TAttribute : Attribute
        => Where(t => !t.IsDefined(typeof(TAttribute), inherit));

    /// <summary>Keeps types in the namespace or in one of its child namespaces.</summary>
    public TypeQuery InNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);
        return Where(t => IsInNamespace(t, @namespace));
    }

    /// <summary>Keeps types in the namespace of <typeparamref name="T"/> or in one of its child namespaces.</summary>
    public TypeQuery InNamespaceOf<T>() => InNamespace(typeof(T).Namespace ?? string.Empty);

    /// <summary>Removes types in the namespace and in its child namespaces.</summary>
    public TypeQuery NotInNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);
        return Where(t => !IsInNamespace(t, @namespace));
    }

    /// <summary>Keeps types whose simple name starts with the prefix.</summary>
    public TypeQuery NameStartsWith(string prefix) => Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Keeps types whose simple name ends with the suffix, for example <c>Repository</c>.</summary>
    public TypeQuery NameEndsWith(string suffix) => Where(t => t.Name.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>Keeps types whose simple name matches a glob pattern, ignoring case.</summary>
    public TypeQuery NameMatches(string pattern)
    {
        NamePattern glob = NamePattern.Glob(pattern);
        return Where(t => glob.IsMatch(t.Name));
    }

    /// <summary>
    /// Keeps classes that can be instantiated: not abstract, not static, not delegates.
    /// Open generic class definitions such as <c>Repository&lt;T&gt;</c> are kept.
    /// </summary>
    public TypeQuery Concrete() => Where(IsConcrete);

    /// <summary>Keeps classes, including abstract ones.</summary>
    public TypeQuery Classes() => Where(t => t.IsClass && !typeof(Delegate).IsAssignableFrom(t));

    /// <summary>Keeps interfaces.</summary>
    public TypeQuery Interfaces() => Where(t => t.IsInterface);

    /// <summary>Keeps public types, including public types nested in public types.</summary>
    public TypeQuery PublicOnly() => Where(t => t.IsVisible);

    /// <summary>
    /// Pairs every concrete class with the constructions of an open generic it implements. A class that
    /// implements <c>IHandler&lt;A&gt;</c> and <c>IHandler&lt;B&gt;</c> yields two pairs.
    /// </summary>
    /// <param name="openGenericType">An open generic interface or class, for example <c>typeof(IHandler&lt;&gt;)</c>.</param>
    public IReadOnlyList<ClosedGenericMatch> ClosedImplementationsOf(Type openGenericType)
    {
        ArgumentNullException.ThrowIfNull(openGenericType);
        if (!openGenericType.IsGenericTypeDefinition)
            throw new ArgumentException("An open generic type such as typeof(IHandler<>) is required.", nameof(openGenericType));

        return this
            .Where(type => IsConcrete(type) && !type.IsGenericTypeDefinition)
            .SelectMany(type => GetClosedTypesOf(type, openGenericType).Select(closed => new ClosedGenericMatch(closed, type)))
            .ToList();
    }

    /// <summary>Pairs every matching type with its <typeparamref name="TAttribute"/> instances.</summary>
    public IReadOnlyList<(Type Type, TAttribute Attribute)> WithAttributeData<TAttribute>(bool inherit = false) where TAttribute : Attribute
        => this.SelectMany(t => t.GetCustomAttributes<TAttribute>(inherit).Select(a => (t, a))).ToList();

    /// <summary>
    /// Finds the methods of the matching types that are marked with <typeparamref name="TAttribute"/>:
    /// public and non-public, instance and static, declared on the type itself.
    /// </summary>
    public IReadOnlyList<(MethodInfo Method, TAttribute Attribute)> MethodsWithAttribute<TAttribute>() where TAttribute : Attribute
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return this
            .SelectMany(t => t.GetMethods(flags))
            .SelectMany(m => m.GetCustomAttributes<TAttribute>(false).Select(a => (m, a)))
            .ToList();
    }

    /// <summary>
    /// Creates one instance of every matching concrete class that is assignable to <typeparamref name="T"/>.
    /// </summary>
    /// <param name="services">Satisfies constructor parameters. Without it a parameterless constructor is required.</param>
    public IReadOnlyList<T> CreateInstances<T>(IServiceProvider? services = null) where T : class
    {
        return this
            .Where(type => IsConcrete(type) && !type.ContainsGenericParameters && typeof(T).IsAssignableFrom(type))
            .Select(type => (T)(services is not null
                ? ActivatorUtilities.CreateInstance(services, type)
                : Activator.CreateInstance(type, nonPublic: true)!))
            .ToList();
    }

    /// <inheritdoc />
    public IEnumerator<Type> GetEnumerator()
    {
        foreach (Type type in _source.Value.Types.Where(Matches))
            yield return type;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static Type[] GetLoadableTypes(Assembly assembly, List<Exception>? errors)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            errors?.AddRange(ex.LoaderExceptions.Where(e => e is not null)!);
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    internal static IEnumerable<Type> GetClosedTypesOf(Type type, Type openGenericType)
    {
        if (openGenericType.IsInterface)
        {
            foreach (Type candidate in type.GetInterfaces().Where(c => c.IsGenericType && c.GetGenericTypeDefinition() == openGenericType))
                yield return candidate;
        }
        else
        {
            for (Type? current = type.BaseType; current is not null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == openGenericType)
                    yield return current;
            }
        }
    }

    internal static bool IsConcrete(Type type)
        => type.IsClass
        && !type.IsAbstract
        && !typeof(Delegate).IsAssignableFrom(type)
        && (!type.ContainsGenericParameters || type.IsGenericTypeDefinition);

    private static bool IsAssignableTo(Type type, Type target)
    {
        if (type == target)
            return false;

        return target.IsGenericTypeDefinition
            ? GetClosedTypesOf(type, target).Any()
            : target.IsAssignableFrom(type);
    }

    private static bool IsInNamespace(Type type, string @namespace)
        => type.Namespace is string actual
        && (actual == @namespace || actual.StartsWith(@namespace + ".", StringComparison.Ordinal));

    private bool Matches(Type type)
    {
        try
        {
            foreach (Func<Type, bool> predicate in _predicates)
            {
                if (!predicate(type))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is TypeLoadException or IOException or BadImageFormatException)
        {
            // The type refers to something that is not there (a missing dependency); it cannot match.
            return false;
        }
    }
}
