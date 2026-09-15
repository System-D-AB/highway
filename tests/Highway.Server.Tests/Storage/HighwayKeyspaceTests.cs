using System;
using FluentAssertions;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests.Storage;

/// <summary>
/// Tests that the family key builders produce the prefix relationships the store's
/// operations depend on: a family prefix is a genuine byte prefix of every full key
/// in that family (so seek-first and DeleteRange work), and different families /
/// different names never alias.
/// </summary>
public class HighwayKeyspaceTests
{
    private static bool StartsWith(byte[] whole, byte[] prefix)
        => whole.Length >= prefix.Length && whole.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    [Fact]
    public void ListEntry_HasListPrefix()
    {
        var prefix = HighwayKeyspace.ListPrefix("invoices");
        var e0 = HighwayKeyspace.ListEntry("invoices", 0);
        var e1 = HighwayKeyspace.ListEntry("invoices", 1);

        StartsWith(e0, prefix).Should().BeTrue();
        StartsWith(e1, prefix).Should().BeTrue();
    }

    [Fact]
    public void ListEntries_SortByAscendingSeq()
    {
        var e0 = HighwayKeyspace.ListEntry("invoices", 0);
        var e1 = HighwayKeyspace.ListEntry("invoices", 1);
        var e2 = HighwayKeyspace.ListEntry("invoices", 2);

        e0.AsSpan().SequenceCompareTo(e1).Should().BeNegative();
        e1.AsSpan().SequenceCompareTo(e2).Should().BeNegative();
    }

    [Fact]
    public void ListEntry_HeadPush_SortsBelowTail()
    {
        // Signed seq: a head-push (negative) must sort below a tail-push (>= 0).
        var head = HighwayKeyspace.ListEntry("invoices", -1);
        var tail0 = HighwayKeyspace.ListEntry("invoices", 0);

        head.AsSpan().SequenceCompareTo(tail0).Should().BeNegative(
            "a redelivered-to-head entry must pop before the current head");
    }

    [Fact]
    public void SiblingNames_DoNotAlias()
    {
        // "invoices" and "invoices2" are different queues; neither prefix contains the other.
        var a = HighwayKeyspace.ListPrefix("invoices");
        var b = HighwayKeyspace.ListPrefix("invoices2");

        StartsWith(b, a).Should().BeFalse(
            "the name terminator must keep 'invoices' from being a prefix of 'invoices2'");
    }

    [Fact]
    public void DifferentFamilies_SameName_Disjoint()
    {
        var list = HighwayKeyspace.ListPrefix("orders");
        var set = HighwayKeyspace.SetPrefix("orders");
        var zset = HighwayKeyspace.SortedSetPrefix("orders");
        var kv = HighwayKeyspace.Kv("orders");

        // The one-byte family tag differs, so the first byte differs.
        list[0].Should().NotBe(set[0]);
        set[0].Should().NotBe(zset[0]);
        zset[0].Should().NotBe(kv[0]);
    }

    [Fact]
    public void SortedSetMember_HasScoreBoundPrefix()
    {
        var member = HighwayKeyspace.SortedSetMember("delayed", 638_000_000_000_000_000L, "msg-1"u8);
        var bound = HighwayKeyspace.SortedSetScoreBound("delayed", 638_000_000_000_000_000L);

        StartsWith(member, bound).Should().BeTrue(
            "a member key must start with its score bound so a range seek finds it");
    }

    [Fact]
    public void SortedSetMembers_OrderByScore()
    {
        var early = HighwayKeyspace.SortedSetMember("delayed", 100, "b"u8);
        var late = HighwayKeyspace.SortedSetMember("delayed", 200, "a"u8);

        // Lower score sorts first regardless of member bytes.
        early.AsSpan().SequenceCompareTo(late).Should().BeNegative();
    }

    [Fact]
    public void SetMember_HasSetPrefix()
    {
        var prefix = HighwayKeyspace.SetPrefix("svc:orders:nodes");
        var member = HighwayKeyspace.SetMember("svc:orders:nodes", "node-A"u8);

        StartsWith(member, prefix).Should().BeTrue();
    }
}
