using System;
using Microsoft.Win32;

namespace ZeroCue.DataProbe.Services
{
    internal static class WindowsStartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
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
                ClearStartupApprovalOverride();
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

            // Windows keeps a separate disabled/enabled state for entries shown in
            // Task Manager's Startup Apps page. Rewriting the Run value does not
            // clear a previous disabled state, so the ZeroCue setting could remain
            // checked while Windows silently skipped it at sign-in. With ZeroCue's
            // own setting enabled, let the Run entry be active again by removing
            // that per-entry override.
            ClearStartupApprovalOverride();
        }

        /// <summary>
        /// Called on every launch. It only repairs a registration whose executable no
        /// longer exists (the portable folder was moved). It never re-enables an entry
        /// the user disabled in Task Manager and never points autostart at whatever
        /// build happens to be running.
        /// </summary>
        public static void RepairIfStale(bool enabled, bool startMinimized)
        {
            if (!enabled)
            {
                return;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command))
                {
                    return;
                }

                var registeredPath = ExtractExecutablePath(command);
                if (registeredPath == null || System.IO.File.Exists(registeredPath))
                {
                    return;
                }

                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return;
                }

                var repaired = $"\"{executablePath}\"" + (startMinimized ? $" {MinimizedArgument}" : string.Empty);
                key.SetValue(ValueName, repaired, RegistryValueKind.String);
            }
            catch
            {
                // Autostart repair is best effort.
            }
        }

        private static string? ExtractExecutablePath(string command)
        {
            command = command.Trim();
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command[1..end] : null;
            }

            var space = command.IndexOf(' ');
            return space > 0 ? command[..space] : command;
        }

        private static void ClearStartupApprovalOverride()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
