using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroCue.DataProbe.Services.Pnp;

namespace ZeroCue.DataProbe.Services.Drivers
{
    /// <summary>
    /// Install/restore logic that runs inside the elevated helper. All decisions are
    /// made from fresh PnP state; nothing written by the unelevated app is trusted
    /// beyond a list of package-name hints, and every package is re-classified from
    /// its INF content before it is touched.
    /// </summary>
    public sealed class DriverOperationEngine
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan WdiTimeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan PnpUtilTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// wdi-simple can stay blocked for minutes after the driver is already bound
        /// (it waits for a device restart that an open handle vetoes). Once the new
        /// package is published and assigned, it gets this long to exit by itself.
        /// </summary>
        internal static readonly TimeSpan WdiCompletionGrace = TimeSpan.FromSeconds(5);

        private readonly IDriverSystem _system;
        private readonly Action<string> _log;
        private readonly Action<string>? _progress;
        private readonly string? _wdiSimplePath;
        private readonly string _workDirectory;
        private IReadOnlyList<PublishedInf>? _infCache;
        private bool _updateReportedRestart;
        private long _infCacheTimestamp;

        public DriverOperationEngine(
            IDriverSystem system,
            Action<string> log,
            string workDirectory,
            string? wdiSimplePath,
            Action<string>? progress = null)
        {
            _system = system;
            _log = log;
            _workDirectory = workDirectory;
            _wdiSimplePath = wdiSimplePath;
            _progress = progress;
        }

        private enum BindingState
        {
            Absent,
            Ready,

            /// <summary>WinUSB is assigned but the old stack still runs until the device restarts.</summary>
            PendingRestart,
            NotBound,
            Problem
        }

        // ── Install ──────────────────────────────────────────────────────

        public async Task<DriverOperationResult> InstallAsync(DriverTarget target, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var definition = DriverCatalog.For(target);
            var result = DriverOperationResult.For(DriverOperationKind.Install, target, DriverOperationStatus.Failed);

            Progress("Detecting device");
            var selection = await SelectDeviceAsync(definition, ct);
            if (selection == null)
            {
                LogVendorSnapshot(definition, "selection-failed", includePhantoms: true);
                result.Status = DriverOperationStatus.NoDevice;
                result.Errors.Add($"No connected {definition.DisplayName} matched a supported identity.");
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            var (variant, productId) = selection.Value;
            result.Variant = variant.Name;
            result.VendorId = variant.VendorId;
            result.ProductId = productId;
            result.IsExperimental = variant.IsExperimental;
            _log($"Selected variant={variant.Name} experimental={variant.IsExperimental} identity={PnpIdentity.FormatHardwareKey(variant.VendorId, productId, null)}.");
            LogVendorSnapshot(definition, "before-install", includePhantoms: false);

            // A composite parent that Windows failed to start (Code 10, 43...) exposes no
            // interfaces: wdi-simple would retry for minutes against nothing.
            var failedParent = await RecoverFailedCompositeParentAsync(variant, productId, ct);
            if (failedParent != null)
            {
                result.Status = DriverOperationStatus.DeviceError;
                result.ProblemCode = failedParent.ProblemCode;
                result.Errors.Add($"{failedParent.InstanceId} reports problem code {failedParent.ProblemCode}; Windows did not start the device, so its interfaces cannot receive a driver.");
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                _log($"Install aborted: composite parent {failedParent} is in an error state.");
                return result;
            }

            if (_wdiSimplePath == null)
            {
                result.Errors.Add("The verified wdi-simple helper is unavailable.");
            }

            var publishedBefore = _system.GetPublishedInfs().Select(inf => inf.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Progress("Closing iCUE");
            var corsair = _system.StopCorsair(_log);
            result.IcueWasRunning = corsair.IcueWasRunning;
            result.IcuePath = corsair.IcuePath;

            try
            {
                await MigrateObsoleteBindingsAsync(definition, variant, productId, result, ct);

                var bindings = definition.BindingsFor(variant, productId);
                bool allowAbsent = definition.PreparesAllVariantStates;
                var usedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var binding in bindings)
                {
                    Progress($"Installing {binding.Name}");
                    await EnsureBindingAsync(variant, binding, allowAbsent, usedPackages, result, ct);
                }

                // Replacing MI_00 (or a PID transition) can re-enumerate the composite device
                // and displace bindings that were ready a moment ago. Converge once more.
                var pending = bindings.Where(binding => !IsBindingSatisfied(variant, binding, allowAbsent)).ToArray();
                if (pending.Length > 0)
                {
                    _log($"Convergence pass for: {string.Join(", ", pending.Select(binding => binding.Name))}.");
                    foreach (var binding in pending)
                    {
                        Progress($"Verifying {binding.Name}");
                        await EnsureBindingAsync(variant, binding, allowAbsent, usedPackages, result, ct);
                    }
                }

                Progress("Verifying installation");
                var missing = await WaitForAsync(
                    () => bindings.Where(binding => !IsBindingSatisfied(variant, binding, allowAbsent)).ToArray(),
                    remaining => remaining.Length == 0,
                    TimeSpan.FromSeconds(10),
                    ct);

                foreach (var binding in missing)
                {
                    result.Errors.Add($"{binding.Name} ({binding.HardwareKey(variant.VendorId)}) is not bound to WinUSB.");
                }

                if (missing.Length == 0)
                {
                    await FinishPendingRestartsAsync(variant, bindings, result, ct);
                }

                var bindingKeys = bindings.Select(binding => binding.HardwareKey(variant.VendorId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var inf in _system.GetPublishedInfs())
                {
                    bool isNew = !publishedBefore.Contains(inf.Name);
                    if ((isNew || usedPackages.Contains(inf.Name)) &&
                        InfClassifier.Classify(inf.Info, definition.AllHardwareKeys) == InfOwnership.ZeroCue &&
                        inf.Info.HardwareKeys.Overlaps(bindingKeys))
                    {
                        result.OwnedPackages.Add(inf.Name);
                    }
                }

                LogVendorSnapshot(definition, "after-install", includePhantoms: false);
            }
            finally
            {
                _system.RestoreCorsair(corsair, _log);
            }

            result.Status = result.Errors.Count > 0
                ? DriverOperationStatus.Failed
                : result.Warnings.Count > 0 ? DriverOperationStatus.SuccessWithWarnings : DriverOperationStatus.Success;
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            _log($"Install finished status={result.Status} elapsedMs={result.ElapsedMs} packages={string.Join(",", result.OwnedPackages)}.");
            return result;
        }

        /// <summary>
        /// Returns the composite parent when it is still failed after one restart attempt,
        /// or null when the device is healthy (or recovered and enumerated its interfaces).
        /// </summary>
        private async Task<PnpDevice?> RecoverFailedCompositeParentAsync(DriverVariant variant, int productId, CancellationToken ct)
        {
            if (!variant.Bindings.Any(binding => binding.ProductId == productId && binding.InterfaceNumber != null))
            {
                return null;
            }

            PnpDevice? FindFailedParent() => _system.GetDevices(new[] { variant.VendorId })
                .FirstOrDefault(device => device.ProductId == productId && device.InterfaceNumber == null && device.ProblemCode != 0);

            var parent = FindFailedParent();
            if (parent == null)
            {
                return null;
            }

            Progress("Restarting device");
            _log($"Composite parent {parent} failed to start; trying one restart.");
            var restart = await _system.RunAsync("pnputil.exe", new[] { "/restart-device", parent.InstanceId }, PnpUtilTimeout, ct);
            _log($"restart-device {parent.InstanceId} exit={restart.ExitCode?.ToString() ?? "<none>"}.");

            var stillFailed = await WaitForAsync(
                () =>
                {
                    var devices = _system.GetDevices(new[] { variant.VendorId });
                    var failed = devices.FirstOrDefault(device => device.ProductId == productId && device.InterfaceNumber == null && device.ProblemCode != 0);
                    bool interfacesBack = devices.Any(device => device.ProductId == productId && device.InterfaceNumber != null);
                    return failed ?? (interfacesBack ? null : parent);
                },
                failed => failed == null,
                TimeSpan.FromSeconds(6),
                ct);

            if (stillFailed == null)
            {
                _log("Composite parent recovered after restart.");
            }

            return stillFailed;
        }

        private async Task<(DriverVariant Variant, int ProductId)?> SelectDeviceAsync(DriverTargetDefinition definition, CancellationToken ct)
        {
            var deadline = StartTimer();
            bool rescanned = false;
            while (true)
            {
                var devices = _system.GetDevices(definition.VendorIds);
                foreach (var variant in definition.Variants)
                {
                    foreach (var productId in variant.ProductIds)
                    {
                        if (devices.Any(device => device.VendorId == variant.VendorId && device.ProductId == productId))
                        {
                            return (variant, productId);
                        }
                    }
                }

                if (deadline.Elapsed > TimeSpan.FromSeconds(4))
                {
                    return null;
                }

                if (!rescanned)
                {
                    _system.Rescan();
                    rescanned = true;
                }

                await _system.DelayAsync(PollInterval, ct);
            }
        }

        private async Task EnsureBindingAsync(
            DriverVariant variant,
            DriverBinding binding,
            bool allowAbsent,
            HashSet<string> usedPackages,
            DriverOperationResult result,
            CancellationToken ct)
        {
            string key = binding.HardwareKey(variant.VendorId);
            string hardwareId = $"USB\\{key}";
            var state = GetBindingState(variant, binding, out var nodes);
            if (state == BindingState.Ready)
            {
                _log($"{binding.Name}: already bound to WinUSB ({string.Join(" | ", nodes.Select(node => node.InstanceId))}).");
                return;
            }

            if (state == BindingState.PendingRestart)
            {
                // Installing again would not help: the device only needs to restart.
                _log($"{binding.Name}: WinUSB assigned, waiting for a device restart ({string.Join(" | ", nodes.Select(node => node.ToString()))}).");
                return;
            }

            var staged = FindStagedPackage(key);
            if (staged != null)
            {
                usedPackages.Add(staged);
            }

            if (state == BindingState.Absent && allowAbsent && staged != null)
            {
                _log($"{binding.Name}: device state not present; ZeroCue package {staged} is staged for {key}.");
                return;
            }

            // Fast path: the package already exists in the Driver Store but Windows kept
            // the previous driver for an existing device instance (Windows does not
            // re-rank drivers when a known device reconnects).
            if ((state is BindingState.NotBound or BindingState.Problem) && staged != null)
            {
                var storePath = _system.GetDriverStoreInfPath(staged);
                if (storePath != null)
                {
                    var update = _system.UpdateDriver(hardwareId, storePath);
                    _log($"{binding.Name}: applied staged package {staged} ok={update.Succeeded} reboot={update.RebootRequired}" +
                        (update.Succeeded ? "." : $" error={NativeDriverApi.DescribeWin32Error(update.Win32Error)}."));
                    // "Reboot required" here means the device could not restart (usually an open
                    // handle); FinishPendingRestartsAsync restarts it or asks for a reconnect.
                    _updateReportedRestart |= update.RebootRequired;
                    if (update.Succeeded && await WaitForBindingAsync(variant, binding, allowAbsent, TimeSpan.FromSeconds(10), ct))
                    {
                        return;
                    }
                }
            }

            if (_wdiSimplePath == null)
            {
                result.Errors.Add($"{binding.Name}: cannot create a WinUSB package without wdi-simple.");
                return;
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var attemptDirectory = Path.Combine(_workDirectory, $"wdi-{key.Replace('&', '_')}-{attempt}");
                Directory.CreateDirectory(attemptDirectory);

                var arguments = new List<string>
                {
                    "-n", variant.DeviceName,
                    "-v", $"0x{variant.VendorId:X4}",
                    "-p", $"0x{binding.ProductId:X4}"
                };
                if (binding.InterfaceNumber is int mi)
                {
                    arguments.Add("-i");
                    arguments.Add(mi.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                arguments.AddRange(new[] { "-t", "0", "-s", "-o", "30000", "-l", "2", "-d", attemptDirectory });

                var attemptTimer = Stopwatch.StartNew();
                var publishedBeforeAttempt = _system.GetPublishedInfs().Select(inf => inf.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var outcome = await _system.RunAsync(
                    _wdiSimplePath,
                    arguments,
                    WdiTimeout,
                    ct,
                    new ProcessCompletionProbe(() => IsWdiWorkComplete(variant, binding, key, allowAbsent, publishedBeforeAttempt), WdiCompletionGrace));
                InvalidateInfCache();
                _log($"{binding.Name}: wdi-simple attempt {attempt}/3 exit={outcome.ExitCode?.ToString() ?? "<none>"} timedOut={outcome.TimedOut} stoppedEarly={outcome.StoppedEarly} elapsedMs={attemptTimer.ElapsedMilliseconds}.");
                LogTail($"wdi-simple {key} attempt {attempt}", outcome.Output);

                if (await WaitForBindingAsync(variant, binding, allowAbsent, TimeSpan.FromSeconds(12), ct))
                {
                    _log($"{binding.Name}: ready after attempt {attempt}.");
                    var newStaged = FindStagedPackage(key);
                    if (newStaged != null)
                    {
                        usedPackages.Add(newStaged);
                    }

                    return;
                }

                // Restart only the exact nodes of this binding before retrying.
                foreach (var node in GetMatchingNodes(variant, binding))
                {
                    var restart = await _system.RunAsync("pnputil.exe", new[] { "/restart-device", node.InstanceId }, PnpUtilTimeout, ct);
                    _log($"{binding.Name}: restart {node.InstanceId} exit={restart.ExitCode}.");
                }

                await _system.DelayAsync(TimeSpan.FromSeconds(1), ct);
            }

            _log($"{binding.Name}: not ready after three attempts.");
        }

        private async Task<bool> WaitForBindingAsync(DriverVariant variant, DriverBinding binding, bool allowAbsent, TimeSpan timeout, CancellationToken ct)
        {
            var timer = StartTimer();
            bool rescanned = false;
            while (true)
            {
                if (IsBindingSatisfied(variant, binding, allowAbsent))
                {
                    return true;
                }

                if (timer.Elapsed >= timeout)
                {
                    return false;
                }

                if (!rescanned && timer.Elapsed > TimeSpan.FromSeconds(2))
                {
                    _system.Rescan();
                    rescanned = true;
                }

                await _system.DelayAsync(PollInterval, ct);
            }
        }

        /// <summary>The driver assignment is done; a pending device restart is resolved at the end.</summary>
        private bool IsBindingSatisfied(DriverVariant variant, DriverBinding binding, bool allowAbsent)
        {
            var state = GetBindingState(variant, binding, out _);
            return state is BindingState.Ready or BindingState.PendingRestart ||
                (state == BindingState.Absent && allowAbsent && FindStagedPackage(binding.HardwareKey(variant.VendorId)) != null);
        }

        /// <summary>
        /// Polled by the process runner while wdi-simple is still running: true once a
        /// package created by this attempt is published and the device is assigned to it.
        /// </summary>
        private bool IsWdiWorkComplete(DriverVariant variant, DriverBinding binding, string hardwareKey, bool allowAbsent, IReadOnlySet<string> publishedBeforeAttempt)
        {
            var state = GetBindingState(variant, binding, out var nodes);
            bool absentAndStaging = state == BindingState.Absent && allowAbsent;
            if (!absentAndStaging && state is not (BindingState.Ready or BindingState.PendingRestart))
            {
                return false;
            }

            var managedKeys = DriverCatalog.All.SelectMany(definition => definition.AllHardwareKeys).ToArray();
            var newPackages = _system.GetPublishedInfs()
                .Where(inf => !publishedBeforeAttempt.Contains(inf.Name) &&
                    inf.Info.HardwareKeys.Contains(hardwareKey) &&
                    InfClassifier.Classify(inf.Info, managedKeys) == InfOwnership.ZeroCue)
                .Select(inf => inf.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A device state that is not connected only needs its package staged.
            return newPackages.Count > 0 &&
                (absentAndStaging || nodes.All(node => node.DriverInfPath != null && newPackages.Contains(node.DriverInfPath)));
        }

        /// <summary>
        /// Restarts devices whose new WinUSB driver is assigned but not running yet.
        /// pnputil /restart-device fails while a program keeps the device open; then the
        /// user has to reconnect it, which always restarts the stack.
        /// </summary>
        private async Task FinishPendingRestartsAsync(DriverVariant variant, IReadOnlyList<DriverBinding> bindings, DriverOperationResult result, CancellationToken ct)
        {
            var pendingNodes = bindings
                .SelectMany(binding => GetMatchingNodes(variant, binding))
                .Where(node => node.IsRestartPending)
                .ToArray();

            if (pendingNodes.Length > 0)
            {
                Progress("Restarting device");
                foreach (var target in RestartTargets(variant, pendingNodes))
                {
                    var restart = await _system.RunAsync("pnputil.exe", new[] { "/restart-device", target }, PnpUtilTimeout, ct);
                    _log($"restart-device {target} exit={restart.ExitCode?.ToString() ?? "<none>"}.");
                    LogTail($"restart-device {target}", restart.Output);
                }

                var stillPending = await WaitForAsync(
                    () => bindings.Where(binding => GetBindingState(variant, binding, out _) == BindingState.PendingRestart).ToArray(),
                    remaining => remaining.Length == 0,
                    TimeSpan.FromSeconds(10),
                    ct);

                if (stillPending.Length > 0)
                {
                    result.ReconnectRequired = true;
                    result.Warnings.Add(
                        $"WinUSB is installed but {string.Join(", ", stillPending.Select(binding => binding.Name))} keeps its previous driver until the device is reconnected (another program may have it open).");
                    _log($"Device restart pending for: {string.Join(", ", stillPending.Select(binding => binding.Name))}; reconnect required.");
                    return;
                }

                _log("Pending device restart completed.");
            }

            // UpdateDriverForPlugAndPlayDevices reports "reboot required" when it cannot restart
            // the device. Keep that flag only if the running WinUSB stack is not confirmed.
            if (_updateReportedRestart)
            {
                bool confirmed = bindings
                    .SelectMany(binding => GetMatchingNodes(variant, binding))
                    .All(node => node.IsWinUsb && node.WinUsbInterfaceActive == true);
                if (confirmed)
                {
                    _log("Driver update reported a pending restart, but every WinUSB interface is active.");
                }
                else
                {
                    result.RebootRequired = true;
                }
            }
        }

        /// <summary>
        /// Interfaces of a composite device restart through their composite parent, which
        /// must be the same SCUF device (never a hub). Other nodes restart themselves.
        /// </summary>
        private static IReadOnlyList<string> RestartTargets(DriverVariant variant, IReadOnlyList<PnpDevice> pendingNodes)
        {
            var targets = new List<string>();
            foreach (var node in pendingNodes)
            {
                string target = node.InstanceId;
                if (node.InterfaceNumber != null &&
                    node.Parent != null &&
                    PnpIdentity.TryParseUsbInstanceId(node.Parent, out var parentVid, out var parentPid, out var parentMi) &&
                    parentVid == variant.VendorId &&
                    parentPid == node.ProductId &&
                    parentMi == null)
                {
                    target = node.Parent;
                }

                if (!targets.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    targets.Add(target);
                }
            }

            return targets;
        }

        private BindingState GetBindingState(DriverVariant variant, DriverBinding binding, out IReadOnlyList<PnpDevice> nodes)
        {
            nodes = GetMatchingNodes(variant, binding);
            if (nodes.Count == 0)
            {
                return BindingState.Absent;
            }

            if (nodes.All(node => node.IsWinUsb && node.ProblemCode == 0))
            {
                return nodes.Any(node => node.IsRestartPending) ? BindingState.PendingRestart : BindingState.Ready;
            }

            return nodes.Any(node => node.IsWinUsb) ? BindingState.Problem : BindingState.NotBound;
        }

        private IReadOnlyList<PnpDevice> GetMatchingNodes(DriverVariant variant, DriverBinding binding) =>
            _system.GetDevices(new[] { variant.VendorId })
                .Where(device => binding.Matches(device, variant.VendorId))
                .ToArray();

        private string? FindStagedPackage(string hardwareKey)
        {
            var managedKeys = DriverCatalog.All.SelectMany(definition => definition.AllHardwareKeys).ToArray();
            return GetPublishedInfsCached()
                .Where(inf => inf.Info.HardwareKeys.Contains(hardwareKey) &&
                    InfClassifier.Classify(inf.Info, managedKeys) == InfOwnership.ZeroCue)
                .Select(inf => inf.Name)
                .OrderByDescending(name => int.TryParse(name.AsSpan(3, name.Length - 7), out var number) ? number : 0)
                .FirstOrDefault();
        }

        private async Task MigrateObsoleteBindingsAsync(
            DriverTargetDefinition definition,
            DriverVariant variant,
            int productId,
            DriverOperationResult result,
            CancellationToken ct)
        {
            var requiredKeys = variant.Bindings.Select(binding => binding.HardwareKey(variant.VendorId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool migrated = false;

            foreach (var obsolete in variant.ObsoleteBindings.Where(binding => binding.ProductId == productId))
            {
                foreach (var node in GetMatchingNodes(variant, obsolete).Where(node => node.IsWinUsb))
                {
                    var inf = node.DriverInfPath == null
                        ? null
                        : _system.GetPublishedInfs().FirstOrDefault(candidate => string.Equals(candidate.Name, node.DriverInfPath, StringComparison.OrdinalIgnoreCase));
                    var ownership = inf == null ? InfOwnership.Foreign : InfClassifier.Classify(inf.Info, definition.AllHardwareKeys);
                    if (inf == null || ownership == InfOwnership.Foreign || inf.Info.HardwareKeys.Overlaps(requiredKeys))
                    {
                        result.Errors.Add($"Refusing to remove unverified obsolete WinUSB binding {node.InstanceId} package={node.DriverInfPath ?? "<none>"}.");
                        continue;
                    }

                    _log($"Repairing obsolete WinUSB binding {node.InstanceId} package={inf.Name} ownership={ownership}.");
                    var remove = await _system.RunAsync("pnputil.exe", new[] { "/remove-device", node.InstanceId }, PnpUtilTimeout, ct);
                    LogTail($"remove-device {node.InstanceId}", remove.Output);
                    if (!remove.Succeeded)
                    {
                        result.Errors.Add($"Could not remove obsolete binding {node.InstanceId} (exit {remove.ExitCode}).");
                        continue;
                    }

                    var delete = await _system.RunAsync("pnputil.exe", new[] { "/delete-driver", inf.Name, "/uninstall", "/force" }, PnpUtilTimeout, ct);
                    LogTail($"delete-driver {inf.Name}", delete.Output);
                    result.RebootRequired |= remove.RebootRequired || delete.RebootRequired;
                    if (!delete.Succeeded)
                    {
                        result.Errors.Add($"Could not delete obsolete package {inf.Name} (exit {delete.ExitCode}).");
                        continue;
                    }

                    migrated = true;
                }
            }

            if (migrated)
            {
                _system.Rescan();
                await _system.DelayAsync(TimeSpan.FromSeconds(1), ct);
            }
        }

        // ── Restore ──────────────────────────────────────────────────────

        public async Task<DriverOperationResult> RestoreAsync(DriverTarget target, IReadOnlyCollection<string> hintedPackages, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var definition = DriverCatalog.For(target);
            var result = DriverOperationResult.For(DriverOperationKind.Restore, target, DriverOperationStatus.Failed);

            Progress("Closing iCUE");
            var corsair = _system.StopCorsair(_log);
            result.IcueWasRunning = corsair.IcueWasRunning;
            result.IcuePath = corsair.IcuePath;

            try
            {
                var managedKeys = definition.AllHardwareKeys.ToArray();
                var packages = new List<(PublishedInf Inf, InfOwnership Ownership)>();
                foreach (var inf in _system.GetPublishedInfs())
                {
                    var ownership = InfClassifier.Classify(inf.Info, managedKeys);
                    if (ownership != InfOwnership.Foreign)
                    {
                        packages.Add((inf, ownership));
                    }
                }

                foreach (var hinted in hintedPackages.Where(name => packages.All(package => !string.Equals(package.Inf.Name, name, StringComparison.OrdinalIgnoreCase))))
                {
                    result.Warnings.Add($"Recorded package {hinted} was not found or is not a verified ZeroCue package; it was left untouched.");
                }

                _log($"Restore candidates: {(packages.Count == 0 ? "none" : string.Join(", ", packages.Select(package => $"{package.Inf.Name}[{package.Ownership}:{string.Join('|', package.Inf.Info.HardwareKeys)}]")))}.");
                var packageNames = packages.Select(package => package.Inf.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var before = GetTargetNodes(definition);
                LogVendorSnapshot(definition, "before-restore", includePhantoms: false);
                if (packageNames.Count == 0 && before.Any(node => node.IsWinUsb))
                {
                    result.Errors.Add("WinUSB is active but no verified ZeroCue package was found for this device.");
                }

                var nodesToReset = before
                    .Where(node => node.DriverInfPath != null && packageNames.Contains(node.DriverInfPath))
                    .OrderByDescending(node => node.InterfaceNumber.HasValue)
                    .ThenByDescending(node => node.InstanceId.Length)
                    .ToArray();
                var parents = before
                    .Where(node => node.InterfaceNumber == null && string.Equals(node.Service, "usbccgp", StringComparison.OrdinalIgnoreCase))
                    .Select(node => node.InstanceId)
                    .ToArray();

                Progress("Removing ZeroCue drivers");
                foreach (var node in nodesToReset)
                {
                    var live = _system.GetDevices(new[] { node.VendorId }).FirstOrDefault(device => device.InstanceId == node.InstanceId);
                    if (live == null)
                    {
                        result.Warnings.Add($"{node.InstanceId} re-enumerated before removal.");
                        continue;
                    }

                    var remove = await _system.RunAsync("pnputil.exe", new[] { "/remove-device", node.InstanceId }, PnpUtilTimeout, ct);
                    result.RebootRequired |= remove.RebootRequired;
                    if (!remove.Succeeded)
                    {
                        var after = _system.GetDevices(new[] { node.VendorId }).FirstOrDefault(device => device.InstanceId == node.InstanceId);
                        if (after == null || !after.IsWinUsb)
                        {
                            result.Warnings.Add($"{node.InstanceId} changed during removal (exit {remove.ExitCode}).");
                        }
                        else
                        {
                            result.Errors.Add($"Could not remove {node.InstanceId} (exit {remove.ExitCode}).");
                            LogTail($"remove-device {node.InstanceId}", remove.Output);
                        }
                    }
                }

                foreach (var (inf, ownership) in packages)
                {
                    var delete = await _system.RunAsync("pnputil.exe", new[] { "/delete-driver", inf.Name, "/uninstall", "/force" }, PnpUtilTimeout, ct);
                    result.RebootRequired |= delete.RebootRequired;
                    bool gone = _system.GetPublishedInfs().All(candidate => !string.Equals(candidate.Name, inf.Name, StringComparison.OrdinalIgnoreCase));
                    if (delete.Succeeded || gone)
                    {
                        result.RemovedPackages.Add(inf.Name);
                        if (ownership != InfOwnership.ZeroCue)
                        {
                            result.Warnings.Add($"Also removed {ownership} package {inf.Name}.");
                        }
                    }
                    else
                    {
                        result.Errors.Add($"Could not delete package {inf.Name} (exit {delete.ExitCode}).");
                        LogTail($"delete-driver {inf.Name}", delete.Output);
                    }
                }

                Progress("Restoring original drivers");
                _system.Rescan();
                await RestartParentsAsync(parents, ct);

                var expected = nodesToReset.Select(node => node.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var timer = StartTimer();
                IReadOnlyList<PnpDevice> current;
                string[] remainingWinUsb;
                string[] notReturned;
                int nextKick = 5;
                while (true)
                {
                    current = GetTargetNodes(definition);
                    remainingWinUsb = current.Where(node => node.IsWinUsb).Select(node => node.InstanceId).ToArray();
                    notReturned = expected
                        .Where(id => !current.Any(node =>
                            string.Equals(node.InstanceId, id, StringComparison.OrdinalIgnoreCase) &&
                            !node.IsWinUsb && node.Service != null && node.ProblemCode == 0))
                        .ToArray();
                    if ((remainingWinUsb.Length == 0 && notReturned.Length == 0) || timer.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        break;
                    }

                    if (timer.Elapsed.TotalSeconds >= nextKick)
                    {
                        nextKick += 5;
                        _system.Rescan();
                        await RestartParentsAsync(parents, ct);
                    }

                    await _system.DelayAsync(PollInterval, ct);
                }

                foreach (var id in remainingWinUsb)
                {
                    result.Errors.Add($"WinUSB is still bound to {id}.");
                }

                bool deviceStillConnected = current.Count > 0;
                foreach (var id in notReturned)
                {
                    if (deviceStillConnected)
                    {
                        result.Errors.Add($"{id} did not return on its original driver. Unplug and reconnect the device.");
                    }
                    else
                    {
                        result.Warnings.Add($"{id} was disconnected during restore; reconnect it to finish.");
                    }
                }

                LogVendorSnapshot(definition, "after-restore", includePhantoms: false);
            }
            finally
            {
                _system.RestoreCorsair(corsair, _log);
            }

            result.Status = result.Errors.Count > 0
                ? DriverOperationStatus.Failed
                : result.Warnings.Count > 0 ? DriverOperationStatus.SuccessWithWarnings : DriverOperationStatus.Success;
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            _log($"Restore finished status={result.Status} elapsedMs={result.ElapsedMs} removed={string.Join(",", result.RemovedPackages)}.");
            return result;
        }

        private async Task RestartParentsAsync(IEnumerable<string> parents, CancellationToken ct)
        {
            foreach (var parent in parents)
            {
                var restart = await _system.RunAsync("pnputil.exe", new[] { "/restart-device", parent }, PnpUtilTimeout, ct);
                if (!restart.Succeeded)
                {
                    _log($"restart-device {parent} exit={restart.ExitCode}.");
                }
            }
        }

        private IReadOnlyList<PnpDevice> GetTargetNodes(DriverTargetDefinition definition) =>
            _system.GetDevices(definition.VendorIds).Where(definition.OwnsDeviceNode).ToArray();

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Reading every oem*.inf costs tens of milliseconds, so readiness polling
        /// reuses a snapshot for up to two seconds.
        /// </summary>
        private IReadOnlyList<PublishedInf> GetPublishedInfsCached()
        {
            long now = Stopwatch.GetTimestamp();
            if (_infCache == null || Stopwatch.GetElapsedTime(_infCacheTimestamp, now) > TimeSpan.FromSeconds(2))
            {
                _infCache = _system.GetPublishedInfs();
                _infCacheTimestamp = now;
            }

            return _infCache;
        }

        private void InvalidateInfCache() => _infCache = null;

        private ClockTimer StartTimer() => new(_system);

        /// <summary>Elapsed time measured on the driver system clock (virtual in tests).</summary>
        private readonly struct ClockTimer
        {
            private readonly IDriverSystem _system;
            private readonly TimeSpan _start;

            public ClockTimer(IDriverSystem system)
            {
                _system = system;
                _start = system.Now;
            }

            public TimeSpan Elapsed => _system.Now - _start;
        }

        private async Task<T> WaitForAsync<T>(Func<T> probe, Func<T, bool> done, TimeSpan timeout, CancellationToken ct)
        {
            var timer = StartTimer();
            while (true)
            {
                var value = probe();
                if (done(value) || timer.Elapsed >= timeout)
                {
                    return value;
                }

                await _system.DelayAsync(PollInterval, ct);
            }
        }

        private void LogVendorSnapshot(DriverTargetDefinition definition, string label, bool includePhantoms)
        {
            var devices = _system.GetDevices(definition.VendorIds, includePhantoms)
                .Where(device => DriverCatalog.All.Any(candidate => candidate.OwnsDeviceNode(device)))
                .OrderBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
            _log($"PnP snapshot {label}: {devices.Length} node(s).");
            foreach (var device in devices)
            {
                _log($"  {device}");
            }
        }

        private void LogTail(string label, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(Math.Max(0, lines.Length - 25)))
            {
                _log($"  [{label}] {line}");
            }
        }

        private void Progress(string message)
        {
            _log(message);
            _progress?.Invoke(message);
        }
    }
}
