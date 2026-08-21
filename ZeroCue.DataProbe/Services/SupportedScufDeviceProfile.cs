using System;
using System.Linq;

namespace ZeroCue.DataProbe.Services
{
    public sealed record WirelessReceiverIdentity(
        int VendorId,
        int ProductId,
        string Variant,
        bool IsExperimental,
        bool UsesCompositeInterfaces);

    public sealed record SupportedScufDeviceProfile
    {
        public required string Name { get; init; }
        public required int VendorId { get; init; }
        public required int WiredPid { get; init; }
        public required int[] ExperimentalWiredPids { get; init; }
        public required int WirelessBasePid { get; init; }
        public required int WirelessActivePid { get; init; }
        public required int HidUsagePage { get; init; }
        public required int HidUsage { get; init; }
        public required int ReportSize { get; init; }
        public required byte WinUsbOutPipe { get; init; }
        public required byte WinUsbInPipe { get; init; }
        public required byte InitCommandChannel { get; init; }
        public required byte InitAckChannel { get; init; }
        public required byte RuntimeCommandChannel { get; init; }
        public required byte RuntimeAckChannel { get; init; }

        public bool IsWired(int vendorId, int productId) =>
            vendorId == VendorId && (productId == WiredPid || ExperimentalWiredPids.Contains(productId));

        public bool IsExperimentalWired(int vendorId, int productId) =>
            vendorId == VendorId && ExperimentalWiredPids.Contains(productId);

        public int[] WiredPids => new[] { WiredPid }.Concat(ExperimentalWiredPids).ToArray();

        public bool IsWirelessReceiver(int vendorId, int productId) =>
            WirelessReceiverIdentities.Any(identity =>
                identity.VendorId == vendorId && identity.ProductId == productId);

        public WirelessReceiverIdentity? FindWirelessReceiver(int vendorId, int productId) =>
            WirelessReceiverIdentities.FirstOrDefault(identity =>
                identity.VendorId == vendorId && identity.ProductId == productId);

        public int[] WirelessReceiverPids => WirelessReceiverIdentities
            .Select(identity => identity.ProductId)
            .Distinct()
            .ToArray();

        public WirelessReceiverIdentity[] WirelessReceiverIdentities { get; init; } = Array.Empty<WirelessReceiverIdentity>();

        public static SupportedScufDeviceProfile ScufEnvisionPro { get; } = new()
        {
            Name = "SCUF Envision Pro",
            VendorId = 0x1B1C,
            WiredPid = 0x3A05,
            ExperimentalWiredPids = new[] { 0x3A04 },
            WirelessBasePid = 0x3A08,
            WirelessActivePid = 0x3A09,
            HidUsagePage = 0xFF42,
            HidUsage = 0x0001,
            ReportSize = 64,
            WinUsbOutPipe = 0x02,
            WinUsbInPipe = 0x82,
            InitCommandChannel = 0x08,
            InitAckChannel = 0x00,
            RuntimeCommandChannel = 0x09,
            RuntimeAckChannel = 0x01,
            WirelessReceiverIdentities = new[]
            {
                new WirelessReceiverIdentity(0x1B1C, 0x3A08, "Envision Pro Wireless USB Receiver V2 (base)", false, true),
                new WirelessReceiverIdentity(0x1B1C, 0x3A09, "Envision Pro Wireless USB Receiver V2 (active)", false, false),
                new WirelessReceiverIdentity(0x2E95, 0x434E, "SCUF PC Controller Dongle V1 (base)", true, true),
                new WirelessReceiverIdentity(0x2E95, 0x5046, "SCUF PC Controller Dongle V1 (active)", true, false)
            }
        };
    }
}
