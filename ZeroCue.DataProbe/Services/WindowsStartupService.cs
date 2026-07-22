using System;
using Microsoft.Win32;

namespace ZeroCue.DataProbe.Services
{
    internal static class WindowsStartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ZeroCue";
        private const string MinimizedArgument = "--minimized";

        public static bool ShouldStartMinimized(string[]? args)
        {
            if (args == null)
            {
                return false;
            }

            foreach (var arg in args)
            {
                if (string.Equals(arg, MinimizedArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static void Configure(bool enabled, bool startMinimized)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key == null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            var command = $"\"{executablePath}\"";
            if (startMinimized)
            {
                command += $" {MinimizedArgument}";
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }
}
