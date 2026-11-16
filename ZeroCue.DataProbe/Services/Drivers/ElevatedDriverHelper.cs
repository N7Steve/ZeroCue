using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroCue.DataProbe.Services.Drivers
{
    /// <summary>
    /// Entry point for <c>zerocue.exe --driver-helper</c>, the only code path that
    /// runs elevated. It never executes anything the unelevated process wrote:
    /// the plan is rebuilt from live PnP state, wdi-simple is hash-verified in
    /// memory and copied to an administrator-only directory before it runs.
    /// </summary>
    public static class ElevatedDriverHelper
    {
        /// <summary>SHA-256 of the bundled libwdi wdi-simple.exe (see scripts/verify-runtime-assets.ps1).</summary>
        public const string WdiSimpleSha256 = "8C22510BD4431152E7DF787D7135A00875AF7BB3AFC090D2AB72E859EFB25E33";

        public const string OperationMutexName = @"Global\ZeroCue.DriverOperation";
        private static readonly TimeSpan OperationDeadline = TimeSpan.FromMinutes(8);

        public static bool IsHelperInvocation(string[] args) =>
            args.Length > 0 && string.Equals(args[0], DriverHelperArguments.Switch, StringComparison.Ordinal);

        public static int Run(string[] args)
        {
            // Never resolve DLLs from the (user-writable) application directory.
            NativeDriverApi.SetDefaultDllDirectories(NativeDriverApi.LOAD_LIBRARY_SEARCH_SYSTEM32);

            if (!DriverHelperArguments.TryParse(args, out var kind, out var target, out var pipeName, out var hints))
            {
                return 64;
            }

            if (!IsElevated())
            {
                return 5;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
            try
            {
                pipe.Connect(15_000);
            }
            catch (Exception)
            {
                // Without a channel the app cannot observe the outcome; do nothing.
                return 3;
            }

            using var channel = new HelperChannel(pipe);

            // A Mutex is owned by the thread that acquired it, so it is taken and
            // released here on the main thread, never across the awaits below
            // (releasing it from a thread-pool continuation throws).
            using var mutex = new Mutex(false, OperationMutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                channel.SendResult(DriverOperationResult.For(kind, target, DriverOperationStatus.Busy, "Another driver operation is already running."));
                return 2;
            }

            try
            {
                return RunOperationAsync(kind, target, hints, channel).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                channel.Log($"Helper failed: {ex}");
                channel.SendResult(DriverOperationResult.For(kind, target, DriverOperationStatus.Failed, ex.Message));
                return 1;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        private static async Task<int> RunOperationAsync(
            DriverOperationKind kind,
            DriverTarget target,
            IReadOnlyList<string> hints,
            HelperChannel channel)
        {
            string workDirectory = CreateProtectedWorkDirectory();
            try
            {
                channel.Log($"Driver helper {kind} target={target} pid={Environment.ProcessId} workDir={workDirectory}.");
                string? wdiPath = kind == DriverOperationKind.Install ? PrepareVerifiedWdiSimple(workDirectory, channel.Log) : null;

                using var deadline = new CancellationTokenSource(OperationDeadline);
                var engine = new DriverOperationEngine(new WindowsDriverSystem(), channel.Log, workDirectory, wdiPath, channel.Progress);
                DriverOperationResult result;
                try
                {
                    result = kind == DriverOperationKind.Install
                        ? await engine.InstallAsync(target, deadline.Token)
                        : await engine.RestoreAsync(target, hints, deadline.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    result = DriverOperationResult.For(kind, target, DriverOperationStatus.TimedOut,
                        $"The driver operation exceeded {OperationDeadline.TotalMinutes:0} minutes.");
                }

                channel.SendResult(result);
                return result.Succeeded ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(workDirectory, recursive: true); } catch { }
            }
        }

        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Creates a fresh directory under %SystemRoot%\Temp whose ACL only grants
        /// SYSTEM and Administrators access, so a same-user process cannot swap the
        /// helper binaries or libwdi's generated files mid-operation.
        /// </summary>
        private static string CreateProtectedWorkDirectory()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            var path = Path.Combine(root, $"ZeroCue-{Guid.NewGuid():N}");

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (var sid in new[] { system, admins })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.SetOwner(admins);
            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                throw new IOException($"Work directory already exists: {path}");
            }

            directory.Create(security);
            return path;
        }

        private static string? PrepareVerifiedWdiSimple(string workDirectory, Action<string> log)
        {
            foreach (var candidate in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "resources", "Assets", "wdi-simple.exe"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "wdi-simple.exe")
            })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                // Hash and copy the same in-memory bytes: no window to swap the file.
                var bytes = File.ReadAllBytes(candidate);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(hash, WdiSimpleSha256, StringComparison.OrdinalIgnoreCase))
                {
                    log($"Refusing wdi-simple at {candidate}: SHA-256 {hash} does not match the pinned {WdiSimpleSha256}.");
                    return null;
                }

                var target = Path.Combine(workDirectory, "wdi-simple.exe");
                File.WriteAllBytes(target, bytes);
                log($"wdi-simple verified (SHA-256 {hash}).");
                return target;
            }

            log("wdi-simple.exe was not found next to the application.");
            return null;
        }

        private sealed class HelperChannel : IDisposable
        {
            private readonly StreamWriter _writer;
            private readonly object _gate = new();
            private bool _broken;

            public HelperChannel(Stream stream)
            {
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            }

            public void Log(string text) => Send(new DriverHelperMessage { Type = "log", Text = text });

            public void Progress(string text) => Send(new DriverHelperMessage { Type = "progress", Text = text });

            public void SendResult(DriverOperationResult result) => Send(new DriverHelperMessage { Type = "result", Result = result });

            private void Send(DriverHelperMessage message)
            {
                lock (_gate)
                {
                    if (_broken)
                    {
                        return;
                    }

                    try
                    {
                        _writer.WriteLine(message.Serialize());
                    }
                    catch (IOException)
                    {
                        // The app went away; keep working so the device is not left half-installed.
                        _broken = true;
                    }
                }
            }

            public void Dispose()
            {
                try { _writer.Dispose(); } catch { }
            }
        }
    }
}
