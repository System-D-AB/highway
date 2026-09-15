using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Highway.Server;
using Highway.Server.Resp;
using Highway.Server.Storage;
using StackExchange.Redis;
using Xunit;

namespace Highway.Server.Tests.Resp;

/// <summary>
/// 040 T5 — TLS endpoints (037 R3.3). The RESP server serves over TLS with a non-HTTP ALPN
/// (verified, not assumed): a raw TLS handshake that offers HTTP ALPN protocols negotiates
/// <b>none</b>, because RESP is not HTTP. Pinned SE.Redis with <c>ssl=true</c> round-trips a
/// real HW.* command; a plaintext server still round-trips too (both are served).
/// </summary>
public class RespServerTlsTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // On Windows the private key must be persisted for the TLS stack to use it; round-trip via PFX.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static async Task<RespServer> StartTlsAsync(X509Certificate2 cert)
        => await RespServer.StartAsync(
            new InMemoryStore(), new HighwayServerOptions(),
            new PasswordAuthenticator(password: null, authDisabled: true),
            IPAddress.Loopback, 0, cert);

    [Fact]
    public async Task Tls_SeRedis_RoundTripsHwCommand()
    {
        using var cert = SelfSigned();
        await using var server = await StartTlsAsync(cert);

        var config = ConfigurationOptions.Parse($"localhost:{server.Port}");
        config.Ssl = true;
        config.SslHost = "localhost";
        config.AbortOnConnectFail = true;
        config.ConnectTimeout = 5000;
        // Trust our self-signed cert for the test.
        config.CertificateValidation += (_, _, _, _) => true;

        using var mux = await ConnectionMultiplexer.ConnectAsync(config);
        mux.IsConnected.Should().BeTrue("SE.Redis with ssl=true must complete the TLS handshake and RESP handshake");

        var reply = await mux.GetDatabase().ExecuteAsync("HW.QSEND", "secure-q", "m1", "body");
        reply.ToString().Should().Be("OK");
    }

    [Fact]
    public async Task Tls_Handshake_NegotiatesNoHttpAlpn()
    {
        using var cert = SelfSigned();
        await using var server = await StartTlsAsync(cert);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

        // Offer HTTP ALPN protocols; the server must decline them (RESP is not HTTP).
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols =
            [
                SslApplicationProtocol.Http2,
                SslApplicationProtocol.Http11,
            ],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        });

        ssl.NegotiatedApplicationProtocol.Protocol.IsEmpty
            .Should().BeTrue("the RESP endpoint advertises no ALPN protocol — it is not an HTTP server (037 R3.3)");
    }

    [Fact]
    public async Task Plaintext_StillServed_AlongsideTls()
    {
        // The plaintext endpoint (no cert) round-trips — both transports are served.
        await using var server = await RespServer.StartAsync(
            new InMemoryStore(), new HighwayServerOptions(),
            new PasswordAuthenticator(null, authDisabled: true), IPAddress.Loopback, 0);

        var config = ConfigurationOptions.Parse($"localhost:{server.Port}");
        config.AbortOnConnectFail = true;
        using var mux = await ConnectionMultiplexer.ConnectAsync(config);
        (await mux.GetDatabase().ExecuteAsync("HW.QSEND", "q", "m", "b")).ToString().Should().Be("OK");
    }
}
