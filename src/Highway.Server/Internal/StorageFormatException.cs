namespace Highway.Server.Internal;

/// <summary>
/// Thrown when a command meets an entry written by a pre-013 Highway (feature 013).
///
/// <para>Feature 013 added a delivery-attempt count to queue and processing entries,
/// which changed how they parse. An old entry read as a current one does <b>not</b>
/// fail on its own — it silently reinterprets its leading bytes, reads a wrong length,
/// and delivers a corrupt payload to an application. That is far worse than an error,
/// so entries carry a version byte and a mismatch is refused loudly.</para>
///
/// <para>Highway had not shipped when this break was introduced, so the realistic
/// remedy is to delete the data directory. Draining the affected queue with the
/// previous version also works.</para>
/// </summary>
internal sealed class StorageFormatException(string key)
    : Exception(
        $"'{key}' holds entries in the pre-013 storage format. " +
        "Drain it with the previous version, or delete the data directory. " +
        "Refusing rather than misparsing, which would deliver a corrupt payload.")
{
    /// <summary>The key whose contents could not be read.</summary>
    public string Key { get; } = key;
}
