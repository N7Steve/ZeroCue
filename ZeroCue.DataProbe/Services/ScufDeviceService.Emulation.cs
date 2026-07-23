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
            if (_rumbleWriter != null)
            {
                lock (_rumbleBuffer)
                {
                    try
                    {
                        if (UsbLock.Wait(10))
                        {
                            try
                            {
                                _rumbleBuffer[8] = largeMotor;
                                _rumbleBuffer[9] = smallMotor;
                                _rumbleWriter.Write(_rumbleBuffer, 100, out int bytesWritten);
                            }
                            finally { UsbLock.Release(); }
                        }
                    }
                    catch { }
                }
            }
            else if (ConnectionKind == ScufConnectionKind.Wireless && _wirelessWinUsbAuxRadioTransport != null)
            {
                try
                {
                    byte[] wirelessRumbleBuffer = new byte[64];
                    lock (_rumbleBuffer)
                    {
                        _rumbleBuffer[8] = largeMotor;
                        _rumbleBuffer[9] = smallMotor;
                        Array.Copy(_rumbleBuffer, wirelessRumbleBuffer, _rumbleBuffer.Length);
                    }
                    _ = _wirelessWinUsbAuxRadioTransport.WriteReportAsync(wirelessRumbleBuffer, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogInput($"[RUMBLE] Error enviando rumble inalambrico: {ex.Message}");
                }
            }
        }

        private void Xbox_FeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            SendRumble(e.LargeMotor, e.SmallMotor);
        }
    }
}
