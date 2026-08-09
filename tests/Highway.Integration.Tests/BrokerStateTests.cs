using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using Highway.Server;
using StackExchange.Redis;
using Xunit;

namespace Highway.Integration.Tests;

/// <summary>
/// Feature 020 T2/T3 — <b>the read path, proven against every security configuration before any
/// view is built on it.</b>
///
/// <para>This ordering is the whole point. Feature 018's pre-018 startup check opened a loopback
/// connection, mirrored the password and forgot TLS, and <b>no TLS-enabled server could start</b>
/// — four failing tests and a broker that would not boot. A read path that works on an open
/// broker and fails on a secured one is not a read path; finding that out after four views are
/// built means rewriting four views.</para>
///
/// <para>The fix was not "be careful". It was <c>LoopbackConnection</c>: one place that builds a
/// self-connection from the server's own options, so a transport setting added later is added
/// once rather than mirrored by every caller.</para>
/// </summary>
public class BrokerStateTests
{
    private const string CertPassword = "highway-test";

    private static byte[] Envelope()
        => Encoding.UTF8.GetBytes("""{"v":1,"src":"t","ts":"2026-08-09T00:00:00Z","body":{}}""");

    /// <summary>Drives one queue into existence and returns its live state.</summary>
    private static async Task<QueueStateSnapshot> ReadQueuesAsync(HighwayTestServer server)
    {
        var db = ConnectionMultiplexer.Connect(server.ConnectionString).GetDatabase();
        db.Execute("HW.QSEND", "bs.queue", "m-1", Envelope());
        db.Execute("HW.QSEND", "bs.queue", "m-2", Envelope());

        var result = await server.ReadQueueStateAsync();
        return new QueueStateSnapshot(result.Unavailable, result.Rows);
    }

    private sealed record QueueStateSnapshot(string? Unavailable, IReadOnlyList<(string Name, long Depth, long Bytes)> Rows);

    // ---- the security matrix (T2) ---------------------------------------------

    [Fact]
    public async Task ReadsState_OnAnOpenBroker()
    {
        using var server = new HighwayTestServer(o => o.Authentication.Password = null);

        var state = await ReadQueuesAsync(server);

        state.Unavailable.Should().BeNull();
        state.Rows.Should().Contain(r => r.Name == "bs.queue" && r.Depth == 2);
    }

    [Fact]
    public async Task ReadsState_OnAPasswordProtectedBroker()
    {
        // HighwayTestServer is authenticated by default, so this is the default path.
        using var server = new HighwayTestServer();

        var state = await ReadQueuesAsync(server);

        state.Unavailable.Should().BeNull("the connection is built from the server's own options, " +
                                          "so the password cannot be forgotten");
        state.Rows.Should().Contain(r => r.Name == "bs.queue");
    }

    [Fact]
    public async Task ReadsState_OverTls()
    {
        var pfx = WriteSelfSignedCertificate();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.Tls.CertFileName = pfx;
                o.Tls.CertPassword = CertPassword;
            });

            // No seeding here, deliberately: seeding needs a TLS-aware client of its own, and
            // what T2 is asserting is that the READ PATH connects. An empty-but-successful read
            // proves that; a populated one would only prove the test could also connect.
            var result = await server.ReadQueueStateAsync();

            // The exact case 018 broke: a plaintext self-connection against a TLS listener.
            result.Unavailable.Should().BeNull(
                "this is the configuration that broke 018 - the connection is built from the " +
                "server's own options, so TLS cannot be forgotten");
        }
        finally { File.Delete(pfx); }
    }

    /// <summary>
    /// The one configuration a self-connection genuinely cannot serve: the server demands a
    /// client certificate that a connection from the broker to itself has no way to present.
    ///
    /// <para><b>It must degrade, not fail.</b> 018's version of this took the whole broker down.
    /// Here the broker runs, the state read reports why it cannot answer, and the reason names
    /// the setting rather than reporting a bare timeout.</para>
    /// </summary>
    [Fact]
    public async Task DegradesWithAReason_UnderMutualTls()
    {
        var pfx = WriteSelfSignedCertificate();
        try
        {
            using var server = new HighwayTestServer(o =>
            {
                o.Tls.CertFileName = pfx;
                o.Tls.CertPassword = CertPassword;
                o.Tls.ClientCertificateRequired = true;
            });

            // The broker started. That is half the assertion.
            var result = await server.ReadQueueStateAsync();

            result.Unavailable.Should().NotBeNull("a self-connection cannot present a client certificate");
            result.Unavailable.Should().Contain("ClientCertificateRequired",
                "the reason names the setting, so an operator is not left reading a timeout");
        }
        finally { File.Delete(pfx); }
    }

    // ---- the contract (T3) ----------------------------------------------------

    [Fact]
    public async Task SubscriberGroupsAppearAsQueues_BecauseTheyAre()
    {
        using var server = new HighwayTestServer();
        var db = ConnectionMultiplexer.Connect(server.ConnectionString).GetDatabase();

        db.Execute("HW.SUBSCRIBE", "bs.channel", "billing");
        db.Execute("HW.PUBLISH", "bs.channel", Envelope());

        var result = await server.ReadQueueStateAsync();

        result.Rows.Should().Contain(r => r.Name == "bs.channel@billing",
            "018 made a group's queue a queue, which is what lets one view cover both verbs");
    }

    [Fact]
    public async Task ByteCountersAreReported_SoFullnessCanBeShownAsAProportion()
    {
        using var server = new HighwayTestServer();
        var db = ConnectionMultiplexer.Connect(server.ConnectionString).GetDatabase();
        db.Execute("HW.QSEND", "bs.bytes", "m-1", Envelope());

        var result = await server.ReadQueueStateAsync();
        var row = result.Rows.Single(r => r.Name == "bs.bytes");

        row.Bytes.Should().BeGreaterThan(0,
            "\"847 MB\" means nothing without the limit beside it; the view needs both numbers");
    }

    private static string WriteSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var path = Path.Combine(Path.GetTempPath(), $"hw-bs-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, CertPassword));
        return path;
    }
}
