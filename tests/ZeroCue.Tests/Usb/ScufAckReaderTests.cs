using ZeroCue.DataProbe.Services;
using Xunit;

namespace ZeroCue.Tests.Usb;

public class ScufAckReaderTests
{
    /// <summary>Scripted transport: frames queued before a write are "already pending", the rest arrive after it.</summary>
    private sealed class ScriptedTransport : IScufRawTransport
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly Queue<byte[]> _afterWrite = new();
        public int Writes { get; private set; }

        public void Pending(params byte[] frame) => _pending.Enqueue(Pad(frame));
        public void Reply(params byte[] frame) => _afterWrite.Enqueue(Pad(frame));

        private static byte[] Pad(byte[] frame)
        {
            var buffer = new byte[64];
            frame.CopyTo(buffer, 0);
            return buffer;
        }

        public Task WriteReportAsync(byte[] report, CancellationToken ct)
        {
            Writes++;
            while (_afterWrite.Count > 0)
            {
                _pending.Enqueue(_afterWrite.Dequeue());
            }

            return Task.CompletedTask;
        }

        public async Task<(bool Success, int BytesRead)> ReadReportAsync(byte[] buffer, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_pending.Count == 0)
            {
                await Task.Delay(1, ct);
                return (false, 0);
            }

            _pending.Dequeue().CopyTo(buffer, 0);
            return (true, 64);
        }

        public Task DrainAsync() => Task.CompletedTask;
        public void LogTransport(string message) { }
    }

    [Fact]
    public async Task Stale_ack_from_a_previous_command_is_not_matched_to_the_next_one()
    {
        var transport = new ScriptedTransport();
        transport.Pending(0x01, 0x00, 0x01, 0x05);   // late ACK of an earlier timed-out command (status 5)
        transport.Reply(0x01, 0x00, 0x01, 0x00);     // the real ACK for this command
        var reader = new ScufAckReader(transport);

        var status = await reader.SendCommandAndWaitAckAsync(new byte[64], 0x01, CancellationToken.None);

        Assert.Equal(0x00, status);
    }

    [Fact]
    public async Task Input_frames_read_while_waiting_are_forwarded_not_dropped()
    {
        var transport = new ScriptedTransport();
        transport.Reply(0x11, 0x04);                 // G-key press arriving during the command
        transport.Reply(0x11, 0x00);                 // ...and its release
        transport.Reply(0x01, 0x00, 0x12, 0x00);
        var forwarded = new List<byte[]>();
        var reader = new ScufAckReader(transport, (frame, length) => forwarded.Add(frame));

        await reader.SendCommandAndWaitAckAsync(new byte[64], 0x12, CancellationToken.None);

        Assert.Equal(2, forwarded.Count);
        Assert.Equal(new byte[] { 0x11, 0x04 }, forwarded[0][..2]);
        Assert.Equal(new byte[] { 0x11, 0x00 }, forwarded[1][..2]);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_protocol_timeout()
    {
        var reader = new ScufAckReader(new ScriptedTransport());
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.SendCommandAndWaitAckAsync(new byte[64], 0x01, cts.Token));
    }

    [Fact]
    public async Task Missing_ack_times_out_with_a_timeout_exception()
    {
        var reader = new ScufAckReader(new ScriptedTransport());

        await Assert.ThrowsAsync<TimeoutException>(() =>
            reader.SendCommandAndWaitAckAsync(new byte[64], 0x01, CancellationToken.None));
    }
}
