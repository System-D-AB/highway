using System.Net;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Security;

/// <summary>
/// Highway's default security posture, in one place (feature 012).
///
/// <para><b>The whole rule:</b> authentication is not required on loopback and is
/// required off it.</para>
///
/// <para>Running with no security is the right configuration for development and
/// evaluation, and anything that taxes it — an exception, a required call, a
/// generated password to copy out of a log — is paid by every newcomer to protect a
/// case that does not exist on loopback. A loopback-bound broker is reachable only by
/// processes already on the machine, which have easier ways in.</para>
///
/// <para>The obvious alternative, requiring authentication everywhere, was rejected:
/// the uniformity is bought by taxing the one configuration where the risk is absent.
/// The concern it was meant to answer — that the tested path would not be the deployed
/// path — is a test-suite problem and is solved there instead: <c>HighwayTestServer</c>
/// authenticates by default, so every integration test exercises <c>AUTH</c> whatever
/// users choose for their own servers.</para>
///
/// <para><b>This rule tests authentication only.</b> TLS is never required, on any
/// address, with or without a password. A certificate is something Highway cannot
/// invent, so requiring one would produce a server that cannot start.</para>
/// </summary>
internal static class SecurityPolicy
{
    /// <summary>
    /// Whether a server on this address must authenticate. The single expression the
    /// rest of Highway's security posture derives from.
    /// </summary>
    public static bool RequiresAuthentication(IPAddress bindAddress)
        => !IPAddress.IsLoopback(bindAddress);

    /// <summary>
    /// Applies the rule: throws when an unauthenticated server would be reachable from
    /// off the machine, and otherwise records what was decided.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The server is bound off loopback with neither authentication configured nor
    /// <c>WithoutAuthentication()</c> called.
    /// </exception>
    public static void Enforce(HighwayServerOptions opts, ILogger? logger)
    {
        var endpoint = $"{opts.BindAddress}:{opts.Port}";

        if (opts.Authentication.IsConfigured)
        {
            logger?.LogInformation("Highway server authentication is enabled on {Endpoint}.", endpoint);
            return;
        }

        if (!RequiresAuthentication(opts.BindAddress))
        {
            // Informational, deliberately not a warning. Warning on a configuration
            // that is correct teaches people to filter the category, and then the real
            // warning below is invisible too. Say what will change when they move off
            // loopback, so the throw is not a surprise when they do.
            logger?.LogInformation(
                "Highway server is running without authentication on {Endpoint} — expected for local " +
                "development. Binding to another address will require credentials.",
                endpoint);
            return;
        }

        if (opts.Authentication.ExplicitlyDisabled)
        {
            logger?.LogWarning(
                "Highway server is running WITHOUT authentication on {Endpoint}. Any host that can reach " +
                "this port can execute every HW.* command and read recorded message payloads.",
                endpoint);
            return;
        }

        // The credential remedy is named first on purpose: the escape hatch is the
        // shorter fix and should not also be the more prominent one.
        throw new InvalidOperationException(
            $"Highway server is bound to {endpoint}, which requires authentication. " +
            "Call WithPassword(password) to secure it, or WithoutAuthentication() to run open " +
            "on a network you trust.");
    }
}
