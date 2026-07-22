using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ZeroCue.DataProbe.Services
{
    public static class ForegroundApplicationService
    {
        public static string GetForegroundProcessPath()
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            _ = GetWindowThreadProcessId(handle, out uint processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return GetProcessImagePath(processId);
            }
        }

        private static string GetProcessImagePath(uint processId)
        {
            var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                var buffer = new StringBuilder(32768);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(processHandle, 0, buffer, ref size)
                    ? buffer.ToString()
                    : string.Empty;
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
