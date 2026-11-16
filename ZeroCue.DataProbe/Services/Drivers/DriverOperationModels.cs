using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeroCue.DataProbe.Services.Drivers
{
    public enum DriverOperationKind
    {
        Install,
        Restore
    }

    public enum DriverOperationStatus
    {
        Success,
        SuccessWithWarnings,
        NoDevice,
        Failed,
        /// <summary>The user declined the UAC prompt.</summary>
        Cancelled,
        TimedOut,
        /// <summary>Another driver operation is already running on this machine.</summary>
        Busy,
        /// <summary>The elevated helper could not be started or did not report a result.</summary>
        HelperUnavailable,
        /// <summary>Windows reports a problem code (e.g. Code 10) on the device, so no interface can be bound.</summary>
        DeviceError
    }

    public sealed class DriverOperationResult
    {
        public DriverOperationKind Kind { get; set; }
        public DriverTarget Target { get; set; }
        public DriverOperationStatus Status { get; set; } = DriverOperationStatus.Failed;
        public bool RebootRequired { get; set; }

        /// <summary>The driver is assigned but the device must be unplugged and reconnected to load it.</summary>
        public bool ReconnectRequired { get; set; }

        /// <summary>Windows problem code when <see cref="Status"/> is DeviceError.</summary>
        public uint ProblemCode { get; set; }
        public string? Variant { get; set; }
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public bool IsExperimental { get; set; }
        public List<string> OwnedPackages { get; set; } = new();
        public List<string> RemovedPackages { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public bool IcueWasRunning { get; set; }
        public string? IcuePath { get; set; }
        public long ElapsedMs { get; set; }

        public bool Succeeded => Status is DriverOperationStatus.Success or DriverOperationStatus.SuccessWithWarnings;

        public static DriverOperationResult For(DriverOperationKind kind, DriverTarget target, DriverOperationStatus status, string? error = null)
        {
            var result = new DriverOperationResult { Kind = kind, Target = target, Status = status };
            if (error != null)
            {
                result.Errors.Add(error);
            }

            return result;
        }
    }

    /// <summary>One newline-delimited JSON message sent from the elevated helper to the app.</summary>
    public sealed class DriverHelperMessage
    {
        public string Type { get; set; } = "log";
        public string? Text { get; set; }
        public DriverOperationResult? Result { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

        public static DriverHelperMessage? TryParse(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<DriverHelperMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public static class DriverHelperArguments
    {
        public const string Switch = "--driver-helper";

        public static string[] Build(DriverOperationKind kind, DriverTarget target, string pipeName, IEnumerable<string> hintedPackages)
        {
            var args = new List<string>
            {
                Switch,
                kind.ToString().ToLowerInvariant(),
                target.ToString().ToLowerInvariant(),
                "--pipe",
                pipeName
            };

            var hints = string.Join(",", hintedPackages);
            if (hints.Length > 0)
            {
                args.Add("--owned");
                args.Add(hints);
            }

            return args.ToArray();
        }

        public static bool TryParse(
            string[] args,
            out DriverOperationKind kind,
            out DriverTarget target,
            out string pipeName,
            out IReadOnlyList<string> hintedPackages)
        {
            kind = default;
            target = default;
            pipeName = string.Empty;
            hintedPackages = Array.Empty<string>();

            if (args.Length < 5 || args[0] != Switch ||
                !Enum.TryParse(args[1], ignoreCase: true, out kind) ||
                !Enum.TryParse(args[2], ignoreCase: true, out target) ||
                args[3] != "--pipe" ||
                !Guid.TryParse(args[4].Replace("ZeroCue.Driver.", string.Empty, StringComparison.Ordinal), out _))
            {
                return false;
            }

            pipeName = args[4];
            if (args.Length >= 7 && args[5] == "--owned")
            {
                var packages = new List<string>();
                foreach (var value in args[6].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (InfClassifier.IsPublishedInfName(value))
                    {
                        packages.Add(value);
                    }
                }

                hintedPackages = packages;
            }

            return true;
        }
    }
}
