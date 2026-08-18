using System.Collections.Generic;
using System.Linq;

namespace ZeroCue.DataProbe.Models
{
    public class ScufProfile
    {
        public const int CurrentFormatVersion = 1;

        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public string Name { get; set; } = "Default";
        public string LinkedAppPath { get; set; } = string.Empty;
        public List<string> LinkedAppPaths { get; set; } = new List<string>();
        public string ShiftModifierButton { get; set; } = "SAX_L";

        // Iluminacion y Eco
        public byte RgbRed { get; set; } = 0;
        public byte RgbGreen { get; set; } = 255;
        public byte RgbBlue { get; set; } = 255;
        public ushort RgbBrightness { get; set; } = 750;
        public byte RumbleIntensity { get; set; } = 100;
        public bool EcoMode { get; set; } = false;
        public string TriggerCurve { get; set; } = "Lineal";
        public double[] CustomCurveX { get; set; } = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double[] CustomCurveY { get; set; } = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double StickDeadzoneMinPercent { get; set; } = 8.0;
        public double StickDeadzoneMaxPercent { get; set; } = 100.0;
        public string StickCurve { get; set; } = "Lineal";
        public double[] StickCustomCurveX { get; set; } = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double[] StickCustomCurveY { get; set; } = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };

        // Normal Layer
        public Dictionary<string, string> PaddleRemapTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> GKeyRemapTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ButtonRemapTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> AdvancedRemapTable { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, int>> AdvancedGestureDelayMs { get; set; } = new Dictionary<string, Dictionary<string, int>>();
        public Dictionary<string, MacroDefinition> Macros { get; set; } = new Dictionary<string, MacroDefinition>();
        public Dictionary<string, MacroDefinition> MacroLibrary { get; set; } = new Dictionary<string, MacroDefinition>();

        // Shift Layer
        public Dictionary<string, string> ShiftPaddleRemapTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ShiftGKeyRemapTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ShiftButtonRemapTable { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, Dictionary<string, string>> ShiftAdvancedRemapTable { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, int>> ShiftAdvancedGestureDelayMs { get; set; } = new Dictionary<string, Dictionary<string, int>>();

        public List<string> GetLinkedAppPaths()
        {
            var paths = new List<string>();
            if (!string.IsNullOrWhiteSpace(LinkedAppPath))
            {
                paths.Add(LinkedAppPath);
            }

            if (LinkedAppPaths != null)
            {
                paths.AddRange(LinkedAppPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
            }

            return paths
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
