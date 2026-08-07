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
}
