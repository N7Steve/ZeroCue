using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCue.DataProbe.Services
{
    public class ScufAckReader
    {
        private const int AckWaitTimeoutMs = 1500;
        private const int AckReadTimeoutMs = 90;

        private const int StaleDrainReadTimeoutMs = 2;
        private const int MaxStaleFramesPerCommand = 8;

        private readonly IScufRawTransport _transport;
        private readonly Action<byte[], int>? _nonAckFrameObserver;

        /// <param name="nonAckFrameObserver">
        /// Receives frames read while waiting for an ACK that are not ACKs. On the wired
        /// Ep82 pipe these are G-key and software-paddle input; dropping them lost
        /// releases and left mapped outputs held.
        /// </param>
        public ScufAckReader(IScufRawTransport transport, Action<byte[], int>? nonAckFrameObserver = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _nonAckFrameObserver = nonAckFrameObserver;
        }

        /// <summary>
        /// Discards ACKs left over from earlier timed-out commands so they cannot be
        /// matched to the next command with the same opcode (which shifted every
        /// following response by one).
        /// </summary>
        private async Task DrainStaleFramesAsync(byte[] buffer, CancellationToken ct)
        {
            int staleAcks = 0;
            for (int i = 0; i < MaxStaleFramesPerCommand; i++)
            {
                var (success, bytesRead) = await _transport.ReadReportAsync(buffer, StaleDrainReadTimeoutMs, ct);
                if (!success)
                {
                    break;
                }

                if (bytesRead >= 4 && buffer[0] == 0x01)
                {
                    staleAcks++;
                }
                else
                {
                    ForwardNonAck(buffer, bytesRead);
                }
            }

            if (staleAcks > 0)
            {
                _transport.LogTransport($"Discarded {staleAcks} stale ACK frame(s) before sending.");
            }
        }

        private void ForwardNonAck(byte[] buffer, int bytesRead)
        {
            var observer = _nonAckFrameObserver;
            if (observer == null || bytesRead <= 0)
            {
                return;
            }

            try
            {
                var frame = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, frame, 0, bytesRead);
                observer(frame, bytesRead);
            }
            catch (Exception ex)
            {
                _transport.LogTransport($"Non-ACK frame observer failed: {ex.Message}");
            }
        }

        public async Task SendCommandAndCheckStatusAsync(byte[] report, byte expectedOpcode, CancellationToken ct, bool strictStatus = true)
        {
            byte status = await SendCommandAndWaitAckAsync(report, 0x00, expectedOpcode, ct, strictStatus);
            if (strictStatus && status != 0x00)
            {
                throw new Exception($"ACK Error status 0x{status:X2} para Opcode 0x{expectedOpcode:X2}");
            }
        }

        public async Task<byte> SendCommandAndWaitAckAsync(byte[] report, byte expectedOpcode, CancellationToken ct, bool strictStatus = true)
        {
            return await SendCommandAndWaitAckAsync(report, 0x00, expectedOpcode, ct, strictStatus);
        }

        public async Task<byte> SendCommandAndWaitAckAsync(byte[] report, byte expectedAckChannel, byte expectedOpcode, CancellationToken ct, bool strictStatus = true)
        {
            _transport.LogTransport($"--- ENVIANDO COMANDO (AckChannel Esperado: 0x{expectedAckChannel:X2}, Opcode Esperado: 0x{expectedOpcode:X2}) ---");
            _transport.LogTransport($"OUT [Ep02] Req: 64 bytes | Payload: {BitConverter.ToString(report).Replace("-", " ")}");

            var readBuffer = new byte[64];
            await DrainStaleFramesAsync(readBuffer, ct);
            await _transport.WriteReportAsync(report, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(AckWaitTimeoutMs);

            try
            {
                var ignoredFrames = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    var (success, bytesRead) = await _transport.ReadReportAsync(readBuffer, AckReadTimeoutMs, ct);
                    if (success && bytesRead >= 4)
                    {
                        if (IsAckFor(readBuffer, expectedAckChannel, expectedOpcode))
                        {
                            if (ignoredFrames > 0)
                            {
                                _transport.LogTransport($"ACK encontrado tras ignorar {ignoredFrames} frame(s) no coincidentes.");
                            }

                            _transport.LogTransport($"IN  [Ep82] ACK recibido: {BitConverter.ToString(readBuffer, 0, bytesRead).Replace("-", " ")}");
                            byte status = readBuffer[3];
                            _transport.LogTransport($"Status Byte (ack[3]): 0x{status:X2}");
                            if (status == 0x00)
                            {
                                _transport.LogTransport($"[SUCCESS] ACK Status OK para Opcode 0x{expectedOpcode:X2}");
                            }
                            else if (!strictStatus)
                            {
                                _transport.LogTransport($"[WARN] ACK Status 0x{status:X2} para Opcode 0x{expectedOpcode:X2} (Tolerado por strictStatus=false)");
                            }
                            else
                            {
                                _transport.LogTransport($"[FAIL] ACK Status Error (0x{status:X2}) para Opcode 0x{expectedOpcode:X2}.");
                            }
                            await Task.Delay(5, ct); // Pequeño delay de cortesía al MCU
                            return status;
                        }

                        ignoredFrames++;
                        if (readBuffer[0] != 0x01)
                        {
                            ForwardNonAck(readBuffer, bytesRead);
                        }

                        if (ignoredFrames <= 3 || ignoredFrames % 25 == 0)
                        {
                            _transport.LogTransport($"ACK ignorado count={ignoredFrames} firstByte=0x{readBuffer[0]:X2} esperando canal=0x{expectedAckChannel:X2} opcode=0x{expectedOpcode:X2}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancellation (disconnect/shutdown) is not a protocol failure.
                throw;
            }
            catch (OperationCanceledException)
            {
                _transport.LogTransport($"TIMEOUT esperando ACK para Opcode 0x{expectedOpcode:X2}");
                throw new TimeoutException($"Timeout esperando ACK para Opcode 0x{expectedOpcode:X2}");
            }

            _transport.LogTransport($"TIMEOUT esperando ACK para Opcode 0x{expectedOpcode:X2}");
            throw new TimeoutException($"Timeout esperando ACK para Opcode 0x{expectedOpcode:X2}");
        }

        private static bool IsAckFor(byte[] buffer, byte expectedAckChannel, byte expectedOpcode)
        {
            return buffer.Length >= 4
                && buffer[0] == 0x01
                && buffer[1] == expectedAckChannel
                && buffer[2] == expectedOpcode;
        }
    }
}
