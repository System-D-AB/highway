using System.Text;
using Highway.Server.Resp.Vendored;

namespace Highway.Server.Commands.Runtime;

/// <summary>
/// Builds a RESP2 reply as bytes — the engine-free replacement for Garnet's
/// <c>MemoryResult&lt;byte&gt;</c> plus the <c>Write*</c> family (039 T1, R1). Every method
/// emits the <b>byte-identical</b> shape the Garnet command produced, so a ported command's
/// reply is indistinguishable on the wire (037 R1.2). The shapes were captured from the
/// current commands (inventory): simple string, error, integer, bulk-string array, null
/// array, three-integer array, flat field array, pair array, empty array.
///
/// <para><b>Vendored formatter (040 T3).</b> The actual RESP bytes are written by the vendored
/// <see cref="RespWriteUtils"/> (037 D4 — copy the writer, own the reader), into a stack- or
/// pool-backed buffer sized up front. This is the one Garnet-derived code path; the reply
/// <i>shapes</i> are still Highway's. A writer holds at most one reply; the last <c>Write*</c>
/// call wins. 040's handler takes <see cref="ToArray"/> and frames it onto the socket; 039's
/// tests decode it directly.</para>
/// </summary>
internal sealed unsafe class RespWriter
{
    private byte[] _reply = [];

    /// <summary>True once any reply has been written.</summary>
    public bool HasReply { get; private set; }

    /// <summary>The reply bytes, or an empty array if nothing was written.</summary>
    public byte[] ToArray() => _reply;

    private void Capture(byte[] buffer, int written)
    {
        var exact = new byte[written];
        Array.Copy(buffer, exact, written);
        _reply = exact;
        HasReply = true;
    }

    /// <summary>Simple string: <c>+&lt;s&gt;\r\n</c>. Used for "OK", "REGISTER".</summary>
    public void SimpleString(string s)
    {
        var body = Encoding.ASCII.GetBytes(s);
        var buffer = new byte[1 + body.Length + 2];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteSimpleString(body, ref curr, ptr + buffer.Length);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>Error: <c>-&lt;msg&gt;\r\n</c>. All Highway errors are <c>ERR HW_{CODE} {detail}</c>.</summary>
    public void Error(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var buffer = new byte[1 + body.Length + 2];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteError(body, ref curr, ptr + buffer.Length);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>Integer: <c>:&lt;n&gt;\r\n</c>.</summary>
    public void Integer(long value)
    {
        var buffer = new byte[24];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteInt64(value, ref curr, ptr + buffer.Length);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>Null array: exactly <c>*-1\r\n</c> — the empty-claim reply (distinct from a null bulk string).</summary>
    public void NullArray()
    {
        var buffer = new byte[5];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteNullArray(ref curr, ptr + buffer.Length);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>Empty array: <c>*0\r\n</c>.</summary>
    public void EmptyArray()
    {
        var buffer = new byte[4];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteEmptyArray(ref curr, ptr + buffer.Length);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>Three-integer array: <c>*3\r\n:&lt;a&gt;\r\n:&lt;b&gt;\r\n:&lt;c&gt;\r\n</c>.</summary>
    public void ThreeIntegers(long a, long b, long c)
    {
        var buffer = new byte[4 + 3 * 24];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            var end = ptr + buffer.Length;
            RespWriteUtils.TryWriteArrayLength(3, ref curr, end);
            RespWriteUtils.TryWriteInt64(a, ref curr, end);
            RespWriteUtils.TryWriteInt64(b, ref curr, end);
            RespWriteUtils.TryWriteInt64(c, ref curr, end);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>
    /// Array of bulk strings: <c>*&lt;n&gt;\r\n</c> then each as <c>$&lt;len&gt;\r\n&lt;bytes&gt;\r\n</c>.
    /// Used for HW.DEQUEUE / HW.QCLAIM's <c>[requestId, payload]</c>.
    /// </summary>
    public void BulkStringArray(params byte[][] values)
    {
        var size = ArrayHeaderSize(values.Length);
        foreach (var v in values) size += BulkStringSize(v.Length);

        var buffer = new byte[size];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            var end = ptr + buffer.Length;
            RespWriteUtils.TryWriteArrayLength(values.Length, ref curr, end);
            foreach (var v in values)
                RespWriteUtils.TryWriteBulkString(v, ref curr, end);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>
    /// Flat array of alternating name/value bulk strings: <c>*&lt;2N&gt;\r\n</c> then each
    /// field and value as a bulk string. Used by HW.STATS.
    /// </summary>
    public void FieldArray(IReadOnlyList<(string Name, string Value)> fields)
    {
        var encoded = new List<byte[]>(fields.Count * 2);
        foreach (var (name, value) in fields)
        {
            encoded.Add(Encoding.UTF8.GetBytes(name));
            encoded.Add(Encoding.UTF8.GetBytes(value));
        }

        var size = ArrayHeaderSize(encoded.Count);
        foreach (var e in encoded) size += BulkStringSize(e.Length);

        var buffer = new byte[size];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            var end = ptr + buffer.Length;
            RespWriteUtils.TryWriteArrayLength(encoded.Count, ref curr, end);
            foreach (var e in encoded)
                RespWriteUtils.TryWriteBulkString(e, ref curr, end);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>
    /// Array of nested arrays (rows), each row a flat array of bulk-string fields. Used by
    /// HW.DISCOVER (rows of <c>[nodeId, age]</c>), HW.REPLAY (event field arrays), HW.DLQ
    /// (dead-letter field arrays), HW.JOB LIST (schedule rows).
    /// </summary>
    public void ArrayOfArrays(IReadOnlyList<IReadOnlyList<byte[]>> rows)
    {
        var size = ArrayHeaderSize(rows.Count);
        foreach (var row in rows)
        {
            size += ArrayHeaderSize(row.Count);
            foreach (var field in row) size += BulkStringSize(field.Length);
        }

        var buffer = new byte[size];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            var end = ptr + buffer.Length;
            RespWriteUtils.TryWriteArrayLength(rows.Count, ref curr, end);
            foreach (var row in rows)
            {
                RespWriteUtils.TryWriteArrayLength(row.Count, ref curr, end);
                foreach (var field in row)
                    RespWriteUtils.TryWriteBulkString(field, ref curr, end);
            }
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>
    /// A pub/sub confirmation frame: <c>*3\r\n$&lt;kindLen&gt;\r\n&lt;kind&gt;\r\n$&lt;chLen&gt;\r\n&lt;channel&gt;\r\n:&lt;count&gt;\r\n</c>
    /// — the shape RESP2 uses for <c>subscribe</c>/<c>unsubscribe</c> replies. <paramref name="kind"/>
    /// is <c>"subscribe"</c> or <c>"unsubscribe"</c>; <paramref name="count"/> is the connection's
    /// remaining subscription count.
    /// </summary>
    public void SubscribeConfirmation(string kind, byte[] channel, long count)
    {
        var kindBytes = Encoding.ASCII.GetBytes(kind);
        var size = ArrayHeaderSize(3) + BulkStringSize(kindBytes.Length) + BulkStringSize(channel.Length) + 24;
        var buffer = new byte[size];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            var end = ptr + buffer.Length;
            RespWriteUtils.TryWriteArrayLength(3, ref curr, end);
            RespWriteUtils.TryWriteBulkString(kindBytes, ref curr, end);
            RespWriteUtils.TryWriteBulkString(channel, ref curr, end);
            RespWriteUtils.TryWriteInt64(count, ref curr, end);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>
    /// A pub/sub push message frame: <c>*3\r\n$7\r\nmessage\r\n$&lt;chLen&gt;\r\n&lt;channel&gt;\r\n$&lt;payloadLen&gt;\r\n&lt;payload&gt;\r\n</c>
    /// — what a subscribed connection receives when a doorbell fires (040 R5). The registry writes
    /// this straight to the subscriber's transport.
    /// </summary>
    public void PushMessage(byte[] channel, byte[] payload)
    {
        var kind = "message"u8.ToArray();
        var size = ArrayHeaderSize(3) + BulkStringSize(kind.Length) + BulkStringSize(channel.Length) + BulkStringSize(payload.Length);
        var buffer = new byte[size];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            var end = ptr + buffer.Length;
            RespWriteUtils.TryWriteArrayLength(3, ref curr, end);
            RespWriteUtils.TryWriteBulkString(kind, ref curr, end);
            RespWriteUtils.TryWriteBulkString(channel, ref curr, end);
            RespWriteUtils.TryWriteBulkString(payload, ref curr, end);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    /// <summary>A single bulk string: <c>$&lt;len&gt;\r\n&lt;bytes&gt;\r\n</c> (e.g. ECHO's reply, a null bulk when <paramref name="value"/> is null).</summary>
    public void BulkString(byte[]? value)
    {
        if (value is null)
        {
            var nb = "$-1\r\n"u8.ToArray();
            _reply = nb;
            HasReply = true;
            return;
        }
        var buffer = new byte[BulkStringSize(value.Length)];
        fixed (byte* ptr = buffer)
        {
            var curr = ptr;
            RespWriteUtils.TryWriteBulkString(value, ref curr, ptr + buffer.Length);
            Capture(buffer, (int)(curr - ptr));
        }
    }

    // ---- size estimation (upper bounds; the vendored writer emits exact digits) ----

    private static int ArrayHeaderSize(int count) => 1 + Digits(count) + 2;
    private static int BulkStringSize(int len) => 1 + Digits(len) + 2 + len + 2;
    private static int Digits(int n)
    {
        if (n == 0) return 1;
        var d = n < 0 ? 1 : 0;
        for (var v = n; v != 0; v /= 10) d++;
        return d;
    }
}
