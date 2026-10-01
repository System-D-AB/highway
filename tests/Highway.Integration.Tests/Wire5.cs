using System.Globalization;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Highway.Integration.Tests;

/// <summary>
/// Protocol 5.0 over a raw StackExchange.Redis connection, for tests that drive the wire directly
/// (feature 068). Every Highway-owned structure is reached through an HW.* command:
/// <list type="bullet">
///   <item>enqueue — <c>HW.CALL</c>, <c>HW.SEND</c> (was HW.QSEND), <c>HW.PUBLISH</c>;</item>
///   <item>claim — <c>HW.CLAIM SVC|Q name node</c> → rows of <c>[token, id, payload]</c> (was HW.DEQUEUE /
///     HW.QCLAIM → <c>[id, payload]</c> or nil); nothing due is an <b>empty array</b>;</item>
///   <item>settle by token — <c>HW.ACK</c> / <c>HW.TOUCH</c> / <c>HW.FAIL token type detail</c> → <c>:1</c> held, <c>:0</c> not;</item>
///   <item>session — <c>HW.SESSION node json</c> (was the heartbeat registration form, HW.SUBSCRIBE and HW.JOB SET);
///     a heartbeat for an unknown node answers <c>+SESSION</c> (was <c>+REGISTER</c>);</item>
///   <item>replies — <c>HW.RESULT requestId</c> takes the reply (was stock GET/DEL on hw:rep:*);
///     idempotency — <c>HW.IDEM BEGIN|END|ABORT</c> (was stock SET NX/GET/DEL on hw:idem:*).</item>
/// </list>
/// </summary>
internal static class Wire5
{
    /// <summary>One claimed message.</summary>
    internal readonly record struct Claim(string Token, string Id, RedisValue Payload)
    {
        public string PayloadText => Payload.ToString();
    }

    // ---- enqueue ----

    public static RedisResult Send(this IDatabase db, string queue, string id, RedisValue payload, long? atTicks = null)
        => atTicks is { } at
            ? db.Execute("HW.SEND", queue, id, payload, "AT", at.ToString(CultureInfo.InvariantCulture))
            : db.Execute("HW.SEND", queue, id, payload);

    public static Task<RedisResult> SendAsync(this IDatabase db, string queue, string id, RedisValue payload, long? atTicks = null)
        => atTicks is { } at
            ? db.ExecuteAsync("HW.SEND", queue, id, payload, "AT", at.ToString(CultureInfo.InvariantCulture))
            : db.ExecuteAsync("HW.SEND", queue, id, payload);

    // ---- claim ----

    public static Claim? ClaimQ(this IDatabase db, string queue, string node) => First(db.Execute("HW.CLAIM", "Q", queue, node));

    public static Claim? ClaimSvc(this IDatabase db, string service, string node) => First(db.Execute("HW.CLAIM", "SVC", service, node));

    public static async Task<Claim?> ClaimQAsync(this IDatabase db, string queue, string node)
        => First(await db.ExecuteAsync("HW.CLAIM", "Q", queue, node).ConfigureAwait(false));

    public static async Task<Claim?> ClaimSvcAsync(this IDatabase db, string service, string node)
        => First(await db.ExecuteAsync("HW.CLAIM", "SVC", service, node).ConfigureAwait(false));

    /// <summary>Every row of an HW.CLAIM reply (an empty list when nothing was due).</summary>
    public static List<Claim> Rows(RedisResult reply)
    {
        var rows = new List<Claim>();
        if (reply.IsNull) return rows;
        foreach (var r in (RedisResult[])reply!)
        {
            var row = (RedisResult[])r!;
            rows.Add(new Claim((string)row[0]!, (string)row[1]!, (RedisValue)row[2]));
        }
        return rows;
    }

    private static Claim? First(RedisResult reply)
    {
        var rows = Rows(reply);
        return rows.Count == 0 ? null : rows[0];
    }

    // ---- settle ----

    public static long Ack(this IDatabase db, string token) => (long)db.Execute("HW.ACK", token);

    public static async Task<long> AckAsync(this IDatabase db, string token) => (long)await db.ExecuteAsync("HW.ACK", token).ConfigureAwait(false);

    public static long Touch(this IDatabase db, string token) => (long)db.Execute("HW.TOUCH", token);

    public static long Fail(this IDatabase db, string token, string type, RedisValue detail) => (long)db.Execute("HW.FAIL", token, type, detail);

    // ---- session ----

    /// <summary>
    /// Declares a session. <paramref name="catalogJson"/> null → no registration (as with heartbeats off);
    /// <paramref name="subscriptions"/> are (channel, group); <paramref name="jobs"/> are (queue, job, expression, template).
    /// </summary>
    public static RedisResult Session(this IDatabase db, string node, string? catalogJson = null,
        IEnumerable<(string Channel, string Group)>? subscriptions = null,
        IEnumerable<(string Queue, string Job, string Expression, byte[] Template)>? jobs = null)
        => db.Execute("HW.SESSION", node, SessionJson(catalogJson, subscriptions, jobs));

    public static Task<RedisResult> SessionAsync(this IDatabase db, string node, string? catalogJson = null,
        IEnumerable<(string Channel, string Group)>? subscriptions = null,
        IEnumerable<(string Queue, string Job, string Expression, byte[] Template)>? jobs = null)
        => db.ExecuteAsync("HW.SESSION", node, SessionJson(catalogJson, subscriptions, jobs));

    /// <summary>Registers a node's catalog (the 5.0 form of the heartbeat registration form).</summary>
    public static RedisResult Register(this IDatabase db, string node, string catalogJson) => db.Session(node, catalogJson);

    /// <summary>Declares one group subscription for <paramref name="node"/> (the 5.0 form of HW.SUBSCRIBE channel group node).</summary>
    public static RedisResult Subscribe(this IDatabase db, string channel, string group, string? node = null)
        => db.Session(node ?? group, subscriptions: [(channel, group)]);

    public static Task<RedisResult> SubscribeAsync(this IDatabase db, string channel, string group, string? node = null)
        => db.SessionAsync(node ?? group, subscriptions: [(channel, group)]);

    public static string SessionJson(string? catalogJson,
        IEnumerable<(string Channel, string Group)>? subscriptions,
        IEnumerable<(string Queue, string Job, string Expression, byte[] Template)>? jobs)
    {
        var sb = new StringBuilder("{");
        if (catalogJson is not null) sb.Append("\"catalog\":").Append(catalogJson).Append(',');
        // 070: a present list is the node's complete subscription set (groups it omits are left). With no
        // subscriptions given - Register - the property is omitted, which leaves memberships unchanged.
        if (subscriptions is not null)
        {
            sb.Append("\"subscriptions\":[");
            sb.Append(string.Join(",", subscriptions.Select(s =>
                $"{{\"channel\":{JsonSerializer.Serialize(s.Channel)},\"group\":{JsonSerializer.Serialize(s.Group)}}}")));
            sb.Append("],");
        }
        sb.Append("\"jobs\":[");
        sb.Append(string.Join(",", (jobs ?? []).Select(j =>
            $"{{\"queue\":{JsonSerializer.Serialize(j.Queue)},\"job\":{JsonSerializer.Serialize(j.Job)}," +
            $"\"expression\":{JsonSerializer.Serialize(j.Expression)},\"template\":\"{Convert.ToBase64String(j.Template)}\"}}")));
        sb.Append("]}");
        return sb.ToString();
    }

    // ---- replies ----

    /// <summary>HW.RESULT: takes (reads and removes) a waiting reply; null when none is waiting.</summary>
    public static RedisValue Result(this IDatabase db, string requestId) => (RedisValue)db.Execute("HW.RESULT", requestId);

    public static async Task<RedisValue> ResultAsync(this IDatabase db, string requestId)
        => (RedisValue)await db.ExecuteAsync("HW.RESULT", requestId).ConfigureAwait(false);
}
