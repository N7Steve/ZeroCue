using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZeroCue.DataProbe.Services.Pnp
{
    /// <summary>
    /// Raises <see cref="Changed"/> shortly after any SCUF/Corsair device interface
    /// arrives or is removed, so the connection loop reacts to hot-plug and to the
    /// receiver's base/active PID transition immediately instead of polling.
    /// </summary>
    public sealed class DevicePresenceMonitor : IDisposable
    {
        private static readonly string[] VendorMarkers = { "vid_1b1c", "vid_2e95" };

        private readonly PnpNative.CM_NOTIFY_CALLBACK _callback;
        private readonly Timer _debounce;
        private IntPtr _registration;
        private int _disposed;

        public event Action? Changed;

        public bool IsActive => _registration != IntPtr.Zero;

        public DevicePresenceMonitor()
        {
            // The delegate must stay alive for as long as the registration exists.
            _callback = OnNotification;
            _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);

            var filter = new PnpNative.CM_NOTIFY_FILTER
            {
                cbSize = (uint)Marshal.SizeOf<PnpNative.CM_NOTIFY_FILTER>(),
                Flags = PnpNative.CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES,
                FilterType = PnpNative.CM_NOTIFY_FILTER_TYPE.DeviceInterface,
                UnionPadding = new byte[384]
            };

            if (PnpNative.CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out var registration) == PnpNative.CR_SUCCESS)
            {
                _registration = registration;
            }
        }

        private uint OnNotification(IntPtr hNotify, IntPtr context, PnpNative.CM_NOTIFY_ACTION action, IntPtr eventData, uint eventDataSize)
        {
            try
            {
                if (action is not (PnpNative.CM_NOTIFY_ACTION.DeviceInterfaceArrival or PnpNative.CM_NOTIFY_ACTION.DeviceInterfaceRemoval) ||
                    eventData == IntPtr.Zero ||
                    eventDataSize <= 24)
                {
                    return 0;
                }

                // CM_NOTIFY_EVENT_DATA: FilterType (4) + Reserved (4) + ClassGuid (16) + SymbolicLink.
                var symbolicLink = Marshal.PtrToStringUni(eventData + 24);
                if (symbolicLink == null)
                {
                    return 0;
                }

                foreach (var marker in VendorMarkers)
                {
                    if (symbolicLink.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        // Interfaces of one device arrive in bursts; coalesce them.
                        _debounce.Change(300, Timeout.Infinite);
                        break;
                    }
                }
            }
            catch
            {
                // Never let an exception cross the native callback boundary.
            }

            return 0;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var registration = Interlocked.Exchange(ref _registration, IntPtr.Zero);
            if (registration != IntPtr.Zero)
            {
                // Blocks until in-flight callbacks finish; must not be called from a callback.
                PnpNative.CM_Unregister_Notification(registration);
            }

            _debounce.Dispose();
        }
    }
}
