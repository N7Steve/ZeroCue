using System;
using System.Diagnostics;

namespace ZeroCue.DataProbe.Services
{
    // Observe radio 0x06 states before runtime G-key overlays and output curves.
    // The input is a normalized radio state, not a firmware timestamp or an
    // independent hardware latency measurement.
    internal sealed class RadioPerformanceTracker
    {
        private readonly byte[] _previous = new byte[16];
        private bool _started;
        private long _windowTimestamp, _previousTimestamp;
        private long _frames, _changes, _stickChanges, _triggerChanges;
        private long _leftStickChanges, _rightStickChanges;
        private long _gapTicks, _maximumGapTicks, _shortGaps, _longGaps;
        private long _gapSamples;

        public string? Observe(byte[] frame, long timestamp)
        {
            if (frame.Length < 16) throw new ArgumentException("Radio state must contain 16 bytes.", nameof(frame));
            if (!_started)
            {
                _started = true;
                _windowTimestamp = timestamp;
                _changes++;
                _stickChanges++;
                _leftStickChanges++;
                _rightStickChanges++;
                _triggerChanges++;
            }
            else
            {
                if (!frame.AsSpan(0, 16).SequenceEqual(_previous)) _changes++;
                if (!frame.AsSpan(1, 8).SequenceEqual(_previous.AsSpan(1, 8))) _stickChanges++;
                if (!frame.AsSpan(1, 4).SequenceEqual(_previous.AsSpan(1, 4))) _leftStickChanges++;
                if (!frame.AsSpan(5, 4).SequenceEqual(_previous.AsSpan(5, 4))) _rightStickChanges++;
                if (frame[9] != _previous[9] || frame[10] != _previous[10] ||
                    (frame[11] & 0x0F) != (_previous[11] & 0x0F)) _triggerChanges++;

                long gap = timestamp - _previousTimestamp;
                _gapTicks += gap;
                _gapSamples++;
                _maximumGapTicks = Math.Max(_maximumGapTicks, gap);
                var gapMs = Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalMilliseconds;
                if (gapMs < 1) _shortGaps++;
                if (gapMs > 8) _longGaps++;
            }
            frame.AsSpan(0, 16).CopyTo(_previous);
            _previousTimestamp = timestamp;
            _frames++;

            var elapsed = Stopwatch.GetElapsedTime(_windowTimestamp, timestamp);
            if (elapsed.TotalSeconds < 10) return null;
            double toMs = 1000.0 / Stopwatch.Frequency;
            var summary = FormattableString.Invariant($"[RADIO-PERF] framesHz={_frames / elapsed.TotalSeconds:F1} changedHz={_changes / elapsed.TotalSeconds:F1} leftStickChangedHz={_leftStickChanges / elapsed.TotalSeconds:F1} rightStickChangedHz={_rightStickChanges / elapsed.TotalSeconds:F1} sticksChangedHz={_stickChanges / elapsed.TotalSeconds:F1} triggersChangedHz={_triggerChanges / elapsed.TotalSeconds:F1} frames={_frames} changes={_changes} avgGapMs={(_gapSamples == 0 ? 0 : _gapTicks * toMs / _gapSamples):F3} maxGapMs={_maximumGapTicks * toMs:F3} gapsUnder1Ms={_shortGaps} gapsOver8Ms={_longGaps} windowMs={elapsed.TotalMilliseconds:F0}");
            _windowTimestamp = timestamp;
            _frames = _changes = _stickChanges = _triggerChanges = 0;
            _leftStickChanges = _rightStickChanges = 0;
            _gapTicks = _maximumGapTicks = _gapSamples = _shortGaps = _longGaps = 0;
            return summary;
        }
    }
}
