using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using ZeroCue.DataProbe.Models;


namespace ZeroCue.DataProbe.Services
{
    public partial class ScufDeviceService
    {

        public void LoadAppSettings()
        {
            try
            {
                var appSettingsFile = ZeroCuePaths.AppSettingsFile;
                if (File.Exists(appSettingsFile))
                {
                    string json = File.ReadAllText(appSettingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        _languageCode = LocalizationService.NormalizeLanguageCode(settings.LanguageCode);
                        _themeName = string.IsNullOrWhiteSpace(settings.ThemeName) ? "DefaultTheme" : settings.ThemeName;
                        _startWithWindows = settings.StartWithWindows;
                        _startMinimized = settings.StartMinimized;
                        _closeBehavior = settings.CloseBehavior;
                        _askBeforeClosing = settings.AskBeforeClosing;
                        _defaultProfileName = string.IsNullOrWhiteSpace(settings.DefaultProfileName) ? "Default" : settings.DefaultProfileName.Trim();
                        WindowsStartupService.Configure(_startWithWindows, _startMinimized);
                    }
                }
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al cargar appsettings.json: {ex.Message}");
            }
        }

        public void SaveAppSettings()
        {
            try
            {
                var settings = new AppSettings
                {
                    LanguageCode = _languageCode,
                    ThemeName = _themeName,
                    StartWithWindows = _startWithWindows,
                    StartMinimized = _startMinimized,
                    CloseBehavior = _closeBehavior,
                    AskBeforeClosing = _askBeforeClosing,
                    DefaultProfileName = _defaultProfileName
                };
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(ZeroCuePaths.AppSettingsFile, json);
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al guardar appsettings.json: {ex.Message}");
            }
        }

        public void LoadMapping(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    _mapping = JsonSerializer.Deserialize<MappingFile>(json);
                }
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al cargar mapping JSON: {ex.Message}");
            }

            if (_mapping == null)
            {
                _mapping = new MappingFile { Mappings = new List<InputMapping>() };
            }

            if (!_mapping.Mappings.Any(m => m.Type == "paddle"))
            {
                LogInput("[WARN] No se detectaron paddles en el JSON. Aplicando fallback de paddles.");
                _mapping.Mappings.Add(new InputMapping { Name = "Paddle_R4", Type = "paddle", ByteIndex = 14, BitMask = 0x10 });
                _mapping.Mappings.Add(new InputMapping { Name = "Paddle_R5", Type = "paddle", ByteIndex = 14, BitMask = 0x08 });
                _mapping.Mappings.Add(new InputMapping { Name = "Paddle_L4", Type = "paddle", ByteIndex = 14, BitMask = 0x20 });
                _mapping.Mappings.Add(new InputMapping { Name = "Paddle_L5", Type = "paddle", ByteIndex = 14, BitMask = 0x40 });
                _mapping.Mappings.Add(new InputMapping { Name = "SAX_L",     Type = "paddle", ByteIndex = 14, BitMask = 0x80 });
                _mapping.Mappings.Add(new InputMapping { Name = "SAX_R",     Type = "paddle", ByteIndex = 15, BitMask = 0x01 });
            }

            if (!_mapping.Mappings.Any(m => m.Type == "gkey"))
            {
                LogInput("[WARN] No se detectaron G-Keys en el JSON. Aplicando fallback de G-Keys.");
                // G1 NO envía por Ep02 — se detecta vía Ep01 bd & 0x08 (como G5)
                _mapping.Mappings.Add(new InputMapping { Name = "G2", Type = "gkey", ByteIndex = 1, BitMask = 0x02 });
                _mapping.Mappings.Add(new InputMapping { Name = "G3", Type = "gkey", ByteIndex = 1, BitMask = 0x01 });
                _mapping.Mappings.Add(new InputMapping { Name = "G4", Type = "gkey", ByteIndex = 1, BitMask = 0x04 });
                // G5 NO envía por Ep02 — se detecta vía Ep01 bd & 0x80
            }
        }

        public void LoadProfile(string profileName)
        {
            try
            {
                string path = ZeroCuePaths.GetProfileFile(profileName);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var profile = JsonSerializer.Deserialize<ScufProfile>(json);
                    if (profile != null)
                    {
                        if (profile.ShiftModifierButton != null) ShiftModifierButton = profile.ShiftModifierButton;

                        RgbRed = profile.RgbRed;
                        RgbGreen = profile.RgbGreen;
                        RgbBlue = profile.RgbBlue;
                        RgbBrightness = profile.RgbBrightness;
                        EcoMode = profile.EcoMode;
                        TriggerCurve = string.IsNullOrWhiteSpace(profile.TriggerCurve) ? "Lineal" : profile.TriggerCurve;
                        CustomCurveX = profile.CustomCurveX ?? new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
                        CustomCurveY = profile.CustomCurveY ?? new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
                        StickDeadzoneMinPercent = profile.StickDeadzoneMinPercent;
                        StickDeadzoneMaxPercent = profile.StickDeadzoneMaxPercent;
                        StickCurve = string.IsNullOrWhiteSpace(profile.StickCurve) ? "Lineal" : profile.StickCurve;
                        StickCustomCurveX = profile.StickCustomCurveX ?? new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
                        StickCustomCurveY = profile.StickCustomCurveY ?? new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };

                        void MergeDict(Dictionary<string, string> target, Dictionary<string, string>? source)
                        {
                            target.Clear();
                            if (source == null) return;
                            foreach (var kvp in source)
                            {
                                target[kvp.Key] = kvp.Value;
                            }
                        }

                        void MergeAdvancedDict(Dictionary<string, Dictionary<string, string>> target, Dictionary<string, Dictionary<string, string>>? source)
                        {
                            target.Clear();
                            if (source == null) return;
                            foreach (var sourceKvp in source)
                            {
                                var gestures = sourceKvp.Value
                                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value) && kvp.Value != "Sin Mapeo")
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                                if (gestures.Count > 0)
                                {
                                    target[sourceKvp.Key] = gestures;
                                }
                            }
                        }

                        void MergeDelayDict(Dictionary<string, Dictionary<string, int>> target, Dictionary<string, Dictionary<string, int>>? source)
                        {
                            target.Clear();
                            if (source == null) return;
                            foreach (var sourceKvp in source)
                            {
                                var delays = sourceKvp.Value
                                    .Where(kvp => kvp.Key == RemapGestureTypes.DoubleTap || kvp.Key == RemapGestureTypes.Hold)
                                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                                if (delays.Count > 0)
                                {
                                    target[sourceKvp.Key] = delays;
                                }
                            }
                        }

                        MergeDict(PaddleRemapTable, profile.PaddleRemapTable);
                        MergeDict(GKeyRemapTable, profile.GKeyRemapTable);
                        MergeDict(ButtonRemapTable, profile.ButtonRemapTable);
                        Macros = profile.Macros != null
                            ? new Dictionary<string, MacroDefinition>(profile.Macros)
                            : new Dictionary<string, MacroDefinition>();
                        MacroLibrary = profile.MacroLibrary != null
                            ? new Dictionary<string, MacroDefinition>(profile.MacroLibrary)
                            : new Dictionary<string, MacroDefinition>();
                        MergeDict(ShiftPaddleRemapTable, profile.ShiftPaddleRemapTable);
                        MergeDict(ShiftGKeyRemapTable, profile.ShiftGKeyRemapTable);
                        MergeDict(ShiftButtonRemapTable, profile.ShiftButtonRemapTable);
                        MergeAdvancedDict(AdvancedRemapTable, profile.AdvancedRemapTable);
                        MergeAdvancedDict(ShiftAdvancedRemapTable, profile.ShiftAdvancedRemapTable);
                        MergeDelayDict(AdvancedGestureDelayMs, profile.AdvancedGestureDelayMs);
                        MergeDelayDict(ShiftAdvancedGestureDelayMs, profile.ShiftAdvancedGestureDelayMs);

                        var defaultPaddles = new Dictionary<string, string>
                        {
                            { "Paddle_R4", "A" },
                            { "Paddle_R5", "B" },
                            { "Paddle_L4", "X" },
                            { "Paddle_L5", "Y" },
                            { "SAX_L", "Left" },
                            { "SAX_R", "Up" }
                        };
                        foreach (var kvp in defaultPaddles)
                        {
                            if (!PaddleRemapTable.ContainsKey(kvp.Key)) PaddleRemapTable[kvp.Key] = kvp.Value;
                            if (!ShiftPaddleRemapTable.ContainsKey(kvp.Key)) ShiftPaddleRemapTable[kvp.Key] = kvp.Value;
                        }

                        var defaultGKeys = new Dictionary<string, string>
                        {
                            { "G1", "LeftShoulder" },
                            { "G2", "RightShoulder" },
                            { "G3", "Back" },
                            { "G4", "Start" },
                            { "G5", "Guide" }
                        };
                        foreach (var kvp in defaultGKeys)
                        {
                            if (!GKeyRemapTable.ContainsKey(kvp.Key)) GKeyRemapTable[kvp.Key] = kvp.Value;
                            if (!ShiftGKeyRemapTable.ContainsKey(kvp.Key)) ShiftGKeyRemapTable[kvp.Key] = kvp.Value;
                        }

                        var defaultButtons = new[] {
                            "A", "B", "X", "Y",
                            "LeftShoulder", "RightShoulder",
                            "Back", "Start",
                            "LeftThumb", "RightThumb",
                            "Up", "Down", "Left", "Right",
                            "Guide"
                        };
                        foreach (var btn in defaultButtons)
                        {
                            if (!ButtonRemapTable.ContainsKey(btn)) ButtonRemapTable[btn] = btn;
                            if (!ShiftButtonRemapTable.ContainsKey(btn)) ShiftButtonRemapTable[btn] = btn;
                        }

                        OnProfileLoaded?.Invoke();
                        LogInput($"[INFO] Perfil '{profileName}' cargado.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al cargar perfil '{profileName}': {ex.Message}");
            }
        }

        public ScufProfile? TryReadProfile(string profileName)
        {
            try
            {
                string path = ZeroCuePaths.GetProfileFile(profileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ScufProfile>(json);
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al leer perfil '{profileName}': {ex.Message}");
                return null;
            }
        }

        public bool TryUpdateProfileLinkedApp(string profileName, string? linkedAppPath)
        {
            try
            {
                var profile = ReadProfileForUpdate(profileName);

                profile.Name = profileName;
                profile.LinkedAppPath = linkedAppPath ?? string.Empty;
                profile.LinkedAppPaths = string.IsNullOrWhiteSpace(linkedAppPath)
                    ? new List<string>()
                    : new List<string> { linkedAppPath };

                WriteProfileForUpdate(profileName, profile);
                LogInput(string.IsNullOrWhiteSpace(profile.LinkedAppPath)
                    ? $"[INFO] Vinculo de aplicacion eliminado para '{profileName}'."
                    : $"[INFO] Perfil '{profileName}' vinculado a '{profile.LinkedAppPath}'.");
                return true;
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al actualizar aplicacion vinculada para '{profileName}': {ex.Message}");
                return false;
            }
        }

        public bool TryAddProfileLinkedApps(string profileName, IEnumerable<string> linkedAppPaths)
        {
            try
            {
                var pathsToAdd = linkedAppPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (pathsToAdd.Count == 0)
                {
                    return false;
                }

                var profile = ReadProfileForUpdate(profileName);
                profile.Name = profileName;

                var mergedPaths = profile.GetLinkedAppPaths();
                foreach (var path in pathsToAdd)
                {
                    if (!mergedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        mergedPaths.Add(path);
                    }
                }

                profile.LinkedAppPath = mergedPaths.FirstOrDefault() ?? string.Empty;
                profile.LinkedAppPaths = mergedPaths;

                WriteProfileForUpdate(profileName, profile);
                LogInput($"[INFO] Perfil '{profileName}' vinculado a {pathsToAdd.Count} aplicacion(es).");
                return true;
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al anadir aplicaciones vinculadas para '{profileName}': {ex.Message}");
                return false;
            }
        }

        public bool TryClearProfileLinkedApps(string profileName)
        {
            return TryUpdateProfileLinkedApp(profileName, string.Empty);
        }

        public bool TryRemoveProfileLinkedApp(string profileName, string linkedAppPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(linkedAppPath))
                {
                    return false;
                }

                var profile = ReadProfileForUpdate(profileName);
                profile.Name = profileName;

                var pathToRemove = Path.GetFullPath(linkedAppPath);
                var remainingPaths = profile.GetLinkedAppPaths()
                    .Where(path => !string.Equals(Path.GetFullPath(path), pathToRemove, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                profile.LinkedAppPath = remainingPaths.FirstOrDefault() ?? string.Empty;
                profile.LinkedAppPaths = remainingPaths;

                WriteProfileForUpdate(profileName, profile);
                LogInput($"[INFO] Vinculo eliminado de '{profileName}': {pathToRemove}.");
                return true;
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al eliminar aplicacion vinculada para '{profileName}': {ex.Message}");
                return false;
            }
        }

        private ScufProfile ReadProfileForUpdate(string profileName)
        {
            string path = ZeroCuePaths.GetProfileFile(profileName);
            if (!File.Exists(path))
            {
                return new ScufProfile();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ScufProfile>(json) ?? new ScufProfile();
        }

        private static void WriteProfileForUpdate(string profileName, ScufProfile profile)
        {
            string path = ZeroCuePaths.GetProfileFile(profileName);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
        }

        public void SaveProfile(string profileName)
        {
            try
            {
                var existingProfile = TryReadProfile(profileName);
                var linkedAppPaths = existingProfile?.GetLinkedAppPaths() ?? new List<string>();
                var profile = new ScufProfile
                {
                    Name = profileName,
                    LinkedAppPath = linkedAppPaths.FirstOrDefault() ?? string.Empty,
                    LinkedAppPaths = linkedAppPaths,
                    ShiftModifierButton = ShiftModifierButton,
                    RgbRed = RgbRed,
                    RgbGreen = RgbGreen,
                    RgbBlue = RgbBlue,
                    RgbBrightness = RgbBrightness,
                    EcoMode = EcoMode,
                    TriggerCurve = TriggerCurve,
                    CustomCurveX = CustomCurveX,
                    CustomCurveY = CustomCurveY,
                    StickDeadzoneMinPercent = StickDeadzoneMinPercent,
                    StickDeadzoneMaxPercent = StickDeadzoneMaxPercent,
                    StickCurve = StickCurve,
                    StickCustomCurveX = StickCustomCurveX,
                    StickCustomCurveY = StickCustomCurveY,
                    PaddleRemapTable = new Dictionary<string, string>(PaddleRemapTable),
                    GKeyRemapTable = new Dictionary<string, string>(GKeyRemapTable),
                    ButtonRemapTable = new Dictionary<string, string>(ButtonRemapTable),
                    AdvancedRemapTable = AdvancedRemapTable.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<string, string>(kvp.Value)),
                    AdvancedGestureDelayMs = AdvancedGestureDelayMs.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<string, int>(kvp.Value)),
                    Macros = Macros.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    MacroLibrary = MacroLibrary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    ShiftPaddleRemapTable = new Dictionary<string, string>(ShiftPaddleRemapTable),
                    ShiftGKeyRemapTable = new Dictionary<string, string>(ShiftGKeyRemapTable),
                    ShiftButtonRemapTable = new Dictionary<string, string>(ShiftButtonRemapTable),
                    ShiftAdvancedRemapTable = ShiftAdvancedRemapTable.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<string, string>(kvp.Value)),
                    ShiftAdvancedGestureDelayMs = ShiftAdvancedGestureDelayMs.ToDictionary(kvp => kvp.Key, kvp => new Dictionary<string, int>(kvp.Value))
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(profile, options);

                string path = ZeroCuePaths.GetProfileFile(profileName);
                File.WriteAllText(path, json);
                LogInput($"[INFO] Perfil '{profileName}' guardado exitosamente.");
            }
            catch (Exception ex)
            {
                LogInput($"[WARN] Error al guardar perfil '{profileName}': {ex.Message}");
            }
        }

        public void ResetToDefaults()
        {
            ShiftModifierButton = string.Empty;
            TriggerCurve = "Lineal";
            CustomCurveX = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
            CustomCurveY = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
            StickDeadzoneMinPercent = 8.0;
            StickDeadzoneMaxPercent = 100.0;
            StickCurve = "Lineal";
            StickCustomCurveX = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
            StickCustomCurveY = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };

            PaddleRemapTable = new Dictionary<string, string>
            {
                { "Paddle_L5",      "Sin Mapeo" },
                { "Paddle_R5",      "Sin Mapeo" },
                { "Paddle_L4",      "Sin Mapeo" },
                { "Paddle_R4",      "Sin Mapeo" },
                { "SAX_L",          "Sin Mapeo" },
                { "SAX_R",          "Sin Mapeo" }
            };

            GKeyRemapTable = new Dictionary<string, string>
            {
                { "G1", "Sin Mapeo" },
                { "G2", "Sin Mapeo" },
                { "G3", "Sin Mapeo" },
                { "G4", "Sin Mapeo" },
                { "G5", "Sin Mapeo" }
            };

            ButtonRemapTable = new Dictionary<string, string>
            {
                { "A",              "A" },
                { "B",              "B" },
                { "X",              "X" },
                { "Y",              "Y" },
                { "LeftShoulder",   "LeftShoulder" },
                { "RightShoulder",  "RightShoulder" },
                { "Back",           "Back" },
                { "Start",          "Start" },
                { "LeftThumb",      "LeftThumb" },
                { "RightThumb",     "RightThumb" },
                { "Up",             "Up" },
                { "Down",           "Down" },
                { "Left",           "Left" },
                { "Right",          "Right" },
                { "Guide",          "Guide" }
            };

            ShiftPaddleRemapTable = new Dictionary<string, string>(PaddleRemapTable);
            ShiftGKeyRemapTable = new Dictionary<string, string>(GKeyRemapTable);
            ShiftButtonRemapTable = new Dictionary<string, string>(ButtonRemapTable);
            AdvancedRemapTable = new Dictionary<string, Dictionary<string, string>>();
            ShiftAdvancedRemapTable = new Dictionary<string, Dictionary<string, string>>();
            AdvancedGestureDelayMs = new Dictionary<string, Dictionary<string, int>>();
            ShiftAdvancedGestureDelayMs = new Dictionary<string, Dictionary<string, int>>();
            Macros = new Dictionary<string, MacroDefinition>();
            MacroLibrary = new Dictionary<string, MacroDefinition>();
        }
    }
}
