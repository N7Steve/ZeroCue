using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZeroCue.DataProbe.Services
{
    public sealed class WirelessWinUsbInputMapLogger : IDisposable
    {
        private const long FrameLogMinIntervalMs = 20;

        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private byte[] _lastFrame = Array.Empty<byte>();
        private int _sequence;
        private int _observedChanges;
        private int _suppressedFrames;
        private long _lastLoggedElapsedMs;
        private bool _disposed;

        public WirelessWinUsbInputMapLogger(
            Action<string> uiLogger,
            bool enabled,
            string source = "WinUSB auxiliary radio input endpoint 0x81")
        {
            if (enabled)
            {
                LogPath = ZeroCueLog.InputMappingLogPath;

                WriteLine("WirelessWinUsbInputMapLogger start.");
                WriteLine($"timestamp={DateTimeOffset.Now:O}");
                WriteLine($"source={source}");
                WriteLine("capture_notes=Press one control at a time, hold about 1 second, release to neutral between controls.");
                WriteLine($"capture_sampling=minIntervalMs={FrameLogMinIntervalMs}; changed frames inside the window are counted as suppressed.");
                WriteLine("frame_format=[FRAME] seq elapsedMs dtMs len diffs bits u16 subframes hex");
                uiLogger($"WirelessWinUsbInputMapLogPath={LogPath}");
            }
        }

        public string? LogPath { get; }

        public void ObserveFrame(byte[] buffer, int length)
        {
            if (_disposed || length <= 0)
            {
                return;
            }

            if (_lastFrame.Length > 0 && IsSameFrame(buffer, length, _lastFrame))
            {
                return;
            }

            var elapsedMs = _sw.ElapsedMilliseconds;
            var shouldLog = _lastFrame.Length == 0 || elapsedMs - _lastLoggedElapsedMs >= FrameLogMinIntervalMs;
            if (!shouldLog)
            {
                _lastFrame = CopyFrame(buffer, length);
                _observedChanges++;
                _suppressedFrames++;
                return;
            }

            var frame = CopyFrame(buffer, length);
            var dtMs = _lastLoggedElapsedMs == 0 ? 0 : elapsedMs - _lastLoggedElapsedMs;
            var label = _lastFrame.Length == 0 ? "BASELINE" : "FRAME";
            var diffs = _lastFrame.Length == 0 ? "<baseline>" : BuildByteDiffs(_lastFrame, frame);
            var bits = _lastFrame.Length == 0 ? "<baseline>" : BuildBitDiffs(_lastFrame, frame);
            var words = BuildChangedWords(_lastFrame, frame);
            var subframes = BuildSubframeSummary(frame);

            WriteLine($"[{label}] seq={_sequence:D5} elapsedMs={elapsedMs} dtMs={dtMs} len={length} diffs={diffs} bits={bits} u16={words} subframes={subframes} hex={ScufReportBuilder.ToHex(frame)}");

            _lastFrame = frame;
            _lastLoggedElapsedMs = elapsedMs;
            _sequence++;
            _observedChanges++;
        }

        public void LogStop(int readCount, int timeoutCount)
        {
            if (_disposed)
            {
                return;
            }

            WriteLine($"WirelessWinUsbInputMapLogger stop elapsedMs={_sw.ElapsedMilliseconds} reads={readCount} timeouts={timeoutCount} framesLogged={_sequence} changedFramesObserved={_observedChanges} framesSuppressedBySampling={_suppressedFrames}");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        private void WriteLine(string line)
        {
            ZeroCueLog.InputMapping($"[WIRELESS-INPUT] {line}");
        }

        private static byte[] CopyFrame(byte[] buffer, int length)
        {
            var frame = new byte[length];
            Buffer.BlockCopy(buffer, 0, frame, 0, length);
            return frame;
        }

        private static bool IsSameFrame(byte[] buffer, int length, byte[] previous)
        {
            if (previous.Length != length)
            {
                return false;
            }

            for (var i = 0; i < length; i++)
            {
                if (buffer[i] != previous[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildByteDiffs(byte[] previous, byte[] current)
        {
            var parts = new List<string>();
            var max = Math.Max(previous.Length, current.Length);
            for (var i = 0; i < max; i++)
            {
                var oldValue = i < previous.Length ? previous[i] : (byte)0x00;
                var newValue = i < current.Length ? current[i] : (byte)0x00;
                if (oldValue != newValue)
                {
                    parts.Add($"b{i}:{oldValue:X2}->{newValue:X2}");
                }
            }

            return parts.Count == 0 ? "<none>" : string.Join(",", parts);
        }

        private static string BuildBitDiffs(byte[] previous, byte[] current)
        {
            var parts = new List<string>();
            var max = Math.Max(previous.Length, current.Length);
            for (var i = 0; i < max; i++)
            {
                var oldValue = i < previous.Length ? previous[i] : (byte)0x00;
                var newValue = i < current.Length ? current[i] : (byte)0x00;
                var set = (byte)(newValue & ~oldValue);
                var cleared = (byte)(oldValue & ~newValue);
                if (set != 0 || cleared != 0)
                {
                    parts.Add($"b{i}:+{set:X2}/-{cleared:X2}");
                }
            }

            return parts.Count == 0 ? "<none>" : string.Join(",", parts);
        }

        private static string BuildChangedWords(byte[] previous, byte[] current)
        {
            if (current.Length < 2)
            {
                return "<none>";
            }

            var indexes = new SortedSet<int>();
            var max = Math.Max(previous.Length, current.Length);
            for (var i = 0; i < max; i++)
            {
                var oldValue = i < previous.Length ? previous[i] : (byte)0x00;
                var newValue = i < current.Length ? current[i] : (byte)0x00;
                if (oldValue == newValue)
                {
                    continue;
                }

                if (i > 0)
                {
                    indexes.Add(i - 1);
                }

                if (i + 1 < current.Length)
                {
                    indexes.Add(i);
                }
            }

            if (indexes.Count == 0)
            {
                return "<none>";
            }

            return string.Join(",", indexes.Select(i => $"@{i}:{BitConverter.ToUInt16(current, i)}"));
        }

        private static string BuildSubframeSummary(byte[] frame)
        {
            if (frame.Length < 16)
            {
                return "<none>";
            }

            var parts = new List<string>();
            for (var offset = 0; offset + 16 <= frame.Length; offset += 16)
            {
                if (frame[offset] != 0x06)
                {
                    continue;
                }

                var dpad = frame[offset + 11] & 0xF0;
                var buttons = frame[offset + 12];
                var gKeys = frame[offset + 13];
                var paddles = frame[offset + 14];
                var sax = frame[offset + 15] & 0x03;
                parts.Add($"@{offset}:d={dpad:X2},b={buttons:X2},g={gKeys:X2},p={paddles:X2},s={sax:X2}");
            }

            return parts.Count == 0 ? "<none>" : string.Join("|", parts);
        }
    }
}
