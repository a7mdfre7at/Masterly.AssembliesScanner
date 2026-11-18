using System.Reflection;
using Masterly.AssembliesScanner.UnitTests.Samples;
using Masterly.AssembliesScanner.UnitTests.EdgeSamples;
using Microsoft.Extensions.DependencyInjection;

namespace Masterly.AssembliesScanner.UnitTests;

public class TypeQueryTests
{
    private const string SamplesNamespace = "Masterly.AssembliesScanner.UnitTests.Samples";

    private static TypeQuery Samples() => Assemblies.TypesIn(typeof(IGreeter).Assembly).InNamespace(SamplesNamespace);

    [Fact]
    public void Concrete_KeepsInstantiableClassesOnly()
    {
        Type[] types = [.. Samples().Concrete()];

        new[] { typeof(Circle), typeof(HiddenSquare), typeof(OrderHandler), typeof(Repository<>) }.ShouldBeSubsetOf(types);
        types.Intersect([typeof(ShapeBase), typeof(StaticHelper), typeof(SampleDelegate), typeof(IGreeter)]).ShouldBeEmpty();
    }

    [Fact]
    public void AssignableTo_FindsSubclasses_ButNotTheTypeItself()
    {
        Samples().AssignableTo<ShapeBase>().ShouldBe([typeof(Circle), typeof(HiddenSquare)], ignoreOrder: true);
        Samples().AssignableTo<ShapeBase>().PublicOnly().ShouldBe([typeof(Circle)]);
    }

    [Fact]
    public void AssignableTo_HandlesClosedAndOpenGenerics()
    {
        Samples().AssignableTo<IHandler<Order>>()
            .ShouldBe([typeof(OrderHandler), typeof(MultiHandler)], ignoreOrder: true);

        Samples().AssignableTo(typeof(IHandler<>))
            .ShouldBe([typeof(OrderHandler), typeof(MultiHandler), typeof(LoggingHandler<>)], ignoreOrder: true);

        Samples().AssignableToAny(typeof(IOrderService), typeof(ICache))
            .ShouldBe([typeof(OrderService), typeof(MemoryCache)], ignoreOrder: true);
    }

    [Fact]
    public void ClosedImplementationsOf_PairsEachClassWithEveryConstructionItImplements()
    {
        var pairs = Samples().ClosedImplementationsOf(typeof(IHandler<>))
            .Select(m => (m.ServiceType, m.ImplementationType))
            .ToList();

        pairs.ShouldBe([
            (typeof(IHandler<Order>), typeof(OrderHandler)),
            (typeof(IHandler<Order>), typeof(MultiHandler)),
            (typeof(IHandler<Invoice>), typeof(MultiHandler))
        ], ignoreOrder: true);
    }

    [Fact]
    public void ClosedImplementationsOf_RequiresAnOpenGeneric()
    {
        Action act = () => Samples().ClosedImplementationsOf(typeof(IHandler<Order>));

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void AttributeFilters_SelectByPresenceAndByValue()
    {
        Samples().WithAttribute<MarkerAttribute>().ShouldBe([typeof(MarkedA), typeof(MarkedB)], ignoreOrder: true);
        Samples().WithAttribute<MarkerAttribute>(m => m.Tag == "b").ShouldBe([typeof(MarkedB)]);
        Samples().Concrete().NameStartsWith("Marked").WithoutAttribute<MarkerAttribute>().ShouldBeEmpty();

        Samples().WithAttributeData<MarkerAttribute>()
            .Select(pair => (pair.Type, pair.Attribute.Tag))
            .ShouldBe([(typeof(MarkedA), "a"), (typeof(MarkedB), "b")], ignoreOrder: true);
    }

    [Fact]
    public void MethodsWithAttribute_FindsPublicAndPrivateMethods()
    {
        var methods = Samples().MethodsWithAttribute<MarkerAttribute>();

        methods.Select(m => (m.Method.Name, m.Attribute.Tag))
            .ShouldBe([("Run", "public"), ("Hidden", "private")], ignoreOrder: true);
    }

    [Fact]
    public void NameAndNamespaceFilters_Narrow()
    {
        Samples().NameEndsWith("Handler").Concrete().ShouldBe([typeof(OrderHandler), typeof(MultiHandler)], ignoreOrder: true);
        Samples().NameMatches("i*service").ShouldBe([typeof(IOrderService)]);
        Samples().Interfaces().NameStartsWith("IOrder").ShouldBe([typeof(IOrderService)]);
        Samples().Classes().ShouldContain(typeof(ShapeBase));
        Samples().Classes().ShouldNotContain(typeof(IGreeter));

        Assemblies.TypesIn(typeof(IGreeter).Assembly).InNamespaceOf<IGreeter>().ShouldContain(typeof(Circle));
        TypeQuery outside = Assemblies.TypesIn(typeof(IGreeter).Assembly).NotInNamespace(SamplesNamespace);
        outside.ShouldNotContain(typeof(Circle));
        outside.ShouldContain(typeof(TypeQueryTests));
    }

    [Fact]
    public void Query_LeavesOutCompilerGeneratedTypes()
    {
        Func<int, int> closure = x => x + GetHashCode();
        closure(1);

        Assemblies.TypesIn(typeof(TypeQueryTests).Assembly).ShouldNotContain(t => t.Name.Contains('<'));
    }

    [Fact]
    public void CreateInstances_BuildsEveryMatchingConcreteClass()
    {
        var shapes = Samples().CreateInstances<ShapeBase>();

        shapes.Select(s => s.GetType()).ShouldBe([typeof(Circle), typeof(HiddenSquare)], ignoreOrder: true);
    }

    [Fact]
    public void Types_OnAScan_LoadsTheMatchesAndQueriesThem()
    {
        TypeQuery types = Assemblies.Scan("Masterly.AssembliesScanner.UnitTests").Types();

        types.AssignableTo<IGreeter>().ShouldBeEmpty();
        types.AssignableTo<ShapeBase>().ShouldContain(typeof(Circle));
        types.LoaderErrors.ShouldBeEmpty();
    }

    [Fact]
    public void Filters_ReturnANewQuery_AndLeaveTheOriginalAlone()
    {
        TypeQuery all = Samples();
        int before = all.Count();

        TypeQuery shapes = all.AssignableTo<ShapeBase>();
        TypeQuery handlers = all.AssignableTo(typeof(IHandler<>));

        shapes.ShouldNotBeSameAs(all);
        shapes.Count().ShouldBe(2);
        handlers.Count().ShouldBe(3);
        all.Count().ShouldBe(before);
    }

    [Fact]
    public void Where_AcceptsAnyPredicate()
    {
        Samples().Where(t => t.GetCustomAttribute<ServiceAttribute>() != null)
            .ShouldBe([typeof(MemoryCache), typeof(SmtpMailer), typeof(Standalone)], ignoreOrder: true);
    }

    [Fact]
    public void Query_CanBeEnumeratedWithoutGenerics()
    {
        TypeQuery shapes = Samples().AssignableTo<ShapeBase>();

        Fixture.Enumerate(shapes).ShouldBe(shapes);
    }

    [Fact]
    public void CreateInstances_UsesTheServiceProvider_WhenThereIsOne()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        var shapes = Samples().CreateInstances<ShapeBase>(services);

        shapes.Select(s => s.GetType()).ShouldBe([typeof(Circle), typeof(HiddenSquare)], ignoreOrder: true);
    }

    [Fact]
    public void AssignableTo_AnOpenGenericClass_FindsTheClassesThatCloseIt()
    {
        TypeQuery edge = Assemblies.TypesIn(typeof(OrderStore).Assembly).InNamespaceOf<OrderStore>();

        edge.AssignableTo(typeof(StoreBase<>)).ShouldBe([typeof(OrderStore)]);
        edge.AssignableTo(typeof(IStore<>)).ShouldBe(
            [typeof(StoreBase<>), typeof(OrderStore), typeof(ListStore<>), typeof(NamedStore<>)],
            ignoreOrder: true);
    }

    [Fact]
    public void APredicateThatHitsAMissingDependency_ExcludesTheType()
    {
        TypeQuery broken = Samples().Where(t =>
        {
            if (t == typeof(Circle))
                throw new TypeLoadException("missing dependency");

            return true;
        });

        broken.ShouldNotContain(typeof(Circle));
        broken.ShouldContain(typeof(HiddenSquare));
    }
}
