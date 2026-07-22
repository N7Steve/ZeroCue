using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZeroCue.DataProbe.Models;

namespace ZeroCue.DataProbe.Services
{
    internal static class ZeroCuePaths
    {
        private const string AppName = "ZeroCue";
        private static readonly object InitLock = new object();
        private static bool _initialized;

        private static readonly string AppRootValue = ResolveAppRoot();
        private static readonly string UserDataRootValue = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppName);
        private static readonly string AppSettingsFileValue = Path.Combine(UserDataRootValue, "appsettings.json");
        private static readonly string ProfilesDirectoryValue = Path.Combine(UserDataRootValue, "Profiles");
        private static readonly string LogsDirectoryValue = Path.Combine(AppRootValue, "logs");

        public static string AppRoot => AppRootValue;
        public static string AppResourcesDirectory => Path.Combine(AppRoot, "resources");

        public static string UserDataRoot
        {
            get
            {
                EnsureInitialized();
                return UserDataRootValue;
            }
        }

        public static string AppSettingsFile
        {
            get
            {
                EnsureInitialized();
                return AppSettingsFileValue;
            }
        }

        public static string ProfilesDirectory
        {
            get
            {
                EnsureInitialized();
                return ProfilesDirectoryValue;
            }
        }

        public static string LogsDirectory
        {
            get
            {
                EnsureInitialized();
                return LogsDirectoryValue;
            }
        }

        public static string GetProfileFile(string profileName)
        {
            return Path.Combine(ProfilesDirectory, $"{profileName}.json");
        }

        public static void ConfigureNativeLibrarySearchPath()
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var entries = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (entries.Any(entry => string.Equals(Path.GetFullPath(entry), AppResourcesDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Environment.SetEnvironmentVariable("PATH", AppResourcesDirectory + Path.PathSeparator + currentPath);
        }

        public static string GetAppPath(params string[] relativeSegments)
        {
            var found = FindAppPath(relativeSegments);
            return found ?? Path.Combine(new[] { AppRoot }.Concat(relativeSegments).ToArray());
        }

        public static string? FindAppFile(params string[] relativeSegments)
        {
            return FindAppPath(relativeSegments, File.Exists);
        }

        public static string? FindAppDirectory(params string[] relativeSegments)
        {
            return FindAppPath(relativeSegments, Directory.Exists);
        }

        public static string? FindExistingFile(string fileName)
        {
            return FindAppFile(fileName);
        }

        public static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (InitLock)
            {
                if (_initialized)
                {
                    return;
                }

                Directory.CreateDirectory(UserDataRootValue);
                Directory.CreateDirectory(ProfilesDirectoryValue);
                Directory.CreateDirectory(LogsDirectoryValue);

                MigrateAppSettingsIfMissing();
                MigrateProfilesIfMissing();
                EnsureDefaultProfileExists();
                EnsureAtLeastOneValidProfileExists();

                _initialized = true;
            }
        }

        private static void MigrateAppSettingsIfMissing()
        {
            if (File.Exists(AppSettingsFileValue))
            {
                return;
            }

            var source = FindAppFile("appsettings.json");
            if (string.IsNullOrWhiteSpace(source) ||
                string.Equals(Path.GetFullPath(source), Path.GetFullPath(AppSettingsFileValue), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                File.Copy(source, AppSettingsFileValue, overwrite: false);
            }
            catch
            {
                // Migration is best effort; saving settings later will recreate the file.
            }
        }

        private static void MigrateProfilesIfMissing()
        {
            foreach (var sourceDirectory in EnumerateAppDirectories("Profiles"))
            {
                if (string.Equals(
                        Path.GetFullPath(sourceDirectory),
                        Path.GetFullPath(ProfilesDirectoryValue),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*.json"))
                    {
                        var destinationFile = Path.Combine(ProfilesDirectoryValue, Path.GetFileName(sourceFile));
                        if (!File.Exists(destinationFile))
                        {
                            File.Copy(sourceFile, destinationFile, overwrite: false);
                        }
                    }
                }
                catch
                {
                    // Keep startup resilient if a legacy profile file is locked or invalid.
                }
            }
        }

        private static void EnsureDefaultProfileExists()
        {
            var defaultProfilePath = Path.Combine(ProfilesDirectoryValue, "Default.json");
            if (File.Exists(defaultProfilePath))
            {
                return;
            }

            WriteDefaultProfile(defaultProfilePath, "Default");
        }

        private static void EnsureAtLeastOneValidProfileExists()
        {
            try
            {
                if (Directory.EnumerateFiles(ProfilesDirectoryValue, "*.json").Any(IsValidProfileFile))
                {
                    return;
                }
            }
            catch
            {
                // Fall through and create a clean fallback profile.
            }

            var fallbackPath = Path.Combine(ProfilesDirectoryValue, "Default.json");
            if (File.Exists(fallbackPath))
            {
                fallbackPath = GetAvailableProfilePath("Default.Recovered");
            }

            WriteDefaultProfile(fallbackPath, Path.GetFileNameWithoutExtension(fallbackPath));
        }

        private static bool IsValidProfileFile(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<ScufProfile>(json);
                return profile != null;
            }
            catch
            {
                return false;
            }
        }

        private static string GetAvailableProfilePath(string baseName)
        {
            var candidate = Path.Combine(ProfilesDirectoryValue, $"{baseName}.json");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            var index = 1;
            do
            {
                candidate = Path.Combine(ProfilesDirectoryValue, $"{baseName}{index}.json");
                index++;
            }
            while (File.Exists(candidate));

            return candidate;
        }

        private static void WriteDefaultProfile(string path, string profileName)
        {
            var profile = CreateDefaultProfileTemplate(profileName);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(profile, options));
        }

        private static ScufProfile CreateDefaultProfileTemplate(string profileName)
        {
            var profile = new ScufProfile
            {
                Name = profileName,
                LinkedAppPath = string.Empty,
                LinkedAppPaths = new List<string>(),
                ShiftModifierButton = "SAX_L",
                RgbRed = 0,
                RgbGreen = 255,
                RgbBlue = 255,
                RgbBrightness = 750,
                RumbleIntensity = 100,
                EcoMode = false,
                TriggerCurve = "Lineal",
                CustomCurveX = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 },
                CustomCurveY = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 },
                StickDeadzoneMinPercent = 8.0,
                StickDeadzoneMaxPercent = 100.0,
                StickCurve = "Lineal",
                StickCustomCurveX = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 },
                StickCustomCurveY = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 },
                PaddleRemapTable = new Dictionary<string, string>
                {
                    ["Paddle_R4"] = "A",
                    ["Paddle_R5"] = "B",
                    ["Paddle_L4"] = "X",
                    ["Paddle_L5"] = "Y",
                    ["SAX_L"] = "Left",
                    ["SAX_R"] = "Up"
                },
                GKeyRemapTable = new Dictionary<string, string>
                {
                    ["G1"] = "LeftShoulder",
                    ["G2"] = "RightShoulder",
                    ["G3"] = "Back",
                    ["G4"] = "Start",
                    ["G5"] = "Guide"
                },
                ButtonRemapTable = new Dictionary<string, string>
                {
                    ["A"] = "A",
                    ["B"] = "B",
                    ["X"] = "X",
                    ["Y"] = "Y",
                    ["LeftShoulder"] = "LeftShoulder",
                    ["RightShoulder"] = "RightShoulder",
                    ["Back"] = "Back",
                    ["Start"] = "Start",
                    ["LeftThumb"] = "LeftThumb",
                    ["RightThumb"] = "RightThumb",
                    ["Up"] = "Up",
                    ["Down"] = "Down",
                    ["Left"] = "Left",
                    ["Right"] = "Right",
                    ["Guide"] = "Guide"
                }
            };

            profile.ShiftPaddleRemapTable = new Dictionary<string, string>(profile.PaddleRemapTable);
            profile.ShiftGKeyRemapTable = new Dictionary<string, string>(profile.GKeyRemapTable);
            profile.ShiftButtonRemapTable = new Dictionary<string, string>(profile.ButtonRemapTable);

            return profile;
        }

        private static string ResolveAppRoot()
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var processDirectory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(processDirectory))
                {
                    return Path.GetFullPath(processDirectory);
                }
            }

            return Path.GetFullPath(AppContext.BaseDirectory);
        }

        private static string? FindAppPath(string[] relativeSegments)
        {
            return FindAppPath(relativeSegments, path => File.Exists(path) || Directory.Exists(path));
        }

        private static string? FindAppPath(string[] relativeSegments, Func<string, bool> exists)
        {
            foreach (var root in EnumerateSearchRoots())
            {
                foreach (var candidate in EnumerateCandidatePaths(root, relativeSegments))
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateAppDirectories(params string[] relativeSegments)
        {
            foreach (var root in EnumerateSearchRoots())
            {
                foreach (var candidate in EnumerateCandidatePaths(root, relativeSegments))
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (Directory.Exists(fullPath))
                    {
                        yield return fullPath;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateCandidatePaths(string root, string[] relativeSegments)
        {
            yield return Path.Combine(new[] { root }.Concat(relativeSegments).ToArray());
            yield return Path.Combine(new[] { root, "resources" }.Concat(relativeSegments).ToArray());
            yield return Path.Combine(new[] { root, "Resources" }.Concat(relativeSegments).ToArray());
        }

        private static IEnumerable<string> EnumerateSearchRoots()
        {
            var roots = new List<string?>()
            {
                AppRoot,
                FindProjectDirectory(),
                FindRepositoryRoot(),
                GetCurrentDirectoryFallback()
            };

            return roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.GetFullPath(root!))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string? FindProjectDirectory()
        {
            foreach (var start in EnumerateRootSearchStarts())
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "ZeroCue.DataProbe.csproj")))
                    {
                        return directory.FullName;
                    }

                    var nestedProject = Path.Combine(directory.FullName, "ZeroCue.DataProbe", "ZeroCue.DataProbe.csproj");
                    if (File.Exists(nestedProject))
                    {
                        return Path.Combine(directory.FullName, "ZeroCue.DataProbe");
                    }

                    directory = directory.Parent;
                }
            }

            return null;
        }

        private static string? FindRepositoryRoot()
        {
            foreach (var start in EnumerateRootSearchStarts())
            {
                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                        File.Exists(Path.Combine(directory.FullName, "ZeroCue.DataProbe", "ZeroCue.DataProbe.csproj")))
                    {
                        return directory.FullName;
                    }

                    directory = directory.Parent;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateRootSearchStarts()
        {
            yield return AppRoot;
            yield return Path.GetFullPath(AppContext.BaseDirectory);

            var current = GetCurrentDirectoryFallback();
            if (!string.IsNullOrWhiteSpace(current))
            {
                yield return current;
            }
        }

        private static string? GetCurrentDirectoryFallback()
        {
            try
            {
                return Path.GetFullPath(Environment.CurrentDirectory);
            }
            catch
            {
                return null;
            }
        }
    }
}
