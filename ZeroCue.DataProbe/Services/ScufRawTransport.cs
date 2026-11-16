using System;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace ZeroCue.DataProbe.Services
{
    public interface IScufRawTransport
    {
        Task WriteReportAsync(byte[] report, CancellationToken ct);
        Task<(bool Success, int BytesRead)> ReadReportAsync(byte[] buffer, int timeoutMs, CancellationToken ct);
        Task DrainAsync();
        void LogTransport(string message);
    }

    public interface IScufTransport : IScufRawTransport
    {
    }

    public class ScufRawTransport : IScufTransport
    {
        private readonly UsbEndpointReader _readerEp82;
        private readonly UsbEndpointWriter _writerEp02;
        private readonly Action<string> _logger;

        public ScufRawTransport(UsbEndpointReader readerEp82, UsbEndpointWriter writerEp02, Action<string> logger)
        {
            _readerEp82 = readerEp82 ?? throw new ArgumentNullException(nameof(readerEp82));
            _writerEp02 = writerEp02 ?? throw new ArgumentNullException(nameof(writerEp02));
            _logger = logger;
        }

        // libusb transfers are synchronous. Callers await these methods from the UI
        // thread (RGB, brightness, rumble settings), so the blocking part always runs
        // on the thread pool instead of freezing the window for up to a second.
        public Task WriteReportAsync(byte[] report, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                var result = _writerEp02.Write(report, 1000, out int transferred);
                if (result == Error.Success)
                {
                    LogTransport($"WRITE OK Ep02 bytesWritten={transferred}");
                    return;
                }

                LogTransport($"WRITE FAIL Ep02 error={result}");
                throw new System.IO.IOException($"libusb interrupt transfer failed: {result}");
            }, ct);
        }

        public Task<(bool Success, int BytesRead)> ReadReportAsync(byte[] buffer, int timeoutMs, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                var err = _readerEp82.Read(buffer, timeoutMs, out int bytesRead);
                return err == Error.Success && bytesRead > 0 ? (true, bytesRead) : (false, 0);
            }, ct);
        }

        public async Task DrainAsync()
        {
            var drainCts = new CancellationTokenSource(100);
            while (!drainCts.Token.IsCancellationRequested && _readerEp82 != null)
            {
                var dummy = new byte[64];
                int dummyRead;
                if (_readerEp82.Read(dummy, 10, out dummyRead) == Error.Success && dummyRead > 0)
                {
                    LogTransport($"[DRAIN] Descartado paquete pendiente de {dummyRead} bytes en Ep82");
                }
                await Task.Delay(1);
            }
        }

        public void LogTransport(string message)
        {
            _logger?.Invoke(message);
        }
    }
}
