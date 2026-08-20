using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZeroCue.DataProbe.Services
{
    internal static class WirelessHidDetectionService
    {
        private const int HidpStatusSuccess = 0x00110000;
        private static readonly SupportedScufDeviceProfile DeviceProfile = SupportedScufDeviceProfile.ScufEnvisionPro;

        public static IReadOnlyList<WirelessHidCandidate> ScanSupportedReceiverCollections(Action<string> log)
        {
            var candidates = new List<WirelessHidCandidate>();
            try
            {
                HidD_GetHidGuid(out var hidGuid);
                foreach (var path in EnumerateHidPaths(hidGuid))
                {
                    if (!PathContainsSupportedWirelessReceiver(path))
                    {
                        continue;
                    }

                    var candidate = InspectPath(path, log);
                    if (candidate != null)
                    {
                        candidates.Add(candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                log($"[WIRELESS-HID] HID enumeration failed: {ex.Message}");
            }

            log($"[WIRELESS-HID] compatible candidate count={candidates.Count(c => c.IsCompatible)}");
            return candidates
                .OrderByDescending(c => c.IsCompatible)
                .ThenByDescending(c => c.PathHasPreferredHint)
                .ThenBy(c => c.DevicePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static WirelessHidCandidate? InspectPath(string path, Action<string> log)
        {
            using var handle = WinUsbInterop.CreateFile(
                path,
                0,
                WinUsbInterop.FILE_SHARE_READ | WinUsbInterop.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WinUsbInterop.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                log($"[WIRELESS-HID] candidate path={path} rejected reason=CreateFile failed for HID caps: {WinUsbInterop.FormatLastWin32Error("CreateFile")}");
                return null;
            }

            var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attrs))
            {
                log($"[WIRELESS-HID] candidate path={path} rejected reason=HidD_GetAttributes failed.");
                return null;
            }

            if (!HidD_GetPreparsedData(handle, out var preparsedData))
            {
                log($"[WIRELESS-HID] candidate VID=0x{attrs.VendorID:X4} PID=0x{attrs.ProductID:X4} path={path} rejected reason=HidD_GetPreparsedData failed.");
                return null;
            }

            try
            {
                var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                var status = HidP_GetCaps(preparsedData, ref caps);
                if (status != HidpStatusSuccess)
                {
                    log($"[WIRELESS-HID] candidate VID=0x{attrs.VendorID:X4} PID=0x{attrs.ProductID:X4} path={path} rejected reason=HidP_GetCaps status=0x{status:X8}.");
                    return null;
                }

                var receiverIdentity = DeviceProfile.FindWirelessReceiver(attrs.VendorID, attrs.ProductID);
                var isWirelessReceiver = receiverIdentity != null;
                var hasPreferredHint = path.IndexOf("mi_04&col02", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasOutputReport = caps.OutputReportByteLength > 0;
                var isConsumerControl = caps.UsagePage == 0x000C;
                var isCompatible =
                    isWirelessReceiver &&
                    !isConsumerControl &&
                    caps.UsagePage == DeviceProfile.HidUsagePage &&
                    caps.Usage == DeviceProfile.HidUsage &&
                    caps.InputReportByteLength == DeviceProfile.ReportSize &&
                    caps.OutputReportByteLength == DeviceProfile.ReportSize &&
                    caps.FeatureReportByteLength == DeviceProfile.ReportSize;

                var reason = isCompatible
                    ? "selected: VID/PID + HID caps match"
                    : BuildRejectReason(isWirelessReceiver, isConsumerControl, hasOutputReport, caps);

                log($"[WIRELESS-HID] candidate transport=HID variant={receiverIdentity?.Variant ?? "unknown"} experimental={receiverIdentity?.IsExperimental ?? false} VID=0x{attrs.VendorID:X4} PID=0x{attrs.ProductID:X4} usagePage=0x{caps.UsagePage:X4} usage=0x{caps.Usage:X4} inputReportLength={caps.InputReportByteLength} outputReportLength={caps.OutputReportByteLength} featureReportLength={caps.FeatureReportByteLength} preferredPathHint={hasPreferredHint} compatible={isCompatible} reason={reason} path={path}");

                return new WirelessHidCandidate(
                    path,
                    attrs.VendorID,
                    attrs.ProductID,
                    caps.UsagePage,
                    caps.Usage,
                    caps.InputReportByteLength,
                    caps.OutputReportByteLength,
                    caps.FeatureReportByteLength,
                    receiverIdentity?.Variant ?? "unknown",
                    receiverIdentity?.IsExperimental ?? false,
                    hasPreferredHint,
                    isCompatible,
                    reason);
            }
            finally
            {
                HidD_FreePreparsedData(preparsedData);
            }
        }

        private static string BuildRejectReason(bool isWirelessReceiver, bool isConsumerControl, bool hasOutputReport, HIDP_CAPS caps)
        {
            if (!isWirelessReceiver) return "VID/PID not in supported wireless receiver family";
            if (isConsumerControl) return "Consumer Control usage page 0x000C";
            if (!hasOutputReport) return "missing OutputReportLength";
            if (caps.UsagePage != DeviceProfile.HidUsagePage) return "usage page mismatch";
            if (caps.Usage != DeviceProfile.HidUsage) return "usage mismatch";
            if (caps.InputReportByteLength != DeviceProfile.ReportSize) return "input report length mismatch";
            if (caps.OutputReportByteLength != DeviceProfile.ReportSize) return "output report length mismatch";
            if (caps.FeatureReportByteLength != DeviceProfile.ReportSize) return "feature report length mismatch";
            return "caps mismatch";
        }

        private static bool PathContainsSupportedWirelessReceiver(string path)
        {
            return DeviceProfile.WirelessReceiverIdentities.Any(identity =>
                path.IndexOf($"vid_{identity.VendorId:x4}", StringComparison.OrdinalIgnoreCase) >= 0 &&
                path.IndexOf($"pid_{identity.ProductId:x4}", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<string> EnumerateHidPaths(Guid hidGuid)
        {
            var guid = hidGuid;
            var infoSet = WinUsbInterop.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, WinUsbInterop.DIGCF_PRESENT | WinUsbInterop.DIGCF_DEVICEINTERFACE);
            if (infoSet == WinUsbInterop.INVALID_HANDLE_VALUE)
            {
                yield break;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    var data = new WinUsbInterop.SP_DEVICE_INTERFACE_DATA
                    {
                        cbSize = (uint)Marshal.SizeOf<WinUsbInterop.SP_DEVICE_INTERFACE_DATA>()
                    };

                    if (!WinUsbInterop.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref guid, index, ref data))
                    {
                        yield break;
                    }

                    var path = GetDevicePath(infoSet, ref data);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
                    }

                    index++;
                }
            }
            finally
            {
                WinUsbInterop.SetupDiDestroyDeviceInfoList(infoSet);
            }
        }

        private static string? GetDevicePath(IntPtr infoSet, ref WinUsbInterop.SP_DEVICE_INTERFACE_DATA data)
        {
            WinUsbInterop.SetupDiGetDeviceInterfaceDetail(infoSet, ref data, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
            if (requiredSize == 0)
            {
                return null;
            }

            var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                if (!WinUsbInterop.SetupDiGetDeviceInterfaceDetail(infoSet, ref data, detailBuffer, requiredSize, out _, IntPtr.Zero))
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

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid HidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }
    }

    internal sealed record WirelessHidCandidate(
        string DevicePath,
        int VendorId,
        int ProductId,
        int UsagePage,
        int Usage,
        int InputReportLength,
        int OutputReportLength,
        int FeatureReportLength,
        string Variant,
        bool IsExperimental,
        bool PathHasPreferredHint,
        bool IsCompatible,
        string Reason);
}
