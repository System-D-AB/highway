namespace Highway.Abstractions;

/// <summary>
/// Marks a service handler whose work changes something outside it — charges a card, sends an email (074). An RPC
/// call that times out leaves its caller not knowing whether the work happened; a handler marked with this and
/// without <see cref="IdempotentAttribute"/> on its request is listed at startup (Information) as a nudge: make the
/// request <c>[Idempotent]</c> and reuse its key on retry, or send the work through a queue. Never an error.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class SideEffectsAttribute : Attribute;
