using System;
using System.Collections.Generic;

namespace ZeroCue.DataProbe.Services
{
    public static class WirelessInitializationSequence
    {
        private static readonly SequenceEntry[] Entries =
        {
            new("02080203", 5, 0),
            new("020801030002", 5, 3),
            new("02080D0024", 5, 6),
            new("02080D0036", 5, 8),
            new("020801030001", 5, 11),
            new("0208025A", 5, 14),
            new("02080255", 32, 17),
            new("02080211", 5, 49),
            new("02080212", 5, 51),
            new("02080203", 5, 55),
            new("02080213", 5, 57),
            new("02080214", 5, 59),
            new("020801030002", 5, 61),
            new("02080211", 5, 63),
            new("02080212", 5, 65),
            new("02080203", 5, 67),
            new("02080203", 5, 69),
            new("02080213", 5, 71),
            new("02080214", 5, 73),
            new("0208025F", 5, 75),
            new("02080D0005", 5, 77),
            new("020809", 5, 79),
            new("020808", 5, 81),
            new("02080501", 5, 83),
            new("02080D0024", 5, 85),
            new("0208024B", 5, 87),
            new("02080266", 5, 89),
            new("02080267", 5, 91),
            new("02080268", 5, 93),
            new("02080269", 5, 95),
            new("0208026A", 5, 97),
            new("0208026B", 5, 99),
            new("02080D0005", 5, 101),
            new("020809", 5, 103),
            new("020808", 5, 105),
            new("02080501", 198, 107),
            new("02080D0001", 5, 305),
            new("0208060003", 9, 307),
            new("02080236", 71, 316),
            new("0208060003000000FF", 236, 387),
            new("02090211", 89, 623),
            new("0208060003", 14, 712),
            new("02090212", 300, 726),
            new("0208060003000000FF", 52, 1034),
            new("02090203", 68, 1087),
            new("02090213", 102, 1155),
            new("02090214", 87, 1257),
            new("020901030002", 53, 1344),
            new("0208060003", 36, 1397),
            new("02090211", 93, 1433),
            new("02090212", 90, 1526),
            new("02090203", 79, 1616),
            new("02090203", 25, 1695),
            new("0208060003000000FF", 45, 1720),
            new("02090213", 92, 1765),
            new("0209025F", 66, 1857),
            new("02090D0005", 80, 1923),
            new("020909", 80, 2003),
            new("0208060003", 14, 2083),
            new("020908", 105, 2097),
            new("02090501", 71, 2202),
            new("020901C00001", 79, 2273),
            new("02090D0005", 13, 2352),
            new("0208060003000000FF", 67, 2365),
            new("020909", 85, 2432),
            new("020908", 104, 2517),
            new("02090501", 61, 2621),
            new("02090202", 6, 2682),
            new("0208060003000000FFFFFF", 87, 2688),
            new("0209020B", 60, 2775),
            new("0208024B", 67, 2775),
            new("0209020D", 70, 2842),
            new("0209020E", 72, 2912),
            new("020901840064", 88, 2984),
            new("020901850064", 80, 3072),
            new("020901840064", 80, 3152),
            new("020901850064", 90, 3232),
            new("0209020F", 93, 3322),
            new("02090210", 69, 3415),
            new("0209020F", 91, 3484),
            new("02090210", 69, 3575),
            new("02090D0002", 68, 3644),
            new("02090600200000000101010101010101010101010101010101010101010101010101010101010101", 210, 3712),
            new("02090501", 70, 3922),
            new("020901840064", 70, 3992),
            new("020901850064", 90, 4062),
            new("02090D0001", 5, 4152),
            new("020906001B", 5, 4155),
            new("020901840064", 94, 4158),
            new("020901850064", 70, 4252),
            new("0209010D", 60, 4322),
            new("020901800002", 90, 4382),
            new("0209017C0008", 90, 4472),
            new("020901DD000D", 100, 4562),
            new("02090D012B", 70, 4662),
            new("02090901", 84, 4732),
            new("02090801", 264, 4816),
            new("0209050101", 72, 5080),
            new("020901810002", 80, 5152),
            new("0209017D0008", 100, 5232),
            new("020901DE000D", 80, 5332),
            new("02090D012B", 80, 5412),
            new("02090901", 94, 5492),
            new("020906013B00000026000400060000141428283C3C5050646401060000141428283C3C5050646402060000140628103C2150426464", 276, 5586),
            new("02090701426464", 90, 5862),
            new("0209050101", 0, 5952),
        };

        public static IReadOnlyList<InitializationReport> Reports { get; } = BuildReports();

        private static IReadOnlyList<InitializationReport> BuildReports()
        {
            var reports = new InitializationReport[Entries.Length];
            for (var index = 0; index < Entries.Length; index++)
            {
                var entry = Entries[index];
                var command = Convert.FromHexString(entry.PayloadHex);
                if (command.Length < 3 || command.Length > 64 || command[0] != 0x02 || (command[1] != 0x08 && command[1] != 0x09))
                {
                    throw new InvalidOperationException($"Invalid wireless initialization report at index {index}.");
                }

                var payload = ScufReportBuilder.Pad64(command);
                var ackChannel = command[1] == 0x08 ? (byte)0x00 : (byte)0x01;
                var name = $"WirelessInit_{index:D3}_{payload[1]:X2}_{payload[2]:X2}_{payload[3]:X2}_{payload[4]:X2}";
                reports[index] = new InitializationReport(
                    name,
                    payload,
                    ackChannel,
                    command[2],
                    AllowNonZeroStatus: true,
                    AllowTimeout: false,
                    entry.DelayAfterMs,
                    entry.RelativeTimestampMs);
            }

            return Array.AsReadOnly(reports);
        }

        private sealed record SequenceEntry(string PayloadHex, int DelayAfterMs, int RelativeTimestampMs);

        public sealed record InitializationReport(
            string Name,
            byte[] Payload64,
            byte ExpectedAckChannel,
            byte ExpectedOpcode,
            bool AllowNonZeroStatus,
            bool AllowTimeout,
            int DelayAfterMs,
            int RelativeTimestampMs);
    }
}
