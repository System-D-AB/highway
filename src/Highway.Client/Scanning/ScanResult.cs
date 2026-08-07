namespace Highway.Client.Scanning;

/// <summary>
/// The result of assembly scanning.
///
/// <para><see cref="Services"/> and <see cref="Channels"/> are what this node
/// <b>hosts</b> — implementations it can execute. <see cref="RequestContracts"/>
/// and <see cref="MessageContracts"/> are what it can <b>address</b>: every
/// contract type carrying <c>[Service]</c> or <c>[Channel]</c> in the scanned
/// assemblies, whether or not this node implements it.</para>
///
/// <para>The distinction is what makes a caller-only node possible. A process
/// that references a contracts library but hosts nothing still needs to know
/// that <c>CreateOrder</c> addresses <c>"orders.create"</c> — otherwise it can
/// never call anything, which is exactly the defect feature 010 found.</para>
/// </summary>
public sealed class ScanResult
{
    /// <summary>Services this node hosts and can execute.</summary>
    public required IReadOnlyList<ServiceDescriptor> Services { get; init; }

    /// <summary>Channels this node has local subscribers for.</summary>
    public required IReadOnlyList<ChannelDescriptor> Channels { get; init; }

    /// <summary>Request type → service name, for every <c>[Service]</c> contract found.</summary>
    public IReadOnlyDictionary<Type, string> RequestContracts { get; init; }
        = new Dictionary<Type, string>();

    /// <summary>Message type → channel name, for every <c>[Channel]</c> contract found.</summary>
    public IReadOnlyDictionary<Type, string> MessageContracts { get; init; }
        = new Dictionary<Type, string>();
}
