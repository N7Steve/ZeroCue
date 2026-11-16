using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZeroCue.DataProbe.Services.Drivers;
using ZeroCue.DataProbe.Services.Pnp;

namespace ZeroCue.DataProbe.Services
{
    /// <summary>
    /// Unelevated side of driver installation. It starts <c>zerocue.exe --driver-helper</c>
    /// through UAC, streams the helper's log and progress over a named pipe and
    /// interprets its structured result. Nothing the elevated helper executes is
    /// written by this process.
    /// </summary>
    public class DriverAutomationService
    {
        private static readonly TimeSpan HelperConnectTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HelperOverallTimeout = TimeSpan.FromMinutes(9);
        private const int ErrorCancelled = 1223;

        private static void Log(string msg)
        {
            ZeroCueLog.Communication($"[DRIVER] {msg}");
        }

        public Task<DriverOperationResult> InstallAsync(DriverTarget target, IProgress<string>? progress = null, CancellationToken ct = default) =>
            RunHelperOperationAsync(DriverOperationKind.Install, target, progress, ct);

        public Task<DriverOperationResult> RestoreAsync(DriverTarget target, IProgress<string>? progress = null, CancellationToken ct = default) =>
            RunHelperOperationAsync(DriverOperationKind.Restore, target, progress, ct);

        private async Task<DriverOperationResult> RunHelperOperationAsync(
            DriverOperationKind kind,
            DriverTarget target,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var definition = DriverCatalog.For(target);
            var stopwatch = Stopwatch.StartNew();
            var hintedPackages = LoadOwnedDriverPackages(definition);
            string pipeName = $"ZeroCue.Driver.{Guid.NewGuid():N}";
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
            {
                return DriverOperationResult.For(kind, target, DriverOperationStatus.HelperUnavailable, "The application path could not be resolved.");
            }

            Log($"Starting elevated {kind} for {definition.LogName}; recorded packages={string.Join(",", hintedPackages)}.");
            DriverOperationResult? result = null;

            using var server = CreatePipeServer(pipeName);
            Process? helper;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                foreach (var argument in DriverHelperArguments.Build(kind, target, pipeName, hintedPackages))
                {
                    psi.ArgumentList.Add(argument);
                }

                // ShellExecute blocks until the UAC prompt is answered; keep it off the UI thread.
                helper = await Task.Run(() => Process.Start(psi), ct).ConfigureAwait(false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                Log("Elevation was cancelled by the user.");
                return DriverOperationResult.For(kind, target, DriverOperationStatus.Cancelled);
            }
            catch (Win32Exception ex)
            {
                Log($"Could not start the elevated helper: {ex.NativeErrorCode} {ex.Message}");
                return DriverOperationResult.For(kind, target, DriverOperationStatus.HelperUnavailable, ex.Message);
            }

            if (helper == null)
            {
                return DriverOperationResult.For(kind, target, DriverOperationStatus.HelperUnavailable, "The elevated helper did not start.");
            }

            using (helper)
            {
                using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                overall.CancelAfter(HelperOverallTimeout);
                try
                {
                    result = await ReadHelperChannelAsync(server, helper, progress, overall.Token).ConfigureAwait(false);
                    await helper.WaitForExitAsync(overall.Token).ConfigureAwait(false);
                    Log($"Elevated helper exited with code {helper.ExitCode} after {stopwatch.ElapsedMilliseconds} ms.");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log("The elevated helper did not finish in time; it keeps running on its own deadline.");
                    result ??= DriverOperationResult.For(kind, target, DriverOperationStatus.TimedOut, "The driver operation did not finish in time.");
                }
            }

            result ??= DriverOperationResult.For(kind, target, DriverOperationStatus.HelperUnavailable, "The elevated helper did not report a result.");
            await CompleteOperationAsync(definition, result, progress).ConfigureAwait(false);
            Log($"{kind} {definition.LogName} completed status={result.Status} reboot={result.RebootRequired} reconnect={result.ReconnectRequired} totalMs={stopwatch.ElapsedMilliseconds}" +
                (result.Errors.Count > 0 ? $" errors=[{string.Join(" | ", result.Errors)}]" : string.Empty) +
                (result.Warnings.Count > 0 ? $" warnings=[{string.Join(" | ", result.Warnings)}]" : string.Empty));
            return result;
        }

        private static NamedPipeServerStream CreatePipeServer(string pipeName)
        {
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User != null)
            {
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }

            // Over-the-shoulder elevation runs the helper as a different administrator.
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                security);
        }

        private static async Task<DriverOperationResult?> ReadHelperChannelAsync(
            NamedPipeServerStream server,
            Process helper,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(HelperConnectTimeout);
                try
                {
                    await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log(helper.HasExited
                        ? $"The elevated helper exited with code {helper.ExitCode} before connecting."
                        : "The elevated helper did not connect.");
                    return null;
                }
            }

            if (NativeDriverApi.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var clientPid) &&
                clientPid != (uint)helper.Id)
            {
                Log($"Rejected driver helper channel from unexpected process {clientPid} (expected {helper.Id}).");
                return null;
            }

            DriverOperationResult? result = null;
            using var reader = new StreamReader(server, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                var message = DriverHelperMessage.TryParse(line);
                if (message == null)
                {
                    continue;
                }

                switch (message.Type)
                {
                    case "progress" when message.Text != null:
                        ZeroCueLog.Communication($"[DRIVER-HELPER] >> {message.Text}");
                        progress?.Report(message.Text);
                        break;
                    case "result" when message.Result != null:
                        if (result == null)
                        {
                            result = message.Result;
                        }
                        else
                        {
                            ZeroCueLog.Communication($"[DRIVER-HELPER] Ignoring additional result status={message.Result.Status} errors=[{string.Join(" | ", message.Result.Errors)}].");
                        }

                        break;
                    default:
                        if (message.Text != null)
                        {
                            ZeroCueLog.Communication($"[DRIVER-HELPER] {message.Text}");
                        }

                        break;
                }
            }

            return result;
        }

        private async Task CompleteOperationAsync(DriverTargetDefinition definition, DriverOperationResult result, IProgress<string>? progress)
        {
            if (result.Kind == DriverOperationKind.Install && result.OwnedPackages.Count > 0)
            {
                SaveOwnedDriverPackages(definition, result.OwnedPackages);
            }
            else if (result.Kind == DriverOperationKind.Restore && result.Succeeded)
            {
                DeleteOwnedDriverPackageManifest(definition);
            }

            // A pending reconnect is already reported; probing endpoints would only fail.
            if (result.Kind == DriverOperationKind.Install &&
                result.Target == DriverTarget.Receiver &&
                result.Succeeded &&
                !result.ReconnectRequired)
            {
                progress?.Report("Verifying receiver");
                var variant = definition.FindVariant(result.VendorId, result.ProductId);
                if (variant != null && !await ValidateReceiverWinUsbTopologyAsync(result.VendorId, result.ProductId, variant).ConfigureAwait(false))
                {
                    result.Warnings.Add("The receiver driver is installed but its endpoints were not ready yet; reconnect the receiver if the controller is not detected.");
                    result.Status = DriverOperationStatus.SuccessWithWarnings;
                }
            }

            if (result.IcueWasRunning)
            {
                RelaunchIcue(result.IcuePath);
            }
        }

        /// <summary>iCUE is relaunched from this unelevated process so it never inherits admin rights.</summary>
        private static void RelaunchIcue(string? recordedPath)
        {
            try
            {
                if (Process.GetProcessesByName("iCUE").Length > 0)
                {
                    return;
                }

                var candidates = new List<string?>
                {
                    recordedPath,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Corsair", "CORSAIR iCUE5 Software", "iCUE.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Corsair", "CORSAIR iCUE4 Software", "iCUE.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Corsair", "CORSAIR iCUE Software", "iCUE.exe")
                };

                var path = candidates.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && File.Exists(candidate));
                if (path != null)
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                    Log("iCUE relaunched.");
                }
            }
            catch (Exception ex)
            {
                Log($"Could not relaunch iCUE: {ex.Message}");
            }
        }

        private async Task<bool> ValidateReceiverWinUsbTopologyAsync(int vid, int pid, DriverVariant driverVariant)
        {
            var receiverIdentities = SupportedScufDeviceProfile.ScufEnvisionPro.WirelessReceiverIdentities
                .Where(identity => identity.VendorId == vid && driverVariant.ProductIds.Contains(identity.ProductId))
                .OrderByDescending(identity => identity.ProductId == pid)
                .ToArray();
            if (receiverIdentities.Length == 0)
            {
                Log($"Receiver driver validation skipped: no runtime receiver states for VID=0x{vid:X4} PID=0x{pid:X4}.");
                return false;
            }

            const int maxAttempts = 6;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                foreach (var receiverIdentity in receiverIdentities)
                {
                    bool runtimeReady = false;
                    bool radioReady = false;
                    try
                    {
                        using (var runtimeTransport = new WirelessDongleWinUsbTransport(
                            message => Log($"receiver validation: {message}"),
                            WirelessWinUsbInterfaceTarget.RuntimeMi04,
                            logReadPayloads: false,
                            receiverIdentity: receiverIdentity))
                        {
                            using var runtimeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            runtimeReady = await runtimeTransport.ConnectAsync(runtimeTimeout.Token).ConfigureAwait(false);
                            await runtimeTransport.DisconnectAsync().ConfigureAwait(false);
                        }

                        if (runtimeReady && receiverIdentity.UsesDedicatedRadioInterface)
                        {
                            using var radioTransport = new WirelessDongleWinUsbTransport(
                                message => Log($"receiver validation: {message}"),
                                WirelessWinUsbInterfaceTarget.RadioMi03,
                                logReadPayloads: false,
                                receiverIdentity: receiverIdentity);
                            using var radioTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            radioReady = await radioTransport.ConnectAsync(radioTimeout.Token).ConfigureAwait(false);
                            await radioTransport.DisconnectAsync().ConfigureAwait(false);
                        }
                        else if (runtimeReady)
                        {
                            radioReady = true;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
                    {
                        Log($"Receiver topology validation attempt={attempt} identity={receiverIdentity.VendorId:X4}:{receiverIdentity.ProductId:X4} transient failure: {ex.Message}");
                    }

                    if (runtimeReady && radioReady)
                    {
                        Log($"Receiver driver validation succeeded identity={receiverIdentity.VendorId:X4}:{receiverIdentity.ProductId:X4} ({(receiverIdentity.UsesUnifiedActiveTransport ? "active" : "base")}).");
                        return true;
                    }
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }

            Log("Receiver driver validation did not observe a ready WinUSB topology; the receiver may be changing state.");
            return false;
        }

        // ── Ownership manifest (hint only; the helper re-verifies every package) ──

        private static string GetDriverPackageManifestPath(DriverTargetDefinition definition) =>
            Path.Combine(ZeroCuePaths.UserDataRoot, "driver-manifests", $"{definition.LogName}.json");

        private static IReadOnlyList<string> LoadOwnedDriverPackages(DriverTargetDefinition definition)
        {
            string manifestPath = GetDriverPackageManifestPath(definition);
            if (!File.Exists(manifestPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<DriverPackageManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || !string.Equals(manifest.Target, definition.LogName, StringComparison.Ordinal))
                {
                    return Array.Empty<string>();
                }

                return manifest.PublishedInfNames
                    .Where(InfClassifier.IsPublishedInfName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Log($"Could not read driver package manifest for {definition.LogName}: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static void SaveOwnedDriverPackages(DriverTargetDefinition definition, IEnumerable<string> newlyOwnedPackages)
        {
            try
            {
                string windowsInfDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
                var allOwnedPackages = LoadOwnedDriverPackages(definition)
                    .Concat(newlyOwnedPackages.Where(InfClassifier.IsPublishedInfName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(package => File.Exists(Path.Combine(windowsInfDirectory, package)))
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (allOwnedPackages.Length == 0)
                {
                    DeleteOwnedDriverPackageManifest(definition);
                    return;
                }

                string manifestPath = GetDriverPackageManifestPath(definition);
                Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
                var manifest = new DriverPackageManifest
                {
                    Target = definition.LogName,
                    HardwareIds = definition.DeviceIdentityKeys.ToArray(),
                    PublishedInfNames = allOwnedPackages
                };
                AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log($"Could not save driver package manifest for {definition.LogName}: {ex.Message}");
            }
        }

        private static void DeleteOwnedDriverPackageManifest(DriverTargetDefinition definition)
        {
            try
            {
                string manifestPath = GetDriverPackageManifestPath(definition);
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            catch (Exception ex)
            {
                Log($"Could not delete driver package manifest for {definition.LogName}: {ex.Message}");
            }
        }

        // ── Diagnostics ──────────────────────────────────────────────────

        /// <summary>Lists present and phantom receiver nodes (fast, no PowerShell).</summary>
        public Task<string> GetWirelessReceiverPnpInstancesAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var devices = PnpQuery.GetUsbDevices(DriverCatalog.Receiver.VendorIds, includePhantoms: true)
                        .Where(DriverCatalog.Receiver.OwnsDeviceNode)
                        .OrderByDescending(device => device.IsPresent)
                        .ThenBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (devices.Length == 0)
                    {
                        return LocalizationService.Get("WirelessReceiverInstancesNotFound");
                    }

                    var builder = new StringBuilder();
                    foreach (var device in devices)
                    {
                        builder.AppendLine($"{(device.IsPresent ? "●" : "○")} {device.InstanceId}");
                        builder.AppendLine($"    service={device.Service ?? "-"} inf={device.DriverInfPath ?? "-"} problem={device.ProblemCode}");
                    }

                    return builder.ToString();
                }
                catch (Exception ex)
                {
                    return string.Format(LocalizationService.Get("PowerShellExceptionFormat"), ex.Message);
                }
            });
        }

        private sealed class DriverPackageManifest
        {
            public string Target { get; init; } = string.Empty;
            public string VidValue { get; init; } = string.Empty;
            public string[] PidValues { get; init; } = Array.Empty<string>();
            public string[] HardwareIds { get; init; } = Array.Empty<string>();
            public string[] PublishedInfNames { get; init; } = Array.Empty<string>();
        }
    }
}
