using System;
using System.Collections.Generic;

namespace ZeroCue.DataProbe.Services
{
    public static class ScufReportBuilder
    {
        public static byte[] Pad64(params byte[] bytes)
        {
            if (bytes.Length > 64)
                throw new ArgumentException("SCUF report cannot exceed 64 bytes.");

            var buffer = new byte[64];
            Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
            return buffer;
        }

        public static string ToHex(byte[] buffer) => BitConverter.ToString(buffer).Replace("-", " ");

        public static byte[] BuildSoftwareModeSelectReport()
        {
            return Pad64(0x02, 0x08, 0x0D, 0x01, 0x02);
        }

        public static byte[] BuildApplyReport()
        {
            return Pad64(0x02, 0x08, 0x05, 0x01, 0x01);
        }

        public static byte[] BuildSoftwareModeMaskReport(bool softwareMode)
        {
            var report = new byte[64];

            report[0] = 0x02;
            report[1] = 0x08;
            report[2] = 0x06;
            report[3] = 0x01;
            report[4] = 0x20;
            report[5] = 0x00;
            report[6] = 0x00;
            report[7] = 0x00;

            for (int i = 0; i < 32; i++)
                report[8 + i] = 0x01;

            if (softwareMode)
            {
                report[8 + 30] = 0x00;
            }

            return report;
        }

        public static byte[] BuildProfileCurve3BReport()
        {
            return new byte[]
            {
                0x02, 0x08, 0x06, 0x01, 0x3B, 0x00, 0x00, 0x00,
                0x26, 0x00, 0x04, 0x00, 0x06, 0x00, 0x00, 0x14,
                0x14, 0x28, 0x28, 0x3C, 0x3C, 0x50, 0x50, 0x64,
                0x64, 0x01, 0x06, 0x00, 0x00, 0x14, 0x14, 0x28,
                0x28, 0x3C, 0x3C, 0x50, 0x50, 0x64, 0x64, 0x02,
                0x06, 0x00, 0x00, 0x14, 0x28, 0x28, 0x46, 0x3C,
                0x55, 0x50, 0x5F, 0x64, 0x64, 0x03, 0x06, 0x00,
                0x00, 0x14, 0x28, 0x28, 0x46, 0x3C, 0x55, 0x50
            };
        }
    }
}
