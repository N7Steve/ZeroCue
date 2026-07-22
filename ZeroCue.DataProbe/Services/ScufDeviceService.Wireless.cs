using System;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client;

namespace ZeroCue.DataProbe.Services
{
    public partial class ScufDeviceService
    {
        private async Task<bool> TryConnectWirelessAsync(bool autoConnect)
        {
            return await TryConnectWirelessWinUsbAsync(autoConnect);
        }

        private async Task<bool> TryConnectWirelessWinUsbAsync(bool autoConnect)
        {
            _wirelessSessionCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _wirelessG5SuppressorActive, 0);
            ResetWirelessRuntimeInputState();
            using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            using var connectLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(_wirelessSessionCts.Token, connectTimeoutCts.Token);
            using var wiredUsbMonitorCts = new CancellationTokenSource();
            var ct = connectLinkedCts.Token;
            var canceledForWiredUsb = false;
            var wiredUsbMonitorTask = Task.Run(async () =>
            {
                while (!wiredUsbMonitorCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500, wiredUsbMonitorCts.Token);
                    if (IsWiredUsbDevicePresent())
                    {
                        canceledForWiredUsb = true;
                        LogInput("[SYSTEM] USB cableado detectado durante probe wireless; cancelando wireless para priorizar USB.");
                        connectLinkedCts.Cancel();
                        break;
                    }
                }
            }, CancellationToken.None);

            try
            {
                if (!autoConnect)
                {
                    LogInput("[WIRELESS-WINUSB] Buscando receiver WinUSB 1B1C:3A08/3A09 con OUT 0x02 / IN 0x82 y MaxPacketSize=64...");
                    WirelessHidDetectionService.ScanSupportedReceiverCollections(LogInput);
                }

                SetWaitingForControllerStatus();

                var probe = new WirelessWinUsbAggressiveSessionProbe(
                    LogInput,
                    UsbLock,
                    new WirelessWinUsbAggressiveSessionProbeOptions
                    {
                        FirstHeartbeatWindowMs = 1500,
                        StableSessionTargetMs = 1500,
                        AckWaitTimeoutMs = 2500,
                        AckReadTimeoutMs = 90,
                        ReplayDelayCapMs = 0,
                        ReplayReportLimit = null,
                        RequireRuntimeSetValidation = true,
                        EnableFileLogging = true,
                        RadioPumpReadDelayMs = 2,
                        RadioInputFrameObserver = (frame, length) =>
                        {
                            BeginWirelessHandshakeStatus();
                            ProcessWirelessWinUsbInputFrame(frame, length);
                        },
                        ControllerActivityObserver = () =>
                        {
                            BeginWirelessHandshakeStatus();
                        }
                    });
                var result = await probe.RunAsync(ct);
                if (!result.WirelessWinUsbAggressiveSessionStable ||
                    result.Transport == null ||
                    result.SessionController == null)
                {
                    if (!canceledForWiredUsb)
                    {
                        SetWaitingForControllerStatus();
                    }
                    LogInput($"[WIRELESS-WINUSB] Fallo probe aggressive stage={result.FailureStage ?? "unknown"} error={result.Error ?? "unknown"} log={result.LogPath}");
                    return false;
                }

                _wirelessWinUsbTransport = result.Transport;
                _wirelessWinUsbTransport.FrameObserver = ProcessWirelessWinUsbInputFrame;
                _wirelessWinUsbAuxRadioTransport = result.AuxiliaryRadioTransport;
                _wirelessWinUsbAuxRadioCts = result.AuxiliaryRadioPumpCts;
                _wirelessWinUsbAuxRadioTask = result.AuxiliaryRadioPumpTask;
                StartWirelessAuxRadioPumpMonitor(_wirelessWinUsbAuxRadioTask, _wirelessWinUsbAuxRadioCts);
                _wirelessSessionController = result.SessionController;
                _wirelessSessionController.SessionLost += HandleWirelessSessionLost;
                _transport = _wirelessWinUsbTransport;
                _ackReader = new ScufAckReader(_transport);
                _modeController = new DeviceModeController(UsbLock, _ackReader, _transport, LogInput, ScufProtocolProfile.Wireless);
                CurrentOperatingState = "WirelessWinUsbAggressiveSession";

                StartWirelessWiredUsbMonitor();
                StartWirelessForegroundMonitor();

                try
                {
                    LogInput("[VIGEM] Inicializando ViGEm en modo wireless WinUSB...");
                    _client = new ViGEmClient();
                    _xbox = _client.CreateXbox360Controller();
                    _xbox.Connect();
                    if (_xbox != null)
                    {
                        _xbox.FeedbackReceived += Xbox_FeedbackReceived;
                    }
                    IsViGEmActive = true;
                }
                catch (Exception ex)
                {
                    IsViGEmActive = false;
                    LogInput($"[VIGEM] WARN wireless WinUSB: {ex.Message}. Se mantiene control/configuracion WinUSB.");
                }

                IsConnected = true;
                IsConnecting = false;
                ConnectionKind = ScufConnectionKind.Wireless;
                TransportName = "Wireless WinUSB";
                SetConnectionStatus(
                    ConnectionStatusState.WirelessConnected,
                    LocalizationService.Get("StatusControllerConnected"),
                        LocalizationService.Get("StatusControllerActive"));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConnectionMode)));
                LogInput("[WIRELESS-WINUSB] Cambio de estado: IsConnected=true ConnectionMode=Wireless WinUSB");

                _ = Task.Run(ApplyWirelessRuntimeProfileAsync, CancellationToken.None);

                return true;
            }
            catch (OperationCanceledException)
            {
                SetConnectionStatus(
                    ConnectionStatusState.ReceiverOnly,
                    LocalizationService.Get("StatusWaitingController"),
                    LocalizationService.Get("StatusConnectControllerHint"));
                LogInput("[WIRELESS-WINUSB] Conexion cancelada/timeout.");
                return false;
            }
            catch (Exception ex)
            {
                SetConnectionStatus(
                    ConnectionStatusState.ReceiverOnly,
                    LocalizationService.Get("StatusWaitingController"),
                    LocalizationService.Get("StatusWirelessSessionFailed"));
                LogInput($"[WIRELESS-WINUSB] Conexion fallida: {ex.Message}");
                return false;
            }
            finally
            {
                try { wiredUsbMonitorCts.Cancel(); } catch { }
                try { await wiredUsbMonitorTask.WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { }
                if (!IsConnected)
                {
                    IsConnecting = false;
                    await CleanupWirelessAsync();
                }
            }
        }

        private async Task ApplyWirelessRuntimeProfileAsync()
        {
            var runtimeCt = _wirelessSessionCts?.Token ?? CancellationToken.None;

            try
            {
                await Task.Delay(250, runtimeCt);
                if (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless)
                {
                    return;
                }

                LogInput("[WIRELESS-WINUSB] Aplicando perfil wireless diferido tras estabilizar la sesion.");
                await RunWirelessG5SuppressorAsync("initial-profile", runtimeCt, requireConnected: true, waitForGate: true);
                await Task.Delay(150, runtimeCt);
                await SetEcoModeAsync(EcoMode, runtimeCt);
                await Task.Delay(150, runtimeCt);
                await SetStaticRgbAsync(RgbRed, RgbGreen, RgbBlue, runtimeCt);
                await Task.Delay(150, runtimeCt);
                await SetBrightnessAsync(RgbBrightness, runtimeCt);
                await Task.Delay(150, runtimeCt);
                await SetRumbleIntensityAsync(RumbleIntensity, runtimeCt);
                await Task.Delay(150, runtimeCt);
                await ReapplyWirelessSoftwareModeAsync("initial-profile", runtimeCt);
                StartWirelessSoftwareModeRefresh();
            }
            catch (OperationCanceledException)
            {
                // Normal cleanup/disconnect path.
            }
            catch (Exception ex)
            {
                LogInput($"[WIRELESS-WINUSB] Runtime profile deferred apply incomplete/unsupported: {ex.Message}");
            }
        }

        private void HandleWirelessSessionLost()
        {
            LogInput("[WIRELESS] Sesion perdida detectada; liberando conexion activa.");
            _ = Task.Run(() =>
            {
                try
                {
                    Disconnect();
                }
                catch (Exception ex)
                {
                    LogInput($"[WIRELESS] Error al liberar conexion perdida: {ex.Message}");
                }
            });
        }

        private void StartWirelessSoftwareModeRefresh()
        {
            StopWirelessSoftwareModeRefresh();
            _wirelessSoftwareModeRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_wirelessSessionCts?.Token ?? CancellationToken.None);
            var ct = _wirelessSoftwareModeRefreshCts.Token;
            _wirelessSoftwareModeRefreshTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(45), ct);
                        if (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless)
                        {
                            continue;
                        }

                        await ReapplyWirelessSoftwareModeAsync("periodic-refresh", ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cleanup.
                }
                catch (Exception ex)
                {
                    LogInput($"[WIRELESS-WINUSB] Software mode refresh loop stopped: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        private void StopWirelessSoftwareModeRefresh()
        {
            _wirelessSoftwareModeRefreshCts?.Cancel();
            if (_wirelessSoftwareModeRefreshTask != null)
            {
                try { _wirelessSoftwareModeRefreshTask.Wait(500); } catch { }
            }

            _wirelessSoftwareModeRefreshTask = null;
            _wirelessSoftwareModeRefreshCts?.Dispose();
            _wirelessSoftwareModeRefreshCts = null;
        }

        private void ScheduleWirelessSoftwareModeRefresh(string reason)
        {
            if (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless)
            {
                return;
            }

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastWirelessSoftwareModeRefreshMs);
            if (now - last < 5000)
            {
                LogInput($"[WIRELESS-WINUSB] Software mode refresh skipped reason={reason} recentMs={now - last}.");
                return;
            }

            if (Interlocked.CompareExchange(ref _lastWirelessSoftwareModeRefreshMs, now, last) != last)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    await ReapplyWirelessSoftwareModeAsync(reason, _wirelessSessionCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    // Normal cleanup.
                }
                catch (Exception ex)
                {
                    LogInput($"[WIRELESS-WINUSB] Software mode refresh failed reason={reason}: {ex.Message}");
                }
            });
        }

        private async Task ReapplyWirelessSoftwareModeAsync(string reason, CancellationToken ct)
        {
            if (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless)
            {
                return;
            }

            if (!await TryEnterWirelessSoftwareModeGateAsync(reason, waitForGate: false, ct))
            {
                return;
            }

            try
            {
                Interlocked.Exchange(ref _lastWirelessSoftwareModeRefreshMs, Environment.TickCount64);
                LogInput($"[WIRELESS-WINUSB] Reapplying Software Mode reason={reason}.");
                await SetStaticRgbAsync(RgbRed, RgbGreen, RgbBlue, ct);
                await Task.Delay(100, ct);
                await SetBrightnessAsync(RgbBrightness, ct);
                await Task.Delay(100, ct);
                await SuppressG5HardwareEcoToggleAsync(ct);
                Interlocked.Exchange(ref _wirelessG5SuppressorActive, 1);
            }
            finally
            {
                _wirelessSoftwareModeGate.Release();
            }
        }

        private async Task RunWirelessG5SuppressorAsync(string reason, CancellationToken ct, bool requireConnected, bool waitForGate)
        {
            if (requireConnected && (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless))
            {
                return;
            }

            if (!await TryEnterWirelessSoftwareModeGateAsync(reason, waitForGate, ct))
            {
                return;
            }

            try
            {
                Interlocked.Exchange(ref _lastWirelessSoftwareModeRefreshMs, Environment.TickCount64);
                LogInput($"[WIRELESS-WINUSB] Applying G5 suppressor reason={reason}.");
                await SuppressG5HardwareEcoToggleAsync(ct);
                Interlocked.Exchange(ref _wirelessG5SuppressorActive, 1);
            }
            finally
            {
                _wirelessSoftwareModeGate.Release();
            }
        }

        private async Task<bool> TryEnterWirelessSoftwareModeGateAsync(string reason, bool waitForGate, CancellationToken ct)
        {
            if (waitForGate)
            {
                await _wirelessSoftwareModeGate.WaitAsync(ct);
                return true;
            }

            if (await _wirelessSoftwareModeGate.WaitAsync(0, ct))
            {
                return true;
            }

            LogInput($"[WIRELESS-WINUSB] Software mode sequence coalesced reason={reason}; another sequence is already running.");
            return false;
        }

        private void StartWirelessForegroundMonitor()
        {
            StopWirelessForegroundMonitor();
            _wirelessForegroundMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_wirelessSessionCts?.Token ?? CancellationToken.None);
            var ct = _wirelessForegroundMonitorCts.Token;
            _wirelessForegroundMonitorTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(1000, ct);
                        if (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless)
                        {
                            continue;
                        }

                        var foregroundPath = ForegroundApplicationService.GetForegroundProcessPath();
                        if (string.IsNullOrWhiteSpace(foregroundPath) ||
                            string.Equals(foregroundPath, _lastWirelessForegroundPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        _lastWirelessForegroundPath = foregroundPath;
                        LogInput($"[FOREGROUND] {foregroundPath}");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cleanup.
                }
                catch (Exception ex)
                {
                    LogInput($"[FOREGROUND] monitor stopped: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        private void StopWirelessForegroundMonitor()
        {
            _wirelessForegroundMonitorCts?.Cancel();
            if (_wirelessForegroundMonitorTask != null)
            {
                try { _wirelessForegroundMonitorTask.Wait(500); } catch { }
            }

            _wirelessForegroundMonitorTask = null;
            _wirelessForegroundMonitorCts?.Dispose();
            _wirelessForegroundMonitorCts = null;
            _lastWirelessForegroundPath = string.Empty;
        }

        private void StartWirelessAuxRadioPumpMonitor(Task? pumpTask, CancellationTokenSource? pumpCts)
        {
            if (pumpTask == null || pumpCts == null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await pumpTask;
                }
                catch (Exception ex)
                {
                    LogInput($"[WIRELESS-WINUSB] Auxiliary MI03 radio pump task ended with error: {ex.Message}");
                }

                if (pumpCts.IsCancellationRequested ||
                    !ReferenceEquals(_wirelessWinUsbAuxRadioTask, pumpTask) ||
                    !IsConnected ||
                    ConnectionKind != ScufConnectionKind.Wireless)
                {
                    return;
                }

                LogInput("[WIRELESS-WINUSB] Auxiliary MI03 radio pump ended unexpectedly during active session; treating wireless session as lost.");
                HandleWirelessSessionLost();
            });
        }

        private void StartWirelessWiredUsbMonitor()
        {
            StopWirelessWiredUsbMonitor();
            _wirelessWiredUsbMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_wirelessSessionCts?.Token ?? CancellationToken.None);
            var ct = _wirelessWiredUsbMonitorCts.Token;
            _wirelessWiredUsbMonitorTask = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(300, ct);
                        if (!IsConnected || ConnectionKind != ScufConnectionKind.Wireless)
                        {
                            continue;
                        }

                        if (!IsWiredUsbDevicePresent())
                        {
                            continue;
                        }

                        LogInput("[WIRELESS] USB cableado detectado durante sesion wireless; liberando wireless para priorizar USB.");
                        Disconnect();
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cleanup.
                }
                catch (Exception ex)
                {
                    LogInput($"[WIRELESS] WARN monitor USB cableado fallido: {ex.Message}");
                }
            }, CancellationToken.None);
        }

        private void StopWirelessWiredUsbMonitor()
        {
            _wirelessWiredUsbMonitorCts?.Cancel();
            if (_wirelessWiredUsbMonitorTask != null && Task.CurrentId != _wirelessWiredUsbMonitorTask.Id)
            {
                try { _wirelessWiredUsbMonitorTask.Wait(500); } catch { }
            }

            _wirelessWiredUsbMonitorTask = null;
            _wirelessWiredUsbMonitorCts?.Dispose();
            _wirelessWiredUsbMonitorCts = null;
        }

        private async Task CleanupWirelessAsync()
        {
            Interlocked.Exchange(ref _wirelessG5SuppressorActive, 0);
            ResetWirelessRuntimeInputState();
            StopWirelessWiredUsbMonitor();
            StopWirelessSoftwareModeRefresh();
            StopWirelessForegroundMonitor();

            if (_wirelessSessionController != null)
            {
                _wirelessSessionController.SessionLost -= HandleWirelessSessionLost;
                try { await _wirelessSessionController.StopHeartbeatAsync(); } catch { }
                _wirelessSessionController = null;
            }

            if (_wirelessWinUsbTransport != null)
            {
                try { await _wirelessWinUsbTransport.DisconnectAsync(); } catch { }
                _wirelessWinUsbTransport = null;
            }

            if (_wirelessWinUsbAuxRadioCts != null)
            {
                LogInput("[WIRELESS-WINUSB] Stopping auxiliary MI03 radio pump.");
                try { _wirelessWinUsbAuxRadioCts.Cancel(); } catch { }
            }

            if (_wirelessWinUsbAuxRadioTask != null)
            {
                try
                {
                    await _wirelessWinUsbAuxRadioTask.WaitAsync(TimeSpan.FromMilliseconds(500));
                    LogInput("[WIRELESS-WINUSB] Auxiliary MI03 radio pump stopped.");
                }
                catch (TimeoutException)
                {
                    LogInput("[WIRELESS-WINUSB] Auxiliary MI03 radio pump did not stop within cleanup timeout.");
                }
                catch (Exception ex)
                {
                    LogInput($"[WIRELESS-WINUSB] Auxiliary MI03 radio pump cleanup error: {ex.Message}");
                }

                _wirelessWinUsbAuxRadioTask = null;
            }

            if (_wirelessWinUsbAuxRadioCts != null)
            {
                try { _wirelessWinUsbAuxRadioCts.Dispose(); } catch { }
                _wirelessWinUsbAuxRadioCts = null;
            }

            if (_wirelessWinUsbAuxRadioTransport != null)
            {
                try { await _wirelessWinUsbAuxRadioTransport.DisconnectAsync(); } catch { }
                _wirelessWinUsbAuxRadioTransport = null;
            }

            _wirelessSessionCts?.Cancel();
            _wirelessSessionCts?.Dispose();
            _wirelessSessionCts = null;
        }
    }
}
