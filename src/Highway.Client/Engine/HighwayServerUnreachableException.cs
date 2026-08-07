namespace Highway.Client.Engine;

/// <summary>
/// The engine could not establish a connection to the configured Highway server
/// at startup. Startup fails fast with this exception — no silent retry loop.
/// </summary>
public sealed class HighwayServerUnreachableException(string endpoint, Exception? inner = null)
    : Exception($"Could not connect to Highway server at '{endpoint}'. Verify the server is running and the Server option is correct.", inner);
