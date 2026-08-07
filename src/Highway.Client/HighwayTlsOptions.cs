using System.Security.Authentication;

namespace Highway.Client;

/// <summary>
/// Client-side transport security (feature 012).
///
/// <para>TLS is opt-in and never required. A certificate is something Highway cannot
/// invent, so a TLS-by-default client would be one that cannot connect. What Highway
/// <i>can</i> demand — a password — it demands as soon as the server is reachable from off
/// the machine; see <c>HighwayServerBuilder.WithPassword</c>.</para>
///
/// <para>Client certificates and private certificate authorities are reached through
/// <see cref="HighwayOptions.ConfigureConnection"/> rather than modelled here, because
/// StackExchange.Redis already exposes them and wrapping every knob would add surface
/// without adding capability.</para>
/// </summary>
public sealed class HighwayTlsOptions
{
    /// <summary>Whether to negotiate TLS. Default: <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Host name the server's certificate must match. When null, the host from the
    /// connection string is used.
    ///
    /// <para>Set this when connecting by IP address to a certificate issued for a name —
    /// otherwise validation fails for a reason that reads like a network fault.</para>
    /// </summary>
    public string? TargetHost { get; set; }

    /// <summary>Permitted protocol versions. When null, the platform default applies.</summary>
    public SslProtocols? Protocols { get; set; }
}
