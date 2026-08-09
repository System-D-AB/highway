using Highway.Server.Security;
using StackExchange.Redis;

namespace Highway.Server.Internal;

/// <summary>
/// Builds a client connection from a server's <b>own</b> options, for the cases where the broker
/// process needs to talk to itself.
///
/// <para><b>One place, deliberately.</b> Feature 018's pre-018 startup check built its own
/// connection string, mirrored the password and forgot TLS — and <b>no TLS-enabled server could
/// start</b>. The defect was not the connection; it was that the configuration had to be
/// mirrored by hand, and a second caller would have mirrored it differently. Every in-process
/// caller now goes through here, so a transport setting added later is added once.</para>
///
/// <para><b>What this still cannot do.</b> Under <see cref="TlsOptions.ClientCertificateRequired"/>
/// the server demands a client certificate that a self-connection has no way to present. That is
/// a named limitation, not a bug to be discovered: callers must degrade with a clear message
/// rather than fail the broker.</para>
/// </summary>
internal static class LoopbackConnection
{
    /// <summary>
    /// Configuration for a connection to this process's own broker.
    /// </summary>
    /// <param name="connectTimeoutMs">
    /// Short by default. A caller talking to itself over loopback either connects promptly or is
    /// not going to, and a long timeout turns a misconfiguration into a hang.
    /// </param>
    public static ConfigurationOptions Configure(HighwayServerOptions opts, int connectTimeoutMs = 5_000)
    {
        var config = new ConfigurationOptions
        {
            EndPoints = { { "localhost", opts.Port } },
            AllowAdmin = true,

            // Never abort: a caller that cannot connect must degrade, not throw into whatever
            // was calling it. Every caller here is diagnostic, and C7.1 applies — a mechanism
            // that observes the system must not be able to break it.
            AbortOnConnectFail = false,
            ConnectTimeout = connectTimeoutMs,
        };

        if (opts.Authentication.IsConfigured)
            config.Password = opts.Authentication.Password;

        if (opts.Tls.IsConfigured)
        {
            config.Ssl = true;
            config.SslHost = "localhost";

            // We are the server. Validating our own certificate proves nothing, and a
            // self-signed certificate — the common case for a loopback broker — would fail the
            // default check and take the caller down with it.
            config.CertificateValidation += (_, _, _, _) => true;
        }

        return config;
    }

    /// <summary>
    /// Why a self-connection cannot work, or <see langword="null"/> when it can. Lets a caller
    /// explain itself instead of reporting a bare timeout.
    /// </summary>
    public static string? Unsupported(HighwayServerOptions opts)
        => opts.Tls.ClientCertificateRequired
            ? "this broker requires a client certificate (Tls.ClientCertificateRequired), which a " +
              "connection from the broker to itself cannot present"
            : null;
}
