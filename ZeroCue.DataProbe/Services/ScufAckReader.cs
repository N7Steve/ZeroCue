using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCue.DataProbe.Services
{
    public class ScufAckReader
    {
        private const int AckWaitTimeoutMs = 1500;
        private const int AckReadTimeoutMs = 90;

        private readonly IScufRawTransport _transport;

        public ScufAckReader(IScufRawTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
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

            await _transport.WriteReportAsync(report, ct);

            var readBuffer = new byte[64];

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
                        if (ignoredFrames <= 3 || ignoredFrames % 25 == 0)
                        {
                            _transport.LogTransport($"ACK ignorado count={ignoredFrames} firstByte=0x{readBuffer[0]:X2} esperando canal=0x{expectedAckChannel:X2} opcode=0x{expectedOpcode:X2}");
                        }

                        await Task.Delay(1, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _transport.LogTransport($"TIMEOUT o Cancelación esperando ACK para Opcode 0x{expectedOpcode:X2}");
                throw new Exception($"Timeout esperando ACK para Opcode 0x{expectedOpcode:X2}");
            }

            _transport.LogTransport($"TIMEOUT esperando ACK para Opcode 0x{expectedOpcode:X2}");
            throw new Exception($"Timeout esperando ACK para Opcode 0x{expectedOpcode:X2}");
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
