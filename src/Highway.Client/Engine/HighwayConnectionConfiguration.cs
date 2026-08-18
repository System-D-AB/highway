namespace Highway.Client.Engine;

using System.Text;
using StackExchange.Redis;

/// <summary>
/// Canonical builder for <see cref="ConfigurationOptions"/> used across Highway engine and cache connections.
/// </summary>
public static class HighwayConnectionConfiguration
{
    /// <summary>
    /// Builds the configuration options with the documented precedence:
    /// 1. Parse connection string
    /// 2. Apply explicit credentials/TLS settings from <paramref name="settings"/>
    /// 3. Invoke caller's <c>ConfigureConnection</c> delegate last
    /// </summary>
    public static ConfigurationOptions Build(string configuration, IHighwayConnectionSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new ArgumentException(
                $"'{configuration}' is not a valid Highway server configuration: configuration cannot be null or whitespace.",
                nameof(configuration));
        }

        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(configuration);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                $"'{ConnectionStringRedactor.Redact(configuration)}' is not a valid Highway server configuration: {ex.Message}",
                nameof(configuration), ex);
        }

        options.AbortOnConnectFail = true;

        if (settings is HighwayOptions { NodeName.Length: > 0 } highwayOptions)
            options.ClientName = Sanitise(highwayOptions.NodeName);

        if (settings is not null)
        {
            if (!string.IsNullOrEmpty(settings.Username)) options.User = settings.Username;
            if (!string.IsNullOrEmpty(settings.Password)) options.Password = settings.Password;

            if (settings.Tls is { Enabled: true } tls)
            {
                options.Ssl = true;
                if (!string.IsNullOrEmpty(tls.TargetHost)) options.SslHost = tls.TargetHost;
                if (tls.Protocols is { } protocols) options.SslProtocols = protocols;
            }

            settings.ConfigureConnection?.Invoke(options);
        }

        return options;
    }

    private static string Sanitise(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }
}
