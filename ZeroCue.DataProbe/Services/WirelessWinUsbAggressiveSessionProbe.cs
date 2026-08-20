using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCue.DataProbe.Services
{
    public sealed class WirelessWinUsbAggressiveSessionProbeResult
    {
        public bool WinUsbDeviceFound { get; init; }
        public bool PipesFound { get; init; }
        public bool ReplayCompleted { get; init; }
        public bool RuntimeValidationOk { get; init; }
        public bool WirelessWinUsbAggressiveSessionStable { get; init; }
        public string? DevicePath { get; init; }
        public string? FailureStage { get; init; }
        public string? Error { get; init; }
        public string? LogPath { get; init; }
        public WirelessDongleWinUsbTransport? Transport { get; init; }
        public WirelessSessionController? SessionController { get; init; }
        public WirelessDongleWinUsbTransport? AuxiliaryRadioTransport { get; init; }
        public CancellationTokenSource? AuxiliaryRadioPumpCts { get; init; }
        public Task? AuxiliaryRadioPumpTask { get; init; }
    }

    public sealed class WirelessWinUsbAggressiveSessionProbeOptions
    {
        public static WirelessWinUsbAggressiveSessionProbeOptions FullValidation { get; } = new();

        public int FirstHeartbeatWindowMs { get; init; } = 10_000;
        public int StableSessionTargetMs { get; init; } = 30_000;
        public int AckWaitTimeoutMs { get; init; } = 1_500;
        public int AckReadTimeoutMs { get; init; } = 90;
        public int? ReplayDelayCapMs { get; init; }
        public int? ReplayReportLimit { get; init; }
        public bool RequireRuntimeSetValidation { get; init; } = true;
        public Action<byte[], int>? RadioInputFrameObserver { get; init; }
        public Action? ControllerActivityObserver { get; init; }
        public bool EnableFileLogging { get; init; } = true;
        public int RadioPumpReadDelayMs { get; init; }
    }

    public sealed class WirelessWinUsbAggressiveSessionProbe
    {
        private const int GlobalTimeoutMs = 30_000;
        private const int MaxAttempts = 5;

        private readonly Action<string> _uiLogger;
        private readonly SemaphoreSlim _ioLock;
        private readonly WirelessWinUsbAggressiveSessionProbeOptions _options;
        private readonly string? _logPath;
        private int _heartbeatOkCount;
        private int _heartbeatFailCount;

        public WirelessWinUsbAggressiveSessionProbe(
            Action<string> logger,
            SemaphoreSlim ioLock,
            WirelessWinUsbAggressiveSessionProbeOptions? options = null)
        {
            _uiLogger = logger;
            _ioLock = ioLock;
            _options = options ?? WirelessWinUsbAggressiveSessionProbeOptions.FullValidation;
            if (_options.EnableFileLogging)
            {
                _logPath = ZeroCueLog.CommunicationLogPath;
            }
        }

        public async Task<WirelessWinUsbAggressiveSessionProbeResult> RunAsync(CancellationToken ct)
        {
            var globalSw = Stopwatch.StartNew();
            var attempts = 0;
            var reconnectCount = 0;
            var retryDelayMs = 150;
            string? lastPath = null;
            string? failureStage = null;
            string? error = null;
            bool deviceFound = false;
            bool pipesFound = false;

            async Task DelayBeforeRetryAsync(string reason)
            {
                Log($"Retry delay reason={reason} delayMs={retryDelayMs}");
                await Task.Delay(retryDelayMs, ct);
                retryDelayMs = Math.Min(1000, retryDelayMs + 150);
            }

            void ResetRetryDelay()
            {
                retryDelayMs = 150;
            }

            try
            {
                Log("WirelessWinUsbAggressiveSessionProbe start.");
                Log($"timestamp={DateTimeOffset.Now:O}");
                Log("WinUSB enabled=true");
                Log("mode=WinUsbDualInterfaceHandshake");
                Log($"options firstHeartbeatWindowMs={_options.FirstHeartbeatWindowMs} stableSessionTargetMs={_options.StableSessionTargetMs} ackWaitTimeoutMs={_options.AckWaitTimeoutMs} ackReadTimeoutMs={_options.AckReadTimeoutMs} replayDelayCapMs={_options.ReplayDelayCapMs?.ToString() ?? "capture"} replayReportLimit={_options.ReplayReportLimit?.ToString() ?? "all"} requireRuntimeSetValidation={_options.RequireRuntimeSetValidation}");
                Log("WinUSB dual-interface receiver session enabled.");
                LogCorsairProcesses();

                while (globalSw.ElapsedMilliseconds < GlobalTimeoutMs && attempts < MaxAttempts)
                {
                    ct.ThrowIfCancellationRequested();
                    failureStage = "EnumerateWinUsb";
                    Log($"Enumeration loop elapsedMs={globalSw.ElapsedMilliseconds} attempts={attempts}/{MaxAttempts}");

                    WirelessDongleWinUsbTransport? radioTransport = null;
                    CancellationTokenSource? radioPumpCts = null;
                    Task? radioPumpTask = null;
                    var runtimeTransport = new WirelessDongleWinUsbTransport(Log, WirelessWinUsbInterfaceTarget.RuntimeMi04);
                    if (!await runtimeTransport.ConnectAsync(ct))
                    {
                        error = "No wireless receiver WinUSB candidate exposed OUT 0x02 / IN 0x82 with 64-byte packets.";
                        Log(error);
                        await runtimeTransport.DisconnectAsync();
                        await DelayBeforeRetryAsync("RuntimeControlPipesUnavailable");
                        continue;
                    }

                    ResetRetryDelay();
                    deviceFound = true;
                    pipesFound = true;
                    attempts++;
                    if (lastPath != null && !string.Equals(lastPath, runtimeTransport.DevicePath, StringComparison.OrdinalIgnoreCase))
                    {
                        reconnectCount++;
                        Log($"WinUSB runtime/control interface reenumerated with another path. old={lastPath} new={runtimeTransport.DevicePath}");
                    }

                    lastPath = runtimeTransport.DevicePath;
                    var selectedReceiverIdentity = runtimeTransport.SelectedReceiverIdentity;
                    Log($"Handshake attempt={attempts} receiverVariant={selectedReceiverIdentity?.Variant ?? "unknown"} experimental={selectedReceiverIdentity?.IsExperimental ?? false} identity={(selectedReceiverIdentity == null ? "unknown" : $"VID_0x{selectedReceiverIdentity.VendorId:X4}:PID_0x{selectedReceiverIdentity.ProductId:X4}")} runtimePath={runtimeTransport.DevicePath}");

                    try
                    {
                        failureStage = "OpenAuxiliaryRadioInput";
                        radioTransport = new WirelessDongleWinUsbTransport(
                            Log,
                            WirelessWinUsbInterfaceTarget.RadioMi03,
                            logReadPayloads: false,
                            receiverIdentity: selectedReceiverIdentity);
                        if (await radioTransport.ConnectAsync(ct))
                        {
                            radioPumpCts = new CancellationTokenSource();
                            radioPumpTask = RunRadioMi03PumpAsync(radioTransport, radioPumpCts.Token, _options.EnableFileLogging);
                            Log($"Auxiliary input WinUSB open radioPath={radioTransport.DevicePath}");
                        }
                        else
                        {
                            Log("Auxiliary input WinUSB pipe 0x81 not available; continuing with runtime/control validation only.");
                            await radioTransport.DisconnectAsync();
                            radioTransport = null;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException)
                    {
                        Log($"Auxiliary input open skipped: {ex.Message}");
                        if (radioTransport != null)
                        {
                            await radioTransport.DisconnectAsync();
                            radioTransport = null;
                        }
                    }

                    try
                    {
                        Log($"WinUSB runtime/control open runtimePath={runtimeTransport.DevicePath}");

                        failureStage = "WirelessInitialization";
                        var replaySw = Stopwatch.StartNew();
                        var replayOk = await RunReplayAsync(runtimeTransport, ct);
                        replaySw.Stop();
                        Log($"ReplayDurationMs={replaySw.ElapsedMilliseconds}");
                        if (!replayOk)
                        {
                            error = "Replay incomplete before runtime validation.";
                            await runtimeTransport.DisconnectAsync();
                            await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                            await DelayBeforeRetryAsync("ReplayIncomplete");
                            continue;
                        }

                        failureStage = "ImmediateHeartbeat";
                        var heartbeatSw = Stopwatch.StartNew();
                        var firstHeartbeatOk = await SendHeartbeatOnceAsync(runtimeTransport, "first", ct);
                        heartbeatSw.Stop();
                        Log($"TimeToFirstHeartbeatMs={heartbeatSw.ElapsedMilliseconds}");
                        if (!firstHeartbeatOk)
                        {
                            error = "Replay finished but heartbeat did not ACK.";
                            Log("WirelessWinUsbNoHeartbeatAck.");
                            await runtimeTransport.DisconnectAsync();
                            await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                            await DelayBeforeRetryAsync("ImmediateHeartbeatFailed");
                            continue;
                        }

                        failureStage = "RuntimeValidation";
                        var runtimeOk = await ValidateRuntimeAsync(runtimeTransport, ct);
                        if (!runtimeOk)
                        {
                            Log("Runtime validation failed; trying one replay retry if still connected.");
                            var retryOk = await RunReplayAsync(runtimeTransport, ct) && await ValidateRuntimeAsync(runtimeTransport, ct);
                            if (!retryOk)
                            {
                                error = "WirelessWinUsbNoRuntimeAck or session rejected after replay retry.";
                                await runtimeTransport.DisconnectAsync();
                                await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                                await DelayBeforeRetryAsync("RuntimeValidationFailed");
                                continue;
                            }
                        }

                        failureStage = "InitialHeartbeatWindow";
                        if (!await RunInitialHeartbeatWindowAsync(runtimeTransport, ct))
                        {
                            error = "Heartbeat lost during the initial heartbeat window.";
                            await runtimeTransport.DisconnectAsync();
                            await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                            await DelayBeforeRetryAsync("InitialHeartbeatWindowFailed");
                            continue;
                        }

                        var sessionController = new WirelessSessionController(
                            runtimeTransport,
                            Log,
                            _ioLock,
                            initialHeartbeatIntervalMs: 750,
                            steadyHeartbeatIntervalMs: 2000,
                            ackWaitTimeoutMs: _options.AckWaitTimeoutMs,
                            ackReadTimeoutMs: _options.AckReadTimeoutMs,
                            initialFailureThreshold: 6,
                            steadyFailureThreshold: 5,
                            initialFailureThresholdWindowMs: 15_000,
                            radioHeartbeatIntervalMs: 0,
                            heartbeatIdleGateMs: 5000);
                        await sessionController.StartHeartbeatAsync(ct);

                        failureStage = "StableSessionWindow";
                        var stableSw = Stopwatch.StartNew();
                        while (stableSw.ElapsedMilliseconds < _options.StableSessionTargetMs)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (radioPumpTask != null &&
                                radioPumpCts != null &&
                                radioPumpTask.IsCompleted &&
                                !radioPumpCts.IsCancellationRequested)
                            {
                                error = "Auxiliary radio input pump stopped; runtime/control session remains under validation.";
                                Log(error);
                                await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                                radioTransport = null;
                                radioPumpTask = null;
                                radioPumpCts = null;
                            }

                            if (sessionController.IsSessionUnstable)
                            {
                                error = "WirelessWinUsbSessionLost during 30-second stability window.";
                                Log(error);
                                await sessionController.StopHeartbeatAsync();
                                await runtimeTransport.DisconnectAsync();
                                await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                                break;
                            }

                            await Task.Delay(250, ct);
                        }

                        Log($"StableSessionDurationMs={stableSw.ElapsedMilliseconds}");
                        Log($"ReconnectCount={reconnectCount}");
                        Log($"HeartbeatOkCount={_heartbeatOkCount}");
                        Log($"HeartbeatFailCount={_heartbeatFailCount}");

                        if (!sessionController.IsSessionUnstable && stableSw.ElapsedMilliseconds >= _options.StableSessionTargetMs)
                        {
                            Log("WirelessWinUsbAggressiveSessionStable = true");
                            return new WirelessWinUsbAggressiveSessionProbeResult
                            {
                                WinUsbDeviceFound = true,
                                PipesFound = true,
                                ReplayCompleted = true,
                                RuntimeValidationOk = true,
                                WirelessWinUsbAggressiveSessionStable = true,
                                DevicePath = runtimeTransport.DevicePath,
                                LogPath = _logPath,
                                Transport = runtimeTransport,
                                SessionController = sessionController,
                                AuxiliaryRadioTransport = radioTransport,
                                AuxiliaryRadioPumpCts = radioPumpCts,
                                AuxiliaryRadioPumpTask = radioPumpTask
                            };
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (runtimeTransport != null)
                        {
                            try { await runtimeTransport.DisconnectAsync(); } catch { }
                        }

                        await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException)
                    {
                        error = ex.Message;
                        Log($"{failureStage} failed: {ex.Message}");
                        Log("Device may have disappeared during replay/runtime/heartbeat; closing handle and retrying next RF window.");
                        if (runtimeTransport != null)
                        {
                            await runtimeTransport.DisconnectAsync();
                        }

                        await StopRadioMi03Async(radioTransport, radioPumpCts, radioPumpTask);
                        await DelayBeforeRetryAsync(failureStage ?? "TransportError");
                    }
                }

                error ??= attempts >= MaxAttempts
                    ? "Max aggressive WinUSB attempts reached."
                    : "Global aggressive WinUSB timeout reached.";
                Log($"WirelessWinUsbAggressiveSessionProbe failed: {error}");
                Log("Recommendation: use ZeroCue receiver driver restoration for the exact detected identity (VID_1B1C PID_3A08/PID_3A09 or experimental VID_2E95 PID_434E) and restart Windows if the receiver keeps reenumerating badly.");

                return new WirelessWinUsbAggressiveSessionProbeResult
                {
                    WinUsbDeviceFound = deviceFound,
                    PipesFound = pipesFound,
                    FailureStage = failureStage,
                    Error = error,
                    LogPath = _logPath
                };
            }
            catch (OperationCanceledException ex)
            {
                error = $"WirelessWinUsbAggressiveSessionProbe canceled, likely because the device/session disappeared or the caller timed out: {ex.Message}";
                Log(error);
                return new WirelessWinUsbAggressiveSessionProbeResult
                {
                    WinUsbDeviceFound = deviceFound,
                    PipesFound = pipesFound,
                    FailureStage = failureStage,
                    Error = error,
                    LogPath = _logPath
                };
            }
            finally
            {
                Log($"result final elapsedMs={globalSw.ElapsedMilliseconds}");
            }
        }

        private async Task<bool> RunReplayAsync(WirelessDongleWinUsbTransport transport, CancellationToken ct)
        {
            var allReports = WirelessInitializationSequence.Reports.ToList();
            var reports = _options.ReplayReportLimit.HasValue
                ? allReports.Take(_options.ReplayReportLimit.Value).ToList()
                : allReports;
            Log($"WirelessInitializationReports count={allReports.Count} replayUsed={reports.Count}");
            if (reports.Count == 0)
            {
                Log("Replay list empty.");
                return false;
            }

            for (var i = 0; i < reports.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var report = reports[i];
                Log($"REPLAY index={i:D3} ts={report.RelativeTimestampMs} name={report.Name} payload={ScufReportBuilder.ToHex(report.Payload64)}");
                await transport.WriteReportAsync(report.Payload64, ct);

                try
                {
                    var ack = await ReadAckAsync(transport, report.ExpectedAckChannel, report.ExpectedOpcode, ct);
                    var status = ack.Length > 3 ? ack[3] : (byte)0xFF;
                    Log($"REPLAY ACK index={i:D3} channel=0x{ack[1]:X2} opcode=0x{ack[2]:X2} status=0x{status:X2} raw={ScufReportBuilder.ToHex(ack)}");
                }
                catch (TimeoutException ex)
                {
                    Log($"REPLAY ACK timeout index={i:D3} allowTimeout={report.AllowTimeout} error={ex.Message}");
                    if (!report.AllowTimeout)
                    {
                        return false;
                    }
                }

                var delayAfterMs = GetReplayDelayAfterMs(report);
                if (delayAfterMs > 0)
                {
                    await Task.Delay(delayAfterMs, ct);
                }
            }

            return true;
        }

        private int GetReplayDelayAfterMs(WirelessInitializationSequence.InitializationReport report)
        {
            if (!_options.ReplayDelayCapMs.HasValue)
            {
                return report.DelayAfterMs;
            }

            var cappedDelay = Math.Min(report.DelayAfterMs, _options.ReplayDelayCapMs.Value);
            if (cappedDelay != report.DelayAfterMs)
            {
                Log($"REPLAY delay capped name={report.Name} capturedDelayAfterMs={report.DelayAfterMs} usedDelayAfterMs={cappedDelay}");
            }

            return cappedDelay;
        }

        private async Task RunRadioMi03PumpAsync(WirelessDongleWinUsbTransport radioTransport, CancellationToken ct, bool enableLogging)
        {
            var buffer = new byte[64];
            var sw = Stopwatch.StartNew();
            var lastLogMs = 0L;
            var readCount = 0;
            var timeoutCount = 0;
            var lastFrame = Array.Empty<byte>();
            var readDelayMs = Math.Max(0, _options.RadioPumpReadDelayMs);
            using var inputMapLogger = new WirelessWinUsbInputMapLogger(Log, enableLogging);

            Log($"Auxiliary radio input pump start: draining IN 0x81 frames while runtime control uses OUT 0x02 / IN 0x82. readDelayMs={readDelayMs}");
            while (!ct.IsCancellationRequested && radioTransport.IsOpen)
            {
                try
                {
                    var (success, bytesRead) = await radioTransport.ReadReportAsync(buffer, 250, ct);
                    if (success && bytesRead > 0)
                    {
                        readCount++;
                        inputMapLogger.ObserveFrame(buffer, bytesRead);
                        var observedFrame = CopyFrame(buffer, bytesRead);
                        _options.ControllerActivityObserver?.Invoke();
                        _options.RadioInputFrameObserver?.Invoke(observedFrame, observedFrame.Length);
                        lastFrame = observedFrame;
                        if (readDelayMs > 0)
                        {
                            await Task.Delay(readDelayMs, ct);
                        }
                    }
                    else
                    {
                        timeoutCount++;
                    }

                    if (readCount == 1 || sw.ElapsedMilliseconds - lastLogMs >= 1000)
                    {
                        lastLogMs = sw.ElapsedMilliseconds;
                        var frame = lastFrame.Length > 0 ? ScufReportBuilder.ToHex(lastFrame) : "<none>";
                        Log($"Auxiliary radio input pump status elapsedMs={sw.ElapsedMilliseconds} reads={readCount} timeouts={timeoutCount} lastFrame={frame}");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is TimeoutException)
                {
                    Log($"Auxiliary radio input pump stopped by transport error: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Auxiliary radio input pump stopped by unexpected error: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }

            inputMapLogger.LogStop(readCount, timeoutCount);
            Log($"Auxiliary radio input pump stop elapsedMs={sw.ElapsedMilliseconds} reads={readCount} timeouts={timeoutCount}");
        }

        private static byte[] CopyFrame(byte[] buffer, int length)
        {
            var frame = new byte[length];
            Buffer.BlockCopy(buffer, 0, frame, 0, length);
            return frame;
        }

        private async Task StopRadioMi03Async(
            WirelessDongleWinUsbTransport? radioTransport,
            CancellationTokenSource? radioPumpCts,
            Task? radioPumpTask)
        {
            try { radioPumpCts?.Cancel(); } catch { }
            if (radioPumpTask != null)
            {
                try { await radioPumpTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
            }
            try { radioPumpCts?.Dispose(); } catch { }
            if (radioTransport != null)
            {
                try { await radioTransport.DisconnectAsync(); } catch { }
            }
        }

        private async Task<bool> ValidateRuntimeAsync(WirelessDongleWinUsbTransport transport, CancellationToken ct)
        {
            Log("Runtime validation: get brightness.");
            await transport.WriteReportAsync(ScufWirelessReports.BuildGetBrightness(), ct);
            var getAck = await ReadAckAsync(transport, 0x01, 0x02, ct);
            var getStatus = getAck.Length > 3 ? getAck[3] : (byte)0xFF;
            Log($"RuntimeGetBrightness ACK status=0x{getStatus:X2} raw={ScufReportBuilder.ToHex(getAck)}");
            if (getStatus == 0x09)
            {
                Log("WirelessWinUsbTransportOkButSessionRejected: runtime get brightness returned 0x09.");
                return false;
            }

            if (getStatus != 0x00)
            {
                Log($"WirelessWinUsbNoRuntimeAck: get brightness status=0x{getStatus:X2}.");
                return false;
            }

            if (!_options.RequireRuntimeSetValidation)
            {
                Log("Runtime validation: set brightness skipped by fast profile after heartbeat/get brightness OK.");
                Log("WirelessWinUsbSessionEstablished = true");
                return true;
            }

            Log("Runtime validation: set brightness 100.");
            await transport.WriteReportAsync(ScufWirelessReports.BuildSetBrightness(1000), ct);
            var setAck = await ReadAckAsync(transport, 0x01, 0x01, ct);
            var setStatus = setAck.Length > 3 ? setAck[3] : (byte)0xFF;
            Log($"RuntimeSetBrightness100 ACK status=0x{setStatus:X2} raw={ScufReportBuilder.ToHex(setAck)}");
            if (setStatus == 0x00)
            {
                Log("WirelessWinUsbSessionEstablished = true");
                return true;
            }

            if (setStatus == 0x09)
            {
                Log("WirelessWinUsbTransportOkButSessionRejected: runtime set brightness returned 0x09.");
            }
            else
            {
                Log($"WirelessWinUsbNoRuntimeAck: set brightness status=0x{setStatus:X2}.");
            }

            return false;
        }

        private async Task<bool> RunInitialHeartbeatWindowAsync(WirelessDongleWinUsbTransport transport, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var consecutiveFailures = 0;
            while (sw.ElapsedMilliseconds < _options.FirstHeartbeatWindowMs)
            {
                ct.ThrowIfCancellationRequested();
                var ok = await SendHeartbeatOnceAsync(transport, $"initialWindow elapsedMs={sw.ElapsedMilliseconds}", ct);
                if (ok)
                {
                    consecutiveFailures = 0;
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        Log("WirelessWinUsbSessionLost: 3 consecutive heartbeat failures in initial window.");
                        return false;
                    }
                }

                await Task.Delay(500, ct);
            }

            return true;
        }

        private async Task<bool> SendHeartbeatOnceAsync(WirelessDongleWinUsbTransport transport, string label, CancellationToken ct)
        {
            Log($"HEARTBEAT {label} payload={ScufReportBuilder.ToHex(ScufWirelessReports.BuildKeepAlive())}");
            await transport.WriteReportAsync(ScufWirelessReports.BuildKeepAlive(), ct);
            try
            {
                var ack = await ReadAckAsync(transport, 0x01, 0x12, ct);
                var status = ack.Length > 3 ? ack[3] : (byte)0xFF;
                if (status == 0x00)
                {
                    _heartbeatOkCount++;
                    Log($"HEARTBEAT OK {label} raw={ScufReportBuilder.ToHex(ack)}");
                    return true;
                }

                _heartbeatFailCount++;
                Log($"HEARTBEAT WARN {label} status=0x{status:X2} raw={ScufReportBuilder.ToHex(ack)}");
                return false;
            }
            catch (TimeoutException ex)
            {
                _heartbeatFailCount++;
                Log($"HEARTBEAT TIMEOUT {label} error={ex.Message}");
                return false;
            }
        }

        private async Task<byte[]> ReadAckAsync(WirelessDongleWinUsbTransport transport, byte expectedAckChannel, byte expectedOpcode, CancellationToken ct)
        {
            var buffer = new byte[64];
            var sw = Stopwatch.StartNew();
            var ignoredFrames = 0;
            while (sw.ElapsedMilliseconds < _options.AckWaitTimeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                var (success, bytesRead) = await transport.ReadReportAsync(buffer, _options.AckReadTimeoutMs, ct);
                if (!success || bytesRead < 4)
                {
                    continue;
                }

                var ack = buffer.Take(bytesRead).ToArray();
                if (ack[0] == 0x01 && ack[1] == expectedAckChannel && ack[2] == expectedOpcode)
                {
                    if (ignoredFrames > 0)
                    {
                        Log($"ACK matched after ignoring {ignoredFrames} non-matching frame(s).");
                    }

                    Log($"ACK candidate expectedChannel=0x{expectedAckChannel:X2} expectedOpcode=0x{expectedOpcode:X2} raw={ScufReportBuilder.ToHex(ack)}");
                    _options.ControllerActivityObserver?.Invoke();
                    return ack;
                }

                ignoredFrames++;
                if (ignoredFrames <= 3 || ignoredFrames % 25 == 0)
                {
                    Log($"ACK ignored frame count={ignoredFrames} firstByte=0x{ack[0]:X2} while waiting channel=0x{expectedAckChannel:X2} opcode=0x{expectedOpcode:X2}");
                }

                await Task.Delay(1, ct);
            }

            throw new TimeoutException($"WirelessAckTimeout channel=0x{expectedAckChannel:X2} opcode=0x{expectedOpcode:X2}");
        }

        private void LogCorsairProcesses()
        {
            foreach (var processName in new[] { "iCUE", "Corsair.Service", "CorsairDeviceControlService" })
            {
                try
                {
                    var count = Process.GetProcessesByName(processName).Length;
                    Log($"process {processName} count={count}");
                }
                catch (Exception ex)
                {
                    Log($"process {processName} inspection failed: {ex.Message}");
                }
            }
        }

        private void Log(string message)
        {
            var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] [WIRELESS-WINUSB] {message}";
            _uiLogger(line);
        }
    }
}
