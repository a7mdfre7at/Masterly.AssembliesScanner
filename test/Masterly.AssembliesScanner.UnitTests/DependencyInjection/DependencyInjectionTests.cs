using System.Reflection;
using Masterly.AssembliesScanner.UnitTests.Samples;
using Microsoft.Extensions.DependencyInjection;
using Masterly.AssembliesScanner.UnitTests.EdgeSamples;

namespace Masterly.AssembliesScanner.UnitTests;

public class DependencyInjectionTests
{
    private const string SamplesNamespace = "Masterly.AssembliesScanner.UnitTests.Samples";
    private static readonly Assembly[] TestAssembly = [typeof(IGreeter).Assembly];

    private static ServiceCollection Register(Action<ServiceRegistrationBuilder> rules)
    {
        var services = new ServiceCollection();
        services.AddFromAssemblies(TestAssembly, rules);
        return services;
    }

    [Fact]
    public void AsImplementedInterfaces_RegistersEveryInterface_ExceptTheRuntimesOwn()
    {
        ServiceCollection services = Register(r => r.AddClassesAssignableTo<IOrderService>().AsImplementedInterfaces().WithScopedLifetime());

        services.Select(d => d.ServiceType).ShouldBe([typeof(IOrderService), typeof(IAuditable)], ignoreOrder: true);
        services.ShouldAllBe(d => d.Lifetime == ServiceLifetime.Scoped && d.ImplementationType == typeof(OrderService));
    }

    [Fact]
    public void AsMatchingInterface_RegistersOnlyTheInterfaceNamedAfterTheClass()
    {
        ServiceCollection services = Register(r => r.AddClassesAssignableTo<IOrderService>().AsMatchingInterface());

        services.ShouldHaveSingleItem().ServiceType.ShouldBe(typeof(IOrderService));
    }

    [Fact]
    public void AsSelf_AndTheDefault_RegisterAClassWithoutInterfacesAsItself()
    {
        ServiceCollection explicitSelf = Register(r => r.AddClassesAssignableTo<ShapeBase>().AsSelf().WithSingletonLifetime());
        ServiceCollection byDefault = Register(r => r.AddClassesAssignableTo<ShapeBase>());

        explicitSelf.Select(d => d.ServiceType).ShouldBe([typeof(Circle), typeof(HiddenSquare)], ignoreOrder: true);
        explicitSelf.ShouldAllBe(d => d.Lifetime == ServiceLifetime.Singleton);
        byDefault.Select(d => d.ServiceType).ShouldBe([typeof(Circle), typeof(HiddenSquare)], ignoreOrder: true);
        byDefault.ShouldAllBe(d => d.Lifetime == ServiceLifetime.Transient);
    }

    [Fact]
    public void AsSelfWithInterfaces_ResolvesTheSameInstanceThroughEveryServiceType()
    {
        using ServiceProvider provider = Register(r => r.AddClassesAssignableTo<IOrderService>().AsSelfWithInterfaces().WithSingletonLifetime())
            .BuildServiceProvider();

        OrderService self = provider.GetRequiredService<OrderService>();

        provider.GetRequiredService<IOrderService>().ShouldBeSameAs(self);
        provider.GetRequiredService<IAuditable>().ShouldBeSameAs(self);
    }

    [Fact]
    public void As_RegistersUnderAnExplicitServiceType_AndRejectsAnUnrelatedOne()
    {
        ServiceCollection services = Register(r => r.AddClassesAssignableTo<ShapeBase>().As<ShapeBase>());
        Action wrong = () => Register(r => r.AddClassesAssignableTo<ShapeBase>().As<IOrderService>());

        services.Count.ShouldBe(2);
        services.ShouldAllBe(d => d.ServiceType == typeof(ShapeBase));
        Should.Throw<InvalidOperationException>(wrong).Message.ShouldContain("cannot be registered as");
    }

    [Fact]
    public void OpenGenericClasses_AreRegisteredAsOpenGenerics()
    {
        using ServiceProvider provider = Register(r => r.AddClassesAssignableTo(typeof(IRepository<>)).AsImplementedInterfaces())
            .BuildServiceProvider();

        provider.GetRequiredService<IRepository<Order>>().ShouldBeOfType<Repository<Order>>();
        provider.GetRequiredService<IRepository<Invoice>>().ShouldBeOfType<Repository<Invoice>>();
    }

    [Fact]
    public void AddClosedTypesOf_RegistersEveryConstruction_AndOpenGenericImplementations()
    {
        ServiceCollection services = Register(r => r.AddClosedTypesOf(typeof(IHandler<>)).WithScopedLifetime());
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        services.Select(d => (d.ServiceType, d.ImplementationType!)).ShouldBe([
            (typeof(IHandler<Order>), typeof(OrderHandler)),
            (typeof(IHandler<Order>), typeof(MultiHandler)),
            (typeof(IHandler<Invoice>), typeof(MultiHandler)),
            (typeof(IHandler<>), typeof(LoggingHandler<>))
        ], ignoreOrder: true);
        scope.ServiceProvider.GetServices<IHandler<Order>>().Select(h => h.GetType())
            .ShouldBe([typeof(OrderHandler), typeof(MultiHandler), typeof(LoggingHandler<Order>)], ignoreOrder: true);
    }

    [Fact]
    public void AddAttributedServices_UsesTheLifetimeServiceTypeAndKeyFromTheAttribute()
    {
        var services = new ServiceCollection();
        services.AddAttributedServices(TestAssembly);
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        services.Count().ShouldBe(3);
        services.Single(d => d.ServiceType == typeof(ICache)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(Standalone)).Lifetime.ShouldBe(ServiceLifetime.Transient);
        ServiceDescriptor mailer = services.Single(d => d.ServiceType == typeof(IMailer));
        mailer.Lifetime.ShouldBe(ServiceLifetime.Scoped);
        mailer.ServiceKey.ShouldBe("smtp");

        provider.GetRequiredService<ICache>().ShouldBeOfType<MemoryCache>();
        scope.ServiceProvider.GetRequiredKeyedService<IMailer>("smtp").ShouldBeOfType<SmtpMailer>();
        scope.ServiceProvider.GetService<IMailer>().ShouldBeNull();
    }

    [Fact]
    public void AddAttributedServices_WithAScan_LoadsOnlyAssembliesThatHaveAttributedTypes()
    {
        var services = new ServiceCollection();

        services.AddAttributedServices(scan => scan.Include("Masterly.*"));

        services.Select(d => d.ServiceType).ShouldBe([typeof(ICache), typeof(IMailer), typeof(Standalone)], ignoreOrder: true);
    }

    [Fact]
    public void AddFromAssemblies_WithAScan_FindsLoadsAndRegisters()
    {
        var services = new ServiceCollection();

        services.AddFromAssemblies(
            scan => scan.Include("Masterly.*").Exclude("Masterly.AssembliesScanner"),
            rules => rules.AddClasses(types => types.InNamespace(SamplesNamespace).NameEndsWith("Service")).AsMatchingInterface().WithScopedLifetime());

        ServiceDescriptor descriptor = services.ShouldHaveSingleItem();
        descriptor.ServiceType.ShouldBe(typeof(IOrderService));
        descriptor.ImplementationType.ShouldBe(typeof(OrderService));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void WithServiceKey_RegistersKeyedServices()
    {
        using ServiceProvider provider = Register(r => r.AddClassesAssignableTo<ICache>().AsImplementedInterfaces().WithServiceKey("memory"))
            .BuildServiceProvider();

        provider.GetRequiredKeyedService<ICache>("memory").ShouldBeOfType<MemoryCache>();
        provider.GetService<ICache>().ShouldBeNull();
    }

    [Fact]
    public void OnDuplicate_ControlsWhatHappensToExistingRegistrations()
    {
        static ServiceCollection Preloaded()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IOrderService>(new OrderService());
            return services;
        }

        static void Apply(ServiceCollection services, DuplicateStrategy strategy)
            => services.AddFromAssemblies(TestAssembly, r => r.AddClassesAssignableTo<IOrderService>().AsMatchingInterface().OnDuplicate(strategy));

        ServiceCollection appended = Preloaded();
        Apply(appended, DuplicateStrategy.Append);
        appended.Count().ShouldBe(2);

        ServiceCollection skipped = Preloaded();
        Apply(skipped, DuplicateStrategy.Skip);
        skipped.ShouldHaveSingleItem().ImplementationInstance.ShouldNotBeNull();

        ServiceCollection replaced = Preloaded();
        Apply(replaced, DuplicateStrategy.Replace);
        replaced.ShouldHaveSingleItem().ImplementationType.ShouldBe(typeof(OrderService));

        Action throwing = () => Apply(Preloaded(), DuplicateStrategy.Throw);
        Should.Throw<InvalidOperationException>(throwing).Message.ShouldContain("already registered");
    }

    [Fact]
    public void SeveralRules_EachKeepTheirOwnSettings()
    {
        ServiceCollection services = Register(r => r
            .AddClassesAssignableTo<IOrderService>().AsMatchingInterface().WithScopedLifetime()
            .AddClassesAssignableTo<ICache>().AsSelf().WithSingletonLifetime());

        services.Select(d => (d.ServiceType, d.Lifetime)).ShouldBe([
            (typeof(IOrderService), ServiceLifetime.Scoped),
            (typeof(MemoryCache), ServiceLifetime.Singleton)
        ], ignoreOrder: true);
    }

    [Fact]
    public void ConfiguringBeforeStartingARule_IsRejected()
    {
        Action noRule = () => Register(r => r.AsSelf());
        Action wrongRule = () => Register(r => r.AddAttributedClasses().AsSelf());

        Should.Throw<InvalidOperationException>(noRule).Message.ShouldStartWith("Start a rule first");
        Should.Throw<InvalidOperationException>(wrongRule);
    }

    [Fact]
    public void AddAttributedServices_RequiresAnAssembly()
    {
        Action act = () => new ServiceCollection().AddAttributedServices([]);

        Should.Throw<ArgumentException>(act);
    }

    private static Func<TypeQuery, TypeQuery> Edge(string namePrefix = "")
        => types => types.InNamespaceOf<OrderStore>().NameStartsWith(namePrefix);

    private static IEnumerable<(Type Service, Type Implementation)> Pairs(IServiceCollection services)
        => services.Select(d => (d.ServiceType, d.ImplementationType!));

    [Fact]
    public void AddClosedTypesOf_RegistersOnlyWhatTheContainerCanBuild()
    {
        ServiceCollection stores = Register(r => r.AddClosedTypesOf(typeof(IStore<>), Edge()));
        ServiceCollection pairs = Register(r => r.AddClosedTypesOf(typeof(IPair<,>), Edge()));

        // ListStore<T> implements IStore<List<T>> and PairStore<T> implements IPair<T, T>:
        // neither has an open generic registration that would resolve.
        Pairs(stores).ShouldBe([
            (typeof(IStore<>), typeof(StoreBase<>)),
            (typeof(IStore<Order>), typeof(OrderStore)),
            (typeof(IStore<>), typeof(NamedStore<>))
        ], ignoreOrder: true);
        pairs.ShouldBeEmpty();
    }

    [Fact]
    public void AddClosedTypesOf_AnOpenGenericClass_RegistersItsSubclasses()
    {
        ServiceCollection services = Register(r => r.AddClosedTypesOf(typeof(StoreBase<>), Edge()).WithScopedLifetime());

        ServiceDescriptor descriptor = services.ShouldHaveSingleItem();
        descriptor.ServiceType.ShouldBe(typeof(StoreBase<Order>));
        descriptor.ImplementationType.ShouldBe(typeof(OrderStore));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddClosedTypesOf_RequiresAnOpenGenericType()
    {
        Action act = () => Register(r => r.AddClosedTypesOf(typeof(IStore<Order>)));

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void AsImplementedInterfaces_OnAnOpenGenericClass_KeepsOnlyItsOpenGenericInterface()
    {
        ServiceCollection services = Register(r => r.AddClasses(Edge("NamedStore")).AsImplementedInterfaces());

        Pairs(services).ShouldBe([(typeof(IStore<>), typeof(NamedStore<>))]);
    }

    [Fact]
    public void AsSelfWithInterfaces_OnAnOpenGenericClass_RegistersBothAsOpenGenerics()
    {
        using ServiceProvider provider = Register(r => r.AddClasses(Edge("StoreBase")).AsSelfWithInterfaces()).BuildServiceProvider();

        provider.GetRequiredService<StoreBase<Invoice>>().ShouldNotBeNull();
        provider.GetRequiredService<IStore<Invoice>>().ShouldBeOfType<StoreBase<Invoice>>();
    }

    [Fact]
    public void AsSelfWithInterfaces_WithAKey_ForwardsTheKeyedInterfaceToTheKeyedClass()
    {
        using ServiceProvider provider = Register(r => r
                .AddClasses(Edge("KeyedThing")).AsSelfWithInterfaces().WithServiceKey("main").WithSingletonLifetime())
            .BuildServiceProvider();

        KeyedThing self = provider.GetRequiredKeyedService<KeyedThing>("main");

        provider.GetRequiredKeyedService<INamed>("main").ShouldBeSameAs(self);
        provider.GetService<INamed>().ShouldBeNull();
    }

    [Fact]
    public void As_AnOpenGeneric_RegistersWhatTheClassMakesOfIt()
    {
        ServiceCollection closed = Register(r => r.AddClasses(Edge("OrderStore")).As(typeof(IStore<>)));
        ServiceCollection open = Register(r => r.AddClasses(Edge("StoreBase")).As(typeof(IStore<>)));
        ServiceCollection self = Register(r => r.AddClasses(Edge("StoreBase")).As(typeof(StoreBase<>)));
        ServiceCollection unrelated = Register(r => r.AddClasses(Edge("KeyedThing")).As(typeof(IStore<>)));
        Action incompatible = () => Register(r => r.AddClasses(Edge("PairStore")).As(typeof(IStore<>)));

        Pairs(closed).ShouldBe([(typeof(IStore<Order>), typeof(OrderStore))]);
        Pairs(open).ShouldBe([(typeof(IStore<>), typeof(StoreBase<>))]);
        Pairs(self).ShouldBe([(typeof(StoreBase<>), typeof(StoreBase<>))]);
        unrelated.ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(incompatible).Message.ShouldContain("cannot be registered as");
    }

    [Fact]
    public void WithTransientLifetime_OverridesAnEarlierLifetime()
    {
        ServiceCollection services = Register(r => r
            .AddClasses(Edge("KeyedThing")).AsSelf().WithSingletonLifetime().WithTransientLifetime());

        services.ShouldHaveSingleItem().Lifetime.ShouldBe(ServiceLifetime.Transient);
    }

    [Theory]
    [InlineData(DuplicateStrategy.Skip)]
    [InlineData(DuplicateStrategy.Throw)]
    [InlineData(DuplicateStrategy.Replace)]
    [InlineData(DuplicateStrategy.Append)]
    public void OnDuplicate_AddsAServiceThatIsNotThereYet(DuplicateStrategy strategy)
    {
        ServiceCollection services = Register(r => r.AddClasses(Edge("KeyedThing")).As<INamed>().OnDuplicate(strategy));

        Pairs(services).ShouldBe([(typeof(INamed), typeof(KeyedThing))]);
    }

    [Fact]
    public void OnDuplicate_TreatsTheSameServiceUnderAnotherKeyAsADifferentService()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<INamed>("other", new KeyedThing());
        Action<ServiceRegistrationBuilder> rule = r => r
            .AddClasses(Edge("KeyedThing")).As<INamed>().WithServiceKey("main").OnDuplicate(DuplicateStrategy.Skip);

        services.AddFromAssemblies(TestAssembly, rule);
        services.AddFromAssemblies(TestAssembly, rule);

        services.Select(d => d.ServiceKey).ShouldBe(["other", "main"]);
    }
}
