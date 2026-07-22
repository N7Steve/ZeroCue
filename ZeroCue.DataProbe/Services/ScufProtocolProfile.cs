namespace ZeroCue.DataProbe.Services
{
    public enum ScufConnectionKind
    {
        Wired,
        Wireless
    }

    public sealed class ScufProtocolProfile
    {
        public required ScufConnectionKind ConnectionKind { get; init; }
        public required byte InitCommandChannel { get; init; }
        public required byte InitAckChannel { get; init; }
        public required byte RuntimeCommandChannel { get; init; }
        public required byte RuntimeAckChannel { get; init; }

        public static ScufProtocolProfile Wired { get; } = new()
        {
            ConnectionKind = ScufConnectionKind.Wired,
            InitCommandChannel = SupportedScufDeviceProfile.ScufEnvisionPro.InitCommandChannel,
            InitAckChannel = SupportedScufDeviceProfile.ScufEnvisionPro.InitAckChannel,
            RuntimeCommandChannel = SupportedScufDeviceProfile.ScufEnvisionPro.InitCommandChannel,
            RuntimeAckChannel = SupportedScufDeviceProfile.ScufEnvisionPro.InitAckChannel
        };

        public static ScufProtocolProfile Wireless { get; } = new()
        {
            ConnectionKind = ScufConnectionKind.Wireless,
            InitCommandChannel = SupportedScufDeviceProfile.ScufEnvisionPro.InitCommandChannel,
            InitAckChannel = SupportedScufDeviceProfile.ScufEnvisionPro.InitAckChannel,
            RuntimeCommandChannel = SupportedScufDeviceProfile.ScufEnvisionPro.RuntimeCommandChannel,
            RuntimeAckChannel = SupportedScufDeviceProfile.ScufEnvisionPro.RuntimeAckChannel
        };
    }
}
