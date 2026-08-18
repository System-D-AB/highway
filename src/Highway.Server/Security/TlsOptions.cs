using System.Security.Cryptography.X509Certificates;
using Garnet.server.TLS;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Security;

/// <summary>
/// Transport security for the Highway server (feature 012).
///
/// <para><b>Off by default and never mandatory.</b> A certificate is something Highway
/// cannot invent, so a TLS-by-default server would be one that cannot start. The
/// bind-address rule that makes authentication mandatory off loopback deliberately does
/// <b>not</b> extend to TLS — Highway can demand a password; it cannot demand a
/// certificate.</para>
///
/// <para><b>Strongly recommended wherever a password crosses a network.</b> RESP
/// <c>AUTH</c> sends the password as an ordinary bulk string, so without TLS it is on the
/// wire in clear text.</para>
/// </summary>
public sealed class TlsOptions
{
    /// <summary>Path to a PFX file. Mutually exclusive with <see cref="CertSubjectName"/>.</summary>
    public string? CertFileName { get; set; }

    /// <summary>Password for the PFX file, when it has one.</summary>
    public string? CertPassword { get; set; }

    /// <summary>
    /// Subject name of a certificate in the machine store. Mutually exclusive with
    /// <see cref="CertFileName"/> — Garnet accepts exactly one.
    /// </summary>
    public string? CertSubjectName { get; set; }

    /// <summary>
    /// How often the certificate is reloaded, in seconds, so a rotated certificate is
    /// picked up without a restart. Zero disables reloading.
    /// </summary>
    public int CertificateRefreshFrequencySeconds { get; set; }

    /// <summary>When true, the server requires and validates a client certificate (mTLS).</summary>
    public bool ClientCertificateRequired { get; set; }

    /// <summary>Revocation checking mode for client certificates.</summary>
    public X509RevocationMode CertificateRevocationCheckMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>Issuer certificate path, for validating client certificates against a private CA.</summary>
    public string? IssuerCertificatePath { get; set; }

    /// <summary>Whether this certificate is an ephemeral self-signed test certificate.</summary>
    public bool IsEphemeral { get; set; }

    /// <summary>
    /// Escape hatch: a fully-formed Garnet TLS configuration, used verbatim.
    ///
    /// <para>Garnet's own <see cref="GarnetTlsOptions"/> carries this warning in its source,
    /// and Highway would be endorsing it by silence if it did not repeat it:</para>
    ///
    /// <para><i>"NOTE: Do not use in production without verifying the implementation
    /// yourself. This class can be replaced with your own implementation when instantiating
    /// GarnetServerOptions."</i></para>
    ///
    /// <para>Everything above is a convenience wrapper over that sample class. If your
    /// deployment needs verified TLS behaviour, supply your own implementation here.</para>
    /// </summary>
    public IGarnetTlsOptions? Settings { get; set; }

    internal bool IsConfigured =>
        Settings is not null || CertFileName is not null || CertSubjectName is not null;

    /// <summary>
    /// Validates at build time, naming the offending value. Loading the certificate here
    /// turns what would otherwise be an opaque handshake failure minutes later into a
    /// startup error naming the file.
    /// </summary>
    public void Validate()
    {
        if (Settings is not null) return;
        if (!IsConfigured) return;

        if (CertFileName is not null && CertSubjectName is not null)
            throw new InvalidOperationException(
                "TlsOptions specifies both CertFileName and CertSubjectName. Garnet accepts exactly one.");

        if (CertFileName is null) return;

        var path = Path.GetFullPath(CertFileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"TLS certificate file '{path}' does not exist.");

        try
        {
            using var _ = X509CertificateLoader.LoadPkcs12FromFile(path, CertPassword);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"TLS certificate '{path}' could not be loaded: {ex.Message}. " +
                "Check the file is a PFX and that CertPassword is correct.", ex);
        }
    }

    internal IGarnetTlsOptions? CreateTlsOptions(ILogger? logger)
    {
        if (Settings is not null) return Settings;
        if (!IsConfigured) return null;

        // enableCluster: false and clientTargetHost: null — Highway does not use cluster
        // mode, and the client-side options that constructor would otherwise build are for
        // cluster gossip, not for Highway clients.
        return new GarnetTlsOptions(
            certFileName: CertFileName,
            certPassword: CertPassword,
            clientCertificateRequired: ClientCertificateRequired,
            certificateRevocationCheckMode: CertificateRevocationCheckMode,
            issuerCertificatePath: IssuerCertificatePath ?? string.Empty,
            certSubjectName: CertSubjectName,
            certificateRefreshFrequency: CertificateRefreshFrequencySeconds,
            enableCluster: false,
            clientTargetHost: null,
            logger: logger);
    }
}
