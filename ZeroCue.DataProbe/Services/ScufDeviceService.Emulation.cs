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
        private bool IsXboxButton(string target)
        {
            target = VirtualTarget.GetBaseTarget(NormalizeVirtualTarget(target));

            return target switch
            {
                "A" or "B" or "X" or "Y" or
                "LeftShoulder" or "RightShoulder" or
                "LeftTrigger" or "RightTrigger" or
                "Back" or "Start" or "Guide" or
                "LeftThumb" or "RightThumb" or
                "Up" or "Down" or "Left" or "Right" or
                "LS_Up" or "LS_Down" or "LS_Left" or "LS_Right" or
                "RS_Up" or "RS_Down" or "RS_Left" or "RS_Right" => true,
                _ => false
            };
        }

        public void TestRumble(byte largeMotor = 255, byte smallMotor = 255)
        {
            LogInput($"[RUMBLE] Testing Rumble (Large: {largeMotor}, Small: {smallMotor})...");
            SendRumble(largeMotor, smallMotor);

            Task.Delay(500).ContinueWith(_ =>
            {
                SendRumble(0, 0);
            });
        }

        private void SendRumble(byte largeMotor, byte smallMotor)
        {
            _ = QueueRumble(largeMotor, smallMotor);
        }

        private Task QueueRumble(byte largeMotor, byte smallMotor)
        {
            // The physical controller latches its last motor state. Coalesce bursts while
            // USB is busy, but always deliver the newest state (especially the 0/0 stop).
            lock (_rumbleDispatchLock)
            {
                _pendingLargeMotor = largeMotor;
                _pendingSmallMotor = smallMotor;
                _rumbleRequestVersion++;

                if (!_rumbleDispatchRunning)
                {
                    _rumbleDispatchRunning = true;
                    _rumbleDispatchTask = Task.Run(DispatchRumbleLoopAsync);
                }

                return _rumbleDispatchTask;
            }
        }

        private async Task DispatchRumbleLoopAsync()
        {
            while (true)
            {
                long dispatchedVersion;

                try
                {
                    if (_rumbleWriter != null)
                    {
                        await UsbLock.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            UsbEndpointWriter? writer = _rumbleWriter;
                            if (writer != null)
                            {
                                lock (_rumbleDispatchLock)
                                {
                                    _rumbleBuffer[8] = _pendingLargeMotor;
                                    _rumbleBuffer[9] = _pendingSmallMotor;
                                    dispatchedVersion = _rumbleRequestVersion;
                                }

                                var error = writer.Write(_rumbleBuffer, 100, out int bytesWritten);
                                if (error != Error.Success || bytesWritten != _rumbleBuffer.Length)
                                {
                                    LogInput($"[RUMBLE] WARN wired write result={error} bytes={bytesWritten}/{_rumbleBuffer.Length}.");
                                }
                            }
                            else
                            {
                                lock (_rumbleDispatchLock)
                                {
                                    dispatchedVersion = _rumbleRequestVersion;
                                }
                            }
                        }
                        finally
                        {
                            UsbLock.Release();
                        }
                    }
                    else if (ConnectionKind == ScufConnectionKind.Wireless &&
                             _wirelessWinUsbAuxRadioTransport != null)
                    {
                        byte[] wirelessRumbleBuffer = new byte[64];
                        lock (_rumbleDispatchLock)
                        {
                            _rumbleBuffer[8] = _pendingLargeMotor;
                            _rumbleBuffer[9] = _pendingSmallMotor;
                            Array.Copy(_rumbleBuffer, wirelessRumbleBuffer, _rumbleBuffer.Length);
                            dispatchedVersion = _rumbleRequestVersion;
                        }

                        await _wirelessWinUsbAuxRadioTransport
                            .WriteReportAsync(wirelessRumbleBuffer, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        lock (_rumbleDispatchLock)
                        {
                            dispatchedVersion = _rumbleRequestVersion;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogInput($"[RUMBLE] Error sending rumble: {ex.Message}");
                    lock (_rumbleDispatchLock)
                    {
                        dispatchedVersion = _rumbleRequestVersion;
                    }
                }

                lock (_rumbleDispatchLock)
                {
                    if (dispatchedVersion == _rumbleRequestVersion)
                    {
                        _rumbleDispatchRunning = false;
                        return;
                    }
                }
            }
        }

        private void StopRumbleBeforeDisconnect()
        {
            if (_rumbleWriter == null &&
                (ConnectionKind != ScufConnectionKind.Wireless || _wirelessWinUsbAuxRadioTransport == null))
            {
                return;
            }

            try
            {
                Task stopTask = QueueRumble(0, 0);
                if (!stopTask.Wait(2000))
                {
                    LogInput("[RUMBLE] WARN stop command did not finish before disconnect timeout.");
                }
                else
                {
                    LogInput("[RUMBLE] Motors stopped before disconnect.");
                }
            }
            catch (Exception ex)
            {
                LogInput($"[RUMBLE] WARN stop command failed during disconnect: {ex.GetBaseException().Message}");
            }
        }

        private void Xbox_FeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            SendRumble(e.LargeMotor, e.SmallMotor);
        }
    }
}
