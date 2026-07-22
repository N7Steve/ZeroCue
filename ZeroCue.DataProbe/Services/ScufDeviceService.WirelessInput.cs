using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ZeroCue.DataProbe.Services
{
    public partial class ScufDeviceService
    {
        private void ProcessWirelessWinUsbInputFrame(byte[] buf, int len)
        {
            if (!TryNormalizeWirelessInputFrame(buf, len, out var frame))
            {
                return;
            }

            if (!ShouldProcessWirelessInputFrame(frame))
            {
                return;
            }

            _wirelessSessionController?.NotifyControllerActivity();

            if (!IsConnected)
            {
                return;
            }

            short lx = BitConverter.ToInt16(frame, 1);
            short lyRaw = BitConverter.ToInt16(frame, 3);
            short rx = BitConverter.ToInt16(frame, 5);
            short ryRaw = BitConverter.ToInt16(frame, 7);
            short ly = lyRaw == short.MinValue ? short.MaxValue : (short)-lyRaw;
            short ry = ryRaw == short.MinValue ? short.MaxValue : (short)-ryRaw;
            ushort lt = (ushort)(frame[9] | ((frame[10] & 0x03) << 8));
            ushort rt = (ushort)((frame[10] >> 2) | ((frame[11] & 0x0F) << 6));

            LeftStickX = lx;
            LeftStickY = ly;
            RightStickX = rx;
            RightStickY = ry;
            LeftTrigger = lt;
            RightTrigger = rt;

            ButtonLB = (frame[12] & 0x10) != 0;
            ButtonRB = (frame[12] & 0x20) != 0;
            ButtonBack = (frame[12] & 0x40) != 0;
            ButtonStart = (frame[12] & 0x80) != 0;
            ButtonX = (frame[12] & 0x04) != 0;
            ButtonY = (frame[12] & 0x08) != 0;
            ButtonB = (frame[12] & 0x02) != 0;
            ButtonA = (frame[12] & 0x01) != 0;
            ButtonL3 = (frame[13] & 0x01) != 0;
            ButtonR3 = (frame[13] & 0x02) != 0;
            var rawGuidePulse = (frame[13] & 0x04) != 0;
            var directG5 = (frame[13] & 0x80) != 0;
            var suppressedG5Route = false;
            ButtonGuide = rawGuidePulse;

            var dpadHat = frame[11] & 0xF0;
            var dpadLeft = dpadHat == 0x60;
            var dpadUp = dpadHat == 0x00;
            var dpadRight = dpadHat == 0x20;
            var dpadDown = dpadHat == 0x40;
            DPadState = dpadUp ? "Up"
                      : dpadRight ? "Right"
                      : dpadDown ? "Down"
                      : dpadLeft ? "Left"
                      : "Neutral";

            var activePhysicalPaddles = GetWirelessWinUsbPaddles(frame);
            var activeGKeys = GetWirelessWinUsbGKeys(frame, suppressedG5Route);
            bool wasG5Active = _g5Active;
            _activeGKeyName = activeGKeys.FirstOrDefault();
            _g1Active = activeGKeys.Contains("G1");
            _g5Active = activeGKeys.Contains("G5");
            if (_g5Active && !wasG5Active)
            {
                var route = directG5
                    ? "runtime/overlay buf[13]=0x80"
                    : "suppressed wireless route buf[13]=0x40";
                var target = (IsShiftHeld ? ShiftGKeyRemapTable : GKeyRemapTable).GetValueOrDefault("G5", "Sin Mapeo");
                LogInput($"[G-KEY]  [G5 pressed (-> {target})] via {route}");
            }

            var now = Environment.TickCount64;
            foreach (var paddle in activePhysicalPaddles)
            {
                _paddleLastSeen[paddle] = now;
            }

            activePhysicalPaddles = new[] { "Paddle_R4", "Paddle_R5", "Paddle_L4", "Paddle_L5", "SAX_L", "SAX_R" }
                .Where(IsPaddleActive)
                .ToList();

            LastPaddleAction = activePhysicalPaddles.Count > 0 ? string.Join("+", activePhysicalPaddles) : "";
            LastGKeyAction = activeGKeys.Count > 0 ? string.Join("+", activeGKeys) : "";
            UpdateShiftHeld(IsWirelessShiftHeld(activePhysicalPaddles, activeGKeys, dpadUp, dpadRight, dpadDown, dpadLeft));

            var activeButtonTable = IsShiftHeld ? ShiftButtonRemapTable : ButtonRemapTable;
            var activePaddleTable = IsShiftHeld ? ShiftPaddleRemapTable : PaddleRemapTable;
            var activeGKeyTable = IsShiftHeld ? ShiftGKeyRemapTable : GKeyRemapTable;
            var activeAdvancedTable = IsShiftHeld ? ShiftAdvancedRemapTable : AdvancedRemapTable;
            _frameTargets.Clear();

            void AddButtonGesture(string sourceName, bool isPressed)
            {
                var simpleTarget = activeButtonTable.TryGetValue(sourceName, out var target) ? target : sourceName;
                CollectGestureTarget(sourceName, isPressed, simpleTarget, activeAdvancedTable, _frameTargets, now, IsShiftHeld);
            }

            AddButtonGesture("A", ButtonA);
            AddButtonGesture("B", ButtonB);
            AddButtonGesture("X", ButtonX);
            AddButtonGesture("Y", ButtonY);
            AddButtonGesture("LeftShoulder", ButtonLB);
            AddButtonGesture("RightShoulder", ButtonRB);
            AddButtonGesture("Back", ButtonBack);
            AddButtonGesture("Start", ButtonStart);
            AddButtonGesture("Guide", ButtonGuide);
            AddButtonGesture("LeftThumb", ButtonL3);
            AddButtonGesture("RightThumb", ButtonR3);
            AddButtonGesture("Up", dpadUp);
            AddButtonGesture("Right", dpadRight);
            AddButtonGesture("Down", dpadDown);
            AddButtonGesture("Left", dpadLeft);

            var ltOut = GetTriggerOutputByte(lt);
            var rtOut = GetTriggerOutputByte(rt);
            CollectTriggerGestureTargets(lt, rt, ltOut, rtOut, activeButtonTable, activeAdvancedTable, _frameTargets, now, IsShiftHeld, out ltOut, out rtOut);

            foreach (var paddle in new[] { "Paddle_R4", "Paddle_R5", "Paddle_L4", "Paddle_L5", "SAX_L", "SAX_R" })
            {
                var simpleTarget = activePaddleTable.TryGetValue(paddle, out var target) ? target : "Sin Mapeo";
                CollectGestureTarget(paddle, activePhysicalPaddles.Contains(paddle), simpleTarget, activeAdvancedTable, _frameTargets, now, IsShiftHeld);
            }

            foreach (var gKey in new[] { "G1", "G2", "G3", "G4", "G5" })
            {
                var simpleTarget = activeGKeyTable.TryGetValue(gKey, out var target) ? target : "Sin Mapeo";
                CollectGestureTarget(gKey, activeGKeys.Contains(gKey), simpleTarget, activeAdvancedTable, _frameTargets, now, IsShiftHeld);
            }

            AddActivePulseTargets(_frameTargets, now);
            ProcessMacroTargets(_frameTargets);
            ProcessActionTargets(_frameTargets);
            ObserveMacroControllerStates(new (string Target, bool IsPressed)[]
            {
                ("A", ButtonA),
                ("B", ButtonB),
                ("X", ButtonX),
                ("Y", ButtonY),
                ("LeftShoulder", ButtonLB),
                ("RightShoulder", ButtonRB),
                ("Back", ButtonBack),
                ("Start", ButtonStart),
                ("Guide", ButtonGuide),
                ("LeftThumb", ButtonL3),
                ("RightThumb", ButtonR3),
                ("Up", dpadUp),
                ("Right", dpadRight),
                ("Down", dpadDown),
                ("Left", dpadLeft),
                ("LeftTrigger", lt > TriggerPressedThreshold),
                ("RightTrigger", rt > TriggerPressedThreshold),
                ("Paddle_R4", activePhysicalPaddles.Contains("Paddle_R4")),
                ("Paddle_R5", activePhysicalPaddles.Contains("Paddle_R5")),
                ("Paddle_L4", activePhysicalPaddles.Contains("Paddle_L4")),
                ("Paddle_L5", activePhysicalPaddles.Contains("Paddle_L5")),
                ("SAX_L", activePhysicalPaddles.Contains("SAX_L")),
                ("SAX_R", activePhysicalPaddles.Contains("SAX_R")),
                ("G1", activeGKeys.Contains("G1")),
                ("G2", activeGKeys.Contains("G2")),
                ("G3", activeGKeys.Contains("G3")),
                ("G4", activeGKeys.Contains("G4")),
                ("G5", activeGKeys.Contains("G5"))
            });

            RaiseFrameProcessedThrottled();
            SubmitVirtualOutput(ltOut, rtOut, lx, ly, rx, ry);
        }

        private bool TryNormalizeWirelessInputFrame(byte[] buf, int len, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            if (len < 16)
            {
                return false;
            }

            if (TryNormalizeWirelessRuntimeGKeyFrame(buf, len, out frame))
            {
                return true;
            }

            var validOffsets = new List<int>();
            for (var offset = 0; offset + 16 <= len; offset += 16)
            {
                if (buf[offset] == 0x06)
                {
                    validOffsets.Add(offset);
                }
            }

            if (validOffsets.Count == 0)
            {
                return false;
            }

            frame = new byte[16];
            var lastOffset = validOffsets[^1];
            Buffer.BlockCopy(buf, lastOffset, frame, 0, 16);

            if (validOffsets.Count == 1)
            {
                StoreWirelessRadioInputFrame(frame);
                ApplyRecentWirelessRuntimeGKeys(frame);
                return true;
            }

            byte buttons = 0;
            byte gKeysAndThumbs = 0;
            byte paddles = 0;
            byte saxR = 0;
            byte? dpad = null;

            foreach (var offset in validOffsets)
            {
                buttons |= buf[offset + 12];
                gKeysAndThumbs |= buf[offset + 13];
                paddles |= buf[offset + 14];
                saxR |= (byte)(buf[offset + 15] & 0x01);

                var candidateDpad = (byte)(buf[offset + 11] & 0xF0);
                if (candidateDpad != 0x80)
                {
                    dpad = candidateDpad;
                }
            }

            frame[12] = buttons;
            frame[13] = gKeysAndThumbs;
            frame[14] = paddles;
            frame[15] = (byte)((frame[15] & ~0x01) | saxR);

            if (dpad.HasValue)
            {
                frame[11] = (byte)((frame[11] & 0x0F) | dpad.Value);
            }

            StoreWirelessRadioInputFrame(frame);
            ApplyRecentWirelessRuntimeGKeys(frame);
            return true;
        }

        private bool TryNormalizeWirelessRuntimeGKeyFrame(byte[] buf, int len, out byte[] frame)
        {
            frame = Array.Empty<byte>();
            if (len < 7 || buf[0] != 0x03 || buf[1] != 0x01 || buf[2] != 0x02)
            {
                return false;
            }

            var gKeyBits = buf[6];
            var now = Environment.TickCount64;
            Interlocked.Exchange(ref _wirelessRuntimeG4LastSeenMs, (gKeyBits & 0x20) != 0 ? now : 0);
            Interlocked.Exchange(ref _wirelessRuntimeG5LastSeenMs, (gKeyBits & 0x40) != 0 ? now : 0);

            frame = GetLastWirelessRadioInputFrameOrNeutral();
            ApplyRecentWirelessRuntimeGKeys(frame);
            return true;
        }

        private void StoreWirelessRadioInputFrame(byte[] frame)
        {
            var copy = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, copy, 0, frame.Length);
            lock (_wirelessInputFrameLock)
            {
                _lastWirelessRadioInputFrame = copy;
            }
        }

        private byte[] GetLastWirelessRadioInputFrameOrNeutral()
        {
            lock (_wirelessInputFrameLock)
            {
                if (_lastWirelessRadioInputFrame != null)
                {
                    var copy = new byte[_lastWirelessRadioInputFrame.Length];
                    Buffer.BlockCopy(_lastWirelessRadioInputFrame, 0, copy, 0, copy.Length);
                    return copy;
                }
            }

            var neutral = new byte[16];
            neutral[0] = 0x06;
            neutral[11] = 0x80;
            return neutral;
        }

        private void ApplyRecentWirelessRuntimeGKeys(byte[] frame)
        {
            var now = Environment.TickCount64;
            var g4LastSeen = Interlocked.Read(ref _wirelessRuntimeG4LastSeenMs);
            if (g4LastSeen != 0 && now - g4LastSeen <= WirelessRuntimeGKeyHoldMs)
            {
                frame[13] |= 0x40;
            }

            var g5LastSeen = Interlocked.Read(ref _wirelessRuntimeG5LastSeenMs);
            if (g5LastSeen != 0 && now - g5LastSeen <= WirelessRuntimeGKeyHoldMs)
            {
                frame[13] |= 0x80;
            }
        }

        private void ResetWirelessRuntimeInputState()
        {
            Interlocked.Exchange(ref _wirelessRuntimeG4LastSeenMs, 0);
            Interlocked.Exchange(ref _wirelessRuntimeG5LastSeenMs, 0);
            lock (_wirelessInputFrameLock)
            {
                _lastWirelessRadioInputFrame = null;
            }
        }

        private bool ShouldProcessWirelessInputFrame(byte[] buf)
        {
            var digitalSignature = BuildWirelessDigitalSignature(buf);
            if (digitalSignature != Volatile.Read(ref _lastWirelessDigitalSignature))
            {
                Volatile.Write(ref _lastWirelessDigitalSignature, digitalSignature);
                Interlocked.Exchange(ref _lastWirelessAnalogProcessMs, Environment.TickCount64);
                return true;
            }

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastWirelessAnalogProcessMs);
            if (now - last < WirelessAnalogProcessMinIntervalMs)
            {
                return false;
            }

            return Interlocked.CompareExchange(ref _lastWirelessAnalogProcessMs, now, last) == last;
        }

        private static int BuildWirelessDigitalSignature(byte[] buf)
        {
            var dpad = buf[11] & 0xF0;
            var buttons = buf[12] | (buf[13] << 8) | (buf[14] << 16);
            var saxR = (buf[15] & 0x01) << 24;
            return buttons | (dpad << 20) | saxR;
        }

        private static List<string> GetWirelessWinUsbPaddles(byte[] buf)
        {
            var paddles = new List<string>();
            if ((buf[14] & 0x20) != 0) paddles.Add("Paddle_L4");
            if ((buf[14] & 0x10) != 0) paddles.Add("Paddle_R4");
            if ((buf[14] & 0x40) != 0) paddles.Add("Paddle_L5");
            if ((buf[14] & 0x08) != 0) paddles.Add("Paddle_R5");
            if ((buf[14] & 0x80) != 0) paddles.Add("SAX_L");
            if ((buf[15] & 0x01) != 0) paddles.Add("SAX_R");
            return paddles;
        }

        private static List<string> GetWirelessWinUsbGKeys(byte[] buf, bool suppressedG5Route)
        {
            var gKeys = new List<string>();
            if ((buf[13] & 0x08) != 0) gKeys.Add("G1");
            if ((buf[13] & 0x10) != 0) gKeys.Add("G2");
            if ((buf[13] & 0x20) != 0) gKeys.Add("G3");
            if ((buf[13] & 0x40) != 0 && !suppressedG5Route) gKeys.Add("G4");
            if ((buf[13] & 0x80) != 0 || suppressedG5Route) gKeys.Add("G5");
            return gKeys;
        }

        private bool IsWirelessShiftHeld(
            IReadOnlyCollection<string> activePhysicalPaddles,
            IReadOnlyCollection<string> activeGKeys,
            bool dpadUp,
            bool dpadRight,
            bool dpadDown,
            bool dpadLeft)
        {
            if (ShiftModifierButton?.StartsWith("Paddle_") == true || ShiftModifierButton?.StartsWith("SAX_") == true)
            {
                return activePhysicalPaddles.Contains(ShiftModifierButton);
            }

            if (ShiftModifierButton?.StartsWith("G") == true)
            {
                return activeGKeys.Contains(ShiftModifierButton);
            }

            return ShiftModifierButton switch
            {
                "A" => ButtonA,
                "B" => ButtonB,
                "X" => ButtonX,
                "Y" => ButtonY,
                "LeftShoulder" => ButtonLB,
                "RightShoulder" => ButtonRB,
                "LT" or "LeftTrigger" => LeftTrigger > TriggerPressedThreshold,
                "RT" or "RightTrigger" => RightTrigger > TriggerPressedThreshold,
                "Back" => ButtonBack,
                "Start" => ButtonStart,
                "Guide" => ButtonGuide,
                "LeftThumb" => ButtonL3,
                "RightThumb" => ButtonR3,
                "Up" => dpadUp,
                "Right" => dpadRight,
                "Down" => dpadDown,
                "Left" => dpadLeft,
                _ => false
            };
        }
    }
}
