using System;
using Microsoft.Extensions.DependencyInjection;

// Types the type-discovery, registration and plugin tests look for.
namespace Masterly.AssembliesScanner.UnitTests.Samples;

/// <summary>The plugin contract implemented by assemblies the tests compile at run time.</summary>
public interface IGreeter
{
    string Greet(string name);
}

public sealed class GreetingOptions
{
    public string Prefix { get; set; } = "Hi";
}

public interface IHandler<T>
{
}

public sealed class Order
{
}

public sealed class Invoice
{
}

public class OrderHandler : IHandler<Order>
{
}

public class MultiHandler : IHandler<Order>, IHandler<Invoice>
{
}

public class LoggingHandler<T> : IHandler<T>
{
}

public interface IRepository<T>
{
}

public class Repository<T> : IRepository<T>
{
}

public interface IOrderService
{
}

public interface IAuditable
{
}

public class OrderService : IOrderService, IAuditable, IDisposable
{
    public void Dispose()
    {
    }
}

public abstract class ShapeBase
{
}

public class Circle : ShapeBase
{
}

internal class HiddenSquare : ShapeBase
{
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class MarkerAttribute(string tag) : Attribute
{
    public string Tag { get; } = tag;
}

[Marker("a")]
public class MarkedA
{
}

[Marker("b")]
public class MarkedB
{
    [Marker("public")]
    public void Run()
    {
    }

    [Marker("private")]
    private static void Hidden()
    {
    }
}

public interface ICache
{
}

[Service(ServiceLifetime.Singleton)]
public class MemoryCache : ICache
{
}

public interface IMailer
{
}

[Service(ServiceLifetime.Scoped, typeof(IMailer), Key = "smtp")]
public class SmtpMailer : IMailer
{
}

[Service]
public class Standalone
{
}

public static class StaticHelper
{
}

public delegate void SampleDelegate();
