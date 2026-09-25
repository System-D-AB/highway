using Highway.Server.Commands.Runtime;
using Highway.Server.Storage;
using Highway.Server.Storage.Boxes;

namespace Highway.Server.Commands.Ported;

/// <summary>
/// Expiring keys (065 T5): RPC reply slots and idempotency markers. Before 3.0 an expired key was filtered
/// on read but never deleted — every uncollected reply and every <c>[Idempotent]</c> marker stayed on disk
/// forever. Now each write also records <c>[box 0][0x13][expiresAt:8][family key]</c>, and the commands
/// that write expiring keys sweep what is due: a seek from the index's floor to now, bounded, at most about
/// once a second, reading only expired entries.
/// </summary>
internal static class ExpirySupport
{
    private const int SweepLimit = 128;
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(10);   // → at most once a second (SweepThrottle)

    /// <summary>Stages <paramref name="value"/> under <paramref name="familyKey"/> with an absolute expiry, indexed for deletion.</summary>
    public static void SetExpiring(CommandContext ctx, IStoreBatch batch, byte[] familyKey, byte[] value, long expiresAtTicks)
    {
        ctx.Store.SetEx(batch, familyKey, value, expiresAtTicks);
        ctx.Store.Set(batch, IndexKey(expiresAtTicks, familyKey), []);
        ctx.Boxes.Lower(BoxKeys.SystemBox, BoxKeys.ExpiryIndex, expiresAtTicks);
    }

    /// <summary>
    /// Deletes up to <see cref="SweepLimit"/> expired keys in <paramref name="batch"/>. Returns the floor to
    /// raise once the batch commits (or null) — pass it to <see cref="AfterCommit"/>.
    /// </summary>
    public static long? Sweep(CommandContext ctx, IStoreBatch batch)
    {
        if (ctx.Sweeps is not null && !ctx.Sweeps.ShouldSweep("expiry", ctx.NowTicks, SweepEvery)) return null;

        var space = BoxKeys.Space(BoxKeys.SystemBox, BoxKeys.ExpiryIndex);
        var floor = ctx.Boxes.Floor(BoxKeys.SystemBox, BoxKeys.ExpiryIndex);
        var page = ctx.Store.Scan(batch, space, BoxKeys.TicksFloor(BoxKeys.SystemBox, BoxKeys.ExpiryIndex, floor), SweepLimit);
        var reachedLive = page.Count < SweepLimit;
        long last = floor;
        foreach (var (indexKey, _) in page)
        {
            var expires = BoxKeys.TicksAt(indexKey);
            if (expires > ctx.NowTicks) { reachedLive = true; break; }
            last = expires;
            ctx.Store.Delete(batch, indexKey);
            var familyKey = indexKey[13..];
            // Delete the key only if it is still the expired write — a key rewritten later with a new
            // expiry has its own, later index entry and stays.
            var framed = ctx.Store.Read(batch, familyKey);
            if (framed is not null && ExpiryFraming.TryReadExpiry(framed) is { } at && at <= ctx.NowTicks)
                ctx.Store.Delete(batch, familyKey);
        }
        return reachedLive ? ctx.NowTicks : last;
    }

    public static void AfterCommit(CommandContext ctx, long? floor)
    {
        if (floor is { } f) ctx.Boxes.Raise(BoxKeys.SystemBox, BoxKeys.ExpiryIndex, f);
    }

    private static byte[] IndexKey(long expiresAtTicks, byte[] familyKey)
        => [.. BoxKeys.TicksFloor(BoxKeys.SystemBox, BoxKeys.ExpiryIndex, expiresAtTicks), .. familyKey];
}
