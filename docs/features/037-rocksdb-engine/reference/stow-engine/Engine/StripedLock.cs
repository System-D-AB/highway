using System;
using System.Threading;
using System.Threading.Tasks;
using Stow.Abstractions;

namespace Stow.Storage.Engine;

/// <summary>
/// A striped lock keyed by (collectionCode, id). The stripe is chosen by hashing
/// the composite key and taking modulus over the stripe count.
///
/// This supplies the read-then-write atomicity that WriteBatch alone cannot provide —
/// it makes unique claims and CAS safe (design § 4, R4.5).
/// </summary>
public sealed class StripedLock : IDisposable
{
    public const int DefaultStripeCount = 256;

    private readonly SemaphoreSlim[] _stripes;

    public StripedLock(int stripeCount = DefaultStripeCount)
    {
        if (stripeCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(stripeCount), "Stripe count must be positive.");

        _stripes = new SemaphoreSlim[stripeCount];
        for (int i = 0; i < stripeCount; i++)
            _stripes[i] = new SemaphoreSlim(1, 1);
    }

    /// <summary>Number of stripes.</summary>
    public int StripeCount => _stripes.Length;

    /// <summary>
    /// Acquires the stripe lock for the given (collectionCode, id) pair.
    /// Returns a disposable that releases the semaphore.
    /// </summary>
    public async ValueTask<IDisposable> LockAsync(uint collectionCode, StowId id, CancellationToken ct = default)
    {
        int stripe = GetStripeIndex(collectionCode, id);
        await _stripes[stripe].WaitAsync(ct).ConfigureAwait(false);
        return new StripeLease(_stripes[stripe]);
    }

    /// <summary>
    /// Synchronous lock acquisition for use in synchronous write paths.
    /// </summary>
    public IDisposable Lock(uint collectionCode, StowId id)
    {
        int stripe = GetStripeIndex(collectionCode, id);
        _stripes[stripe].Wait();
        return new StripeLease(_stripes[stripe]);
    }

    /// <summary>
    /// Acquires the lock for a specific stripe index directly.
    /// Used for unique constraint locking where the stripe is computed from the unique key hash.
    /// </summary>
    public IDisposable LockByStripe(int stripeIndex)
    {
        _stripes[stripeIndex].Wait();
        return new StripeLease(_stripes[stripeIndex]);
    }

    /// <summary>
    /// Computes the stripe index from (collectionCode, id).
    /// </summary>
    internal int GetStripeIndex(uint collectionCode, StowId id)
    {
        unchecked
        {
            int hash = (int)collectionCode * 397;
            hash ^= id.GetHashCode();
            // Ensure non-negative
            hash &= 0x7FFF_FFFF;
            return hash % _stripes.Length;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _stripes.Length; i++)
            _stripes[i].Dispose();
    }

    private sealed class StripeLease : IDisposable
    {
        private SemaphoreSlim _semaphore;

        public StripeLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _semaphore, null);
            s?.Release();
        }
    }
}
