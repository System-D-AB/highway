using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Highway.Server.Resp;

/// <summary>
/// The Kestrel <see cref="ConnectionHandler"/> that is the socket Garnet used to be (037 D3, R6.3).
/// Per connection it runs a read loop: pull bytes off the pipe, parse whole frames with
/// <see cref="RespReader"/> (need-more on partials, never a throw), hand each to a
/// <see cref="RespSession"/>, and write the reply frames back. Commands cannot tell a socket
/// delivered them — the session and dispatcher are transport-free (037 R10.1).
///
/// <para><b>Graceful shutdown (R3.4):</b> the connection-close token flows into the loop, so an
/// in-flight read unblocks on shutdown and the loop exits cleanly; a command already dispatching
/// runs to completion (commands are synchronous and short). Oversized or malformed frames close
/// the connection with a legible RESP error rather than corrupting the stream.</para>
/// </summary>
internal sealed class RespConnectionHandler : ConnectionHandler
{
    private readonly IRespServerHost _host;
    private readonly ILogger<RespConnectionHandler>? _logger;

    public RespConnectionHandler(IRespServerHost host, ILogger<RespConnectionHandler>? logger = null)
    {
        _host = host;
        _logger = logger;
    }

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var input = connection.Transport.Input;
        var output = connection.Transport.Output;
        var maxFrame = _host.MaxFrameBytes;

        // One session + one subscription registration per connection. The registry lets a
        // server-internal doorbell publish push a frame straight to this connection's output.
        var subscriber = _host.CreateSubscriber(connection.ConnectionId, output);
        var session = new RespSession(_host.Dispatcher, _host.Authenticator, subscriber, connection.RemoteEndPoint);

        var closeToken = connection.ConnectionClosed;

        try
        {
            while (true)
            {
                ReadResult read;
                try
                {
                    read = await input.ReadAsync(closeToken);
                }
                catch (OperationCanceledException)
                {
                    break; // shutdown or connection close — leave cleanly
                }

                var buffer = read.Buffer;
                var handled = await ProcessBufferAsync(buffer, session, subscriber, output, connection, maxFrame);

                // Advance past everything we consumed; keep the unparsed remainder for the next read.
                input.AdvanceTo(handled.Consumed, buffer.End);

                if (handled.Close || read.IsCompleted)
                    break;
            }
        }
        catch (ConnectionResetException)
        {
            // The client vanished mid-stream — nothing to do; teardown below.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "RESP connection {Id} ended with an error", connection.ConnectionId);
        }
        finally
        {
            session.OnConnectionClosed();   // a counted client leaves the herd (042-1c C-T1)
            _host.RemoveSubscriber(connection.ConnectionId);
            await output.CompleteAsync();
        }
    }

    private static async Task<(SequencePosition Consumed, bool Close)> ProcessBufferAsync(
        ReadOnlySequence<byte> buffer,
        RespSession session,
        ISubscriptionSink subscriber,
        PipeWriter output,
        ConnectionContext connection,
        int maxFrame)
    {
        var consumed = buffer.Start;
        var remaining = buffer;

        while (!remaining.IsEmpty)
        {
            var status = RespReader.TryReadCommand(remaining, maxFrame, out var frame, out var frameEnd, out var error);

            if (status == RespReadStatus.Incomplete)
                break; // need more bytes; keep `consumed` at the last whole frame

            if (status == RespReadStatus.ProtocolError)
            {
                // A malformed frame poisons the stream position — reply with the error and close.
                await WriteAsync(output, System.Text.Encoding.UTF8.GetBytes($"-ERR protocol error: {error}\r\n"));
                return (consumed, Close: true);
            }

            var result = session.Handle(frame);
            foreach (var reply in result.Replies)
                if (reply.Length > 0)
                    await WriteAsync(output, reply);

            consumed = frameEnd;
            remaining = remaining.Slice(frameEnd);

            if (result.Close)
                return (consumed, Close: true);
        }

        return (consumed, Close: false);
    }

    private static async Task WriteAsync(PipeWriter output, byte[] bytes)
    {
        output.Write(bytes);
        await output.FlushAsync();
    }
}
