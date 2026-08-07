namespace Highway.Abstractions;

/// <summary>
/// Marker interface for RPC request types. The generic parameter declares the response type.
/// </summary>
public interface IReturn<TResponse> where TResponse : Output;
