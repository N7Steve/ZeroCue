using System;
using System.Diagnostics;

namespace ZeroCue.DataProbe.Services
{
    // Each instance has one serialized caller; no sampling tasks or packet logs.
    internal sealed class StageTimingTracker
    {
        private readonly string _name;
        private readonly string _firstStage;
        private readonly string _secondStage;
        private long _windowTimestamp;
        private bool _started;
        private long _samples, _firstTicks, _secondTicks, _firstMax, _secondMax;

        public StageTimingTracker(string name, string firstStage, string secondStage)
        {
            _name = name;
            _firstStage = firstStage;
            _secondStage = secondStage;
        }

        public string? Observe(long timestamp, long firstTicks, long secondTicks)
        {
            if (!_started)
            {
                _started = true;
                _windowTimestamp = timestamp;
            }
            _samples++;
            _firstTicks += firstTicks;
            _secondTicks += secondTicks;
            _firstMax = Math.Max(_firstMax, firstTicks);
            _secondMax = Math.Max(_secondMax, secondTicks);
            var elapsed = Stopwatch.GetElapsedTime(_windowTimestamp, timestamp);
            if (elapsed.TotalSeconds < 10) return null;

            double toMs = 1000.0 / Stopwatch.Frequency;
            var summary = FormattableString.Invariant($"[{_name}] samples={_samples} avg{_firstStage}Ms={_firstTicks * toMs / _samples:F3} max{_firstStage}Ms={_firstMax * toMs:F3} avg{_secondStage}Ms={_secondTicks * toMs / _samples:F3} max{_secondStage}Ms={_secondMax * toMs:F3} windowMs={elapsed.TotalMilliseconds:F0}");
            _windowTimestamp = timestamp;
            _samples = _firstTicks = _secondTicks = _firstMax = _secondMax = 0;
            return summary;
        }
    }
}
