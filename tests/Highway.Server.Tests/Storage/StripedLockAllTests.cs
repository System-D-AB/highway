using FluentAssertions;
using Highway.Server.Storage;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>070 R2-4: the every-stripe fallback a re-planning session or purge takes on its last attempt.</summary>
public class StripedLockAllTests
{
    [Fact]
    public async Task LockAll_WaitsForAHeldStripe_AndThenExcludesEveryName()
    {
        using var locks = new StripedLock(16);
        Task<IDisposable> all;
        using (locks.Lock("some.queue"))
        {
            all = Task.Run(locks.LockAll);
            (await Task.WhenAny(all, Task.Delay(200))).Should().NotBeSameAs(all, "one stripe is held");
        }
        using (await all)
        {
            var other = Task.Run(() => locks.Lock("anything"));
            (await Task.WhenAny(other, Task.Delay(200))).Should().NotBeSameAs(other, "every stripe is held");
            _ = other.ContinueWith(t => t.Result.Dispose(), TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task LockAll_TakesStripesInTheOrderLockManyDoes()
    {
        // LockMany holds stripe 1 and is about to wait for stripe 2 when LockAll starts. In ascending order LockAll
        // stops at stripe 1 holding only stripe 0, so LockMany gets 2 and both finish; any other order lets LockAll take
        // stripe 2 first and the two wait on each other for ever.
        using var locks = new StripedLock(4);
        string On(int stripe) => Enumerable.Range(0, 1000).Select(i => $"n{i}").First(n => locks.StripeOf(n) == stripe);
        using var allReachedStripe1 = new ManualResetEventSlim();
        var manyThread = -1;
        var started = 0;
        Task<IDisposable>? all = null;
        locks.StripeWaiting = stripe =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref manyThread))
            {
                if (stripe == 2 && Interlocked.Exchange(ref started, 1) == 0)
                {
                    all = Task.Run(locks.LockAll);
                    allReachedStripe1.Wait(TimeSpan.FromSeconds(5));
                }
            }
            else if (stripe == 1) allReachedStripe1.Set();
        };

        var many = Task.Run(() =>
        {
            Volatile.Write(ref manyThread, Environment.CurrentManagedThreadId);
            using (locks.LockMany([On(1), On(2)])) { }
        });

        (await Task.WhenAny(many, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(many, "LockMany is not blocked by LockAll");
        all.Should().NotBeNull();
        (await Task.WhenAny(all!, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(all, "then LockAll completes");
        locks.StripeWaiting = null;
        (await all!).Dispose();
    }

    [Fact]
    public async Task LockAll_AndLockMany_NeverDeadlock()
    {
        using var locks = new StripedLock(8);
        var names = Enumerable.Range(0, 32).Select(i => $"n{i}").ToArray();
        var work = Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            using (i % 10 == 0 ? locks.LockAll() : locks.LockMany(names.Skip(i % 20).Take(5))) { }
        }));

        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(all, "both take stripes in ascending order");
    }
}
