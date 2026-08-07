using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Highway.Client;
using Highway.Client.Engine;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 012 — transport security, end to end.
///
/// <para>The certificate is generated <b>in the test</b>. No fixture files, no certificate
/// authority, no manual setup — the same "integration tests need no external
/// infrastructure" rule the embedded Garnet server exists to keep. A mocked handshake would
/// prove nothing: TLS either negotiates against a real socket or it does not.</para>
/// </summary>
public class TlsTests : IDisposable
{
    private readonly string _pfxPath;
    private const string CertPassword = "test-cert-password";

    public TlsTests()
    {
        _pfxPath = Path.Combine(Path.GetTempPath(), $"highway-tls-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(_pfxPath, CreateSelfSignedPfx());
    }

    public void Dispose()
    {
        try { File.Delete(_pfxPath); } catch { /* best effort */ }
    }

    /// <summary>A self-signed certificate for "localhost", valid for a day.</summary>
    private static byte[] CreateSelfSignedPfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));

        return cert.Export(X509ContentType.Pfx, CertPassword);
    }

    private HighwayTestServer StartTlsServer() => new(o =>
    {
        o.Tls.CertFileName = _pfxPath;
        o.Tls.CertPassword = CertPassword;
    });

    /// <summary>
    /// The certificate is self-signed, so the client must be told to trust it. This is what
    /// <c>ConfigureConnection</c> exists for — Highway does not model every
    /// StackExchange.Redis knob, it exposes the ones underneath.
    /// </summary>
    private static HighwayOptions TrustingClient() => new()
    {
        Tls = new HighwayTlsOptions { Enabled = true, TargetHost = "localhost" },
        ConfigureConnection = c => c.CertificateValidation += (_, _, _, _) => true,
    };

    [Fact]
    public async Task Tls_RoundTrips_WithASelfSignedCertificate()
    {
        using var server = StartTlsServer();

        await using var connection = await HighwayConnection.ConnectAsync(
            server.ConnectionString, TrustingClient());

        // A command over the encrypted socket, not merely a successful handshake.
        await connection.CallAsync("tls.svc", "req-1", "payload"u8.ToArray());

        connection.Should().NotBeNull();
    }

    [Fact]
    public async Task FullClientBehaviour_WorksOverTls()
    {
        using var server = StartTlsServer();

        await using var host = await EngineNode.StartAsync(
            server.ConnectionString, "tls-host", ApplyTls);
        await using var caller = await EngineNode.StartAsync(
            server.ConnectionString, "tls-caller", ApplyTls);

        var response = await caller.Client.ExecuteAsync(new ItEchoRequest { Value = "encrypted" });

        response.StatusCode.Should().Be(200);
        response.Value.Should().Be("encrypted");

        static void ApplyTls(HighwayOptions o)
        {
            o.Tls = new HighwayTlsOptions { Enabled = true, TargetHost = "localhost" };
            o.ConfigureConnection = c => c.CertificateValidation += (_, _, _, _) => true;
        }
    }

    /// <summary>
    /// A mismatched handshake is one of the least legible failures in networking, so it is
    /// asserted rather than assumed to be obvious.
    /// </summary>
    [Fact]
    public async Task PlaintextClientAgainstATlsServer_Fails()
    {
        using var server = StartTlsServer();

        var connect = async () => await HighwayConnection.ConnectAsync(server.ConnectionString);

        await connect.Should().ThrowAsync<Exception>(
            "a plaintext client cannot speak to a TLS listener");
    }

    [Fact]
    public async Task TlsClientAgainstAPlaintextServer_Fails()
    {
        using var server = new HighwayTestServer();

        var connect = async () => await HighwayConnection.ConnectAsync(
            server.ConnectionString, TrustingClient());

        await connect.Should().ThrowAsync<Exception>();
    }

    /// <summary>
    /// Authentication and TLS compose: the password is the thing TLS is protecting.
    /// </summary>
    [Fact]
    public async Task TlsAndPasswordTogether()
    {
        const string password = "tls-and-auth-4417";

        using var server = new HighwayTestServer(o =>
        {
            o.Tls.CertFileName = _pfxPath;
            o.Tls.CertPassword = CertPassword;
            o.Authentication.Password = password;
        });

        var options = TrustingClient();
        options.Password = password;

        await using var connection = await HighwayConnection.ConnectAsync(server.ConnectionString, options);
        connection.Should().NotBeNull();
    }

    // ---- build-time validation -------------------------------------------

    [Fact]
    public void MissingCertificateFile_FailsAtBuild_NamingThePath()
    {
        var act = () => new HighwayServerBuilder()
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithTls("does-not-exist.pfx")
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does-not-exist.pfx*does not exist*",
                "a startup error naming the file beats an opaque handshake failure later");
    }

    [Fact]
    public void WrongCertificatePassword_FailsAtBuild()
    {
        var act = () => new HighwayServerBuilder()
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithTls(_pfxPath, "not-the-password")
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*could not be loaded*");
    }

    [Fact]
    public void BothFileAndSubjectName_IsRejected()
    {
        var act = () => new HighwayServerBuilder()
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithTls(t =>
            {
                t.CertFileName = _pfxPath;
                t.CertSubjectName = "CN=localhost";
            })
            .Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one*");
    }

    /// <summary>
    /// TLS is never required — not on loopback, not off it, not alongside a password.
    /// Highway can demand a password; it cannot invent a certificate.
    /// </summary>
    [Fact]
    public void TlsIsNeverRequired_EvenOffLoopback()
    {
        using var server = new HighwayServerBuilder()
            .WithBindAddress(IPAddress.Any)
            .WithPort(Highway.Server.Internal.EphemeralPort.Probe())
            .WithPassword("secured-but-plaintext")
            .Build();

        server.Should().NotBeNull("authentication is mandatory off loopback; TLS is not");
    }
}
