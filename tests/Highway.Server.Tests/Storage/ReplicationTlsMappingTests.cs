using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Highway.Server.Storage.Replication;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// 076: replication left the RESP connection for its own port, so it must not leave TLS behind. A broker that serves
/// TLS replicates over TLS with the same certificate; one that does not, does not.
/// </summary>
public class ReplicationTlsMappingTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void ABrokerServingTls_ReplicatesOverTls_TrustingItsSharedSelfSignedCertificate()
    {
        using var cert = SelfSigned();
        var crystal = HighwayReplication.CrystalOptionsFor(new HighwayReplicationOptions
        {
            TlsCertificate = cert,
            TlsRequireClientCertificate = true,
        });

        crystal.Certificate.Should().BeSameAs(cert);
        crystal.RequireClientCertificate.Should().BeTrue("mutual TLS on RESP is mutual TLS on replication");
        crystal.TrustedCertificates.Should().ContainSingle().Which.Should().BeSameAs(cert,
            "a replica set shares its self-signed certificate, so it is trusted as itself");
    }

    [Fact]
    public void ABrokerWithoutTls_ReplicatesInTheClear_AsItsRespPortIs()
    {
        var crystal = HighwayReplication.CrystalOptionsFor(new HighwayReplicationOptions { TlsRequireClientCertificate = true });

        crystal.Certificate.Should().BeNull();
        crystal.RequireClientCertificate.Should().BeFalse("there is no TLS to require a client certificate in");
        crystal.TrustedCertificates.Should().BeNull();
    }

    [Fact]
    public void TheSecret_IsTheConfiguredOne_OrAWellKnownOneWhenThereIsNone()
    {
        HighwayReplication.CrystalOptionsFor(new HighwayReplicationOptions { ReplicationSecret = "s3cret" }).Secret.Should().Be("s3cret");
        HighwayReplication.CrystalOptionsFor(new HighwayReplicationOptions()).Secret.Should().Be(HighwayReplication.UnauthenticatedSecret);
    }
}
