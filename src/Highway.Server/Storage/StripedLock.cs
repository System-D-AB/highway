namespace Highway.Server.Storage;

/// <summary>
/// A striped lock keyed by a logical name (a queue, a channel@group, a service). The
/// stripe is chosen by hashing the name modulo the stripe count.
///
/// <para>This supplies the read-then-write atomicity a single <c>WriteBatch</c> cannot:
/// a claim reads the head, decides, and writes — and two claims on the same queue must
/// not both take the same head. The command layer (039) acquires the stripe for the
/// structure it operates on before opening its batch, exactly as the physical-layout
/// design (§3, and 037 design §3) prescribes: <c>take lock → snapshot → build batch →
/// commit</c>.</para>
///
/// <para>Ported from <c>stow-rocksdb</c>
/// (<c>docs/features/037-rocksdb-engine/reference/stow-engine/Engine/StripedLock.cs</c>),
/// re-keyed from <c>(collectionCode, id)</c> to Highway's logical name. Locks are keyed to
/// stripes so distinct names contend only on a hash collision; the same name always maps to
/// the same stripe, so operations on one structure serialize.</para>
/// </summary>
public sealed class StripedLock : IDisposable
{
    /// <summary>Default stripe count — enough that unrelated names rarely collide.</summary>
    public const int DefaultStripeCount = 256;

    private readonly SemaphoreSlim[] _stripes;

    public StripedLock(int stripeCount = DefaultStripeCount)
    {
        if (stripeCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(stripeCount), "Stripe count must be positive.");

        _stripes = new SemaphoreSlim[stripeCount];
        for (var i = 0; i < stripeCount; i++)
            _stripes[i] = new SemaphoreSlim(1, 1);
    }

    /// <summary>Number of stripes.</summary>
    public int StripeCount => _stripes.Length;

    /// <summary>
    /// Acquires the lock for <paramref name="name"/> synchronously. Returns a disposable
    /// that releases it. Operations on the same name serialize; operations on names in
    /// different stripes proceed in parallel.
    /// </summary>
    public IDisposable Lock(string name)
    {
        var stripe = StripeOf(name);
        _stripes[stripe].Wait();
        return new Lease(_stripes[stripe]);
    }

    /// <summary>Async variant, for a caller on an async path.</summary>
    public async ValueTask<IDisposable> LockAsync(string name, CancellationToken ct = default)
    {
        var stripe = StripeOf(name);
        await _stripes[stripe].WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_stripes[stripe]);
    }

    /// <summary>The stripe index a name maps to. Stable for the life of the process.</summary>
    internal int StripeOf(string name)
    {
        // Ordinal hash, made non-negative, modulo the stripe count. A fixed algorithm so
        // the same name always lands on the same stripe within a process.
        var hash = 17;
        foreach (var c in name)
            hash = unchecked(hash * 31 + c);
        return (hash & 0x7FFF_FFFF) % _stripes.Length;
    }

    public void Dispose()
    {
        foreach (var s in _stripes)
            s.Dispose();
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
