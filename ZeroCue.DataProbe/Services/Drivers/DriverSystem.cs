using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZeroCue.DataProbe.Services.Pnp;

namespace ZeroCue.DataProbe.Services.Drivers
{
    public sealed record PublishedInf(string Name, InfInfo Info);

    /// <param name="StoppedEarly">The completion probe reported the work done and the process was ended.</param>
    public sealed record ProcessOutcome(int? ExitCode, bool TimedOut, string Output, bool StoppedEarly = false)
    {
        /// <summary>0 = success, 3010 = success but a reboot is required.</summary>
        public bool Succeeded => ExitCode is 0 or 3010;

        public bool RebootRequired => ExitCode == 3010;
    }

    /// <summary>
    /// Lets a caller end a process that already did its job but does not exit (wdi-simple
    /// blocking on a vetoed device restart). <see cref="IsComplete"/> is polled about once
    /// per second; once it has stayed true for <see cref="Grace"/> the process is ended.
    /// </summary>
    public sealed record ProcessCompletionProbe(Func<bool> IsComplete, TimeSpan Grace);

    public sealed record DriverUpdateOutcome(bool Succeeded, bool RebootRequired, int Win32Error);

    public sealed record CorsairState(IReadOnlyList<string> RunningServices, bool IcueWasRunning, string? IcuePath);

    /// <summary>
    /// Side effects used by <see cref="DriverOperationEngine"/>. Kept behind an
    /// interface so the install/restore decision logic can be tested without
    /// touching real drivers.
    /// </summary>
    public interface IDriverSystem
    {
        IReadOnlyList<PnpDevice> GetDevices(IReadOnlyCollection<int> vendorIds, bool includePhantoms = false);
        void Rescan();
        IReadOnlyList<PublishedInf> GetPublishedInfs();
        string? GetDriverStoreInfPath(string publishedInfName);
        DriverUpdateOutcome UpdateDriver(string hardwareId, string driverStoreInfPath);
        Task<ProcessOutcome> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, ProcessCompletionProbe? completion = null);
        Task DelayAsync(TimeSpan delay, CancellationToken ct);

        /// <summary>Monotonic clock used for every wait so tests can run on virtual time.</summary>
        TimeSpan Now { get; }

        CorsairState StopCorsair(Action<string> log);
        void RestoreCorsair(CorsairState state, Action<string> log);
    }

    public sealed class WindowsDriverSystem : IDriverSystem
    {
        private static readonly string[] CorsairServices =
        {
            "CorsairDeviceControlService",
            "CorsairService",
            "CorsairGamingAudioCfgService64",
            "CorsairCpuIdService",
            "iCUEUpdateService"
        };

        private static readonly string[] CorsairProcesses = { "iCUE", "iCUEDevicePluginHost" };

        public IReadOnlyList<PnpDevice> GetDevices(IReadOnlyCollection<int> vendorIds, bool includePhantoms = false) =>
            PnpQuery.GetUsbDevices(vendorIds, includePhantoms);

        public void Rescan() => PnpQuery.RescanDevices();

        public IReadOnlyList<PublishedInf> GetPublishedInfs()
        {
            var infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            var result = new List<PublishedInf>();
            foreach (var path in Directory.EnumerateFiles(infDirectory, "oem*.inf"))
            {
                var name = Path.GetFileName(path);
                if (!InfClassifier.IsPublishedInfName(name))
                {
                    continue;
                }

                try
                {
                    // libwdi INFs are small; skip anything unusually large.
                    var info = new FileInfo(path);
                    if (info.Length > 256 * 1024)
                    {
                        continue;
                    }

                    result.Add(new PublishedInf(name, InfClassifier.Parse(File.ReadAllText(path))));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return result;
        }

        public string? GetDriverStoreInfPath(string publishedInfName) =>
            NativeDriverApi.GetDriverStoreInfPath(publishedInfName);

        public DriverUpdateOutcome UpdateDriver(string hardwareId, string driverStoreInfPath)
        {
            bool ok = NativeDriverApi.UpdateDriverForPlugAndPlayDevicesW(
                IntPtr.Zero,
                hardwareId,
                driverStoreInfPath,
                NativeDriverApi.INSTALLFLAG_FORCE | NativeDriverApi.INSTALLFLAG_NONINTERACTIVE,
                out var reboot);
            return new DriverUpdateOutcome(ok, reboot, ok ? 0 : Marshal.GetLastWin32Error());
        }

        public async Task<ProcessOutcome> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, ProcessCompletionProbe? completion = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            bool timedOut = false;
            bool stoppedEarly = false;
            try
            {
                if (completion == null)
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                else
                {
                    stoppedEarly = await WaitForExitOrCompletionAsync(process, completion, timeoutCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
            }

            if (timedOut || stoppedEarly)
            {
                // Only the exact child we started (and its own children) is terminated.
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }

            string output = string.Empty;
            try
            {
                var outText = await stdout.WaitAsync(TimeSpan.FromSeconds(2));
                var errText = await stderr.WaitAsync(TimeSpan.FromSeconds(2));
                output = (outText + Environment.NewLine + errText).Trim();
            }
            catch
            {
            }

            ct.ThrowIfCancellationRequested();
            return new ProcessOutcome(process.HasExited ? process.ExitCode : null, timedOut, output, stoppedEarly);
        }

        /// <summary>Returns true when the probe stayed satisfied for the grace period while the process kept running.</summary>
        private static async Task<bool> WaitForExitOrCompletionAsync(Process process, ProcessCompletionProbe completion, CancellationToken ct)
        {
            var exit = process.WaitForExitAsync(ct);
            TimeSpan? completeSince = null;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var finished = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(1), ct));
                if (finished == exit)
                {
                    await exit;
                    return false;
                }

                ct.ThrowIfCancellationRequested();
                bool complete;
                try
                {
                    complete = completion.IsComplete();
                }
                catch
                {
                    complete = false;
                }

                if (!complete)
                {
                    completeSince = null;
                    continue;
                }

                completeSince ??= clock.Elapsed;
                if (clock.Elapsed - completeSince.Value >= completion.Grace)
                {
                    return !process.HasExited;
                }
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

        private static readonly long ClockOrigin = Stopwatch.GetTimestamp();

        public TimeSpan Now => Stopwatch.GetElapsedTime(ClockOrigin);

        public CorsairState StopCorsair(Action<string> log)
        {
            var running = CorsairServices
                .Where(name => NativeDriverApi.QueryServiceState(name) == NativeDriverApi.SERVICE_RUNNING)
                .ToArray();

            string? icuePath = null;
            bool icueWasRunning = false;
            foreach (var processName in CorsairProcesses)
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        if (processName == "iCUE")
                        {
                            icueWasRunning = true;
                            try { icuePath ??= process.MainModule?.FileName; } catch { }
                        }

                        try { process.Kill(); } catch (Exception ex) { log($"Could not stop {processName} pid={process.Id}: {ex.Message}"); }
                    }
                }
            }

            foreach (var service in running)
            {
                if (!NativeDriverApi.StopService(service))
                {
                    log($"Could not request stop for service {service}.");
                }
            }

            // Wait for an actual stop instead of a fixed sleep.
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(8))
            {
                bool processesGone = CorsairProcesses.All(name => Process.GetProcessesByName(name).Length == 0);
                bool servicesStopped = running.All(name => NativeDriverApi.QueryServiceState(name) is null or NativeDriverApi.SERVICE_STOPPED);
                if (processesGone && servicesStopped)
                {
                    break;
                }

                Thread.Sleep(150);
            }

            log($"Corsair software stopped in {deadline.ElapsedMilliseconds} ms (services previously running: {(running.Length == 0 ? "none" : string.Join(",", running))}; iCUE running: {icueWasRunning}).");
            return new CorsairState(running, icueWasRunning, icuePath);
        }

        public void RestoreCorsair(CorsairState state, Action<string> log)
        {
            foreach (var service in state.RunningServices)
            {
                if (!NativeDriverApi.StartService(service))
                {
                    log($"Could not restart service {service}.");
                }
            }

            if (state.RunningServices.Count > 0)
            {
                log($"Restarted Corsair services: {string.Join(",", state.RunningServices)}.");
            }
        }
    }
}
