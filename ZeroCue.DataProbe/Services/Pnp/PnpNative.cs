using System;
using System.Runtime.InteropServices;

namespace ZeroCue.DataProbe.Services.Pnp
{
    /// <summary>
    /// Minimal CfgMgr32 surface used for fast, in-process device enumeration and
    /// hot-plug notification. Every call is read-only except
    /// <see cref="CM_Reenumerate_DevNode"/>, which only asks PnP to rescan.
    /// </summary>
    internal static class PnpNative
    {
        public const int CR_SUCCESS = 0x00;
        public const int CR_NO_SUCH_DEVNODE = 0x0D;
        public const int CR_BUFFER_SMALL = 0x1A;
        public const int CR_NO_SUCH_VALUE = 0x25;

        public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
        public const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
        public const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
        public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
        public const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;

        public const uint DN_STARTED = 0x00000008;
        public const uint DN_HAS_PROBLEM = 0x00000400;

        /// <summary>DN_NEED_RESTART (alias of DN_LIAR): the device must restart before its new driver runs.</summary>
        public const uint DN_NEED_RESTART = 0x00000100;

        public const uint CM_REGISTRY_HARDWARE = 0x00000000;
        public const uint RegDisposition_OpenExisting = 0x00000001;
        public const uint KEY_READ = 0x00020019;
        public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;

        public const uint DEVPROP_TYPE_UINT32 = 0x00000007;
        public const uint DEVPROP_TYPE_INT32 = 0x00000006;
        public const uint DEVPROP_TYPE_NTSTATUS = 0x00000017;
        public const uint DEVPROP_TYPE_STRING = 0x00000012;
        public const uint DEVPROP_TYPE_STRING_LIST = 0x00002012;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;

            public DEVPROPKEY(string guid, uint pid)
            {
                fmtid = new Guid(guid);
                this.pid = pid;
            }
        }

        private const string DeviceBaseGuid = "a45c254e-df1c-4efd-8020-67d146a850e0";
        private const string DeviceProblemGuid = "4340a6c5-93fa-4706-972c-7b648008a5a7";
        private const string DeviceDriverGuid = "a8b865dd-2e3d-4094-ad97-e593a70c75d6";

        public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new(DeviceBaseGuid, 2);
        public static readonly DEVPROPKEY DEVPKEY_Device_HardwareIds = new(DeviceBaseGuid, 3);
        public static readonly DEVPROPKEY DEVPKEY_Device_Service = new(DeviceBaseGuid, 6);
        public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(DeviceBaseGuid, 14);
        public static readonly DEVPROPKEY DEVPKEY_Device_ProblemCode = new(DeviceProblemGuid, 3);
        public static readonly DEVPROPKEY DEVPKEY_Device_Parent = new(DeviceProblemGuid, 8);
        public static readonly DEVPROPKEY DEVPKEY_Device_ProblemStatus = new(DeviceProblemGuid, 12);
        public static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath = new(DeviceDriverGuid, 5);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string? pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_PropertyW(
            uint dnDevInst,
            ref DEVPROPKEY propertyKey,
            out uint propertyType,
            [Out] byte[]? propertyBuffer,
            ref uint propertyBufferSize,
            uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

        /// <summary>Opens the device's hardware ("Device Parameters") key read-only.</summary>
        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Open_DevNode_Key(
            uint dnDevNode,
            uint samDesired,
            uint ulHardwareProfile,
            uint disposition,
            out Microsoft.Win32.SafeHandles.SafeRegistryHandle phkDevice,
            uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_SizeW(
            out uint pulLen,
            ref Guid interfaceClassGuid,
            string? pDeviceID,
            uint ulFlags);

        // ── Notifications ────────────────────────────────────────────────

        public enum CM_NOTIFY_FILTER_TYPE
        {
            DeviceInterface = 0,
            DeviceHandle = 1,
            DeviceInstance = 2
        }

        public enum CM_NOTIFY_ACTION
        {
            DeviceInterfaceArrival = 0,
            DeviceInterfaceRemoval = 1,
            DeviceQueryRemove = 2,
            DeviceQueryRemoveFailed = 3,
            DeviceRemovePending = 4,
            DeviceRemoveComplete = 5,
            DeviceCustomEvent = 6,
            DeviceInstanceEnumerated = 7,
            DeviceInstanceStarted = 8,
            DeviceInstanceRemoved = 9
        }

        public const uint CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CM_NOTIFY_FILTER
        {
            public uint cbSize;
            public uint Flags;
            public CM_NOTIFY_FILTER_TYPE FilterType;
            public uint Reserved;
            public Guid ClassGuid;

            // The native union is MAX_DEVICE_ID_LEN (200) WCHARs; ClassGuid
            // occupies the first 16 bytes. Pad to the full union size.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 384)]
            public byte[] UnionPadding;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate uint CM_NOTIFY_CALLBACK(
            IntPtr hNotify,
            IntPtr context,
            CM_NOTIFY_ACTION action,
            IntPtr eventData,
            uint eventDataSize);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Register_Notification(
            ref CM_NOTIFY_FILTER pFilter,
            IntPtr pContext,
            CM_NOTIFY_CALLBACK pCallback,
            out IntPtr pNotifyContext);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Unregister_Notification(IntPtr notifyContext);
    }
}
