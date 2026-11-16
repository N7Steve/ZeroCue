using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZeroCue.DataProbe.Services.Pnp
{
    /// <summary>One USB device node as reported by the PnP manager.</summary>
    public sealed record PnpDevice(
        string InstanceId,
        int VendorId,
        int ProductId,
        int? InterfaceNumber,
        bool IsPresent,
        string? Service,
        string? DriverInfPath,
        uint ProblemCode,
        uint ProblemStatus,
        string? Parent,
        string? Description,
        uint DevNodeStatus = 0,
        bool? WinUsbInterfaceActive = null)
    {
        public bool IsWinUsb => string.Equals(Service, "WinUSB", StringComparison.OrdinalIgnoreCase);

        public bool HasProblem => IsPresent && ProblemCode != 0;

        /// <summary>
        /// The driver assignment changed but the running stack did not: Windows flags the
        /// node DN_NEED_RESTART (usually because an app held the old driver open), or the
        /// node is assigned to WinUSB but its WinUSB device interface is not registered.
        /// The device only becomes usable after a restart or a physical reconnect.
        /// </summary>
        public bool IsRestartPending =>
            IsPresent &&
            ((DevNodeStatus & 0x00000100 /* DN_NEED_RESTART */) != 0 || (IsWinUsb && WinUsbInterfaceActive == false));

        /// <summary>Hardware identity without instance suffix, e.g. <c>VID_1B1C&amp;PID_3A08&amp;MI_04</c>.</summary>
        public string HardwareKey => PnpIdentity.FormatHardwareKey(VendorId, ProductId, InterfaceNumber);

        public override string ToString() =>
            $"{InstanceId} present={IsPresent} service={Service ?? "<none>"} inf={DriverInfPath ?? "<none>"} problem={ProblemCode}" +
            (ProblemStatus != 0 ? $" status=0x{ProblemStatus:X8}" : string.Empty) +
            (IsRestartPending ? $" restartPending=true dn=0x{DevNodeStatus:X8} winusbInterface={WinUsbInterfaceActive?.ToString() ?? "unknown"}" : string.Empty);
    }

    public static class PnpIdentity
    {
        private static readonly Regex UsbInstanceRegex = new(
            @"^USB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})(?:&MI_(?<mi>[0-9A-F]{2}))?\\",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool TryParseUsbInstanceId(string instanceId, out int vendorId, out int productId, out int? interfaceNumber)
        {
            vendorId = 0;
            productId = 0;
            interfaceNumber = null;
            var match = UsbInstanceRegex.Match(instanceId);
            if (!match.Success)
            {
                return false;
            }

            vendorId = int.Parse(match.Groups["vid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            productId = int.Parse(match.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (match.Groups["mi"].Success)
            {
                interfaceNumber = int.Parse(match.Groups["mi"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return true;
        }

        public static string FormatHardwareKey(int vendorId, int productId, int? interfaceNumber) =>
            interfaceNumber is int mi
                ? $"VID_{vendorId:X4}&PID_{productId:X4}&MI_{mi:X2}"
                : $"VID_{vendorId:X4}&PID_{productId:X4}";
    }

    /// <summary>
    /// Fast CfgMgr32 enumeration of USB device nodes. A full scan of the USB
    /// enumerator takes a few milliseconds, unlike Get-PnpDevice (~1 s).
    /// </summary>
    public static class PnpQuery
    {
        /// <summary>Enumerates USB nodes for the given vendor IDs.</summary>
        /// <param name="includePhantoms">Include nodes that are not currently present.</param>
        public static IReadOnlyList<PnpDevice> GetUsbDevices(IReadOnlyCollection<int> vendorIds, bool includePhantoms = false)
        {
            var result = new List<PnpDevice>();
            foreach (var instanceId in GetDeviceIdList("USB", includePhantoms))
            {
                if (!PnpIdentity.TryParseUsbInstanceId(instanceId, out var vid, out var pid, out var mi) ||
                    !vendorIds.Contains(vid))
                {
                    continue;
                }

                var device = TryReadDevice(instanceId, vid, pid, mi);
                if (device != null && (includePhantoms || device.IsPresent))
                {
                    result.Add(device);
                }
            }

            return result;
        }

        public static PnpDevice? GetDevice(string instanceId)
        {
            if (!PnpIdentity.TryParseUsbInstanceId(instanceId, out var vid, out var pid, out var mi))
            {
                return null;
            }

            return TryReadDevice(instanceId, vid, pid, mi);
        }

        /// <summary>Asks PnP to rescan the device tree (equivalent to pnputil /scan-devices).</summary>
        public static bool RescanDevices()
        {
            if (PnpNative.CM_Locate_DevNodeW(out var root, null, PnpNative.CM_LOCATE_DEVNODE_NORMAL) != PnpNative.CR_SUCCESS)
            {
                return false;
            }

            return PnpNative.CM_Reenumerate_DevNode(root, PnpNative.CM_REENUMERATE_SYNCHRONOUS) == PnpNative.CR_SUCCESS;
        }

        private static IEnumerable<string> GetDeviceIdList(string enumerator, bool includePhantoms)
        {
            uint flags = PnpNative.CM_GETIDLIST_FILTER_ENUMERATOR;
            if (!includePhantoms)
            {
                flags |= PnpNative.CM_GETIDLIST_FILTER_PRESENT;
            }

            // The list can grow between the size query and the read; retry a few times.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (PnpNative.CM_Get_Device_ID_List_SizeW(out var length, enumerator, flags) != PnpNative.CR_SUCCESS || length == 0)
                {
                    return Array.Empty<string>();
                }

                var buffer = new char[length];
                int cr = PnpNative.CM_Get_Device_ID_ListW(enumerator, buffer, length, flags);
                if (cr == PnpNative.CR_BUFFER_SMALL)
                {
                    continue;
                }

                if (cr != PnpNative.CR_SUCCESS)
                {
                    return Array.Empty<string>();
                }

                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }

            return Array.Empty<string>();
        }

        private static PnpDevice? TryReadDevice(string instanceId, int vid, int pid, int? mi)
        {
            if (PnpNative.CM_Locate_DevNodeW(out var devInst, instanceId, PnpNative.CM_LOCATE_DEVNODE_PHANTOM) != PnpNative.CR_SUCCESS)
            {
                return null;
            }

            // CM_Get_DevNode_Status fails with CR_NO_SUCH_DEVINST for phantoms,
            // which is the documented way to test presence.
            bool present = PnpNative.CM_Get_DevNode_Status(out var status, out var problem, devInst, 0) == PnpNative.CR_SUCCESS;
            uint problemCode = present ? problem : 0;
            uint problemStatus = present ? ReadUInt32(devInst, PnpNative.DEVPKEY_Device_ProblemStatus) ?? 0 : 0;
            string? service = ReadString(devInst, PnpNative.DEVPKEY_Device_Service);
            bool? winUsbInterfaceActive = present && problemCode == 0 && string.Equals(service, "WinUSB", StringComparison.OrdinalIgnoreCase)
                ? IsWinUsbInterfaceActive(devInst, instanceId)
                : null;

            return new PnpDevice(
                instanceId,
                vid,
                pid,
                mi,
                present,
                service,
                ReadString(devInst, PnpNative.DEVPKEY_Device_DriverInfPath),
                problemCode,
                problemStatus,
                ReadString(devInst, PnpNative.DEVPKEY_Device_Parent),
                ReadString(devInst, PnpNative.DEVPKEY_Device_FriendlyName) ?? ReadString(devInst, PnpNative.DEVPKEY_Device_DeviceDesc),
                present ? status : 0,
                winUsbInterfaceActive);
        }

        /// <summary>
        /// True when one of the WinUSB device interfaces declared in the node's
        /// DeviceInterfaceGUIDs is registered and enabled; false when none is (the
        /// WinUSB stack is not running yet); null when the node declares no GUID.
        /// </summary>
        private static bool? IsWinUsbInterfaceActive(uint devInst, string instanceId)
        {
            var guids = ReadDeviceInterfaceGuids(devInst);
            if (guids.Count == 0)
            {
                return null;
            }

            foreach (var guid in guids)
            {
                var classGuid = guid;
                // An empty list is a single terminating NUL, so length 1 means "none".
                if (PnpNative.CM_Get_Device_Interface_List_SizeW(out var length, ref classGuid, instanceId, PnpNative.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) == PnpNative.CR_SUCCESS &&
                    length > 1)
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<Guid> ReadDeviceInterfaceGuids(uint devInst)
        {
            var result = new List<Guid>();
            if (PnpNative.CM_Open_DevNode_Key(devInst, PnpNative.KEY_READ, 0, PnpNative.RegDisposition_OpenExisting, out var handle, PnpNative.CM_REGISTRY_HARDWARE) != PnpNative.CR_SUCCESS)
            {
                handle.Dispose();
                return result;
            }

            try
            {
                using var key = Microsoft.Win32.RegistryKey.FromHandle(handle);
                var values = key.GetValue("DeviceInterfaceGUIDs") switch
                {
                    string[] multi => multi,
                    string single => new[] { single },
                    _ => key.GetValue("DeviceInterfaceGUID") is string legacy ? new[] { legacy } : Array.Empty<string>()
                };

                foreach (var value in values)
                {
                    if (Guid.TryParse(value, out var guid))
                    {
                        result.Add(guid);
                    }
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
            finally
            {
                handle.Dispose();
            }

            return result;
        }

        private static byte[]? ReadProperty(uint devInst, PnpNative.DEVPROPKEY key, out uint type)
        {
            uint size = 0;
            int cr = PnpNative.CM_Get_DevNode_PropertyW(devInst, ref key, out type, null, ref size, 0);
            if (cr != PnpNative.CR_BUFFER_SMALL || size == 0)
            {
                return null;
            }

            var buffer = new byte[size];
            cr = PnpNative.CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0);
            return cr == PnpNative.CR_SUCCESS ? buffer : null;
        }

        private static string? ReadString(uint devInst, PnpNative.DEVPROPKEY key)
        {
            var buffer = ReadProperty(devInst, key, out var type);
            if (buffer == null || (type != PnpNative.DEVPROP_TYPE_STRING && type != PnpNative.DEVPROP_TYPE_STRING_LIST))
            {
                return null;
            }

            var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            if (type == PnpNative.DEVPROP_TYPE_STRING_LIST)
            {
                text = string.Join(";", text.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            }

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static uint? ReadUInt32(uint devInst, PnpNative.DEVPROPKEY key)
        {
            var buffer = ReadProperty(devInst, key, out var type);
            if (buffer == null || buffer.Length < 4)
            {
                return null;
            }

            return type switch
            {
                PnpNative.DEVPROP_TYPE_UINT32 or PnpNative.DEVPROP_TYPE_INT32 or PnpNative.DEVPROP_TYPE_NTSTATUS => BitConverter.ToUInt32(buffer, 0),
                _ => null
            };
        }
    }
}
