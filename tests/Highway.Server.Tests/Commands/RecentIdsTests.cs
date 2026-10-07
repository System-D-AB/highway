using FluentAssertions;
using Highway.Server.Commands.Runtime;
using Xunit;

namespace Highway.Server.Tests.Commands;

public class RecentIdsTests
{
    private static readonly long T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>
    /// The sequence every command runs: look the id up; a hit is a duplicate; otherwise (the write commits and)
    /// the id is admitted. True when the id was new.
    /// </summary>
    private static bool Offer<TValue>(RecentIds<TValue> ids, string scope, string id, long nowTicks, TValue value = default!)
    {
        if (ids.TryGet(scope, id, nowTicks, out _))
        {
            ids.RecordDuplicate();
            return false;
        }
        ids.Admit(scope, id, nowTicks, value);
        return true;
    }

    [Fact]
    public void Clear_ForgetsEveryId()
    {
        var ids = new RecentIds<byte>(TimeSpan.FromMinutes(10));
        Offer(ids, "q", "a", T0).Should().BeTrue();
        Offer(ids, "q", "b", T0).Should().BeTrue();

        ids.Clear();

        ids.Count.Should().Be(0);
        Offer(ids, "q", "a", T0 + 1).Should().BeTrue("a cleared id is new again");
    }

    [Fact]
    public void AdmittedId_IsDetectedAsDuplicateWithinWindow()
    {
        var ids = new RecentIds<long>(TimeSpan.FromMinutes(10));
        Offer(ids, "chan", "id-1", T0, 42).Should().BeTrue();
        Offer(ids, "chan", "id-1", T0 + TimeSpan.FromMinutes(1).Ticks, 42).Should().BeFalse();

        ids.TryGet("chan", "id-1", T0 + TimeSpan.FromMinutes(2).Ticks, out var val).Should().BeTrue();
        val.Should().Be(42);
    }

    [Fact]
    public void AfterWindow_IdCanBeAdmittedAgain()
    {
        var ids = new RecentIds<long>(TimeSpan.FromMinutes(10));
        Offer(ids, "chan", "id-1", T0, 42).Should().BeTrue();
        ids.TryGet("chan", "id-1", T0 + TimeSpan.FromMinutes(11).Ticks, out _).Should().BeFalse();
        Offer(ids, "chan", "id-1", T0 + TimeSpan.FromMinutes(11).Ticks, 99).Should().BeTrue();

        ids.TryGet("chan", "id-1", T0 + TimeSpan.FromMinutes(12).Ticks, out var val).Should().BeTrue();
        val.Should().Be(99);
    }

    [Fact]
    public void Capacity_BoundsMemory_EvictingOldestFirst()
    {
        var ids = new RecentIds<string>(TimeSpan.FromHours(1), capacity: 3);
        Offer(ids, "s", "a", 1, "vA").Should().BeTrue();
        Offer(ids, "s", "b", 2, "vB").Should().BeTrue();
        Offer(ids, "s", "c", 3, "vC").Should().BeTrue();
        Offer(ids, "s", "d", 4, "vD").Should().BeTrue();

        ids.Count.Should().Be(3);
        // 'a' was evicted as oldest
        ids.TryGet("s", "a", 5, out _).Should().BeFalse();
        ids.TryGet("s", "b", 5, out var vb).Should().BeTrue();
        vb.Should().Be("vB");
        ids.TryGet("s", "c", 5, out var vc).Should().BeTrue();
        vc.Should().Be("vC");
        ids.TryGet("s", "d", 5, out var vd).Should().BeTrue();
        vd.Should().Be("vD");
    }

    [Fact]
    public void Admit_UnconditionallyStoresOrUpdatesValue()
    {
        var ids = new RecentIds<int>(TimeSpan.FromMinutes(5));
        ids.Admit("scope", "key-1", T0, 10);
        ids.TryGet("scope", "key-1", T0 + 10, out var v1).Should().BeTrue();
        v1.Should().Be(10);

        ids.Admit("scope", "key-1", T0 + 20, 20);
        ids.TryGet("scope", "key-1", T0 + 30, out var v2).Should().BeTrue();
        v2.Should().Be(20);
    }

    [Fact]
    public void Forget_RemovesId_AllowingReadmission()
    {
        var ids = new RecentIds<byte>(TimeSpan.FromMinutes(10));
        Offer(ids, "scope", "id-1", T0).Should().BeTrue();
        ids.Forget("scope", "id-1");
        Offer(ids, "scope", "id-1", T0 + 10).Should().BeTrue();
    }

    [Fact]
    public void WindowZeroOrNegative_DisablesFiltering()
    {
        var ids = new RecentIds<int>(TimeSpan.Zero);
        Offer(ids, "scope", "id-1", T0, 123).Should().BeTrue();
        Offer(ids, "scope", "id-1", T0, 456).Should().BeTrue("window 0 always admits");
        ids.TryGet("scope", "id-1", T0, out _).Should().BeFalse("window 0 does not store");
    }

    [Fact]
    public void Scopes_AreIsolated()
    {
        var ids = new RecentIds<int>(TimeSpan.FromMinutes(10));
        Offer(ids, "scope1", "id-1", T0, 1).Should().BeTrue();
        Offer(ids, "scope2", "id-1", T0, 2).Should().BeTrue();

        ids.TryGet("scope1", "id-1", T0, out var v1).Should().BeTrue();
        v1.Should().Be(1);
        ids.TryGet("scope2", "id-1", T0, out var v2).Should().BeTrue();
        v2.Should().Be(2);
    }
}
