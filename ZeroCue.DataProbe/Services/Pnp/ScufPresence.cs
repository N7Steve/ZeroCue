using System;
using System.Collections.Generic;
using System.Linq;
using ZeroCue.DataProbe.Services.Drivers;

namespace ZeroCue.DataProbe.Services.Pnp
{
    public enum ScufConnectionPath
    {
        Wired,
        Receiver
    }

    public enum ScufDeviceReadiness
    {
        /// <summary>Every required interface is bound to WinUSB and started.</summary>
        Ready,

        /// <summary>Interfaces are still enumerating (normal for a second after plug-in).</summary>
        Settling,

        /// <summary>The device is present but bound to another driver (HID, iCUE...).</summary>
        MissingDriver,

        /// <summary>
        /// WinUSB is assigned but the old driver stack is still running (an app held the
        /// device open during the driver change). Unplugging and reconnecting fixes it.
        /// </summary>
        RestartPending,

        /// <summary>Windows reports a problem code (e.g. Code 10) for the device.</summary>
        DeviceError
    }

    public sealed record ScufDeviceStatus(
        ScufConnectionPath Path,
        DriverVariant Variant,
        int ProductId,
        ScufDeviceReadiness Readiness,
        uint ProblemCode,
        IReadOnlyList<PnpDevice> Nodes)
    {
        public string Identity => PnpIdentity.FormatHardwareKey(Variant.VendorId, ProductId, null);

        public string Describe() =>
            $"{Path} {Identity} [{Variant.Name}] readiness={Readiness}" +
            (ProblemCode != 0 ? $" problem={ProblemCode}" : string.Empty) +
            $" nodes={string.Join(" | ", Nodes.Select(node => $"{node.HardwareKey}:{node.Service ?? "-"}{(node.ProblemCode != 0 ? $"!{node.ProblemCode}" : string.Empty)}{(node.IsRestartPending ? "!restart" : string.Empty)}"))}";
    }

    /// <summary>
    /// Snapshot of which SCUF devices are connected and whether ZeroCue can open
    /// them. Computing it costs a few milliseconds, so the connection loop uses it
    /// to decide what to try instead of blindly probing USB transports.
    /// </summary>
    public sealed record ScufPresenceSnapshot(IReadOnlyList<ScufDeviceStatus> Devices)
    {
        public static ScufPresenceSnapshot Empty { get; } = new(Array.Empty<ScufDeviceStatus>());

        public ScufDeviceStatus? Wired => Best(ScufConnectionPath.Wired);

        public ScufDeviceStatus? Receiver => Best(ScufConnectionPath.Receiver);

        public bool IsWiredReady => Wired?.Readiness == ScufDeviceReadiness.Ready;

        public bool IsReceiverReady => Receiver?.Readiness == ScufDeviceReadiness.Ready;

        /// <summary>Stable text used to detect changes and to log them once.</summary>
        public string Signature => Devices.Count == 0
            ? "none"
            : string.Join("; ", Devices.Select(device => device.Describe()));

        private ScufDeviceStatus? Best(ScufConnectionPath path) =>
            Devices.Where(device => device.Path == path)
                .OrderBy(device => device.Readiness)
                .FirstOrDefault();

        public static ScufPresenceSnapshot Capture()
        {
            try
            {
                return Evaluate(PnpQuery.GetUsbDevices(DriverCatalog.AllVendorIds));
            }
            catch
            {
                return Empty;
            }
        }

        public static ScufPresenceSnapshot Evaluate(IReadOnlyList<PnpDevice> presentNodes)
        {
            var devices = new List<ScufDeviceStatus>();
            foreach (var (definition, path) in new[]
            {
                (DriverCatalog.Gamepad, ScufConnectionPath.Wired),
                (DriverCatalog.Receiver, ScufConnectionPath.Receiver)
            })
            {
                foreach (var variant in definition.Variants)
                {
                    foreach (var productId in variant.ProductIds)
                    {
                        var nodes = presentNodes
                            .Where(node => node.IsPresent && node.VendorId == variant.VendorId && node.ProductId == productId)
                            .ToArray();
                        if (nodes.Length == 0)
                        {
                            continue;
                        }

                        var bindings = variant.Bindings.Where(binding => binding.ProductId == productId).ToArray();
                        var (readiness, problem) = EvaluateBindings(nodes, bindings, variant.VendorId);
                        devices.Add(new ScufDeviceStatus(path, variant, productId, readiness, problem, nodes));
                    }
                }
            }

            return new ScufPresenceSnapshot(devices);
        }

        private static (ScufDeviceReadiness Readiness, uint Problem) EvaluateBindings(
            IReadOnlyList<PnpDevice> nodes,
            IReadOnlyList<DriverBinding> bindings,
            int vendorId)
        {
            // A failed composite parent (Code 10, 43...) hides every interface.
            var parent = nodes.FirstOrDefault(node => node.InterfaceNumber == null);
            bool usesInterfaces = bindings.Any(binding => binding.InterfaceNumber != null);
            if (usesInterfaces && parent != null && parent.ProblemCode != 0)
            {
                return (ScufDeviceReadiness.DeviceError, parent.ProblemCode);
            }

            var worst = ScufDeviceReadiness.Ready;
            uint problem = 0;
            foreach (var binding in bindings)
            {
                var node = nodes.FirstOrDefault(candidate => binding.Matches(candidate, vendorId));
                ScufDeviceReadiness state;
                if (node == null)
                {
                    state = ScufDeviceReadiness.Settling;
                }
                else if (node.IsWinUsb)
                {
                    state = node.ProblemCode != 0
                        ? ScufDeviceReadiness.DeviceError
                        : node.IsRestartPending ? ScufDeviceReadiness.RestartPending : ScufDeviceReadiness.Ready;
                }
                else if (node.ProblemCode is 0 or 28 /* CM_PROB_FAILED_INSTALL: no driver */)
                {
                    state = ScufDeviceReadiness.MissingDriver;
                }
                else
                {
                    state = ScufDeviceReadiness.DeviceError;
                }

                if (state > worst)
                {
                    worst = state;
                    problem = state == ScufDeviceReadiness.DeviceError ? node?.ProblemCode ?? 0 : 0;
                }
            }

            return (worst, problem);
        }
    }
}
