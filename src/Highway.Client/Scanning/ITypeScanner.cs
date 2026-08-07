using System.Reflection;

namespace Highway.Client.Scanning;

/// <summary>
/// Scans assemblies to discover Highway services and channel subscribers.
/// </summary>
public interface ITypeScanner
{
    ScanResult Scan(IReadOnlyList<Assembly> assemblies);
}
