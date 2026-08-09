using FluentAssertions;
using Highway.Server.Internal;
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
/// <para>This test fails when <see cref="HighwayKeys"/> gains a helper. That is deliberate and
/// is the entire mechanism: whoever adds it has to say which column it belongs in.</para>
/// </summary>
public class BoundedStructureTests
{
    /// <summary>
    /// Every key-shape Highway creates, and what bounds it. Adding a row is how a new structure
    /// gets accepted; the compiler and the count check below make skipping it hard.
    /// </summary>
    private static readonly (string Shape, string BoundedBy)[] Structures =
    [
        // --- growth driven by TRAFFIC: these must have a real cap ---
        ("hw:q:{queue}:q",                  "MaxQueueBytes (016) — covers group queues too, since 018"),
        ("hw:svc:{service}:q",              "MaxDeliveryAttempts + dead-lettering (013); an RPC caller applies backpressure by waiting"),
        ("hw:q:{queue}:dlq",                "MaxDeadLetterEntries (013)"),
        ("hw:svc:{service}:dlq",            "MaxDeadLetterEntries (013)"),
        ("hw:q:{queue}:delayed",            "bounded with its queue — a delayed message is a queued one that has not arrived"),
        ("hw:rep:{requestId}",              "ReplySlotTtl (005) — expires whether or not anyone collects it"),

        // --- growth driven by TOPOLOGY: bounded by node and name count, not by traffic ---
        ("hw:q:{queue}:proc:{nodeId}",      "EXEMPT: bounded by WorkerConcurrency x nodes, and drained by the lease sweep"),
        ("hw:svc:{service}:proc:{nodeId}",  "EXEMPT: as above"),
        ("hw:q:{queue}:nodes",              "EXEMPT: one entry per worker node; pruned on departure (006)"),
        ("hw:svc:{service}:nodes",          "EXEMPT: as above"),
        ("hw:q:{queue}:nodelist",           "EXEMPT: main-store mirror of :nodes (004.1)"),
        ("hw:svc:{service}:nodelist",       "EXEMPT: as above"),
        ("hw:ch:{channel}:groups",          "EXEMPT: one entry per subscriber group; removed by HW.UNSUBSCRIBE"),
        ("hw:ch:{channel}:grplist",         "EXEMPT: main-store mirror of :groups (004.1)"),
        ("hw:reg:node:{nodeId}",            "EXEMPT: one per node; expired by heartbeat timeout (006)"),
        ("hw:reg:svc:{service}",            "EXEMPT: one per service name"),

        // --- scalars: fixed size by construction ---
        ("hw:q:{queue}:bytes",              "SCALAR: one integer, deleted when it reaches zero"),
        ("hw:ch:{channel}:seq",             "SCALAR: one integer"),

        // --- not storage at all ---
        ("hw:door:q:{queue}",               "NOT STORED: a pub/sub doorbell channel, never persisted"),
        ("hw:door:svc:{service}",           "NOT STORED: as above"),
    ];

    /// <summary>
    /// The guard. <see cref="HighwayKeys"/>' public key helpers must match the table above, so a
    /// new structure cannot be added without someone stating what bounds it.
    /// </summary>
    [Fact]
    public void EveryKeyShape_IsAccountedFor()
    {
        var helpers = typeof(HighwayKeys)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && m.GetParameters().Length > 0)
            .Select(m => m.Name)
            .Distinct()
            .ToArray();

        helpers.Should().HaveCount(Structures.Length,
            "every key shape must appear in the Structures table with what bounds it. If this " +
            "fails because you added a key helper: add its row. That is the point of the test — " +
            "C4.4 went unmet for three features because nothing forced this question to be asked.");
    }

    [Fact]
    public void NoStructureIsUnaccountedOrSilentlyUnbounded()
    {
        foreach (var (shape, boundedBy) in Structures)
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

        foreach (var (shape, boundedBy) in trafficDriven)
            boundedBy.Should().MatchRegex("Max|Ttl|bounded with",
                $"{shape} grows with traffic, so it needs a named limit rather than an argument");
    }
}
