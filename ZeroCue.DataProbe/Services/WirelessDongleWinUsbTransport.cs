using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ZeroCue.DataProbe.Services
{
    public enum WirelessWinUsbInterfaceTarget
    {
        AnyControl,
        RadioMi03,
        RuntimeMi04
    }

    public sealed class WirelessDongleWinUsbTransport : IScufTransport, IDisposable
    {
        private const int ReportLength = 64;
        private const int DefaultWriteTimeoutMs = 750;
        private const int DefaultReadTimeoutMs = 1000;

        private static readonly SupportedScufDeviceProfile DeviceProfile = SupportedScufDeviceProfile.ScufEnvisionPro;
        private readonly Action<string> _logger;
        private readonly WirelessWinUsbInterfaceTarget _interfaceTarget;
        private readonly bool _logReadPayloads;
        private readonly WirelessReceiverIdentity? _receiverIdentity;
        private SafeFileHandle? _deviceHandle;
        private IntPtr _winUsbHandle;
        private byte _outPipeId = 0x02;
        private byte _inPipeId = 0x82;
        private bool _disposed;
        private readonly Stopwatch _connectClock = new();
        private bool _firstWriteLogged;

        public WirelessDongleWinUsbTransport(
            Action<string> logger,
            WirelessWinUsbInterfaceTarget interfaceTarget = WirelessWinUsbInterfaceTarget.AnyControl,
            bool logReadPayloads = true,
            WirelessReceiverIdentity? receiverIdentity = null)
        {
            _logger = logger;
            _interfaceTarget = interfaceTarget;
            _logReadPayloads = logReadPayloads;
            _receiverIdentity = receiverIdentity;
        }

        public string? DevicePath { get; private set; }
        public IReadOnlyList<WinUsbPipeDescriptor> Pipes { get; private set; } = Array.Empty<WinUsbPipeDescriptor>();
        public long TimeToOpenWinUsbMs { get; private set; }
        public long? TimeToFirstReplayReportMs { get; private set; }
        public WirelessReceiverIdentity? SelectedReceiverIdentity { get; private set; }
        public Action<byte[], int>? FrameObserver { get; set; }
        public bool IsOpen => _winUsbHandle != IntPtr.Zero && _deviceHandle is { IsInvalid: false, IsClosed: false };

        public async Task<bool> ConnectAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _connectClock.Restart();
            _firstWriteLogged = false;
            LogTransport("=== Wireless WinUSB transport connect ===");
            var receiverIdentities = _receiverIdentity == null
                ? DeviceProfile.WirelessReceiverIdentities
                : new[] { _receiverIdentity };
            LogTransport($"Enumerating WinUSB/USB interface candidates profile={DeviceProfile.Name} identities={string.Join(',', receiverIdentities.Select(FormatIdentity))} scope={(_receiverIdentity == null ? "all" : "exact")} target={_interfaceTarget} requiredControlPipes OUT=0x{DeviceProfile.WinUsbOutPipe:X2} IN=0x{DeviceProfile.WinUsbInPipe:X2} reportSize={DeviceProfile.ReportSize}.");

            var candidates = receiverIdentities
                .SelectMany(identity => WinUsbInterop.EnumerateUsbDevicePaths(
                        $"vid_{identity.VendorId:x4}",
                        $"pid_{identity.ProductId:x4}",
                        LogTransport)
                    .Select(path => new ReceiverCandidate(path, identity)))
                .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => CandidatePriority(candidate.Path))
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            LogTransport($"WinUSB candidate count={candidates.Count}");
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var path = candidate.Path;
                var (vid, pid) = TryParseVidPid(path);
                LogTransport($"WinUSB candidate transport=WinUSB variant={candidate.Identity.Variant} experimental={candidate.Identity.IsExperimental} VID={FormatNullableHex(vid)} PID={FormatNullableHex(pid)} path={path} priority={CandidatePriority(path)}");
                if (TryOpenCandidate(path, out var pipes))
                {
                    DevicePath = path;
                    Pipes = pipes;
                    SelectedReceiverIdentity = candidate.Identity;
                    TimeToOpenWinUsbMs = _connectClock.ElapsedMilliseconds;
                    LogTransport($"WinUSB selected variant={candidate.Identity.Variant} experimental={candidate.Identity.IsExperimental} identity={FormatIdentity(candidate.Identity)} path={path}");
                    LogTransport($"WinUSB selected pipes OUT=0x{_outPipeId:X2} IN=0x{_inPipeId:X2}");
                    LogTransport($"TimeToOpenWinUsbMs={TimeToOpenWinUsbMs}");
                    return await Task.FromResult(true);
                }

                await Task.Delay(1, ct);
            }

            LogTransport("WirelessWinUsbControlPipesNotFound: no candidate exposed a supported 64-byte OUT/IN pipe pair.");
            return false;
        }

        private int CandidatePriority(string path)
        {
            if (_interfaceTarget == WirelessWinUsbInterfaceTarget.RadioMi03)
            {
                if (path.IndexOf("mi_03", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                return 1;
            }

            if (_interfaceTarget == WirelessWinUsbInterfaceTarget.RuntimeMi04)
            {
                if (path.IndexOf("mi_04&col02", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                if (path.IndexOf("mi_04", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                if (path.IndexOf("col02", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
                return 3;
            }

            return 0;
        }

        public Task DisconnectAsync()
        {
            LogTransport("Wireless WinUSB transport disconnect.");
            DisposeHandles();
            return Task.CompletedTask;
        }

        public Task WriteReportAsync(byte[] report, CancellationToken ct)
        {
            if (report.Length != ReportLength)
            {
                throw new InvalidOperationException($"WinUSB wireless reports must be exactly {ReportLength} bytes. Got {report.Length}.");
            }

            if (!IsOpen)
            {
                throw new InvalidOperationException("Wireless WinUSB transport is not open.");
            }

            if (_outPipeId == 0x00)
            {
                throw new InvalidOperationException("Wireless WinUSB candidate has no writable OUT pipe selected; refusing to write.");
            }

            ct.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                SetPipeTimeout(_outPipeId, DefaultWriteTimeoutMs, "write");

                if (!_firstWriteLogged)
                {
                    _firstWriteLogged = true;
                    TimeToFirstReplayReportMs = _connectClock.ElapsedMilliseconds;
                    LogTransport($"First WritePipe timestamp={DateTimeOffset.Now:O} TimeToFirstReplayReportMs={TimeToFirstReplayReportMs}");
                }

                var sw = Stopwatch.StartNew();
                unsafe
                {
                    var ok = WinUsbInterop.WinUsb_WritePipe(_winUsbHandle, _outPipeId, report, ReportLength, out var transferred, null);
                    sw.Stop();
                    if (!ok)
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err == WinUsbInterop.ERROR_SEM_TIMEOUT || err == WinUsbInterop.ERROR_OPERATION_ABORTED)
                        {
                            LogTransport($"WRITE WinUSB TIMEOUT/ABORT pipe=0x{_outPipeId:X2} durationMs={sw.ElapsedMilliseconds} win32={err}");
                            return;
                        }

                        if (err == WinUsbInterop.ERROR_GEN_FAILURE)
                        {
                            LogTransport($"WRITE WinUSB ERROR_GEN_FAILURE pipe=0x{_outPipeId:X2} durationMs={sw.ElapsedMilliseconds}. Treating as recoverable without resetting OUT pipe.");
                            Thread.Sleep(5);
                            return;
                        }
                        LogTransport($"WRITE WinUSB FAIL pipe=0x{_outPipeId:X2} durationMs={sw.ElapsedMilliseconds} win32={err} {new System.ComponentModel.Win32Exception(err).Message}");
                        throw new IOException($"WinUsb_WritePipe failed win32={err} {new System.ComponentModel.Win32Exception(err).Message}");
                    }

                    LogTransport($"WRITE WinUSB OK pipe=0x{_outPipeId:X2} bytes={transferred} durationMs={sw.ElapsedMilliseconds} payload={ScufReportBuilder.ToHex(report)}");
                }
            }, ct);
        }

        public Task<(bool Success, int BytesRead)> ReadReportAsync(byte[] buffer, int timeoutMs, CancellationToken ct)
        {
            if (buffer.Length < ReportLength)
            {
                throw new InvalidOperationException($"Read buffer must be at least {ReportLength} bytes.");
            }

            if (!IsOpen)
            {
                throw new InvalidOperationException("Wireless WinUSB transport is not open.");
            }

            ct.ThrowIfCancellationRequested();
            var effectiveTimeoutMs = timeoutMs <= 0 ? DefaultReadTimeoutMs : timeoutMs;
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                SetPipeTimeout(_inPipeId, effectiveTimeoutMs, "read");
                Array.Clear(buffer, 0, buffer.Length);

                var sw = Stopwatch.StartNew();
                unsafe
                {
                    var ok = WinUsbInterop.WinUsb_ReadPipe(_winUsbHandle, _inPipeId, buffer, ReportLength, out var transferred, null);
                    sw.Stop();
                    if (ok && transferred > 0)
                    {
                        NotifyFrameObserver(buffer, (int)transferred);
                        if (_logReadPayloads)
                        {
                            LogTransport($"READ WinUSB OK pipe=0x{_inPipeId:X2} bytes={transferred} durationMs={sw.ElapsedMilliseconds} payload={ScufReportBuilder.ToHex(buffer.Take((int)transferred).ToArray())}");
                        }
                        return (true, (int)transferred);
                    }

                    var err = Marshal.GetLastWin32Error();
                    if (err == WinUsbInterop.ERROR_SEM_TIMEOUT || err == WinUsbInterop.ERROR_OPERATION_ABORTED)
                    {
                        if (_logReadPayloads)
                        {
                            LogTransport($"READ WinUSB TIMEOUT pipe=0x{_inPipeId:X2} timeoutMs={effectiveTimeoutMs} durationMs={sw.ElapsedMilliseconds} win32={err}");
                        }
                        return (false, 0);
                    }

                    if (err == WinUsbInterop.ERROR_GEN_FAILURE)
                    {
                        LogTransport($"READ WinUSB ERROR_GEN_FAILURE pipe=0x{_inPipeId:X2} durationMs={sw.ElapsedMilliseconds}. Clearing stall and treating as recoverable.");
                        WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, _inPipeId);
                        Thread.Sleep(5);
                        return (false, 0);
                    }

                    LogTransport($"READ WinUSB FAIL pipe=0x{_inPipeId:X2} durationMs={sw.ElapsedMilliseconds} win32={err} {new System.ComponentModel.Win32Exception(err).Message}");
                    throw new IOException($"WinUsb_ReadPipe failed win32={err} {new System.ComponentModel.Win32Exception(err).Message}");
                }
            }, ct);
        }

        public async Task DrainAsync()
        {
            var buffer = new byte[ReportLength];
            using var cts = new CancellationTokenSource(100);
            while (!cts.Token.IsCancellationRequested && IsOpen)
            {
                try
                {
                    var (success, bytesRead) = await ReadReportAsync(buffer, 10, cts.Token);
                    if (success)
                    {
                        LogTransport($"[DRAIN] Wireless WinUSB discarded {bytesRead} bytes.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    break;
                }
            }
        }

        public void LogTransport(string message)
        {
            _logger($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}");
        }

        private void NotifyFrameObserver(byte[] buffer, int bytesRead)
        {
            var observer = FrameObserver;
            if (observer == null)
            {
                return;
            }

            try
            {
                var frame = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, frame, 0, bytesRead);
                observer(frame, frame.Length);
            }
            catch (Exception ex)
            {
                LogTransport($"Frame observer ignored error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeHandles();
        }

        private bool TryOpenCandidate(string path, out IReadOnlyList<WinUsbPipeDescriptor> pipes)
        {
            pipes = Array.Empty<WinUsbPipeDescriptor>();
            DisposeHandles();

            _deviceHandle = WinUsbInterop.OpenDeviceHandle(path);
            if (_deviceHandle.IsInvalid)
            {
                LogTransport(WinUsbInterop.FormatLastWin32Error("CreateFile WinUSB candidate failed"));
                _deviceHandle.Dispose();
                _deviceHandle = null;
                return false;
            }

            LogTransport("CreateFile WinUSB candidate OK.");
            if (!WinUsbInterop.WinUsb_Initialize(_deviceHandle, out _winUsbHandle))
            {
                LogTransport(WinUsbInterop.FormatLastWin32Error("WinUsb_Initialize failed"));
                DisposeHandles();
                return false;
            }

            // Disable USB Selective Suspend for this WinUSB session to improve RF dongle stability
            byte autoSuspend = 0;
            if (!WinUsbInterop.WinUsb_SetPowerPolicy(_winUsbHandle, WinUsbInterop.AUTO_SUSPEND, sizeof(byte), ref autoSuspend))
            {
                LogTransport(WinUsbInterop.FormatLastWin32Error("WinUsb_SetPowerPolicy (AUTO_SUSPEND) failed, continuing anyway"));
            }

            if (!WinUsbInterop.WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, out var descriptor))
            {
                LogTransport(WinUsbInterop.FormatLastWin32Error("WinUsb_QueryInterfaceSettings failed"));
                DisposeHandles();
                return false;
            }

            LogTransport($"WinUSB interface descriptor number={descriptor.bInterfaceNumber} alt={descriptor.bAlternateSetting} endpointCount={descriptor.bNumEndpoints} class=0x{descriptor.bInterfaceClass:X2}. Interface number is diagnostic only.");

            var foundPipes = new List<WinUsbPipeDescriptor>();
            for (byte i = 0; i < descriptor.bNumEndpoints; i++)
            {
                if (!WinUsbInterop.WinUsb_QueryPipe(_winUsbHandle, 0, i, out var pipe))
                {
                    LogTransport(WinUsbInterop.FormatLastWin32Error($"WinUsb_QueryPipe index={i} failed"));
                    continue;
                }

                var pipeInfo = new WinUsbPipeDescriptor(pipe.PipeId, pipe.MaximumPacketSize, pipe.Interval, pipe.PipeType.ToString());
                foundPipes.Add(pipeInfo);
                LogTransport($"WinUSB pipe index={i} id=0x{pipe.PipeId:X2} type={pipe.PipeType} maxPacket={pipe.MaximumPacketSize} interval={pipe.Interval}");
            }

            var hasOut02 = foundPipes.Any(p => p.PipeId == DeviceProfile.WinUsbOutPipe && p.MaximumPacketSize == DeviceProfile.ReportSize);
            var hasIn82 = foundPipes.Any(p => p.PipeId == DeviceProfile.WinUsbInPipe && p.MaximumPacketSize == DeviceProfile.ReportSize);
            var hasOut01 = foundPipes.Any(p => p.PipeId == 0x01 && p.MaximumPacketSize == ReportLength);
            var hasIn81 = foundPipes.Any(p => p.PipeId == 0x81 && p.MaximumPacketSize == ReportLength);

            if (_interfaceTarget == WirelessWinUsbInterfaceTarget.RadioMi03 && hasIn81)
            {
                _outPipeId = hasOut01 ? (byte)0x01 : (byte)0x00;
                _inPipeId = 0x81;
                LogTransport($"WinUSB auxiliary input pipe selection OK interface={descriptor.bInterfaceNumber} OUT={(hasOut01 ? "0x01" : "<none>")} IN=0x81 MaxPacketSize=64. Used for read-only radio frames; interface number was not required.");
            }
            else if ((_interfaceTarget == WirelessWinUsbInterfaceTarget.RuntimeMi04 || _interfaceTarget == WirelessWinUsbInterfaceTarget.AnyControl) && hasOut02 && hasIn82)
            {
                _outPipeId = DeviceProfile.WinUsbOutPipe;
                _inPipeId = DeviceProfile.WinUsbInPipe;
                LogTransport($"WinUSB pipe selection OK interface={descriptor.bInterfaceNumber} OUT=0x{_outPipeId:X2} IN=0x{_inPipeId:X2} MaxPacketSize={DeviceProfile.ReportSize}. Selected by pipes, not interface number.");
            }
            else
            {
                LogTransport($"WinUSB pipe selection rejected target={_interfaceTarget} hasOut02={hasOut02} hasIn82={hasIn82} hasOut01={hasOut01} hasIn81={hasIn81} reason={(hasOut02 || hasIn82 ? "incomplete required pipe pair" : "required control pipes not present")}");
                DisposeHandles();
                return false;
            }

            pipes = foundPipes;
            if (_outPipeId != 0x00)
            {
                SetPipeTimeout(_outPipeId, DefaultWriteTimeoutMs, "write");
            }
            SetPipeTimeout(_inPipeId, DefaultReadTimeoutMs, "read");
            return true;
        }

        private void SetPipeTimeout(byte pipeId, int timeoutMs, string direction)
        {
            var value = (uint)Math.Clamp(timeoutMs, 1, 30_000);
            if (!WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, pipeId, WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref value))
            {
                var err = Marshal.GetLastWin32Error();
                LogTransport($"WinUsb_SetPipePolicy {direction} timeout failed pipe=0x{pipeId:X2} timeoutMs={timeoutMs} win32={err} {new System.ComponentModel.Win32Exception(err).Message}");
            }

            uint autoClearStall = 1;
            if (!WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, pipeId, WinUsbInterop.AUTO_CLEAR_STALL, sizeof(uint), ref autoClearStall))
            {
                var err = Marshal.GetLastWin32Error();
                LogTransport($"WinUsb_SetPipePolicy {direction} AUTO_CLEAR_STALL failed pipe=0x{pipeId:X2} win32={err}");
            }
        }

        private void DisposeHandles()
        {
            if (_winUsbHandle != IntPtr.Zero)
            {
                try { WinUsbInterop.WinUsb_Free(_winUsbHandle); } catch { }
                _winUsbHandle = IntPtr.Zero;
            }

            _deviceHandle?.Dispose();
            _deviceHandle = null;
            DevicePath = null;
            SelectedReceiverIdentity = null;
            Pipes = Array.Empty<WinUsbPipeDescriptor>();
            _outPipeId = 0x02;
            _inPipeId = 0x82;
        }

        private static (int? Vid, int? Pid) TryParseVidPid(string path)
        {
            static int? ParseAfter(string source, string marker)
            {
                var index = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0 || index + marker.Length + 4 > source.Length)
                {
                    return null;
                }

                var hex = source.Substring(index + marker.Length, 4);
                return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)
                    ? value
                    : null;
            }

            return (ParseAfter(path, "vid_"), ParseAfter(path, "pid_"));
        }

        private static string FormatNullableHex(int? value) =>
            value.HasValue ? $"0x{value.Value:X4}" : "<unknown>";

        private static string FormatIdentity(WirelessReceiverIdentity identity) =>
            $"{identity.Variant}[VID_0x{identity.VendorId:X4},PID_0x{identity.ProductId:X4},experimental={identity.IsExperimental}]";

        private sealed record ReceiverCandidate(string Path, WirelessReceiverIdentity Identity);
    }

    public sealed record WinUsbPipeDescriptor(
        byte PipeId,
        ushort MaximumPacketSize,
        byte Interval,
        string PipeType);
}
