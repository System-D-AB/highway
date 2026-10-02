namespace Highway.Server.Internal;

/// <summary>
/// Doorbell channel names — the only Highway names still visible on the wire (RESP pub/sub channels a
/// client subscribes to). Storage keys are private to the broker since 3.0 (<c>HighwayNames</c> /
/// <c>BoxKeys</c>); the Garnet-era <c>hw:</c> key builders that used to live here were removed in 070
/// because nothing addressed those keys any more.
/// </summary>
internal static class HighwayKeys
{
    /// <summary>Rung when a message is enqueued on a queue or group queue.  hw:door:q:{queue}</summary>
    public static string QueueDoorbell(string queue) => $"hw:door:q:{queue}";

    /// <summary>Rung by HW.CALL after enqueue.  hw:door:svc:{service}</summary>
    public static string ServiceDoorbell(string service) => $"hw:door:svc:{service}";

    /// <summary>Rung by HW.REPLY after writing the reply slot.  hw:door:rep</summary>
    public const string ReplyDoorbell = "hw:door:rep";

    /// <summary>Rung when a notification is enqueued to a dead-letter feed (073).  hw:door:dead:{target}</summary>
    public static string DeadLetterDoorbell(string target) => $"hw:door:dead:{target}";
}
