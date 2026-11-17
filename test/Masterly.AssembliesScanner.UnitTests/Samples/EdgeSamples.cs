using Masterly.AssembliesScanner.UnitTests.Samples;

// Generic shapes the registration tests need. They live outside the Samples namespace and implement
// none of its interfaces, so the tests that count the samples are not affected.
namespace Masterly.AssembliesScanner.UnitTests.EdgeSamples;

public interface IStore<T>
{
}

public interface IPair<TKey, TValue>
{
}

public interface INamed
{
}

/// <summary>An open generic class that maps one to one onto its interface.</summary>
public class StoreBase<T> : IStore<T>
{
}

/// <summary>Closes a generic base class, and through it a generic interface.</summary>
public class OrderStore : StoreBase<Order>
{
}

/// <summary>Its interface argument is not its own type parameter, so it has no open generic registration.</summary>
public class ListStore<T> : IStore<List<T>>
{
}

/// <summary>Its interface has more type arguments than the class has parameters.</summary>
public class PairStore<T> : IPair<T, T>
{
}

/// <summary>An open generic class with one generic and one plain interface.</summary>
public class NamedStore<T> : IStore<T>, INamed
{
}

public class KeyedThing : INamed
{
}
