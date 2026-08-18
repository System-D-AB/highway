namespace Highway.Assurance.Tests;

using FluentAssertions;
using Highway.Assurance.Notifications.Mailer;
using Highway.Assurance.Notifications.Subs;
using Highway.Client;
using Highway.Client.Scanning;
using Xunit;

public class NotificationsRoleBoundaryTests
{
    [Fact]
    public void NotificationsSubsRole_HostsNoQueueProcessor()
    {
        var scanner = new DefaultTypeScanner();
        var subsAssembly = typeof(UserSignedUpSubscriber).Assembly;
        var contractsAssembly = typeof(Highway.Assurance.Contracts.SendEmail).Assembly;

        var allAssemblies = new[] { contractsAssembly, subsAssembly };
        var handlerAssemblies = HostingBoundary.SelectHandlerAssemblies(
            HostingMode.ExplicitOnly,
            allAssemblies,
            entryAssembly: null,
            declared: [subsAssembly]);

        var scanResult = scanner.Scan(allAssemblies, handlerAssemblies);

        scanResult.Queues.Should().BeEmpty("The subs role MUST host zero queue processors so that the gap phase can accumulate messages");
        scanResult.Channels.Should().NotBeEmpty("The subs role must host subscribers");
        scanResult.Channels.SelectMany(c => c.Subscribers).Should().HaveCount(2);
    }

    [Fact]
    public void NotificationsMailerRole_HostsNoSubscribers()
    {
        var scanner = new DefaultTypeScanner();
        var mailerAssembly = typeof(SendEmailProcessor).Assembly;
        var contractsAssembly = typeof(Highway.Assurance.Contracts.SendEmail).Assembly;

        var allAssemblies = new[] { contractsAssembly, mailerAssembly };
        var handlerAssemblies = HostingBoundary.SelectHandlerAssemblies(
            HostingMode.ExplicitOnly,
            allAssemblies,
            entryAssembly: null,
            declared: [mailerAssembly]);

        var scanResult = scanner.Scan(allAssemblies, handlerAssemblies);

        scanResult.Queues.Should().HaveCount(1);
        scanResult.Queues[0].Name.Should().Be("email.send");
        scanResult.Channels.SelectMany(c => c.Subscribers).Should().BeEmpty("The mailer role must host zero subscribers");
    }
}
