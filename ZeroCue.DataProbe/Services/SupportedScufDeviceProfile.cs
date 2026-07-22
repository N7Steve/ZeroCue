namespace ZeroCue.DataProbe.Services
{
    public sealed record SupportedScufDeviceProfile
    {
        public required string Name { get; init; }
        public required int VendorId { get; init; }
        public required int WiredPid { get; init; }
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
            vendorId == VendorId && productId == WiredPid;

        public bool IsWirelessReceiver(int vendorId, int productId) =>
            vendorId == VendorId && (productId == WirelessBasePid || productId == WirelessActivePid);

        public int[] WirelessReceiverPids => new[] { WirelessBasePid, WirelessActivePid };

        public static SupportedScufDeviceProfile ScufEnvisionPro { get; } = new()
        {
            Name = "SCUF Envision Pro",
            VendorId = 0x1B1C,
            WiredPid = 0x3A05,
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
            RuntimeAckChannel = 0x01
        };
    }
}
