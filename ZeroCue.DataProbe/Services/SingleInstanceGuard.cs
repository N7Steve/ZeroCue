using System;
using System.Threading;

namespace ZeroCue.DataProbe.Services
{
    /// <summary>
    /// Ensures a single ZeroCue process per user session. Two instances would
    /// fight over the USB interfaces, create two virtual pads and overwrite each
    /// other's profiles. A second launch asks the running instance to show itself.
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        private const string MutexName = @"Local\ZeroCue.SingleInstance";
        private const string ShowEventName = @"Local\ZeroCue.ShowMainWindow";

        private readonly Mutex _mutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _registration;

        private SingleInstanceGuard(Mutex mutex)
        {
            _mutex = mutex;
        }

        public static SingleInstanceGuard? TryAcquire()
        {
            var mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous owner crashed; ownership passes to us.
            }

            return new SingleInstanceGuard(mutex);
        }

        public static void SignalExistingInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var showEvent))
                {
                    using (showEvent)
                    {
                        showEvent.Set();
                    }
                }
            }
            catch
            {
                // Best effort: the running instance may still be starting.
            }
        }

        /// <summary>Invokes <paramref name="onShowRequested"/> on a thread-pool thread when another launch signals.</summary>
        public void ListenForShowRequests(Action onShowRequested)
        {
            _showEvent ??= new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _registration?.Unregister(null);
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) => onShowRequested(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        public static SingleInstanceGuard? Current { get; set; }

        public void Dispose()
        {
            _registration?.Unregister(null);
            _showEvent?.Dispose();
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }
}
