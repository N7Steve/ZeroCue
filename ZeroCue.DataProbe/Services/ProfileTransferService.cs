using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroCue.DataProbe.Models;

namespace ZeroCue.DataProbe.Services
{
    internal enum ProfileImportFailure
    {
        InvalidDocument,
        TooLarge,
        UnsupportedVersion
    }

    internal sealed class ProfileImportException : Exception
    {
        public ProfileImportFailure Failure { get; }
        public int? FormatVersion { get; }

        public ProfileImportException(ProfileImportFailure failure, Exception? innerException = null, int? formatVersion = null)
            : base(failure.ToString(), innerException)
        {
            Failure = failure;
            FormatVersion = formatVersion;
        }
    }

    internal sealed record PreparedProfileImport(ScufProfile Profile, string SuggestedName);

    internal static class ProfileTransferService
    {
        private const long MaxImportBytes = 2 * 1024 * 1024;
        private const int MaxDictionaryEntries = 512;
        private const int MaxMacros = 256;
        private const int MaxMacroSteps = 2048;
        private const int MaxStringLength = 512;

        private static readonly JsonSerializerOptions ImportOptions = new()
        {
            MaxDepth = 64,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true
        };

        private static readonly HashSet<string> ProfileMarkerProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            nameof(ScufProfile.PaddleRemapTable),
            nameof(ScufProfile.GKeyRemapTable),
            nameof(ScufProfile.ButtonRemapTable),
            nameof(ScufProfile.TriggerCurve),
            nameof(ScufProfile.StickCurve),
            nameof(ScufProfile.Macros),
            nameof(ScufProfile.RgbBrightness)
        };

        public static PreparedProfileImport ReadImport(string sourcePath)
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists)
            {
                throw new FileNotFoundException(null, sourcePath);
            }

            if (file.Length > MaxImportBytes)
            {
                throw new ProfileImportException(ProfileImportFailure.TooLarge);
            }

            try
            {
                string json = File.ReadAllText(file.FullName);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.EnumerateObject().Any(property => ProfileMarkerProperties.Contains(property.Name)))
                {
                    throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
                }

                var profile = JsonSerializer.Deserialize<ScufProfile>(json, ImportOptions)
                    ?? throw new ProfileImportException(ProfileImportFailure.InvalidDocument);

                if (profile.FormatVersion != ScufProfile.CurrentFormatVersion)
                {
                    throw new ProfileImportException(
                        ProfileImportFailure.UnsupportedVersion,
                        formatVersion: profile.FormatVersion);
                }

                Normalize(profile);
                Validate(profile);

                string suggestedName = CreateSafeSuggestedName(profile.Name, Path.GetFileNameWithoutExtension(file.Name));
                return new PreparedProfileImport(profile, suggestedName);
            }
            catch (ProfileImportException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new ProfileImportException(ProfileImportFailure.InvalidDocument, ex);
            }
        }

        public static void SaveImport(ScufProfile profile, string profileName)
        {
            profile.FormatVersion = ScufProfile.CurrentFormatVersion;
            profile.Name = profileName;
            profile.LinkedAppPath = string.Empty;
            profile.LinkedAppPaths = new List<string>();
            Normalize(profile);
            Validate(profile);
            WriteProfile(ZeroCuePaths.GetProfileFile(profileName), profile);
        }

        public static void Export(ScufProfile profile, string profileName, string destinationPath)
        {
            profile.FormatVersion = ScufProfile.CurrentFormatVersion;
            profile.Name = profileName;

            // App links are machine-local absolute paths. Excluding them makes exports portable
            // and avoids disclosing local usernames or installation directories when shared.
            profile.LinkedAppPath = string.Empty;
            profile.LinkedAppPaths = new List<string>();

            Normalize(profile);
            Validate(profile);
            WriteProfile(destinationPath, profile);
        }

        private static void WriteProfile(string path, ScufProfile profile)
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(profile, WriteOptions));
        }

        private static void Normalize(ScufProfile profile)
        {
            profile.Name ??= string.Empty;
            profile.LinkedAppPath ??= string.Empty;
            profile.LinkedAppPaths ??= new List<string>();
            profile.ShiftModifierButton ??= string.Empty;
            profile.TriggerCurve = string.IsNullOrWhiteSpace(profile.TriggerCurve) ? "Lineal" : profile.TriggerCurve;
            profile.CustomCurveX ??= DefaultCurve();
            profile.CustomCurveY ??= DefaultCurve();
            profile.StickCurve = string.IsNullOrWhiteSpace(profile.StickCurve) ? "Lineal" : profile.StickCurve;
            profile.StickCustomCurveX ??= DefaultCurve();
            profile.StickCustomCurveY ??= DefaultCurve();
            profile.PaddleRemapTable ??= new Dictionary<string, string>();
            profile.GKeyRemapTable ??= new Dictionary<string, string>();
            profile.ButtonRemapTable ??= new Dictionary<string, string>();
            profile.AdvancedRemapTable ??= new Dictionary<string, Dictionary<string, string>>();
            profile.AdvancedGestureDelayMs ??= new Dictionary<string, Dictionary<string, int>>();
            profile.Macros ??= new Dictionary<string, MacroDefinition>();
            profile.MacroLibrary ??= new Dictionary<string, MacroDefinition>();
            profile.ShiftPaddleRemapTable ??= new Dictionary<string, string>();
            profile.ShiftGKeyRemapTable ??= new Dictionary<string, string>();
            profile.ShiftButtonRemapTable ??= new Dictionary<string, string>();
            profile.ShiftAdvancedRemapTable ??= new Dictionary<string, Dictionary<string, string>>();
            profile.ShiftAdvancedGestureDelayMs ??= new Dictionary<string, Dictionary<string, int>>();
        }

        private static void Validate(ScufProfile profile)
        {
            if (profile.RgbBrightness > 1000
                || profile.RumbleIntensity > 100
                || !IsFiniteRange(profile.StickDeadzoneMinPercent, 0, 95)
                || !IsFiniteRange(profile.StickDeadzoneMaxPercent, 5, 100)
                || profile.StickDeadzoneMinPercent >= profile.StickDeadzoneMaxPercent
                || !IsValidCurve(profile.CustomCurveX, requireMonotonic: true)
                || !IsValidCurve(profile.CustomCurveY, requireMonotonic: false)
                || !IsValidCurve(profile.StickCustomCurveX, requireMonotonic: true)
                || !IsValidCurve(profile.StickCustomCurveY, requireMonotonic: false))
            {
                throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
            }

            ValidateDictionary(profile.PaddleRemapTable);
            ValidateDictionary(profile.GKeyRemapTable);
            ValidateDictionary(profile.ButtonRemapTable);
            ValidateDictionary(profile.ShiftPaddleRemapTable);
            ValidateDictionary(profile.ShiftGKeyRemapTable);
            ValidateDictionary(profile.ShiftButtonRemapTable);
            ValidateNestedDictionary(profile.AdvancedRemapTable);
            ValidateNestedDictionary(profile.ShiftAdvancedRemapTable);
            ValidateDelayDictionary(profile.AdvancedGestureDelayMs);
            ValidateDelayDictionary(profile.ShiftAdvancedGestureDelayMs);
            ValidateMacros(profile.Macros);
            ValidateMacros(profile.MacroLibrary);
        }

        private static void ValidateDictionary(Dictionary<string, string> values)
        {
            if (values.Count > MaxDictionaryEntries
                || values.Any(pair => !IsValidText(pair.Key) || !IsValidText(pair.Value)))
            {
                throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
            }
        }

        private static void ValidateNestedDictionary(Dictionary<string, Dictionary<string, string>> values)
        {
            if (values.Count > MaxDictionaryEntries
                || values.Any(pair => !IsValidText(pair.Key) || pair.Value == null))
            {
                throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
            }

            foreach (var nested in values.Values)
            {
                ValidateDictionary(nested);
            }
        }

        private static void ValidateDelayDictionary(Dictionary<string, Dictionary<string, int>> values)
        {
            if (values.Count > MaxDictionaryEntries
                || values.Any(pair => !IsValidText(pair.Key) || pair.Value == null))
            {
                throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
            }

            foreach (var nested in values.Values)
            {
                if (nested.Count > MaxDictionaryEntries
                    || nested.Any(pair => !IsValidText(pair.Key) || pair.Value < 0 || pair.Value > 60000))
                {
                    throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
                }
            }
        }

        private static void ValidateMacros(Dictionary<string, MacroDefinition> macros)
        {
            if (macros.Count > MaxMacros)
            {
                throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
            }

            foreach (var pair in macros)
            {
                var macro = pair.Value;
                if (!IsValidText(pair.Key)
                    || macro == null
                    || !IsValidText(macro.Id, allowEmpty: true)
                    || !IsValidText(macro.Name)
                    || macro.Steps == null
                    || macro.Steps.Count > MaxMacroSteps)
                {
                    throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
                }

                foreach (var step in macro.Steps)
                {
                    if (step == null
                        || step.DelayMs < 0
                        || step.DelayMs > 60000
                        || !IsValidText(step.Target, allowEmpty: true)
                        || (step.InputKind != MacroInputKinds.Gamepad
                            && step.InputKind != MacroInputKinds.Keyboard
                            && step.InputKind != MacroInputKinds.Mouse)
                        || (step.Action != MacroActions.Down && step.Action != MacroActions.Up))
                    {
                        throw new ProfileImportException(ProfileImportFailure.InvalidDocument);
                    }
                }
            }
        }

        private static bool IsValidCurve(double[] values, bool requireMonotonic)
        {
            if (values.Length != 5 || values.Any(value => !IsFiniteRange(value, 0, 1)))
            {
                return false;
            }

            return !requireMonotonic || values.Zip(values.Skip(1), (left, right) => left <= right).All(value => value);
        }

        private static bool IsFiniteRange(double value, double minimum, double maximum)
        {
            return double.IsFinite(value) && value >= minimum && value <= maximum;
        }

        private static bool IsValidText(string? value, bool allowEmpty = false)
        {
            return value != null
                && value.Length <= MaxStringLength
                && (allowEmpty || !string.IsNullOrWhiteSpace(value));
        }

        private static string CreateSafeSuggestedName(string? profileName, string fallbackName)
        {
            string candidate = string.IsNullOrWhiteSpace(profileName) ? fallbackName : profileName.Trim();
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                candidate = candidate.Replace(invalidCharacter, ' ');
            }

            candidate = candidate.Trim().TrimEnd('.', ' ');
            if (candidate.Length > 80)
            {
                candidate = candidate[..80].TrimEnd('.', ' ');
            }

            return candidate;
        }

        private static double[] DefaultCurve()
        {
            return new[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        }
    }
}
