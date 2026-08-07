namespace Highway.Client.Scanning;

/// <summary>
/// The result of assembly scanning — all discovered services and channels.
/// </summary>
public sealed class ScanResult
{
    public required IReadOnlyList<ServiceDescriptor> Services { get; init; }
    public required IReadOnlyList<ChannelDescriptor> Channels { get; init; }
}
