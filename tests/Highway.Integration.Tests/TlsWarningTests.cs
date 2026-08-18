namespace Highway.Integration.Tests;

using System.Security.Authentication;
using FluentAssertions;
using Highway.Client;
using Highway.Server;
using Highway.Server.Security;
using Microsoft.Extensions.Logging;
using Xunit;

public class TlsWarningTests
{
    private sealed class TestLoggerProvider : ILoggerProvider, ILogger
    {
        public readonly List<string> LoggedMessages = [];

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (LoggedMessages)
                {
                    LoggedMessages.Add(formatter(state, exception));
                }
            }
        }

        public void Dispose() { }
    }

    [Fact]
    public void Server_CleartextAuth_LogsWarning()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));

        var server = new HighwayServerBuilder()
            .WithPassword("super-secret")
            .WithLoggerFactory(loggerFactory)
            .Build();

        loggerProvider.LoggedMessages.Should().Contain(msg =>
            msg.Contains("Transport security (TLS) is disabled while authentication is configured"));
    }

    [Fact]
    public void Server_TlsClientCertificateNotRequired_LogsWarningAndStarts()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));

        var pfxPath = Path.Combine(Path.GetTempPath(), $"highway-warn-test-{Guid.NewGuid():N}.pfx");
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(pfxPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));

        try
        {
            var server = new HighwayServerBuilder()
                .WithTls(pfxPath)
                .WithTls(opt => opt.ClientCertificateRequired = false)
                .WithLoggerFactory(loggerFactory)
                .Build();

            server.Start();
            server.Dispose();

            loggerProvider.LoggedMessages.Should().Contain(msg =>
                msg.Contains("TLS option ClientCertificateRequired is false"));
        }
        finally
        {
            try { File.Delete(pfxPath); } catch { }
        }
    }

    [Fact]
    public void Server_TlsClientCertRequiredWithoutIssuerPath_LogsWarningAndStarts()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));

        var pfxPath = Path.Combine(Path.GetTempPath(), $"highway-warn-test-{Guid.NewGuid():N}.pfx");
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(pfxPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));

        try
        {
            var server = new HighwayServerBuilder()
                .WithTls(pfxPath)
                .WithTls(opt =>
                {
                    opt.ClientCertificateRequired = true;
                    opt.IssuerCertificatePath = null;
                })
                .WithLoggerFactory(loggerFactory)
                .Build();

            server.Start();
            server.Dispose();

            loggerProvider.LoggedMessages.Should().Contain(msg =>
                msg.Contains("TLS option ClientCertificateRequired is true but IssuerCertificatePath is not specified"));
        }
        finally
        {
            try { File.Delete(pfxPath); } catch { }
        }
    }

    [Fact]
    public void Server_EphemeralCert_LogsWarning()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));

        var pfxPath = Path.Combine(Path.GetTempPath(), $"highway-warn-test-{Guid.NewGuid():N}.pfx");
        // Create dummy file for validate
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(pfxPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));

        try
        {
            var server = new HighwayServerBuilder()
                .WithTls(pfxPath)
                .WithTls(opt => opt.IsEphemeral = true)
                .WithLoggerFactory(loggerFactory)
                .Build();

            loggerProvider.LoggedMessages.Should().Contain(msg =>
                msg.Contains("Using ephemeral self-signed certificate for TLS"));
        }
        finally
        {
            try { File.Delete(pfxPath); } catch { }
        }
    }

    [Fact]
    public void Client_DeprecatedProtocol_LogsWarning()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var logger = loggerFactory.CreateLogger("Test");

        var options = new HighwayOptions
        {
            Server = "127.0.0.1:6379",
            Tls = new HighwayTlsOptions
            {
                Enabled = true,
#pragma warning disable SYSLIB0039 // Obsolete TLS protocol tested intentionally
                Protocols = SslProtocols.Tls | SslProtocols.Tls12
#pragma warning restore SYSLIB0039
            }
        };

        HighwayOptionsValidator.ValidateTls(options, logger);

        loggerProvider.LoggedMessages.Should().Contain(msg =>
            msg.Contains("includes deprecated versions (< TLS 1.2)"));
    }

    [Fact]
    public void Client_CleartextAuth_LogsWarning()
    {
        var loggerProvider = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(loggerProvider));
        var logger = loggerFactory.CreateLogger("Test");

        var options = new HighwayOptions
        {
            Server = "127.0.0.1:6379",
            Password = "mypassword"
        };

        HighwayOptionsValidator.ValidateTls(options, logger);

        loggerProvider.LoggedMessages.Should().Contain(msg =>
            msg.Contains("Transport security (TLS) is disabled while authentication is configured"));
    }
}
