using System.Collections.Generic;
using System.Linq;

namespace ZeroCue.DataProbe.Services
{
    public partial class ScufDeviceService
    {
        private void InitTelemetry()
        {
            ZeroCueLog.Communication("[TELEMETRY] Device telemetry initialized.");
            ZeroCueLog.InputMapping($"[MAPPING] G-Key fallback bitmasks: G1=0x{GetGKeyMask("G1"):X2} G2=0x{GetGKeyMask("G2"):X2} G3=0x{GetGKeyMask("G3"):X2} G4=0x{GetGKeyMask("G4"):X2} G5=0x{GetGKeyMask("G5"):X2}");
            ZeroCueLog.InputMapping($"[MAPPING] G-Key remap table: {string.Join(", ", GKeyRemapTable.Select(kv => $"{kv.Key} -> {kv.Value}"))}");
            ZeroCueLog.InputMapping($"[MAPPING] Paddle remap table: {string.Join(", ", PaddleRemapTable.Select(kv => $"{kv.Key} -> {kv.Value}"))}");
            ZeroCueLog.InputMapping($"[MAPPING] Button remap table: {string.Join(", ", ButtonRemapTable.Select(kv => $"{kv.Key} -> {kv.Value}"))}");
        }

        private int GetGKeyMask(string gkeyName)
        {
            var mapping = _mapping?.Mappings.FirstOrDefault(x => x.Type == "gkey" && x.Name == gkeyName);
            return mapping?.BitMask ?? 0;
        }

        private void LogVigemChanges(
            bool a, bool b, bool x, bool y,
            bool lb, bool rb, bool back, bool start,
            bool up, bool down, bool left, bool right,
            bool l3, bool r3, bool guide,
            byte lt, byte rt,
            short lx, short ly, short rx, short ry)
        {
            var changes = new List<string>();

            if (a != _vigemTracker.A) { _vigemTracker.A = a; changes.Add($"A={(a ? "1" : "0")}"); }
            if (b != _vigemTracker.B) { _vigemTracker.B = b; changes.Add($"B={(b ? "1" : "0")}"); }
            if (x != _vigemTracker.X) { _vigemTracker.X = x; changes.Add($"X={(x ? "1" : "0")}"); }
            if (y != _vigemTracker.Y) { _vigemTracker.Y = y; changes.Add($"Y={(y ? "1" : "0")}"); }
            if (lb != _vigemTracker.LB) { _vigemTracker.LB = lb; changes.Add($"LB={(lb ? "1" : "0")}"); }
            if (rb != _vigemTracker.RB) { _vigemTracker.RB = rb; changes.Add($"RB={(rb ? "1" : "0")}"); }
            if (back != _vigemTracker.Back) { _vigemTracker.Back = back; changes.Add($"Back={(back ? "1" : "0")}"); }
            if (start != _vigemTracker.Start) { _vigemTracker.Start = start; changes.Add($"Start={(start ? "1" : "0")}"); }
            if (up != _vigemTracker.Up) { _vigemTracker.Up = up; changes.Add($"Up={(up ? "1" : "0")}"); }
            if (down != _vigemTracker.Down) { _vigemTracker.Down = down; changes.Add($"Down={(down ? "1" : "0")}"); }
            if (left != _vigemTracker.Left) { _vigemTracker.Left = left; changes.Add($"Left={(left ? "1" : "0")}"); }
            if (right != _vigemTracker.Right) { _vigemTracker.Right = right; changes.Add($"Right={(right ? "1" : "0")}"); }
            if (l3 != _vigemTracker.L3) { _vigemTracker.L3 = l3; changes.Add($"L3={(l3 ? "1" : "0")}"); }
            if (r3 != _vigemTracker.R3) { _vigemTracker.R3 = r3; changes.Add($"R3={(r3 ? "1" : "0")}"); }
            if (guide != _vigemTracker.Guide) { _vigemTracker.Guide = guide; changes.Add($"Guide={(guide ? "1" : "0")}"); }

            if (changes.Count > 0)
            {
                WriteTelemetryInput($"[VIGEM-OUT] {string.Join(", ", changes)}");
            }
        }
    }
}
