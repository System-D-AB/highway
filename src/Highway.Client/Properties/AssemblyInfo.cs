using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Highway.Client.Tests")]

// Lets NSubstitute/Castle proxy the internal engine interfaces (IHighwayConnection,
// IHighwayEngineInternals) so the engine can be unit-tested without a live server.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
