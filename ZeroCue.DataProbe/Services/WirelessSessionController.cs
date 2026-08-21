using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCue.DataProbe.Services
{
    public sealed class WirelessSessionController
    {
        private readonly IScufTransport _transport;
        private readonly Action<string> _logger;
        private readonly SemaphoreSlim? _ioLock;
        private readonly int _initialHeartbeatIntervalMs;
        private readonly int _steadyHeartbeatIntervalMs;
        private readonly int _ackWaitTimeoutMs;
        private readonly int _ackReadTimeoutMs;
        private readonly int _initialFailureThreshold;
        private readonly int _steadyFailureThreshold;
        private readonly int _initialFailureThresholdWindowMs;
        private readonly int _radioHeartbeatIntervalMs;
        private readonly int _heartbeatIdleGateMs;
        private CancellationTokenSource? _heartbeatCts;
        private Task? _heartbeatTask;
        private int _heartbeatFailures;
        private int _sessionLostRaised;
        private long _lastControllerActivityTicks;
        private long _lastRecentActivitySkipLogTicks;

        public WirelessSessionController(
            IScufTransport transport,
            Action<string> logger,
            SemaphoreSlim? ioLock = null,
            int initialHeartbeatIntervalMs = 1000,
            int steadyHeartbeatIntervalMs = 1000,
            int ackWaitTimeoutMs = 1500,
            int ackReadTimeoutMs = 90,
            int initialFailureThreshold = 6,
            int steadyFailureThreshold = 3,
            int initialFailureThresholdWindowMs = 15_000,
            int radioHeartbeatIntervalMs = 45_000,
            int heartbeatIdleGateMs = 0)
        {
            _transport = transport;
            _logger = logger;
            _ioLock = ioLock;
            _initialHeartbeatIntervalMs = initialHeartbeatIntervalMs;
            _steadyHeartbeatIntervalMs = steadyHeartbeatIntervalMs;
            _ackWaitTimeoutMs = ackWaitTimeoutMs;
            _ackReadTimeoutMs = ackReadTimeoutMs;
            _initialFailureThreshold = Math.Max(steadyFailureThreshold, initialFailureThreshold);
            _steadyFailureThreshold = Math.Max(1, steadyFailureThreshold);
            _initialFailureThresholdWindowMs = Math.Max(0, initialFailureThresholdWindowMs);
            _radioHeartbeatIntervalMs = Math.Max(0, radioHeartbeatIntervalMs);
            _heartbeatIdleGateMs = Math.Max(0, heartbeatIdleGateMs);
        }

        public bool IsSessionUnstable { get; private set; }
        public event Action? SessionLost;

        public void NotifyControllerActivity()
        {
            Interlocked.Exchange(ref _lastControllerActivityTicks, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _heartbeatFailures, 0);
        }

        public Task StartHeartbeatAsync(CancellationToken ct)
        {
            if (_heartbeatTask != null)
            {
                return Task.CompletedTask;
            }

            _heartbeatFailures = 0;
            _sessionLostRaised = 0;
            NotifyControllerActivity();
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_heartbeatCts.Token), CancellationToken.None);
            Log($"Wireless heartbeat start initialIntervalMs={_initialHeartbeatIntervalMs} steadyIntervalMs={_steadyHeartbeatIntervalMs} radioIntervalMs={_radioHeartbeatIntervalMs} ackWaitTimeoutMs={_ackWaitTimeoutMs} idleGateMs={_heartbeatIdleGateMs} initialFailureThreshold={_initialFailureThreshold} steadyFailureThreshold={_steadyFailureThreshold} initialFailureWindowMs={_initialFailureThresholdWindowMs}.");
            return Task.CompletedTask;
        }

        public async Task StopHeartbeatAsync()
        {
            Log("Wireless heartbeat stop requested.");
            _heartbeatCts?.Cancel();
            if (_heartbeatTask != null && Task.CurrentId != _heartbeatTask.Id)
            {
                try { await _heartbeatTask; } catch { }
            }

            _heartbeatTask = null;
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
            Log("Wireless heartbeat stopped.");
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var nextRadioHeartbeatMs = _radioHeartbeatIntervalMs > 0
                ? _radioHeartbeatIntervalMs
                : long.MaxValue;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var intervalMs = started.Elapsed < TimeSpan.FromSeconds(10)
                        ? _initialHeartbeatIntervalMs
                        : _steadyHeartbeatIntervalMs;
                    var delayTarget = DateTime.UtcNow.AddMilliseconds(intervalMs);
                    var drainBuffer = new byte[64];
                    while (DateTime.UtcNow < delayTarget && !ct.IsCancellationRequested)
                    {
                        var timeLeftMs = (int)(delayTarget - DateTime.UtcNow).TotalMilliseconds;
                        if (timeLeftMs <= 0) break;

                        var readTimeoutMs = Math.Min(90, timeLeftMs);

                        if (_ioLock != null)
                        {
                            await _ioLock.WaitAsync(ct);
                        }

                        try
                        {
                            // Drain the selected runtime IN pipe continuously to prevent dongle FIFO overflow.
                            // Active identities also deliver controller input on this same unified pipe.
                            await _transport.ReadReportAsync(drainBuffer, readTimeoutMs, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Ignore benign drain interruptions; hard transport errors are handled above.
                        }
                        finally
                        {
                            _ioLock?.Release();
                        }
                    }

                    var recentActivityMs = GetMillisecondsSinceControllerActivity();
                    if (_heartbeatIdleGateMs > 0 &&
                        recentActivityMs < _heartbeatIdleGateMs &&
                        started.ElapsedMilliseconds < nextRadioHeartbeatMs)
                    {
                        LogRecentActivitySkip(recentActivityMs);
                        continue;
                    }

                    if (_ioLock != null)
                    {
                        await _ioLock.WaitAsync(ct);
                    }

                    try
                    {
                        if (started.ElapsedMilliseconds >= nextRadioHeartbeatMs)
                        {
                            var radioAck = await SendHeartbeatAsync(ScufWirelessReports.BuildRadioKeepAlive(), 0x00, 0x12, "RADIO HEARTBEAT", ct);
                            ValidateHeartbeatAck(radioAck, "RADIO HEARTBEAT");
                            nextRadioHeartbeatMs = started.ElapsedMilliseconds + _radioHeartbeatIntervalMs;
                        }

                        var ack = await SendHeartbeatAsync(ScufWirelessReports.BuildKeepAlive(), 0x01, 0x12, "HEARTBEAT", ct);
                        ValidateHeartbeatAck(ack, "HEARTBEAT");
                    }
                    finally
                    {
                        _ioLock?.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _heartbeatFailures++;
                    Log($"HEARTBEAT FAIL consecutiveFailures={_heartbeatFailures} error={ex.Message}");
                }

                var failureThreshold = started.ElapsedMilliseconds < _initialFailureThresholdWindowMs
                    ? _initialFailureThreshold
                    : _steadyFailureThreshold;
                if (_heartbeatFailures >= failureThreshold)
                {
                    IsSessionUnstable = true;
                    Log($"WirelessSessionLost: heartbeat failed {_heartbeatFailures} consecutive times threshold={failureThreshold} elapsedMs={started.ElapsedMilliseconds}.");
                    RaiseSessionLost();
                    break;
                }
            }
        }

        private async Task<byte[]> SendHeartbeatAsync(byte[] payload, byte expectedAckChannel, byte expectedOpcode, string label, CancellationToken ct)
        {
            Log($"{label} payload={ScufReportBuilder.ToHex(payload)}");
            await _transport.WriteReportAsync(payload, ct);
            return await ReadAckAsync(expectedAckChannel, expectedOpcode, ct);
        }

        private void ValidateHeartbeatAck(byte[] ack, string label)
        {
            var status = ack.Length > 3 ? ack[3] : (byte)0xFF;
            if (status == 0x00)
            {
                _heartbeatFailures = 0;
                Log($"{label} OK raw={ScufReportBuilder.ToHex(ack)}");
                return;
            }

            Log($"{label} WARN status=0x{status:X2} raw={ScufReportBuilder.ToHex(ack)}");
            throw new InvalidOperationException($"{label} status=0x{status:X2}");
        }

        private void RaiseSessionLost()
        {
            if (Interlocked.Exchange(ref _sessionLostRaised, 1) == 1)
            {
                return;
            }

            try
            {
                SessionLost?.Invoke();
            }
            catch (Exception ex)
            {
                Log($"WirelessSessionLost handler failed: {ex.Message}");
            }
        }

        private async Task<byte[]> ReadAckAsync(byte expectedAckChannel, byte expectedOpcode, CancellationToken ct)
        {
            var buffer = new byte[64];
            var started = Stopwatch.StartNew();
            var ignoredFrames = 0;
            while (started.ElapsedMilliseconds < _ackWaitTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                var (success, bytesRead) = await _transport.ReadReportAsync(buffer, _ackReadTimeoutMs, ct);
                if (!success || bytesRead < 4)
                {
                    continue;
                }

                var received = buffer.Take(bytesRead).ToArray();
                if (received[0] == 0x01 && received[1] == expectedAckChannel && received[2] == expectedOpcode)
                {
                    NotifyControllerActivity();
                    if (ignoredFrames > 0)
                    {
                        Log($"ACK matched after ignoring {ignoredFrames} non-matching frame(s).");
                    }

                    Log($"ACK raw={ScufReportBuilder.ToHex(received)}");
                    Log($"ACK interpreted channel=0x{received[1]:X2} opcode=0x{received[2]:X2} status=0x{received[3]:X2}");
                    return received;
                }

                ignoredFrames++;
                NotifyControllerActivity();
                if (ignoredFrames <= 3 || ignoredFrames % 25 == 0)
                {
                    Log($"ACK ignored frame count={ignoredFrames} firstByte=0x{received[0]:X2} while waiting channel=0x{expectedAckChannel:X2} opcode=0x{expectedOpcode:X2}");
                }

                await Task.Delay(1, ct);
            }

            throw new TimeoutException($"WirelessAckTimeout channel=0x{expectedAckChannel:X2} opcode=0x{expectedOpcode:X2}");
        }

        private long GetMillisecondsSinceControllerActivity()
        {
            var lastTicks = Interlocked.Read(ref _lastControllerActivityTicks);
            if (lastTicks == 0)
            {
                return long.MaxValue;
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - lastTicks;
            return elapsedTicks * 1000 / Stopwatch.Frequency;
        }

        private void LogRecentActivitySkip(long recentActivityMs)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var lastLogTicks = Interlocked.Read(ref _lastRecentActivitySkipLogTicks);
            if (lastLogTicks != 0 && (nowTicks - lastLogTicks) * 1000 / Stopwatch.Frequency < 5_000)
            {
                return;
            }

            Interlocked.Exchange(ref _lastRecentActivitySkipLogTicks, nowTicks);
            Log($"HEARTBEAT skipped because controller input is active recentActivityMs={recentActivityMs} idleGateMs={_heartbeatIdleGateMs}.");
        }

        private void Log(string message)
        {
            _logger($"[WIRELESS] {message}");
        }
    }
}
