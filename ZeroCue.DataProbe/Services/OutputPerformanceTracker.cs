using System;
using System.Diagnostics;

namespace ZeroCue.DataProbe.Services
{
    // Called by the serialized input processor. Counts exact final virtual
    // states, after curves, deadzones, trigger conversion and remapping.
    internal sealed class OutputPerformanceTracker
    {
        private readonly string _mode;
        private bool _started;
        private long _windowTimestamp;
        private long _previousTimestamp;
        private ulong _previousDigital;
        private ulong _previousAxes;
        private long _reports;
        private long _changes;
        private long _leftStickChanges, _rightStickChanges;
        private long _submitTicks;
        private long _maximumSubmitTicks;
        private long _maximumGapTicks;

        public OutputPerformanceTracker(string mode) => _mode = mode;

        public string? Observe(ulong digital, ulong axes, long timestamp, long submitTicks)
        {
            if (!_started)
            {
                _started = true;
                _windowTimestamp = timestamp;
                _previousTimestamp = timestamp;
                _changes++;
                _leftStickChanges++;
                _rightStickChanges++;
            }
            else if (digital != _previousDigital || axes != _previousAxes)
            {
                _changes++;
                if ((uint)axes != (uint)_previousAxes) _leftStickChanges++;
                if ((axes >> 32) != (_previousAxes >> 32)) _rightStickChanges++;
            }

            _reports++;
            _submitTicks += submitTicks;
            _maximumSubmitTicks = Math.Max(_maximumSubmitTicks, submitTicks);
            _maximumGapTicks = Math.Max(_maximumGapTicks, timestamp - _previousTimestamp);
            _previousDigital = digital;
            _previousAxes = axes;
            _previousTimestamp = timestamp;

            var elapsed = Stopwatch.GetElapsedTime(_windowTimestamp, timestamp);
            if (elapsed.TotalSeconds < 10) return null;

            var ticksToMilliseconds = 1000.0 / Stopwatch.Frequency;
            var summary = FormattableString.Invariant($"[OUTPUT-PERF] mode={_mode} reportsHz={_reports / elapsed.TotalSeconds:F1} changedHz={_changes / elapsed.TotalSeconds:F1} leftStickChangedHz={_leftStickChanges / elapsed.TotalSeconds:F1} rightStickChangedHz={_rightStickChanges / elapsed.TotalSeconds:F1} reports={_reports} changes={_changes} avgSubmitMs={_submitTicks * ticksToMilliseconds / _reports:F3} maxSubmitMs={_maximumSubmitTicks * ticksToMilliseconds:F3} maxGapMs={_maximumGapTicks * ticksToMilliseconds:F3} windowMs={elapsed.TotalMilliseconds:F0}");
            _windowTimestamp = timestamp;
            _reports = 0;
            _changes = 0;
            _leftStickChanges = _rightStickChanges = 0;
            _submitTicks = 0;
            _maximumSubmitTicks = 0;
            _maximumGapTicks = 0;
            return summary;
        }
    }
}
