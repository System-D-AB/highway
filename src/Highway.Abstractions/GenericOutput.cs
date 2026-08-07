namespace Highway.Abstractions;

/// <summary>
/// A generic output used when a service fails to produce its typed response.
/// The engine creates this with a status code and error detail.
/// </summary>
public sealed class GenericOutput : Output;
