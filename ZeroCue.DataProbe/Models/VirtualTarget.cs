using System;
using System.Globalization;

namespace ZeroCue.DataProbe.Models
{
    public static class VirtualTarget
    {
        private const char TriggerPercentSeparator = '@';

        public static string GetBaseTarget(string? target)
        {
            return TryGetTriggerOutputPercent(target, out var baseTarget, out _)
                ? baseTarget
                : target ?? string.Empty;
        }

        public static bool IsTriggerTarget(string? target)
        {
            return TryGetTriggerOutputPercent(target, out _, out _);
        }

        public static int GetTriggerOutputPercent(string? target)
        {
            return TryGetTriggerOutputPercent(target, out _, out var percent) ? percent : 100;
        }

        public static string WithTriggerOutputPercent(string target, int percent)
        {
            var baseTarget = GetBaseTarget(target);
            var canonicalTarget = baseTarget switch
            {
                "LT" => "LeftTrigger",
                "RT" => "RightTrigger",
                _ => baseTarget
            };

            if (canonicalTarget is not ("LeftTrigger" or "RightTrigger"))
            {
                return target;
            }

            var clampedPercent = Math.Clamp(percent, 0, 100);
            return clampedPercent == 100
                ? canonicalTarget
                : $"{canonicalTarget}{TriggerPercentSeparator}{clampedPercent.ToString(CultureInfo.InvariantCulture)}";
        }

        public static byte ScaleTriggerOutput(byte value, string? target)
        {
            var percent = GetTriggerOutputPercent(target);
            return (byte)Math.Clamp(
                (int)Math.Round(value * percent / 100.0, MidpointRounding.AwayFromZero),
                0,
                byte.MaxValue);
        }

        public static bool TryGetTriggerOutputPercent(string? target, out string baseTarget, out int percent)
        {
            baseTarget = target ?? string.Empty;
            percent = 100;

            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            var separatorIndex = target.LastIndexOf(TriggerPercentSeparator);
            if (separatorIndex > 0
                && separatorIndex < target.Length - 1
                && int.TryParse(
                    target[(separatorIndex + 1)..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedPercent))
            {
                var candidate = target[..separatorIndex];
                if (candidate is "LT" or "RT" or "LeftTrigger" or "RightTrigger")
                {
                    baseTarget = candidate;
                    percent = Math.Clamp(parsedPercent, 0, 100);
                    return true;
                }
            }

            return target is "LT" or "RT" or "LeftTrigger" or "RightTrigger";
        }
    }
}
