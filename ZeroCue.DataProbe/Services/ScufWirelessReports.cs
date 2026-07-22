namespace ZeroCue.DataProbe.Services
{
    public enum ScufWirelessCommandChannel : byte
    {
        Init = 0x08,
        RuntimeSettings = 0x09
    }

    public static class ScufWirelessReports
    {
        public static byte[] BuildSetBrightness(ushort value)
        {
            if (value > 1000) value = 1000;

            var report = CreateLogicalReport();
            report[0] = 0x02;
            report[1] = (byte)ScufWirelessCommandChannel.RuntimeSettings;
            report[2] = 0x01;
            report[3] = 0x02;
            report[4] = 0x00;
            report[5] = (byte)(value & 0xFF);
            report[6] = (byte)(value >> 8);
            return report;
        }

        public static byte[] BuildGetBrightness()
        {
            var report = CreateLogicalReport();
            report[0] = 0x02;
            report[1] = (byte)ScufWirelessCommandChannel.RuntimeSettings;
            report[2] = 0x02;
            report[3] = 0x02;
            report[4] = 0x00;
            return report;
        }

        public static byte[] BuildKeepAlive()
        {
            var report = CreateLogicalReport();
            report[0] = 0x02;
            report[1] = (byte)ScufWirelessCommandChannel.RuntimeSettings;
            report[2] = 0x12;
            report[3] = 0x00;
            return report;
        }

        public static byte[] BuildRadioKeepAlive()
        {
            var report = CreateLogicalReport();
            report[0] = 0x02;
            report[1] = (byte)ScufWirelessCommandChannel.Init;
            report[2] = 0x12;
            report[3] = 0x00;
            return report;
        }

        private static byte[] CreateLogicalReport() => new byte[64];
    }
}
