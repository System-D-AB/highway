namespace Highway.Server.Dashboard;

/// <summary>
/// Extension methods to enable the Highway dashboard on a server builder.
/// </summary>
public static class HighwayDashboardBuilderExtensions
{
    /// <summary>
    /// Enables the dashboard on the specified port with default settings.
    /// </summary>
    public static HighwayServerBuilder WithDashboard(this HighwayServerBuilder builder, int port = 7500)
    {
        return builder.WithDashboard(d => d.Port = port);
    }

    /// <summary>
    /// Enables the dashboard with full configuration.
    /// </summary>
    public static HighwayServerBuilder WithDashboard(this HighwayServerBuilder builder, Action<DashboardOptions> configure)
    {
        var options = new DashboardOptions();
        configure(options);
        options.Enabled = true; // calling WithDashboard IS the opt-in
        options.Validate();

        builder.AddComponent(ctx => new DashboardComponent(options, ctx));
        return builder;
    }

    /// <summary>
    /// Serves the machine-facing health endpoints (<c>/health</c>, <c>/ready</c>, <c>/replication</c>,
    /// feature 052) <b>without</b> the dashboard UI — for a headless broker that still needs an
    /// orchestrator/load-balancer to probe it. <see cref="WithDashboard(HighwayServerBuilder, int)"/>
    /// already includes these endpoints; use this instead when the UI is not wanted. Do not call both.
    /// </summary>
    public static HighwayServerBuilder WithHealthEndpoints(this HighwayServerBuilder builder, int port = 7500)
        => builder.WithHealthEndpoints(d => d.Port = port);

    /// <summary>The configurable form of <see cref="WithHealthEndpoints(HighwayServerBuilder, int)"/>.</summary>
    public static HighwayServerBuilder WithHealthEndpoints(this HighwayServerBuilder builder, Action<DashboardOptions> configure)
    {
        var options = new DashboardOptions();
        configure(options);
        options.Enabled = false;          // health only — no UI
        options.HealthEndpoints = true;   // the opt-in
        options.Validate();

        builder.AddComponent(ctx => new DashboardComponent(options, ctx));
        return builder;
    }
}
