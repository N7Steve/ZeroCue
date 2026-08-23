using System;
using System.Linq;

namespace ZeroCue.DataProbe.Services
{
    public sealed record WiredDeviceIdentity(
        int VendorId,
        int ProductId,
        string Variant,
        bool IsExperimental);

    public sealed record WirelessReceiverIdentity(
        int VendorId,
        int ProductId,
        string Variant,
        bool IsExperimental,
        bool UsesCompositeInterfaces,
        byte ControlOutPipe,
        byte ControlInPipe,
        bool UsesDedicatedRadioInterface)
    {
        public bool UsesUnifiedActiveTransport => !UsesDedicatedRadioInterface;
    }

    public sealed record SupportedScufDeviceProfile
    {
        public required string Name { get; init; }
        public required int WirelessBasePid { get; init; }
        public required int WirelessActivePid { get; init; }
        public required int HidUsagePage { get; init; }
        public required int HidUsage { get; init; }
        public required int ReportSize { get; init; }
        public required byte InitCommandChannel { get; init; }
        public required byte InitAckChannel { get; init; }
        public required byte RuntimeCommandChannel { get; init; }
        public required byte RuntimeAckChannel { get; init; }

        public bool IsWired(int vendorId, int productId) =>
            WiredDeviceIdentities.Any(identity =>
                identity.VendorId == vendorId && identity.ProductId == productId);

        public bool IsExperimentalWired(int vendorId, int productId) =>
            FindWiredDevice(vendorId, productId)?.IsExperimental == true;

        public WiredDeviceIdentity? FindWiredDevice(int vendorId, int productId) =>
            WiredDeviceIdentities.FirstOrDefault(identity =>
                identity.VendorId == vendorId && identity.ProductId == productId);

        public WiredDeviceIdentity[] WiredDeviceIdentities { get; init; } = Array.Empty<WiredDeviceIdentity>();

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
            WirelessBasePid = 0x3A08,
            WirelessActivePid = 0x3A09,
            HidUsagePage = 0xFF42,
            HidUsage = 0x0001,
            ReportSize = 64,
            InitCommandChannel = 0x08,
            InitAckChannel = 0x00,
            RuntimeCommandChannel = 0x09,
            RuntimeAckChannel = 0x01,
            WiredDeviceIdentities = new[]
            {
                new WiredDeviceIdentity(0x1B1C, 0x3A05, "Envision Pro wired controller V2", false),
                new WiredDeviceIdentity(0x1B1C, 0x3A04, "Envision wired controller V2", true),
                new WiredDeviceIdentity(0x2E95, 0x434D, "SCUF Envision Pro wired controller V1", true),
                new WiredDeviceIdentity(0x2E95, 0x434B, "SCUF Envision wired controller V1", true)
            },
            WirelessReceiverIdentities = new[]
            {
                new WirelessReceiverIdentity(0x1B1C, 0x3A08, "Envision Pro Wireless USB Receiver V2 (base)", false, true, 0x02, 0x82, true),
                new WirelessReceiverIdentity(0x1B1C, 0x3A09, "Envision Pro Wireless USB Receiver V2 (active)", false, false, 0x01, 0x81, false),
                new WirelessReceiverIdentity(0x2E95, 0x434E, "SCUF PC Controller Dongle V1 (base)", true, true, 0x02, 0x82, true),
                new WirelessReceiverIdentity(0x2E95, 0x5046, "SCUF PC Controller Dongle V1 (active)", true, false, 0x01, 0x81, false)
            }
        };
    }
}
