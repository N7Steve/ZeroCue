using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZeroCue.DataProbe.Services
{
    internal static class WinUsbInterop
    {
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        public const int ERROR_NO_MORE_ITEMS = 259;
        public const int ERROR_SEM_TIMEOUT = 121;
        public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        public const int ERROR_GEN_FAILURE = 31;
        public const int ERROR_OPERATION_ABORTED = 995;
        public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        public const uint AUTO_CLEAR_STALL = 0x04;
        public const uint AUTO_SUSPEND = 0x81;

        private static readonly Guid GUID_DEVINTERFACE_USB_DEVICE = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        private static readonly Guid LOCAL_LIBWDI_DEVICE_GUID = new("5239B516-A6D6-4685-96E2-A68585377C29");

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_GetAssociatedInterface(IntPtr InitializeHandle, byte AssociatedInterfaceIndex, out IntPtr AssociatedInterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryInterfaceSettings(IntPtr InterfaceHandle, byte AlternateInterfaceNumber, out USB_INTERFACE_DESCRIPTOR UsbAltInterfaceDescriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_QueryPipe(IntPtr InterfaceHandle, byte AlternateInterfaceNumber, byte PipeIndex, out WINUSB_PIPE_INFORMATION PipeInformation);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, byte PipeID, uint PolicyType, uint ValueLength, ref uint Value);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_SetPowerPolicy(IntPtr InterfaceHandle, uint PolicyType, uint ValueLength, ref byte Value);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern unsafe bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, byte[] Buffer, uint BufferLength, out uint LengthTransferred, NativeOverlapped* Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern unsafe bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, byte[] Buffer, uint BufferLength, out uint LengthTransferred, NativeOverlapped* Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Free(IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ResetPipe(IntPtr InterfaceHandle, byte PipeID);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern unsafe bool WinUsb_GetOverlappedResult(IntPtr InterfaceHandle, NativeOverlapped* Overlapped, out uint NumberOfBytesTransferred, bool Wait);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        public static IEnumerable<string> EnumerateUsbDevicePaths(string vid, string pid, Action<string>? log = null)
        {
            foreach (var guid in EnumerateInterfaceGuids(vid, pid, log))
            {
                foreach (var path in EnumerateUsbDevicePathsForGuid(guid, vid, pid, log))
                {
                    yield return path;
                }
            }

            foreach (var path in EnumerateRegistryDeviceInterfacePaths(vid, pid, log))
            {
                yield return path;
            }
        }

        private static IEnumerable<Guid> EnumerateInterfaceGuids(string vid, string pid, Action<string>? log)
        {
            var guids = new List<Guid>
            {
                GUID_DEVINTERFACE_USB_DEVICE,
                LOCAL_LIBWDI_DEVICE_GUID
            };

            try
            {
                using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usbKey == null)
                {
                    return guids.Distinct().ToList();
                }

                foreach (var deviceKeyName in usbKey.GetSubKeyNames())
                {
                    if (deviceKeyName.IndexOf(vid, StringComparison.OrdinalIgnoreCase) < 0 ||
                        deviceKeyName.IndexOf(pid, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    using var deviceKey = usbKey.OpenSubKey(deviceKeyName);
                    if (deviceKey == null)
                    {
                        continue;
                    }

                    foreach (var instanceName in deviceKey.GetSubKeyNames())
                    {
                        using var parametersKey = deviceKey.OpenSubKey($@"{instanceName}\Device Parameters");
                        var value = parametersKey?.GetValue("DeviceInterfaceGUIDs");
                        foreach (var parsed in ParseRegistryGuids(value))
                        {
                            guids.Add(parsed);
                            log?.Invoke($"Registry DeviceInterfaceGUID candidate for {deviceKeyName}\\{instanceName}: {parsed}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Registry DeviceInterfaceGUID scan failed: {ex.Message}");
            }

            return guids.Distinct().ToList();
        }

        private static IEnumerable<Guid> ParseRegistryGuids(object? value)
        {
            if (value is string single)
            {
                if (Guid.TryParse(single.Trim('{', '}'), out var guid))
                {
                    yield return guid;
                }

                yield break;
            }

            if (value is string[] many)
            {
                foreach (var item in many)
                {
                    if (Guid.TryParse(item.Trim('{', '}'), out var guid))
                    {
                        yield return guid;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateRegistryDeviceInterfacePaths(string vid, string pid, Action<string>? log)
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey == null)
            {
                yield break;
            }

            foreach (var deviceKeyName in usbKey.GetSubKeyNames())
            {
                if (deviceKeyName.IndexOf(vid, StringComparison.OrdinalIgnoreCase) < 0 ||
                    deviceKeyName.IndexOf(pid, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                using var deviceKey = usbKey.OpenSubKey(deviceKeyName);
                if (deviceKey == null)
                {
                    continue;
                }

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    using var parametersKey = deviceKey.OpenSubKey($@"{instanceName}\Device Parameters");
                    foreach (var guid in ParseRegistryGuids(parametersKey?.GetValue("DeviceInterfaceGUIDs")))
                    {
                        var normalizedDeviceKeyName = deviceKeyName.ToLowerInvariant();
                        var normalizedInstanceName = instanceName.ToLowerInvariant();
                        var normalizedGuid = guid.ToString("D").ToLowerInvariant();
                        var path = $@"\\?\usb#{normalizedDeviceKeyName}#{normalizedInstanceName}#{{{normalizedGuid}}}";
                        log?.Invoke($"Registry fallback device path={path}");
                        yield return path;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateUsbDevicePathsForGuid(Guid interfaceGuid, string vid, string pid, Action<string>? log)
        {
            log?.Invoke($"Enumerating device interface GUID={interfaceGuid}");
            var guid = interfaceGuid;
            var infoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (infoSet == INVALID_HANDLE_VALUE)
            {
                log?.Invoke($"SetupDiGetClassDevs failed win32={Marshal.GetLastWin32Error()}");
                yield break;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA
                    {
                        cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
                    };

                    if (!SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref guid, index, ref data))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err != ERROR_NO_MORE_ITEMS)
                        {
                            log?.Invoke($"SetupDiEnumDeviceInterfaces stopped win32={err}");
                        }

                        yield break;
                    }

                    var path = GetDevicePath(infoSet, ref data);
                    if (path != null &&
                        path.IndexOf(vid, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        path.IndexOf(pid, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        yield return path;
                    }

                    index++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }
        }

        public static SafeFileHandle OpenDeviceHandle(string devicePath)
        {
            return CreateFile(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);
        }

        public static string FormatLastWin32Error(string prefix)
        {
            var error = Marshal.GetLastWin32Error();
            return $"{prefix}: win32={error} {new System.ComponentModel.Win32Exception(error).Message}";
        }

        private static string? GetDevicePath(IntPtr infoSet, ref SP_DEVICE_INTERFACE_DATA data)
        {
            SetupDiGetDeviceInterfaceDetail(infoSet, ref data, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
            if (requiredSize == 0)
            {
                return null;
            }

            var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(infoSet, ref data, detailBuffer, requiredSize, out _, IntPtr.Zero))
                {
                    return null;
                }

                return Marshal.PtrToStringAuto(detailBuffer + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct USB_INTERFACE_DESCRIPTOR
        {
            public byte bLength;
            public byte bDescriptorType;
            public byte bInterfaceNumber;
            public byte bAlternateSetting;
            public byte bNumEndpoints;
            public byte bInterfaceClass;
            public byte bInterfaceSubClass;
            public byte bInterfaceProtocol;
            public byte iInterface;
        }

        public enum USBD_PIPE_TYPE
        {
            Control = 0,
            Isochronous = 1,
            Bulk = 2,
            Interrupt = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINUSB_PIPE_INFORMATION
        {
            public USBD_PIPE_TYPE PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }
    }
}
