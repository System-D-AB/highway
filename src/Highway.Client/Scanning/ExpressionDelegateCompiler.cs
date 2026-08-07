using System.Linq.Expressions;

namespace Highway.Client.Scanning;

/// <summary>
/// Compiles delegates using Expression trees for near-zero-overhead invocation.
/// </summary>
internal sealed class ExpressionDelegateCompiler : IDelegateCompiler
{
    public Func<object, object, CancellationToken, Task<object>> CompileServiceDelegate(
        Type serviceType, Type requestType, Type responseType)
    {
        // Parameters: (object svc, object req, CancellationToken ct)
        var svcParam = Expression.Parameter(typeof(object), "svc");
        var reqParam = Expression.Parameter(typeof(object), "req");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        // Cast parameters to concrete types
        var castSvc = Expression.Convert(svcParam, serviceType);
        var castReq = Expression.Convert(reqParam, requestType);

        // Find ExecuteAsync method
        var method = serviceType.GetMethod("ExecuteAsync", [requestType, typeof(CancellationToken)])!;

        // Call: ((ServiceType)svc).ExecuteAsync((RequestType)req, ct)
        var call = Expression.Call(castSvc, method, castReq, ctParam);

        // Compile as Func<object, object, CancellationToken, Task> (Task<TRes> is covariant to Task)
        var lambda = Expression.Lambda<Func<object, object, CancellationToken, Task>>(call, svcParam, reqParam, ctParam);
        var compiledAsync = lambda.Compile();

        // Wrap to box the result from Task<TRes> to Task<object>
        return async (svc, req, ct) =>
        {
            var task = compiledAsync(svc, req, ct);
            await task.ConfigureAwait(false);

            // Get the Result property from Task<TRes>
            var resultProperty = task.GetType().GetProperty("Result")!;
            return resultProperty.GetValue(task)!;
        };
    }

    /// <summary>Compiles <c>IProcess&lt;T&gt;.ProcessAsync</c> (feature 014).</summary>
    public Func<object, object, CancellationToken, Task> CompileProcessorDelegate(
        Type processorType, Type messageType)
    {
        var procParam = Expression.Parameter(typeof(object), "proc");
        var msgParam = Expression.Parameter(typeof(object), "msg");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var castProc = Expression.Convert(procParam, processorType);
        var castMsg = Expression.Convert(msgParam, messageType);

        var method = processorType.GetMethod("ProcessAsync", [messageType, typeof(CancellationToken)])!;
        var call = Expression.Call(castProc, method, castMsg, ctParam);

        return Expression.Lambda<Func<object, object, CancellationToken, Task>>(
            call, procParam, msgParam, ctParam).Compile();
    }

    public Func<object, object, CancellationToken, Task> CompileSubscriberDelegate(
        Type subscriberType, Type messageType)
    {
        // Parameters: (object sub, object msg, CancellationToken ct)
        var subParam = Expression.Parameter(typeof(object), "sub");
        var msgParam = Expression.Parameter(typeof(object), "msg");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        // Cast parameters
        var castSub = Expression.Convert(subParam, subscriberType);
        var castMsg = Expression.Convert(msgParam, messageType);

        // Find SubscribeAsync method
        var method = subscriberType.GetMethod("SubscribeAsync", [messageType, typeof(CancellationToken)])!;

        // Call: ((SubscriberType)sub).SubscribeAsync((MessageType)msg, ct)
        var call = Expression.Call(castSub, method, castMsg, ctParam);

        // Compile to Func<object, object, CancellationToken, Task>
        var lambda = Expression.Lambda<Func<object, object, CancellationToken, Task>>(call, subParam, msgParam, ctParam);
        return lambda.Compile();
    }
}
