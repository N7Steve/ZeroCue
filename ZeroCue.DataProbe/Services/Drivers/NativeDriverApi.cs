using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeroCue.DataProbe.Services.Drivers
{
    internal static class NativeDriverApi
    {
        public const uint INSTALLFLAG_FORCE = 0x00000001;
        public const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;
        public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateDriverForPlugAndPlayDevicesW(
            IntPtr hwndParent,
            string hardwareId,
            string fullInfPath,
            uint installFlags,
            [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupGetInfDriverStoreLocationW(
            string fileName,
            IntPtr alternatePlatformInfo,
            string? localeName,
            [Out] StringBuilder? returnBuffer,
            uint returnBufferSize,
            out uint requiredSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDefaultDllDirectories(uint directoryFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

        /// <summary>Resolves the DriverStore FileRepository INF for a published oemNN.inf.</summary>
        public static string? GetDriverStoreInfPath(string publishedInfName)
        {
            if (!SetupGetInfDriverStoreLocationW(publishedInfName, IntPtr.Zero, null, null, 0, out var required) &&
                Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */)
            {
                return null;
            }

            if (required == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)required);
            return SetupGetInfDriverStoreLocationW(publishedInfName, IntPtr.Zero, null, buffer, required, out _)
                ? buffer.ToString()
                : null;
        }

        // ── Service control ──────────────────────────────────────────────

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_START = 0x0010;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        public const uint SERVICE_STOPPED = 0x00000001;
        public const uint SERVICE_RUNNING = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr service, uint control, out SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartServiceW(IntPtr service, uint numArgs, IntPtr args);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>Returns the service state, or null when the service does not exist.</summary>
        public static uint? QueryServiceState(string serviceName)
        {
            return WithService<uint?>(serviceName, SERVICE_QUERY_STATUS, null, handle =>
                QueryServiceStatus(handle, out var status) ? status.dwCurrentState : null);
        }

        public static bool StopService(string serviceName)
        {
            return WithService(serviceName, SERVICE_STOP | SERVICE_QUERY_STATUS, false, handle =>
            {
                if (QueryServiceStatus(handle, out var status) && status.dwCurrentState == SERVICE_STOPPED)
                {
                    return true;
                }

                return ControlService(handle, SERVICE_CONTROL_STOP, out _) ||
                    Marshal.GetLastWin32Error() == 1062; // ERROR_SERVICE_NOT_ACTIVE
            });
        }

        public static bool StartService(string serviceName)
        {
            return WithService(serviceName, SERVICE_START, false, handle =>
                StartServiceW(handle, 0, IntPtr.Zero) ||
                Marshal.GetLastWin32Error() == 1056); // ERROR_SERVICE_ALREADY_RUNNING
        }

        private static T WithService<T>(string serviceName, uint access, T fallback, Func<IntPtr, T> action)
        {
            var manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero)
            {
                return fallback;
            }

            try
            {
                var service = OpenServiceW(manager, serviceName, access);
                if (service == IntPtr.Zero)
                {
                    return fallback;
                }

                try
                {
                    return action(service);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        public static string DescribeWin32Error(int error) => $"{error} ({new Win32Exception(error).Message})";
    }
}
