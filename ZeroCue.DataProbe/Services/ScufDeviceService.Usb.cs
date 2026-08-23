using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using ZeroCue.DataProbe.Models;


namespace ZeroCue.DataProbe.Services
{
    public partial class ScufDeviceService
    {

        private Task? _autoConnectTask;
        private readonly SemaphoreSlim _connectionAttemptGate = new(1, 1);
        private int _connectionSuspensionCount;
        private long _nextWirelessAutoProbeTimestamp;

        public async Task<IDisposable> SuspendConnectionsAsync()
        {
            Interlocked.Increment(ref _connectionSuspensionCount);
            Disconnect();
            bool gateAcquired = false;

            try
            {
                await _connectionAttemptGate.WaitAsync();
                gateAcquired = true;
                if (IsConnected || IsConnecting)
                {
                    Disconnect();
                }
                LogInput("[SYSTEM] Controller connections suspended for driver maintenance.");
                return new ConnectionSuspension(this);
            }
            catch
            {
                if (gateAcquired)
                {
                    _connectionAttemptGate.Release();
                }
                Interlocked.Decrement(ref _connectionSuspensionCount);
                throw;
            }
        }

        public void StartAutoPolling()
        {
            if (_autoConnectTask == null)
            {
                _autoConnectTask = Task.Run(AutoConnectionLoop);
            }
        }

        private async Task AutoConnectionLoop()
        {
            while (true)
            {
                if (Volatile.Read(ref _connectionSuspensionCount) == 0 && !IsConnected)
                {
                    await ConnectAsync(autoConnect: true);
                }
                await Task.Delay(2000);
            }
        }

        public async Task ConnectAsync(bool autoConnect = false)
        {
            if (IsConnected) return;
            if (Volatile.Read(ref _disconnecting) != 0) return;
            if (Volatile.Read(ref _connectionSuspensionCount) != 0) return;

            await _connectionAttemptGate.WaitAsync();
            try
            {
                if (IsConnected) return;
                if (Volatile.Read(ref _disconnecting) != 0) return;
                if (Volatile.Read(ref _connectionSuspensionCount) != 0) return;

                IsConnecting = false;
                if (!autoConnect)
                {
                    SetWaitingForControllerStatus();
                    LogInput($"[SYSTEM] Iniciando busqueda USB cableada por perfil: {DeviceProfile.Name} identities={string.Join(',', DeviceProfile.WiredDeviceIdentities.Select(identity => $"{identity.VendorId:X4}:{identity.ProductId:X4}{(identity.IsExperimental ? "[experimental]" : string.Empty)}"))} reportSize={DeviceProfile.ReportSize}.");
                }

                await Task.Run(async () =>
                {
                try
                {
                    _context = new UsbContext();
                    var dev = FindSupportedWiredUsbDevice(_context, !autoConnect);

                    if (dev == null)
                    {
                        _context.Dispose();
                        _context = null;

                        if (await TryConnectWirelessAsync(autoConnect))
                        {
                            return;
                        }

                        _context = new UsbContext();
                        dev = FindSupportedWiredUsbDevice(_context, true);
                        if (dev != null)
                        {
                            LogInput("[SYSTEM] Dispositivo USB detectado tras finalizar o interrumpir el probe wireless.");
                        }
                    }

                    if (dev == null)
                    {
                        _context?.Dispose();
                        _context = null;

                        if (autoConnect)
                        {
                            SetWaitingForControllerStatus();
                            return;
                        }
                        throw new Exception("Mando Scuf no encontrado. Asegura el driver WinUSB.");
                    }
                    var wiredIdentity = DeviceProfile.FindWiredDevice(dev.VendorId, dev.ProductId);
                    if (wiredIdentity?.IsExperimental == true)
                    {
                        LogInput($"[EXPERIMENTAL] Dispositivo cableado variant={wiredIdentity.Variant} VID=0x{dev.VendorId:X4} PID=0x{dev.ProductId:X4} aceptado usando el protocolo de {DeviceProfile.Name}; la compatibilidad de esta identidad no esta validada.");
                    }
                    IsConnecting = true;
                    SetConnectionStatus(
                        ConnectionStatusState.UsbConnecting,
                        LocalizationService.Get("StatusConnecting"),
                        LocalizationService.Get("StatusPreparingController"));
                    LogInput($"[SYSTEM] Dispositivo USB cableado seleccionado VID=0x{dev.VendorId:X4} PID=0x{dev.ProductId:X4}; validando protocolo wired OUT 02 {DeviceProfile.InitCommandChannel:X2} / IN 01 {DeviceProfile.InitAckChannel:X2}.");
                    _device = dev;
                    _device.Open();

                    LogInput("[SYSTEM] Estableciendo configuración USB activa #1...");
                    _device.SetConfiguration(1);

                    // Reclaim interfaces
                    int[] requiredIfaces = { 0, DATA_IFACE, HANDSHAKE_IFACE };
                    foreach (int i in requiredIfaces)
                    {
                        bool isActive = false;
                        bool supportsDetach = _device.SupportsDetachKernelDriver();
                        if (supportsDetach)
                        {
                            isActive = _device.IsKernelDriverActive(i);
                            if (isActive)
                            {
                                LogInput($"[SYSTEM] Separando controlador del núcleo activo para interfaz #{i}...");
                                _device.DetachKernelDriver(i);
                            }
                        }
                        LogInput($"[SYSTEM] Reclamando interfaz #{i} (detachSoportado={supportsDetach}, núcleoActivo={isActive})...");
                        _device.ClaimInterface(i);
                    }

                    // Handshake Control Transfers
                    UsbDevice.ControlTransferTimeout = 1000;
                    LogInput("[SYSTEM] Enviando transferencia de control: Handshake Fase 1 (0x0303)...");
                    if (!SendControl(HANDSHAKE_IFACE, new byte[] { 0x03, 0x1D, 0x01 }, 0x0303))
                        throw new Exception("Handshake Fase 1 falló.");
                    Thread.Sleep(100);

                    LogInput("[SYSTEM] Enviando transferencia de control: Handshake Fase 2 (0x03CC)...");
                    if (!SendControl(HANDSHAKE_IFACE, new byte[] { 0xCC, 0x60 }, 0x03CC))
                        throw new Exception("Handshake Fase 2 falló.");
                    Thread.Sleep(200);

                    // SET_IDLE commands
                    LogInput("[SYSTEM] Enviando solicitudes de clase SET_IDLE a interfaces 3 y 4...");
                    TrySendClassRequest(DATA_IFACE, 0x0A, 0);
                    TrySendClassRequest(HANDSHAKE_IFACE, 0x0A, 0);
                    Thread.Sleep(100);

                    // Initialize ViGEmBus
                    try
                    {
                        SetConnectionStatus(
                            ConnectionStatusState.UsbConnecting,
                            LocalizationService.Get("StatusConnecting"),
                        LocalizationService.Get("StatusPreparingController"));
                        LogInput("[VIGEM] Inicializando cliente virtual ViGEmClient...");
                        _client = new ViGEmClient();
                        LogInput("[VIGEM] Creando instancia de controlador virtual Xbox 360...");
                        _xbox = _client.CreateXbox360Controller();
                        _xbox.AutoSubmitReport = false;
                        LogInput("[VIGEM] Conectando controlador virtual Xbox 360 al sistema...");
                        _xbox.Connect();
                        IsViGEmActive = true;
                        LogInput("[VIGEM] Controlador virtual Xbox 360 inicializado y enlazado correctamente");
                    }
                    catch (Exception ex)
                    {
                        IsViGEmActive = false;
                        LogInput($"[VIGEM] ERROR: {ex.Message}. Corriendo en modo monitor.");
                    }

                    // Open readers
                    LogInput("[SYSTEM] Abriendo Endpoint de Lectura para Ep01 (Entradas principales)...");
                    _reader = _device.OpenEndpointReader(ReadEndpointID.Ep01);

                    try
                    {
                        LogInput("[SYSTEM] Abriendo Endpoint de Escritura para Ep01 (Rumble)...");
                        _rumbleWriter = _device.OpenEndpointWriter(WriteEndpointID.Ep01);
                    }
                    catch (Exception ex)
                    {
                        LogInput($"[WARN] No se pudo abrir Endpoint de Escritura Ep01: {ex.Message}");
                    }

                    try
                    {
                        LogInput("[SYSTEM] Inicializando UsbEndpointWriter para Ep02...");
                        _writer2 = _device.OpenEndpointWriter(WriteEndpointID.Ep02);
                        LogInput("[OK] Writer Ep02 inicializado correctamente.");
                    }
                    catch (Exception ex)
                    {
                        LogInput($"[WARN] Fallo inicializando Ep02: {ex.Message}");
                    }


                    if (_xbox != null)
                    {
                        _xbox.FeedbackReceived += Xbox_FeedbackReceived;
                    }

                    try
                    {
                        LogInput("[SYSTEM] Abriendo Endpoint de Lectura para Ep82 (G-Keys)...");
                        _reader2 = _device.OpenEndpointReader(ReadEndpointID.Ep02);
                        LogInput("[OK] G-Keys habilitadas en Ep82.");
                    }
                    catch (Exception ex)
                    {
                        _reader2 = null;
                        LogInput($"[WARN] No se pudo abrir Ep82 para G-Keys: {ex.Message}");
                    }

                    LogInput("[SYSTEM] Ejecutando inicializacion de Software Mode...");
                    await InitializeUsbSoftwareModeOrThrowAsync(CancellationToken.None);

                    // Start background thread for polling
                    LogInput("[SYSTEM] Iniciando hilo de captura/polling en segundo plano...");
                    _cts = new CancellationTokenSource();
                    _pollingTask = Task.Run(() => PollingLoop(_cts.Token));

                    IsConnected = true;
                    IsConnecting = false;
                    ConnectionKind = ScufConnectionKind.Wired;
                    TransportName = "Wired USB";
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConnectionMode)));
                    SetConnectionStatus(
                        ConnectionStatusState.UsbConnected,
                        LocalizationService.Get("StatusControllerConnected"),
                        LocalizationService.Get("StatusControllerActive"));
                    LogInput("[SYSTEM] Dispositivo conectado y configurado correctamente");
                }
                catch (Exception ex)
                {
                    IsConnecting = false;
                    Disconnect();
                    if (!autoConnect)
                    {
                        SetConnectionStatus(
                            ConnectionStatusState.Error,
                            LocalizationService.Get("StatusCannotConnect"),
                            ex.Message);
                        LogInput($"[FATAL] Conexión fallida: {ex.Message}");
                    }
                }
                });
            }
            finally
            {
                _connectionAttemptGate.Release();
            }
        }

        private sealed class ConnectionSuspension : IDisposable
        {
            private ScufDeviceService? _owner;

            public ConnectionSuspension(ScufDeviceService owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;

                Interlocked.Decrement(ref owner._connectionSuspensionCount);
                owner._connectionAttemptGate.Release();
                owner.LogInput("[SYSTEM] Controller connections resumed after driver maintenance.");
            }
        }

        public void Disconnect()

        {
            if (Interlocked.Exchange(ref _disconnecting, 1) == 1)
            {
                try { _cts?.Cancel(); } catch { }
                return;
            }

            LogInput("[SYSTEM] Iniciando proceso de desconexión manual...");

            if (_xbox != null)
            {
                _xbox.FeedbackReceived -= Xbox_FeedbackReceived;
            }
            if (_rumbleWriter != null)
            {
                try { GC.SuppressFinalize(_rumbleWriter); } catch { }
                _rumbleWriter = null;
            }
            if (_writer2 != null)
            {
                _writer2 = null;
            }

            _cts?.Cancel();

            try { CleanupWirelessAsync().Wait(1000); } catch { }

            if (_modeController != null)
            {
                try { _modeController.StopIcueKeepAliveAsync().Wait(500); } catch { }
                _modeController = null;
            }

            var pollingTask = _pollingTask;
            bool calledFromPolling = pollingTask != null && Task.CurrentId == pollingTask.Id;
            bool pollingStopped = true;

            if (pollingTask != null && !calledFromPolling)
            {
                try
                {
                    pollingStopped = pollingTask.Wait(2000);
                }
                catch
                {
                    pollingStopped = true;
                }

                if (!pollingStopped)
                {
                    LogInput("[WARN] El polling USB no se detuvo a tiempo; se omite la liberacion nativa para evitar un crash.");
                }
            }
            else if (calledFromPolling)
            {
                pollingStopped = false;
                LogInput("[WARN] Desconexion solicitada desde el polling USB; se omite la liberacion nativa inmediata.");
            }

            if (pollingStopped)
            {
                _pollingTask = null;
            }
            _cts = null;

            _reader = null;
            _reader2 = null;
            _writer2 = null;
            _rumbleWriter = null;

            try { WriteTelemetryComm($"\n=== Session Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==="); }
            catch { }

            try
            {
                bool canDisposeUsb = pollingStopped && !calledFromPolling;
                if (canDisposeUsb && _device != null && _device.IsOpen)
                {
                    try { _device.ReleaseInterface(0); } catch { }
                    try { _device.ReleaseInterface(DATA_IFACE); } catch { }
                    try { _device.ReleaseInterface(HANDSHAKE_IFACE); } catch { }

                    // Re-attach every kernel driver independently so one failed interface
                    // cannot prevent the remaining interfaces from being restored.
                    try
                    {
                        if (_device.SupportsDetachKernelDriver())
                        {
                            TryReattachKernelDriver(0);
                            TryReattachKernelDriver(DATA_IFACE);
                            TryReattachKernelDriver(HANDSHAKE_IFACE);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogInput(string.Format(
                            LocalizationService.Get("KernelDriverCapabilityCheckFailedFormat"),
                            ex.Message));
                    }

                    _device.Close();
                    if (_device is IDisposable disposableDevice)
                    {
                        try { disposableDevice.Dispose(); } catch { }
                    }
                    else
                    {
                        try { GC.SuppressFinalize(_device); } catch { }
                    }
                    LogInput("[SYSTEM] Dispositivo USB cerrado y liberado correctamente");
                }
                else if (!canDisposeUsb && (_device != null || _context != null))
                {
                    LogInput("[WARN] Recursos USB separados sin Dispose nativo porque el polling seguia cerrando.");
                }
                _device = null;

                if (canDisposeUsb && _context != null)
                {
                    _context.Dispose();
                    LogInput("[SYSTEM] Contexto de USB liberado");
                }
                _context = null;

                if (IsViGEmActive)
                {
                    try { _xbox?.Disconnect(); } catch { }
                    _xbox = null;
                    _client?.Dispose();
                    _client = null;
                    LogInput("[VIGEM] Controlador virtual Xbox 360 detenido y recursos liberados");
                }

                IsConnected = false;
                IsConnecting = false;
                IsViGEmActive = false;
                ConnectionKind = ScufConnectionKind.Wired;
                TransportName = "Wired USB";
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ConnectionMode)));
                SetWaitingForControllerStatus();
                LogInput("[SYSTEM] Desconectado del dispositivo");
                WriteTelemetryComm("=== Disconnected manually ===");
            }
            catch (Exception ex)
            {
                SetConnectionStatus(
                    ConnectionStatusState.Error,
                    LocalizationService.Get("StatusDisconnectError"),
                    ex.Message);
                LogInput($"[ERROR] Fallo al desconectar: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _disconnecting, 0);
            }
        }

        private void TryReattachKernelDriver(int interfaceId)
        {
            try
            {
                _device?.AttachKernelDriver(interfaceId);
            }
            catch (Exception ex)
            {
                LogInput(string.Format(
                    LocalizationService.Get("KernelDriverReattachFailedFormat"),
                    interfaceId,
                    ex.Message));
            }
        }

        private async Task InitializeUsbSoftwareModeOrThrowAsync(CancellationToken ct)
        {
            if (_writer2 == null || _reader2 == null)
            {
                throw new InvalidOperationException("Canal USB de configuracion no disponible para Software Mode.");
            }

            _transport = new ScufRawTransport(_reader2, _writer2, LogInput);
            _ackReader = new ScufAckReader(_transport);
            _modeController = new DeviceModeController(UsbLock, _ackReader, _transport, LogInput);

            Exception? lastError = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        LogInput("[SYSTEM] Reintentando inicializacion de Software Mode...");
                    }

                    await InitializeDeviceInSoftwareModeAsync(ct);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    LogInput($"[WARN] Intento {attempt}/2 de Software Mode fallo: {ex.Message}");

                    if (attempt < 2)
                    {
                        await Task.Delay(300, ct);
                    }
                }
            }

            throw new TimeoutException("No se pudo completar el handshake de Software Mode por USB.", lastError);
        }

        private bool SendControl(int iface, byte[] payload, int wValue)
        {
            if (_device == null) return false;
            byte[] buf = new byte[DeviceProfile.ReportSize];
            Array.Copy(payload, buf, payload.Length);
            var setup = new UsbSetupPacket((byte)0x21, 0x09, (short)wValue, (short)iface, 64);
            try
            {
                int transferred = _device.ControlTransfer(setup, buf, 0, 64);
                return transferred > 0;
            }
            catch
            {
                return false;
            }
        }

        private void RequestUsbDisconnectFromPolling()
        {
            try { _cts?.Cancel(); } catch { }
            _ = Task.Run(Disconnect);
        }

        private void PollingLoop(CancellationToken token)
        {
            if (_reader == null) return;

            byte[] buf = new byte[DeviceProfile.ReportSize];
            byte[] last = new byte[FRAME_SIZE];
            bool baselineCaptured = false;

            byte[] buf2 = new byte[DeviceProfile.ReportSize];
            byte[] last2 = new byte[FRAME_SIZE];
byte _swPaddlesState = 0;
            byte _lastSwPaddlesState = 0;
            byte _swGKeysState = 0;
            byte _lastSwGKeysState = 0;
            last2[0] = 0x11;  // Report ID conocido para G-Keys
            // last2[1..] ya es 0x00 (estado idle) por defecto del array
            bool baselineCaptured2 = true;  // No capturar baseline — usar idle conocido

            int consecutiveErrors = 0;

            while (!token.IsCancellationRequested)
            {
                if (_modeController != null && _modeController.IsHandshakeActive)
                {
                    Thread.Sleep(10);
                    continue;
                }

                int xferred = 0;
                Error err = Error.Success;

                if (UsbLock.Wait(0))
                {
                    try
                    {
                        err = _reader.Read(buf, 150, out xferred);
                        if (err != Error.Success && err != Error.Io)
                        {
                            consecutiveErrors++;
                            if (consecutiveErrors > 10)
                            {
                                LogInput($"[FATAL] Demasiados errores consecutivos ({err}). Desconectando...");
                                RequestUsbDisconnectFromPolling();
                                break;
                            }
                        }
                        else
                        {
                            consecutiveErrors = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogInput($"[FATAL] Excepción durante lectura USB. Mando desconectado? {ex.Message}");
                        RequestUsbDisconnectFromPolling();
                        break;
                    }
                    finally
                    {
                        UsbLock.Release();
                    }
                }
                else
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (err != Error.Success || xferred == 0)
                {
                    continue;
                }

                // ── Read Endpoint 0x82 (Ep02 - G-Keys) with remap + suppression ──
                _activeGKeyTargets.Clear();

                if (_reader2 != null)
                {
                    if (UsbLock.Wait(0))
                    {
                        try
                        {
                            int xferred2 = 0;
                            var err2 = _reader2.Read(buf2, 1, out xferred2);
                            if (err2 == Error.Success && xferred2 > 0)
                            {
                                int len2 = Math.Min(xferred2, FRAME_SIZE);

                                // Raw telemetry: log every Ep02 read that has changed data
                                bool changed2 = false;
                                if (!baselineCaptured2)
                                {
                                    Array.Copy(buf2, last2, len2);
                                    baselineCaptured2 = true;
                                }
                                else
                                {
                                    for (int i = 0; i < len2; i++)
                                    {
                                        if (buf2[i] != last2[i]) changed2 = true;
                                    }

                                    if (changed2)
                                    {
                                        if (buf2[0] == 0x11)
                                        {
                                            if (buf2[1] != 0x00)
                                            {
                                                // Try to match against known G-Key bitmasks
                                                var mapping = _mapping?.Mappings.FirstOrDefault(m => m.Type == "gkey" && m.BitMask == buf2[1]);
                                                if (mapping != null)
                                                {
                                                    _activeGKeyName = mapping.Name;

                                                    if (GKeyRemapTable.TryGetValue(mapping.Name, out var target))
                                                    {
                                                        LastGKeyAction = $"{mapping.Name} → {target}";
                                                        LogInput($"[G-KEY]  [{mapping.Name} pressed (→ {target})] raw=0x{buf2[1]:X2}");
                                                    }
                                                    else
                                                    {
                                                        LastGKeyAction = $"{mapping.Name} pressed";
                                                        LogInput($"[G-KEY]  [{mapping.Name} pressed] raw=0x{buf2[1]:X2}");
                                                    }
                                                }
                                                else
                                                {
                                                    // UNKNOWN G-Key — bitmask not in our table
                                                    _activeGKeyName = $"UNKNOWN_0x{buf2[1]:X2}";
                                                    LastGKeyAction = $"Unknown 0x{buf2[1]:X2}";
                                                    LogInput($"[G-KEY]  [UNKNOWN G-Key raw=0x{buf2[1]:X2} — no bitmask match, suppressing Guide]");
                                                    WriteTelemetryInput($"[G-KEY-DIAG] buf2[1]=0x{buf2[1]:X2} does NOT match any configured gkey. Table: {string.Join(" ", _mapping?.Mappings.Where(m => m.Type == "gkey").Select(m => $"{m.Name}=0x{m.BitMask:X2}") ?? Array.Empty<string>())}");
                                                }
                                            }
                                            else
                                            {
                                                // G-Key released — only log if we had an active Ep02 G-Key
                                                if (_activeGKeyName != null)
                                                {
                                                    string releasedName = _activeGKeyName;
                                                    _activeGKeyName = null;
                                                    LastGKeyAction = $"{releasedName} released";
                                                    LogInput($"[G-KEY]  [{releasedName} released]");
                                                }
                                                else
                                                {
                                                    WriteTelemetryComm($"[EP02-IDLE] buf2[1]=0x00 (baseline settle — ignored)");
                                                }
                                            }
                                        }
                                        else if (buf2[0] == 0x03 && buf2[2] == 0x02)
                                        {
                                            _swPaddlesState = buf2[5];
                                            _swGKeysState = buf2[6];
                                            WriteTelemetryInput($"[EP02-SW-BTNS] Paddles=0x{_swPaddlesState:X2} GKeys=0x{_swGKeysState:X2}");
                                        }
                                        else
                                        {
                                            // Non-0x11 report from Ep02 — log for diagnostics
                                            WriteTelemetryComm($"[EP02-OTHER] reportId=0x{buf2[0]:X2} data={HexDump(buf2, len2)}");
                                        }

                                        Array.Copy(buf2, last2, len2);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            UsbLock.Release();
                        }
                    }
                }

                // Compute G-Key remap targets for Ep02 G-Keys (G1-G4 only)
                // G5 usa _g5Active y se gestiona en el bloque Ep01 más abajo
                if (_activeGKeyName != null && !_activeGKeyName.StartsWith("G5") && !_activeGKeyName.StartsWith("UNKNOWN_"))
                {
                    _activeGKeyTargets.Add(_activeGKeyName);
                }

                if (err == Error.Timeout) continue;
                if (err != Error.Success)
                {
                    Thread.Sleep(10);
                    continue;
                }
                if (xferred == 0) continue;

                int len = Math.Min(xferred, FRAME_SIZE);
                if (!baselineCaptured)
                {
                    Array.Copy(buf, last, len);
                    baselineCaptured = true;
                    continue;
                }

                if (!IsConnected)
                {
                    continue;
                }

                // ── Parse inputs ──
                short lx = (short)(buf[1] | (buf[2] << 8));
                short ly = (short)(buf[3] | (buf[4] << 8));
                short rx = (short)(buf[5] | (buf[6] << 8));
                short ry = (short)(buf[7] | (buf[8] << 8));

                // Triggers are 10-bit (0-1023), packed across bytes 9-10-11:
                //   LT = B9[7:0] | BA[1:0]<<8       (low 8 in B9, high 2 in BA bits 0-1)
                //   RT = BA[7:2]>>2 | BB[3:0]<<6     (low 6 in BA bits 2-7, high 4 in BB bits 0-3)
                ushort lt = (ushort)(buf[9] | ((buf[10] & 0x03) << 8));
                ushort rt = (ushort)(((buf[10] >> 2) & 0x3F) | ((buf[11] & 0x0F) << 6));

                // Hat switch uses bits 4-7 of byte 11 (0x80=neutral, 0x60=Up, 0x40=Down, 0x20=Left, 0x00=Right)
                byte hat = (byte)(buf[11] & 0xF0);
                byte bc = buf[12];
                byte bd = buf[13];

                // Feed sticks and triggers to service properties
                LeftStickX = lx; LeftStickY = ly == short.MinValue ? short.MaxValue : (short)-ly;
                RightStickX = rx; RightStickY = ry == short.MinValue ? short.MaxValue : (short)-ry;
                LeftTrigger = lt; RightTrigger = rt;

                var newlyPressedPaddles = new List<string>();
                var activePhysicalPaddles = new List<string>();
                var activeSoftwareGKeys = new List<string>();

                if (CurrentOperatingState == "SoftwareIcueSession")
                {
                    void CheckPaddleSw(byte mask, string name)
                    {
                        bool isPressed = (_swPaddlesState & mask) != 0;
                        bool wasPressed = (_lastSwPaddlesState & mask) != 0;
                        if (isPressed) activePhysicalPaddles.Add(name);
                        if (isPressed && !wasPressed) newlyPressedPaddles.Add(name);
                    }

                    CheckPaddleSw(0x04, "Paddle_R5");
                    CheckPaddleSw(0x10, "Paddle_L4");
                    CheckPaddleSw(0x08, "Paddle_R4");
                    CheckPaddleSw(0x20, "Paddle_L5");
                    CheckPaddleSw(0x40, "SAX_L");
                    CheckPaddleSw(0x80, "SAX_R");

                    _lastSwPaddlesState = _swPaddlesState;

                    void CheckGKeySw(byte mask, string name)
                    {
                        bool isPressed = (_swGKeysState & mask) != 0;
                        bool wasPressed = (_lastSwGKeysState & mask) != 0;

                        if (isPressed)
                        {
                            activeSoftwareGKeys.Add(name);
                            _activeGKeyTargets.Add(name);
                        }

                        if (isPressed == wasPressed)
                        {
                            return;
                        }

                        if (isPressed)
                        {
                            var target = GKeyRemapTable.TryGetValue(name, out var remap) ? remap : "?";
                            LastGKeyAction = $"{name} → {target}";
                            LogInput($"[G-KEY]  [{name} pressed (→ {target})] via Ep02 swGKeys=0x{_swGKeysState:X2}");
                        }
                        else
                        {
                            LastGKeyAction = $"{name} released";
                            LogInput($"[G-KEY]  [{name} released] via Ep02 swGKeys=0x{_swGKeysState:X2}");
                        }
                    }

                    CheckGKeySw(0x04, "G1");
                    CheckGKeySw(0x08, "G2");
                    CheckGKeySw(0x10, "G3");
                    CheckGKeySw(0x20, "G4");
                    CheckGKeySw(0x40, "G5");
                    _lastSwGKeysState = _swGKeysState;

                    _activeGKeyName = null;
                    if (activeSoftwareGKeys.Count > 0) _activeGKeyName = activeSoftwareGKeys[0];
                }
                else
                {
                    if (_mapping != null)
                    {
                        foreach (var m in _mapping.Mappings)
                        {
                            if (m.Type == "paddle")
                            {
                                bool wasPressed = (last[m.ByteIndex] & m.BitMask) != 0;
                                bool isPressed = (buf[m.ByteIndex] & m.BitMask) != 0;
                                if (isPressed) activePhysicalPaddles.Add(m.Name);
                                if (isPressed && !wasPressed) newlyPressedPaddles.Add(m.Name);
                            }
                        }
                    }
                }

                bool ep01G1Active = (bd & 0x08) != 0;
                bool ep01G5Active = (bd & 0x80) != 0;
                bool wasG5Active = _g5Active;

                if (ep01G1Active)
                {
                    _activeGKeyTargets.Add("G1");
                }

                if (ep01G5Active)
                {
                    _activeGKeyTargets.Add("G5");
                }

                _g1Active = _activeGKeyTargets.Contains("G1");
                _g5Active = _activeGKeyTargets.Contains("G5");

                if (_g5Active && !wasG5Active)
                {
                    var route = ep01G5Active ? "Ep01 bd=0x80" : "Ep02 swGKeys=0x40";
                    LogInput($"[G5] Press detected via {route}; ECO reapply disabled. Suppressor should prevent hardware ECO toggle.");
                }

                if (_xbox != null)
                {
                    // ── Sticks and triggers ──
                    var ltOut = GetTriggerOutputByte(lt);
                    var rtOut = GetTriggerOutputByte(rt);

                    // ── Collect paddle targets (which buttons paddles activate this frame) ──
                    _activePaddleTargets.Clear();
                    long now = Environment.TickCount64;
                    var smoothedActivePaddles = new HashSet<string>();
                    if (_mapping != null)
                    {
                        foreach (var m in _mapping.Mappings)
                        {
                            if (m.Type == "paddle")
                            {
                                bool bitActive = activePhysicalPaddles.Contains(m.Name);
                                if (bitActive)
                                {
                                    _paddleLastSeen[m.Name] = now;
                                }

                                bool isHeld = _paddleLastSeen.TryGetValue(m.Name, out var lastSeen)
                                              && (now - lastSeen) < PADDLE_HOLD_MS;

                                if (isHeld)
                                {
                                    smoothedActivePaddles.Add(m.Name);
                                }
                            }
                        }
                    }


                    bool isShiftHeld = false;
                    byte bdCleanCheck = (byte)(bd & 0x03);

                    // Check if shift modifier is held
                    if (ShiftModifierButton?.StartsWith("Paddle_") == true || ShiftModifierButton?.StartsWith("SAX_") == true)
                    {
                        isShiftHeld = activePhysicalPaddles.Contains(ShiftModifierButton);
                    }
                    else if (ShiftModifierButton?.StartsWith("G") == true)
                    {
                        if (ShiftModifierButton == "G1") isShiftHeld = _g1Active;
                        else if (ShiftModifierButton == "G5") isShiftHeld = _g5Active;
                        else if (_activeGKeyName == ShiftModifierButton) isShiftHeld = true;
                    }
                    else
                    {
                        if (ShiftModifierButton == "A" && (bc & 0x01) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "B" && (bc & 0x02) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "X" && (bc & 0x04) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "Y" && (bc & 0x08) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "LeftShoulder" && (bc & 0x10) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "RightShoulder" && (bc & 0x20) != 0) isShiftHeld = true;
                        if ((ShiftModifierButton == "LT" || ShiftModifierButton == "LeftTrigger") && lt > TriggerPressedThreshold) isShiftHeld = true;
                        if ((ShiftModifierButton == "RT" || ShiftModifierButton == "RightTrigger") && rt > TriggerPressedThreshold) isShiftHeld = true;
                        if (ShiftModifierButton == "Back" && (bc & 0x40) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "Start" && (bc & 0x80) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "LeftThumb" && (bdCleanCheck & 0x01) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "RightThumb" && (bdCleanCheck & 0x02) != 0) isShiftHeld = true;
                        if (ShiftModifierButton == "Up" && hat == 0x00) isShiftHeld = true;
                        if (ShiftModifierButton == "Right" && hat == 0x20) isShiftHeld = true;
                        if (ShiftModifierButton == "Down" && hat == 0x40) isShiftHeld = true;
                        if (ShiftModifierButton == "Left" && hat == 0x60) isShiftHeld = true;
                    }

                    UpdateShiftHeld(isShiftHeld);

                    var activePaddleTable = IsShiftHeld ? ShiftPaddleRemapTable : PaddleRemapTable;
                    var activeGKeyTable = IsShiftHeld ? ShiftGKeyRemapTable : GKeyRemapTable;
                    var activeButtonTable = IsShiftHeld ? ShiftButtonRemapTable : ButtonRemapTable;
                    var activeAdvancedTable = IsShiftHeld ? ShiftAdvancedRemapTable : AdvancedRemapTable;

                    // ══════════════════════════════════════════════════════════
                    // FULL SUPPRESSION: Collect ALL targets into _frameTargets.
                    // The virtual controller ONLY receives what is in this set.
                    // Hardware buttons are NEVER passed through directly.
                    // ══════════════════════════════════════════════════════════
                    // FULL SUPPRESSION: Collect ALL targets into _frameTargets.
                    // The virtual controller ONLY receives what is in this set.
                    // Hardware buttons are NEVER passed through directly.
                    // ══════════════════════════════════════════════════════════
                    _frameTargets.Clear();

                    void AddButtonGesture(string sourceName, bool isPressed)
                    {
                        var simpleTarget = activeButtonTable.TryGetValue(sourceName, out var target) ? target : sourceName;
                        CollectGestureTarget(sourceName, isPressed, simpleTarget, activeAdvancedTable, _frameTargets, now, IsShiftHeld);
                    }

                    AddButtonGesture("A", (bc & 0x01) != 0);
                    AddButtonGesture("B", (bc & 0x02) != 0);
                    AddButtonGesture("X", (bc & 0x04) != 0);
                    AddButtonGesture("Y", (bc & 0x08) != 0);
                    AddButtonGesture("LeftShoulder", (bc & 0x10) != 0);
                    AddButtonGesture("RightShoulder", (bc & 0x20) != 0);
                    AddButtonGesture("Back", (bc & 0x40) != 0);
                    AddButtonGesture("Start", (bc & 0x80) != 0);
                    AddButtonGesture("LeftThumb", ((bd & 0x03) & 0x01) != 0);
                    AddButtonGesture("RightThumb", ((bd & 0x03) & 0x02) != 0);
                    AddButtonGesture("Up", hat == 0x00);
                    AddButtonGesture("Right", hat == 0x20);
                    AddButtonGesture("Down", hat == 0x40);
                    AddButtonGesture("Left", hat == 0x60);
                    CollectTriggerGestureTargets(lt, rt, ltOut, rtOut, activeButtonTable, activeAdvancedTable, _frameTargets, now, IsShiftHeld, out ltOut, out rtOut);

                    foreach (var paddle in new[] { "Paddle_R4", "Paddle_R5", "Paddle_L4", "Paddle_L5", "SAX_L", "SAX_R" })
                    {
                        var simpleTarget = activePaddleTable.TryGetValue(paddle, out var target) ? target : "Sin Mapeo";
                        CollectGestureTarget(paddle, smoothedActivePaddles.Contains(paddle), simpleTarget, activeAdvancedTable, _frameTargets, now, IsShiftHeld);
                    }

                    foreach (var gKey in new[] { "G1", "G2", "G3", "G4", "G5" })
                    {
                        var simpleTarget = activeGKeyTable.TryGetValue(gKey, out var target) ? target : "Sin Mapeo";
                        CollectGestureTarget(gKey, _activeGKeyTargets.Contains(gKey), simpleTarget, activeAdvancedTable, _frameTargets, now, IsShiftHeld);
                    }

                    AddActivePulseTargets(_frameTargets, now);
                    ProcessMacroTargets(_frameTargets);
                    ProcessActionTargets(_frameTargets);
                    ObserveMacroControllerStates(new (string Target, bool IsPressed)[]
                    {
                        ("A", (bc & 0x01) != 0),
                        ("B", (bc & 0x02) != 0),
                        ("X", (bc & 0x04) != 0),
                        ("Y", (bc & 0x08) != 0),
                        ("LeftShoulder", (bc & 0x10) != 0),
                        ("RightShoulder", (bc & 0x20) != 0),
                        ("Back", (bc & 0x40) != 0),
                        ("Start", (bc & 0x80) != 0),
                        ("LeftThumb", (bd & 0x01) != 0),
                        ("RightThumb", (bd & 0x02) != 0),
                        ("Guide", (bd & 0x04) != 0),
                        ("Up", hat == 0x00),
                        ("Right", hat == 0x20),
                        ("Down", hat == 0x40),
                        ("Left", hat == 0x60),
                        ("LeftTrigger", lt > TriggerPressedThreshold),
                        ("RightTrigger", rt > TriggerPressedThreshold),
                        ("Paddle_R4", smoothedActivePaddles.Contains("Paddle_R4")),
                        ("Paddle_R5", smoothedActivePaddles.Contains("Paddle_R5")),
                        ("Paddle_L4", smoothedActivePaddles.Contains("Paddle_L4")),
                        ("Paddle_L5", smoothedActivePaddles.Contains("Paddle_L5")),
                        ("SAX_L", smoothedActivePaddles.Contains("SAX_L")),
                        ("SAX_R", smoothedActivePaddles.Contains("SAX_R")),
                        ("G1", _activeGKeyTargets.Contains("G1")),
                        ("G2", _activeGKeyTargets.Contains("G2")),
                        ("G3", _activeGKeyTargets.Contains("G3")),
                        ("G4", _activeGKeyTargets.Contains("G4")),
                        ("G5", _activeGKeyTargets.Contains("G5"))
                    });
                    SubmitVirtualOutput(ltOut, rtOut, lx, LeftStickY, rx, RightStickY);
                }

                // ── Compare with last frame for logs and UI highlights ──
                bool changed = false;
                for (int i = 0; i < len; i++)
                {
                    if (buf[i] != last[i]) changed = true;
                }

                if (changed)
                {
                    // Update digital button properties on UI — reflects hardware, not virtual output
                    ButtonA     = ((bc & 0x01) != 0);
                    ButtonB     = ((bc & 0x02) != 0);
                    ButtonX     = ((bc & 0x04) != 0);
                    ButtonY     = ((bc & 0x08) != 0);
                    ButtonLB    = ((bc & 0x10) != 0);
                    ButtonRB    = ((bc & 0x20) != 0);
                    ButtonBack  = ((bc & 0x40) != 0);
                    ButtonStart = ((bc & 0x80) != 0);
                    ButtonL3    = ((bd & 0x01) != 0);
                    ButtonR3    = ((bd & 0x02) != 0);

                    bool rawGuide = (bd & 0x04) != 0;
                    if (rawGuide)
                    {
                        _guidePressedAt = Environment.TickCount64;
                        ButtonGuide = true;
                        LogInput("[BUTTON] Guide (Home) physical release pulse detected (0x04) - activating virtual Guide");
                    }

                    byte hatFiltered = hat;


                    DPadState = hatFiltered == 0x00 ? "Up"
                              : hatFiltered == 0x40 ? "Down"
                              : hatFiltered == 0x60 ? "Left"
                              : hatFiltered == 0x20 ? "Right"
                              : "Neutral";

                    // Telemetry: log Ep01 button byte changes (bytes 0, 11-15) with annotations, ignoring axes to avoid jitter noise
                    for (int bi = 0; bi < Math.Min(len, 16); bi++)
                    {
                        if (bi >= 1 && bi <= 10) continue; // Skip axes with jitter

                        if (buf[bi] != last[bi])
                        {
                            byte diff = (byte)(buf[bi] ^ last[bi]);

                            // Prevent log spam from Right Trigger jitter in lower 4 bits of byte 11
                            if (bi == 11 && (diff & 0xF0) == 0) continue;

                            string annotation = "";
                            if (bi == 11) // Hat and triggers high bits
                            {
                                var parts = new List<string>();
                                byte hatVal = (byte)(buf[bi] & 0xF0);
                                byte previousHat = (byte)(last[bi] & 0xF0);
                                if (hatVal != previousHat)
                                {
                                    string hatStr = hatVal == 0x00 ? "Up"
                                                  : hatVal == 0x40 ? "Down"
                                                  : hatVal == 0x60 ? "Left"
                                                  : hatVal == 0x20 ? "Right"
                                                  : "Neutral";
                                    parts.Add($"Hat={hatStr}");
                                }
                                if (parts.Count > 0) annotation = $" [{string.Join("+", parts)}]";
                            }
                            else if (bi == 13) // bd byte — G-Keys, Thumbsticks and Home/Guide
                            {
                                var parts = new List<string>();
                                if ((diff & 0x01) != 0) parts.Add("L3");
                                if ((diff & 0x02) != 0) parts.Add("R3");
                                if ((diff & 0x04) != 0) parts.Add("GuidePulse");
                                if ((diff & 0x08) != 0) parts.Add("G1");
                                if ((diff & 0x10) != 0) parts.Add("G2-phantom");
                                if ((diff & 0x20) != 0) parts.Add("G3-phantom");
                                if ((diff & 0x40) != 0) parts.Add("G4-phantom");
                                if ((diff & 0x80) != 0) parts.Add("G5");
                                if (parts.Count > 0) annotation = $" [{string.Join("+", parts)}]";
                            }
                            WriteTelemetryInput($"[EP01-BYTE{bi}] 0x{last[bi]:X2} → 0x{buf[bi]:X2} (diff=0x{diff:X2}){annotation}");
                        }
                    }
                    // Log G1 state changes (detected from Ep01)
                    bool g1Now = (bd & 0x08) != 0;
                    bool g1Prev = (last.Length > 13) ? (last[13] & 0x08) != 0 : false;
                    if (g1Now != g1Prev)
                    {
                        if (g1Now)
                        {
                            string g1t = GKeyRemapTable.GetValueOrDefault("G1", "?");
                            LastGKeyAction = $"G1 → {g1t}";
                            LogInput($"[G-KEY]  [G1 pressed (→ {g1t})] via Ep01 bd=0x{bd:X2}");
                        }
                        else
                        {
                            LastGKeyAction = "G1 released";
                            LogInput("[G-KEY]  [G1 released]");
                        }
                    }

                    // Log G5 state changes (detected from Ep01)
                    bool g5Now = (bd & 0x80) != 0;
                    bool g5Prev = (last.Length > 13) ? (last[13] & 0x80) != 0 : false;
                    if (g5Now != g5Prev)
                    {
                        if (g5Now)
                        {
                            string g5t = GKeyRemapTable.GetValueOrDefault("G5", "?");
                            LastGKeyAction = $"G5 → {g5t}";
                            LogInput($"[G-KEY]  [G5 pressed (→ {g5t})] via Ep01 bd=0x{bd:X2}");
                        }
                        else
                        {
                            LastGKeyAction = "G5 released";
                            LogInput("[G-KEY]  [G5 released]");
                        }
                    }

                    if (_mapping != null)
                    {
                        var loggedKeys = new HashSet<string>();

                        for (int i = 0; i < len; i++)
                        {
                            if (buf[i] != last[i])
                            {
                                var byteMappings = _mapping.Mappings.Where(m => m.ByteIndex == i).ToList();
                                if (byteMappings.Count > 0)
                                {
                                    foreach (var m in byteMappings)
                                    {
                                        if (m.Type.StartsWith("button"))
                                        {
                                            // Skip Guide — bd & 0x80 is G5, not a real button
                                            if (m.Name == "Guide") continue;

                                            bool wasPressed = (last[i] & m.BitMask) != 0;
                                            bool isPressed = (buf[i] & m.BitMask) != 0;
                                            if (wasPressed != isPressed && !loggedKeys.Contains(m.Name))
                                            {
                                                loggedKeys.Add(m.Name);
                                                LogInput($"[INPUT]  [{m.Name} {(isPressed ? "pressed" : "released")}]");
                                            }
                                        }
                                        else if (m.Type == "paddle")
                                        {
                                            bool wasPressed = (last[i] & m.BitMask) != 0;
                                            bool isPressed = (buf[i] & m.BitMask) != 0;
                                            if (wasPressed != isPressed && !loggedKeys.Contains(m.Name))
                                            {
                                                loggedKeys.Add(m.Name);
                                                if (isPressed && PaddleRemapTable.TryGetValue(m.Name, out var remap) && remap != "Sin Mapeo" && !string.IsNullOrEmpty(remap))
                                                {
                                                    LastPaddleAction = $"{m.Name} → {remap}";
                                                    LogInput($"[PADDLE] [{m.Name} pressed (→ {remap})]");
                                                }
                                                else
                                                {
                                                    if (!isPressed) LastPaddleAction = "";
                                                    LogInput($"[PADDLE] [{m.Name} {(isPressed ? "pressed" : "released")}]");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    Array.Copy(buf, last, len);
                }

                if (ButtonGuide && (Environment.TickCount64 - _guidePressedAt) > 200)
                {
                    ButtonGuide = false;
                }

                RaiseFrameProcessedThrottled();
            }
        }

        private UsbDevice? FindSupportedWiredUsbDevice(UsbContext context, bool logCandidates)
        {
            UsbDevice? selected = null;
            foreach (var device in context.List())
            {
                var isCandidate = DeviceProfile.IsWired(device.VendorId, device.ProductId);
                var isExperimental = DeviceProfile.IsExperimentalWired(device.VendorId, device.ProductId);
                if (isCandidate || logCandidates)
                {
                    LogInput($"[DETECT] USB candidate transport=LibUSB VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4} supported={isCandidate} experimental={isExperimental} reason={(isCandidate ? (isExperimental ? "VID/PID experimental wired profile" : "VID/PID supported wired") : "VID/PID not in supported profile")}");
                }

                if (selected == null && isCandidate && device is UsbDevice usbDevice)
                {
                    selected = usbDevice;
                    continue;
                }

                if (device is IDisposable disposableDevice)
                {
                    try { disposableDevice.Dispose(); } catch { }
                }
            }

            return selected;
        }

        private bool IsWiredUsbDevicePresent()
        {
            try
            {
                using var context = new UsbContext();
                bool found = false;
                foreach (var device in context.List())
                {
                    if (!found && DeviceProfile.IsWired(device.VendorId, device.ProductId))
                    {
                        found = true;
                    }
                    if (device is IDisposable disposableDevice)
                    {
                        try { disposableDevice.Dispose(); } catch { }
                    }
                }
                return found;
            }
            catch
            {
                return false;
            }
        }

        public void SetEcoMode(bool enable)
        {
            if (_writer2 != null)
            {
                byte[] payload = new byte[DeviceProfile.ReportSize];
                payload[0] = 0x02;
                payload[1] = DeviceProfile.InitCommandChannel;
                payload[2] = 0x01;
                payload[3] = 0x0B;
                payload[4] = 0x00;
                payload[5] = (byte)(enable ? 0x01 : 0x00);

                try
                {
                    _writer2.Write(payload, 1000, out int transferred);
                }
                catch (Exception ex)
                {
                    LogInput($"[WARN] Error enviando EcoMode={enable}: {ex.Message}");
                }
            }
        }




    }
}
