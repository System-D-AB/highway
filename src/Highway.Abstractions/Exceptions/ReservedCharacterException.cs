namespace Highway.Abstractions.Exceptions;

/// <summary>
/// Thrown during assembly scanning when a <c>[Queue]</c> or <c>[Channel]</c> name
/// contains the reserved <c>@</c> character (feature 018).
///
/// <para><c>@</c> is reserved for derived group-queue names: when pub/sub unifies onto
/// the queue engine, each subscriber group's queue is named <c>{channel}@{group}</c>.
/// Without the reservation, a user-declared queue named <c>orders.placed@billing</c>
/// would collide with the <c>billing</c> group of the <c>orders.placed</c> channel.</para>
/// </summary>
public sealed class ReservedCharacterException(string attributeName, string name, char reserved)
    : Exception(
        $"The [{attributeName}(\"{name}\")] name contains '{reserved}' which is reserved " +
        $"for internal group-queue routing. Choose a name without '{reserved}'.");
