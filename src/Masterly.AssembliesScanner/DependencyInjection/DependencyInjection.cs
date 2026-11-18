using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.AssembliesScanner;

/// <summary>What to do when a service type is already registered.</summary>
public enum DuplicateStrategy
{
    /// <summary>Add the registration next to the existing ones. This is how <c>IEnumerable&lt;T&gt;</c> injection gets every implementation.</summary>
    Append,

    /// <summary>Keep the existing registration and skip the new one.</summary>
    Skip,

    /// <summary>Remove the existing registrations of the service type, then add the new one.</summary>
    Replace,

    /// <summary>Throw an <see cref="InvalidOperationException"/>.</summary>
    Throw
}

/// <summary>
/// Marks a class for registration by
/// <see cref="AssemblyScanningServiceCollectionExtensions.AddAttributedServices(IServiceCollection, Assembly[])"/>.
/// Apply it more than once to register the class under several service types or keys.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ServiceAttribute : Attribute
{
    /// <summary>Registers the class as transient.</summary>
    public ServiceAttribute()
        : this(ServiceLifetime.Transient)
    {
    }

    /// <summary>Registers the class with the given lifetime, as its interfaces, or as itself when it has none.</summary>
    public ServiceAttribute(ServiceLifetime lifetime)
    {
        Lifetime = lifetime;
    }

    /// <summary>Registers the class with the given lifetime as one specific service type.</summary>
    public ServiceAttribute(ServiceLifetime lifetime, Type serviceType)
    {
        Lifetime = lifetime;
        ServiceType = serviceType;
    }

    /// <summary>The lifetime of the registration.</summary>
    public ServiceLifetime Lifetime { get; }

    /// <summary>The service type, or <c>null</c> to use the class's interfaces (or the class itself when it has none).</summary>
    public Type? ServiceType { get; }

    /// <summary>The key for a keyed registration, or <c>null</c> for an ordinary one.</summary>
    public object? Key { get; set; }
}

/// <summary>
/// Describes which classes to register and how. Each <c>Add…</c> method starts a rule; the <c>As…</c>,
/// lifetime and duplicate methods that follow configure that rule.
/// </summary>
public sealed class ServiceRegistrationBuilder
{
    private readonly List<Rule> _rules = [];

    internal ServiceRegistrationBuilder()
    {
    }

    private enum RuleKind
    {
        Classes,
        ClosedTypes,
        Attributed
    }

    private enum Selector
    {
        Default,
        Self,
        ImplementedInterfaces,
        MatchingInterface,
        SelfWithInterfaces,
        Explicit
    }

    private Rule Current => _rules.Count > 0
        ? _rules[^1]
        : throw new InvalidOperationException("Start a rule first with AddClasses, AddClassesAssignableTo, AddClosedTypesOf or AddAttributedClasses.");

    /// <summary>Starts a rule over the concrete classes selected by an optional filter.</summary>
    public ServiceRegistrationBuilder AddClasses(Func<TypeQuery, TypeQuery>? filter = null)
    {
        _rules.Add(new Rule(RuleKind.Classes) { Filter = filter });
        return this;
    }

    /// <summary>Starts a rule over the concrete classes that implement or derive from <typeparamref name="T"/>.</summary>
    public ServiceRegistrationBuilder AddClassesAssignableTo<T>() => AddClasses(types => types.AssignableTo<T>());

    /// <summary>Starts a rule over the concrete classes that implement or derive from the given type, which may be an open generic.</summary>
    public ServiceRegistrationBuilder AddClassesAssignableTo(Type type) => AddClasses(types => types.AssignableTo(type));

    /// <summary>
    /// Starts a rule that registers every class under each construction of an open generic it implements,
    /// for example every <c>IHandler&lt;T&gt;</c>. Generic classes such as <c>LoggingHandler&lt;T&gt;</c>
    /// are registered as the open generic.
    /// </summary>
    public ServiceRegistrationBuilder AddClosedTypesOf(Type openGenericType, Func<TypeQuery, TypeQuery>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(openGenericType);
        if (!openGenericType.IsGenericTypeDefinition)
            throw new ArgumentException("An open generic type such as typeof(IHandler<>) is required.", nameof(openGenericType));

        _rules.Add(new Rule(RuleKind.ClosedTypes) { OpenGeneric = openGenericType, Filter = filter });
        return this;
    }

    /// <summary>
    /// Starts a rule over the classes marked with <see cref="ServiceAttribute"/>. The attribute supplies the
    /// lifetime, service type and key; only <see cref="OnDuplicate"/> applies to this rule.
    /// </summary>
    public ServiceRegistrationBuilder AddAttributedClasses(Func<TypeQuery, TypeQuery>? filter = null)
    {
        _rules.Add(new Rule(RuleKind.Attributed) { Filter = filter });
        return this;
    }

    /// <summary>Registers each class as itself.</summary>
    public ServiceRegistrationBuilder AsSelf() => Select(Selector.Self);

    /// <summary>Registers each class as every interface it implements, apart from the runtime's own such as <see cref="IDisposable"/>.</summary>
    public ServiceRegistrationBuilder AsImplementedInterfaces() => Select(Selector.ImplementedInterfaces);

    /// <summary>Registers each class as the interface named after it: <c>OrderService</c> as <c>IOrderService</c>.</summary>
    public ServiceRegistrationBuilder AsMatchingInterface() => Select(Selector.MatchingInterface);

    /// <summary>
    /// Registers each class as itself and as its interfaces, with the interfaces resolving to the same
    /// instance as the class within a lifetime.
    /// </summary>
    public ServiceRegistrationBuilder AsSelfWithInterfaces() => Select(Selector.SelfWithInterfaces);

    /// <summary>Registers each class as <typeparamref name="TService"/>.</summary>
    public ServiceRegistrationBuilder As<TService>() => As(typeof(TService));

    /// <summary>Registers each class as the given service type. Call again to add more service types.</summary>
    public ServiceRegistrationBuilder As(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        Current.Selector = Selector.Explicit;
        Current.ExplicitTypes.Add(serviceType);
        return this;
    }

    /// <summary>Sets the lifetime of the current rule. The default is transient.</summary>
    public ServiceRegistrationBuilder WithLifetime(ServiceLifetime lifetime)
    {
        Current.Lifetime = lifetime;
        return this;
    }

    /// <summary>Registers the classes of the current rule as singletons.</summary>
    public ServiceRegistrationBuilder WithSingletonLifetime() => WithLifetime(ServiceLifetime.Singleton);

    /// <summary>Registers the classes of the current rule as scoped.</summary>
    public ServiceRegistrationBuilder WithScopedLifetime() => WithLifetime(ServiceLifetime.Scoped);

    /// <summary>Registers the classes of the current rule as transient.</summary>
    public ServiceRegistrationBuilder WithTransientLifetime() => WithLifetime(ServiceLifetime.Transient);

    /// <summary>Registers the classes of the current rule as keyed services under the given key.</summary>
    public ServiceRegistrationBuilder WithServiceKey(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Current.Key = key;
        return this;
    }

    /// <summary>Sets what happens when a service type of the current rule is already registered. The default is <see cref="DuplicateStrategy.Append"/>.</summary>
    public ServiceRegistrationBuilder OnDuplicate(DuplicateStrategy strategy)
    {
        Current.Strategy = strategy;
        return this;
    }

    internal void Apply(IServiceCollection services, IReadOnlyList<Assembly> assemblies)
    {
        foreach (Rule rule in _rules)
        {
            TypeQuery query = new TypeQuery(assemblies).Concrete();
            if (rule.Filter is not null)
                query = rule.Filter(query);

            foreach (Type type in query)
            {
                switch (rule.Kind)
                {
                    case RuleKind.Classes:
                        Register(services, type, rule.Selector, rule.ExplicitTypes, rule.Lifetime, rule.Key, rule.Strategy);
                        break;

                    case RuleKind.ClosedTypes:
                        IEnumerable<Type> closedServices = TypeQuery.GetClosedTypesOf(type, rule.OpenGeneric!)
                            .Select(closed => Normalize(type, closed))
                            .OfType<Type>();

                        foreach (Type service in closedServices)
                            Add(services, Describe(service, type, rule.Lifetime, rule.Key), rule.Strategy);
                        break;

                    case RuleKind.Attributed:
                        foreach (ServiceAttribute attribute in type.GetCustomAttributes<ServiceAttribute>(false))
                        {
                            Selector selector = attribute.ServiceType is null ? Selector.Default : Selector.Explicit;
                            Type[] explicitTypes = attribute.ServiceType is null ? [] : [attribute.ServiceType];
                            Register(services, type, selector, explicitTypes, attribute.Lifetime, attribute.Key, rule.Strategy);
                        }
                        break;
                }
            }
        }
    }

    private static void Register(
        IServiceCollection services,
        Type implementation,
        Selector selector,
        IReadOnlyList<Type> explicitTypes,
        ServiceLifetime lifetime,
        object? key,
        DuplicateStrategy strategy)
    {
        switch (selector)
        {
            case Selector.Self:
                Add(services, Describe(implementation, implementation, lifetime, key), strategy);
                break;

            case Selector.ImplementedInterfaces:
                foreach (Type service in GetServiceInterfaces(implementation))
                    Add(services, Describe(service, implementation, lifetime, key), strategy);
                break;

            case Selector.MatchingInterface:
                foreach (Type service in GetServiceInterfaces(implementation).Where(s => s.Name == "I" + implementation.Name))
                    Add(services, Describe(service, implementation, lifetime, key), strategy);
                break;

            case Selector.SelfWithInterfaces:
                Add(services, Describe(implementation, implementation, lifetime, key), strategy);

                // Open generics cannot be registered through a factory, so they get a plain registration.
                IEnumerable<ServiceDescriptor> forwarded = GetServiceInterfaces(implementation).Select(service =>
                    implementation.IsGenericTypeDefinition
                        ? Describe(service, implementation, lifetime, key)
                        : Forward(service, implementation, lifetime, key));

                foreach (ServiceDescriptor descriptor in forwarded)
                    Add(services, descriptor, strategy);
                break;

            case Selector.Explicit:
                foreach (Type requested in explicitTypes)
                {
                    Type service = requested;
                    if (requested.IsGenericTypeDefinition && !implementation.IsGenericTypeDefinition)
                    {
                        foreach (Type closed in TypeQuery.GetClosedTypesOf(implementation, requested))
                            Add(services, Describe(closed, implementation, lifetime, key), strategy);

                        continue;
                    }

                    bool compatible = requested.IsGenericTypeDefinition
                        ? requested == implementation || TypeQuery.GetClosedTypesOf(implementation, requested).Any()
                        : requested.IsAssignableFrom(implementation);

                    if (!compatible)
                        throw new InvalidOperationException($"'{implementation.FullName}' cannot be registered as '{requested.FullName}' because it does not implement or derive from it.");

                    Add(services, Describe(service, implementation, lifetime, key), strategy);
                }
                break;

            default:
                List<Type> interfaces = [.. GetServiceInterfaces(implementation)];
                if (interfaces.Count == 0)
                    interfaces.Add(implementation);

                foreach (Type service in interfaces)
                    Add(services, Describe(service, implementation, lifetime, key), strategy);
                break;
        }
    }

    /// <summary>The interfaces of a class that are worth registering, shaped so the container accepts them.</summary>
    private static IEnumerable<Type> GetServiceInterfaces(Type implementation)
        => implementation.GetInterfaces()
            // IDisposable, IEquatable<T>, IEnumerable<T> and friends are not services.
            .Where(candidate => candidate.Assembly != typeof(object).Assembly)
            .Select(candidate => Normalize(implementation, candidate))
            .OfType<Type>();

    /// <summary>
    /// Turns the interface of an open generic class into the open generic the container needs
    /// (<c>IRepository&lt;T&gt;</c> of <c>Repository&lt;T&gt;</c> becomes <c>IRepository&lt;&gt;</c>),
    /// or returns <c>null</c> when the pair cannot be registered.
    /// </summary>
    private static Type? Normalize(Type implementation, Type service)
    {
        if (!implementation.IsGenericTypeDefinition)
            return service.ContainsGenericParameters ? null : service;

        if (!service.IsGenericType)
            return null;

        Type[] arguments = service.GetGenericArguments();
        Type[] parameters = implementation.GetGenericArguments();
        if (arguments.Length != parameters.Length)
            return null;

        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] != parameters[i])
                return null;
        }

        return service.GetGenericTypeDefinition();
    }

    private static ServiceDescriptor Describe(Type service, Type implementation, ServiceLifetime lifetime, object? key)
        => key is null
            ? new ServiceDescriptor(service, implementation, lifetime)
            : new ServiceDescriptor(service, key, implementation, lifetime);

    private static ServiceDescriptor Forward(Type service, Type implementation, ServiceLifetime lifetime, object? key)
        => key is null
            ? new ServiceDescriptor(service, provider => provider.GetRequiredService(implementation), lifetime)
            : new ServiceDescriptor(service, key, (provider, serviceKey) => provider.GetRequiredKeyedService(implementation, serviceKey), lifetime);

    private static void Add(IServiceCollection services, ServiceDescriptor descriptor, DuplicateStrategy strategy)
    {
        switch (strategy)
        {
            case DuplicateStrategy.Skip:
                if (!services.Any(d => IsSameService(d, descriptor)))
                    services.Add(descriptor);
                break;

            case DuplicateStrategy.Replace:
                for (int i = services.Count - 1; i >= 0; i--)
                {
                    if (IsSameService(services[i], descriptor))
                        services.RemoveAt(i);
                }
                services.Add(descriptor);
                break;

            case DuplicateStrategy.Throw:
                if (services.Any(d => IsSameService(d, descriptor)))
                    throw new InvalidOperationException($"A service of type '{descriptor.ServiceType.FullName}' is already registered.");
                services.Add(descriptor);
                break;

            default:
                services.Add(descriptor);
                break;
        }
    }

    private static bool IsSameService(ServiceDescriptor left, ServiceDescriptor right)
        => left.ServiceType == right.ServiceType && Equals(left.ServiceKey, right.ServiceKey);

    private Rule ConfigureCurrent()
    {
        Rule rule = Current;
        if (rule.Kind != RuleKind.Classes)
            throw new InvalidOperationException("Service types can only be chosen for rules started with AddClasses or AddClassesAssignableTo.");

        return rule;
    }

    private ServiceRegistrationBuilder Select(Selector selector)
    {
        ConfigureCurrent().Selector = selector;
        return this;
    }

    private sealed class Rule(RuleKind kind)
    {
        public RuleKind Kind { get; } = kind;
        public Func<TypeQuery, TypeQuery>? Filter { get; init; }
        public Type? OpenGeneric { get; init; }
        public Selector Selector { get; set; }
        public List<Type> ExplicitTypes { get; } = [];
        public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;
        public DuplicateStrategy Strategy { get; set; } = DuplicateStrategy.Append;
        public object? Key { get; set; }
    }
}

/// <summary>Registers services found by scanning assemblies.</summary>
public static class AssemblyScanningServiceCollectionExtensions
{
    /// <summary>
    /// Scans for assemblies, loads the matching ones and registers their classes according to the rules.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="scan">Chooses the assemblies.</param>
    /// <param name="register">Chooses the classes and how they are registered.</param>
    public static IServiceCollection AddFromAssemblies(this IServiceCollection services, Action<AssemblyScanner> scan, Action<ServiceRegistrationBuilder> register)
    {
        ArgumentNullException.ThrowIfNull(scan);

        AssemblyScanner scanner = Assemblies.Scan();
        scan(scanner);
        return services.AddFromAssemblies(scanner.Scan().Load(), register);
    }

    /// <summary>Registers the classes of the given assemblies according to the rules.</summary>
    public static IServiceCollection AddFromAssemblies(this IServiceCollection services, IEnumerable<Assembly> assemblies, Action<ServiceRegistrationBuilder> register)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(register);

        var builder = new ServiceRegistrationBuilder();
        register(builder);
        builder.Apply(services, assemblies.Distinct().ToList());
        return services;
    }

    /// <summary>Registers every class marked with <see cref="ServiceAttribute"/> in the given assemblies.</summary>
    public static IServiceCollection AddAttributedServices(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Length == 0)
            throw new ArgumentException("Pass at least one assembly to look in.", nameof(assemblies));

        return services.AddFromAssemblies(assemblies, rules => rules.AddAttributedClasses());
    }

    /// <summary>
    /// Scans for assemblies and registers every class marked with <see cref="ServiceAttribute"/>.
    /// Only assemblies whose metadata shows such a class are loaded.
    /// </summary>
    public static IServiceCollection AddAttributedServices(this IServiceCollection services, Action<AssemblyScanner> scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return services.AddFromAssemblies(
            scanner =>
            {
                scan(scanner);
                scanner.ContainingTypesWithAttribute<ServiceAttribute>();
            },
            rules => rules.AddAttributedClasses());
    }
}
