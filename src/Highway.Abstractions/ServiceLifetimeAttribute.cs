namespace Highway.Abstractions;

/// <summary>
/// Highway service lifetime options (mirrors Microsoft.Extensions.DependencyInjection.ServiceLifetime
/// without taking a dependency on it).
/// </summary>
public enum HighwayServiceLifetime
{
    /// <summary>
    /// A new instance is created per request scope (default).
    /// </summary>
    Scoped = 0,

    /// <summary>
    /// A single shared instance for the lifetime of the application.
    /// </summary>
    Singleton = 1,

    /// <summary>
    /// A new instance is created each time it is requested.
    /// </summary>
    Transient = 2
}

/// <summary>
/// Overrides the default service lifetime (Scoped) for a service or subscriber implementation.
/// Apply to the service implementation class (the AsyncService&lt;T,TRes&gt; subclass).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ServiceLifetimeAttribute(HighwayServiceLifetime lifetime) : Attribute
{
    public HighwayServiceLifetime Lifetime { get; } = lifetime;
}
