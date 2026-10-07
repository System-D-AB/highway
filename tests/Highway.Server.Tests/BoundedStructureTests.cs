using FluentAssertions;
using Highway.Server.Internal;
using Highway.Server.Storage.Boxes;
using Highway.Server.Storage.Layout;
using Xunit;

namespace Highway.Server.Tests;

/// <summary>
/// Feature 016 T11 / R3.4 — <b>every structure Highway creates is bounded, or exempt for a
/// stated reason.</b>
///
/// <para><b>The enumeration is the requirement, not the caps.</b> Caps can be added at any time;
/// what stops the <i>next</i> feature quietly introducing an unbounded structure is a test that
/// fails the moment one appears. C4.4 existed for three features while pub/sub group queues had
/// no bound at all, because nothing was watching.</para>
///
/// <para>This test fails when <see cref="HighwayNames"/>, <see cref="BoxKeys"/> or <see cref="HighwayKeys"/> gains a member. That is deliberate and
/// is the entire mechanism: whoever adds it has to say which column it belongs in.</para>
/// </summary>
public class BoundedStructureTests
{
    /// <summary>
    /// Every structure Highway stores, and what bounds it — keyed by the vocabulary member that names it:
    /// a <see cref="HighwayNames"/> builder (the registry, memberships, reply slots, sequences), a
    /// <see cref="BoxKeys"/> space (everything in a work box or the system box, 065), or a
    /// <see cref="HighwayKeys"/> doorbell. Retargeted in 070 from the Garnet-era <c>hw:</c> key helpers,
    /// which no longer described what is stored.
    /// </summary>
    private static readonly (string Member, string Shape, string BoundedBy)[] Structures =
    [
        // --- growth driven by TRAFFIC: these must have a real cap ---
        ("Ready",         "box/01 [seq]",               "MaxQueueBytes (016) for queues and group queues; MaxDeliveryAttempts + dead-lettering for services"),
        ("Delayed",       "box/04 [due][seq]",          "bounded with its queue — a delayed message is a queued one that has not arrived (MaxQueueBytes)"),
        ("Dead",          "box/05 [seq]",               "MaxDeadLetterEntries (013)"),
        ("ReplySlot",     "k rep:{requestId}",          "ReplySlotTtl (005) — expires whether or not anyone collects it, and is deleted (C4.10)"),
        ("ExpiryIndex",   "box0/13 [expiresAt][key]",   "bounded with its keys (ReplySlotTtl / idempotency TTL) — one entry per expiring key"),

        // --- growth driven by TOPOLOGY or in-flight work: bounded by nodes, names and concurrency ---
        ("InFlight",      "box/02 [seq]",               "EXEMPT: bounded by WorkerConcurrency x nodes; recovered by the lease sweep"),
        ("Lease",         "box/03 [claim][seq]",        "EXEMPT: one entry per in-flight claim, as above"),
        ("NodeIndex",     "box/0A [node][seq]",         "EXEMPT: one entry per in-flight claim, as above"),
        ("Workers",       "box/07 [node]",              "EXEMPT: one entry per worker node"),
        ("RpcIndex",      "box/0B [requestId]",         "EXEMPT: one entry per queued RPC request, removed with it (074)"),
        ("Schedules",     "box/08 [job]",               "EXEMPT: one per declared job (028); replaced, never grown, by each fire"),
        ("ScheduleIndex", "box/09 [nextFire][job]",     "EXEMPT: one per declared job, as above"),
        ("NameIndex",     "box0/01 [kind][name]",       "EXEMPT: one per service / queue / group queue name"),
        ("Meta",          "box0/02 [box]",              "EXEMPT: one per box, as above (retired boxes keep a small record)"),
        ("ChannelGroups", "s ch:{channel}:groups",      "EXEMPT: one entry per subscriber group; removed on retirement"),
        ("GroupMembers",  "s grp:{channel}@{group}:members", "EXEMPT: one entry per node backing the group (025); removed when a session stops declaring it (070) and deleted with the group"),
        ("NodeSubs",      "s reg:node:{node}:subs",     "EXEMPT: one entry per group a node declares; diffed by each session (070), deleted by BYE PURGE"),
        ("FeedMembers",   "s feed:members:{target}",    "EXEMPT: one entry per node declaring the feed (073); removed when session stops declaring it or disconnects"),
        ("NodeFeeds",     "s reg:node:{node}:feeds",    "EXEMPT: one entry per feed a node declares (073); diffed by each session, deleted by BYE PURGE"),
        ("RegistrationNodeList", "s reg:nodes",         "EXEMPT: one entry per registered node; pruned on departure (006)"),
        ("RegistrationService",  "s reg:svc:{service}", "EXEMPT: one entry per node hosting the service"),
        ("RegistrationNode",     "k reg:node:{node}",   "EXEMPT: one per node; expired by heartbeat timeout (006)"),
        ("RegistrationSeen",     "k reg:seen:{node}",   "EXEMPT: one per node, as above"),
        ("ServiceServed",        "k reg:served:{service}", "EXEMPT: one per service name ever declared (074 fail-fast); a few bytes, never deleted"),
        ("Families",      "box0/10 (the family keys)",  "EXEMPT: the container of the rows above; bounded by them"),

        // --- scalars: fixed size by construction ---
        ("Counters",      "box/06 [counter]",           "SCALAR: a fixed set of integers per box"),
        ("Allocator",     "box0/03",                    "SCALAR: one integer"),
        ("CatalogVersion","box0/04",                    "SCALAR: one integer"),
        ("Format",        "box0/05",                    "SCALAR: one integer"),
        ("ChannelSeq",    "n ch:{channel}:seq",         "SCALAR: one integer per channel"),
        ("ListSequence",  "n seq:{list}",               "SCALAR: one integer per list (store list primitive)"),

        // --- names, not stored structures ---
        ("ServiceQueue",  "svc:{service}:q",            "NOT STORED: the name a storage-format error gives a service's box"),
        ("Queue",         "q:{queue}:q",                "NOT STORED: as above, for a queue's box"),
        ("QueueDoorbell", "hw:door:q:{queue}",          "NOT STORED: a pub/sub doorbell channel, never persisted"),
        ("ServiceDoorbell","hw:door:svc:{service}",     "NOT STORED: as above"),
        ("DeadLetterDoorbell","hw:door:dead:{target}",  "NOT STORED: a dead letter feed doorbell channel, never persisted"),
        ("ReplyDoorbell", "hw:door:rep",                "NOT STORED: as above"),
    ];

    /// <summary>
    /// The guard. Every vocabulary member that can name a stored structure must have a row, and every row
    /// must name a real member — so a new structure cannot be added without someone stating what bounds it.
    /// </summary>
    [Fact]
    public void EveryKeyShape_IsAccountedFor()
    {
        const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        var members = typeof(HighwayNames).GetMethods(Flags).Where(m => m.ReturnType == typeof(string)).Select(m => m.Name)
            .Concat(typeof(HighwayNames).GetFields(Flags).Where(f => f.IsLiteral).Select(f => f.Name))
            .Concat(typeof(HighwayKeys).GetMethods(Flags).Where(m => m.ReturnType == typeof(string)).Select(m => m.Name))
            .Concat(typeof(HighwayKeys).GetFields(Flags).Where(f => f.IsLiteral).Select(f => f.Name))
            .Concat(typeof(BoxKeys).GetFields(Flags).Where(f => f.IsLiteral && f.FieldType == typeof(byte)).Select(f => f.Name))
            .ToHashSet(StringComparer.Ordinal);

        Structures.Select(s => s.Member).Should().BeEquivalentTo(members,
            "every stored structure must appear in the Structures table with what bounds it. If this " +
            "fails because you added a key builder or a box space: add its row. That is the point of the " +
            "test — C4.4 went unmet for three features because nothing forced this question to be asked.");
    }

    [Fact]
    public void NoStructureIsUnaccountedOrSilentlyUnbounded()
    {
        foreach (var (_, shape, boundedBy) in Structures)
        {
            boundedBy.Should().NotBeNullOrWhiteSpace($"{shape} must say what bounds it");

            // "TODO", "unknown" and friends are how an unbounded structure gets waved through.
            boundedBy.Should().NotContainAny("TODO", "unknown", "none", "unbounded");
        }
    }

    [Fact]
    public void TrafficDrivenStructures_HaveARealCap()
    {
        // The distinction that matters: a structure whose size follows TRAFFIC needs a cap,
        // because traffic is unbounded. One that follows TOPOLOGY is bounded by how many nodes
        // and names exist, which an operator already controls.
        var trafficDriven = Structures
            .Where(x => !x.BoundedBy.StartsWith("EXEMPT")
                     && !x.BoundedBy.StartsWith("SCALAR")
                     && !x.BoundedBy.StartsWith("NOT STORED"))
            .ToArray();

        trafficDriven.Should().NotBeEmpty();

        foreach (var (_, shape, boundedBy) in trafficDriven)
            boundedBy.Should().MatchRegex("Max|Ttl|bounded with",
                $"{shape} grows with traffic, so it needs a named limit rather than an argument");
    }
}
