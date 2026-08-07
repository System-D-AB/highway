using System.Reflection;

namespace Highway.Client.Scanning;

/// <summary>
/// Provides the set of assemblies to scan for Highway services and channels.
/// </summary>
public interface IAssemblySource
{
    IReadOnlyList<Assembly> GetAssemblies();
}
