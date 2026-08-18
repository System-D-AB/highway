namespace Highway.Server.Host.Configuration;

/// <summary>
/// A configuration error whose message names the offending key (feature 031 R2.4).
/// Mapped to <see cref="ExitCodes.ConfigurationInvalid"/> by the host.
/// </summary>
public sealed class ConfigurationException(string message) : Exception(message);
