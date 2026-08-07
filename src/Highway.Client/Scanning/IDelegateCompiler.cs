namespace Highway.Client.Scanning;

/// <summary>
/// Compiles strongly-typed delegates for invoking service and subscriber methods.
/// </summary>
internal interface IDelegateCompiler
{
    /// <summary>
    /// Compiles a delegate for calling AsyncService&lt;TReq,TRes&gt;.ExecuteAsync(request, ct).
    /// Signature: (object service, object request, CancellationToken ct) → Task&lt;object&gt;
    /// </summary>
    Func<object, object, CancellationToken, Task<object>> CompileServiceDelegate(Type serviceType, Type requestType, Type responseType);

    /// <summary>
    /// Compiles a delegate for calling ISubscribe&lt;T&gt;.SubscribeAsync(message, ct).
    /// Signature: (object subscriber, object message, CancellationToken ct) → Task
    /// </summary>
    Func<object, object, CancellationToken, Task> CompileSubscriberDelegate(Type subscriberType, Type messageType);
}
