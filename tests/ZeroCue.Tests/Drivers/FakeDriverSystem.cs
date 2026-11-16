using ZeroCue.DataProbe.Services.Drivers;
using ZeroCue.DataProbe.Services.Pnp;
using Xunit;

namespace ZeroCue.Tests.Drivers;

/// <summary>In-memory PnP + driver store used to exercise the driver engine without touching Windows.</summary>
internal sealed class FakeDriverSystem : IDriverSystem
{
    public List<PnpDevice> Devices { get; } = new();
    public Dictionary<string, InfInfo> Infs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Commands { get; } = new();
    public List<(string HardwareId, string InfPath)> Updates { get; } = new();
    public int WdiRuns { get; private set; }
    public bool CorsairStopped { get; private set; }
    public bool CorsairRestored { get; private set; }
    public int DeleteDriverExitCode { get; set; }

    /// <summary>UpdateDriver assigns WinUSB but leaves the node DN_NEED_RESTART (an app held it open).</summary>
    public bool UpdateLeavesRestartPending { get; set; }

    /// <summary>Whether pnputil /restart-device can restart the device (false = vetoed by an open handle).</summary>
    public bool RestartSucceeds { get; set; } = true;

    /// <summary>Simulates wdi-simple; receives the completion probe the engine passed.</summary>
    public Func<IReadOnlyList<string>, ProcessCompletionProbe?, ProcessOutcome>? OnWdi { get; set; }

    public const uint NeedRestart = 0x00000100;

    /// <summary>Whether restarting a failed composite parent clears its problem code and enumerates its interfaces.</summary>
    public bool RestartRecoversParent { get; set; }

    /// <summary>Original (non-WinUSB) service each node returns to after removal.</summary>
    public Dictionary<string, string> OriginalServices { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PnpDevice AddDevice(string instanceId, string? service, string? inf = null, uint problem = 0, string? parent = null, uint status = 0)
    {
        Assert.True(PnpIdentity.TryParseUsbInstanceId(instanceId, out var vid, out var pid, out var mi));
        var device = new PnpDevice(instanceId, vid, pid, mi, true, service, inf, problem, 0, parent, null, status);
        Devices.Add(device);
        return device;
    }

    public void AddInf(string name, string content) => Infs[name] = InfClassifier.Parse(content);

    public IReadOnlyList<PnpDevice> GetDevices(IReadOnlyCollection<int> vendorIds, bool includePhantoms = false) =>
        Devices.Where(device => vendorIds.Contains(device.VendorId) && (includePhantoms || device.IsPresent)).ToArray();

    public void Rescan()
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            var device = Devices[i];
            if (!device.IsPresent && OriginalServices.TryGetValue(device.InstanceId, out var original))
            {
                Devices[i] = device with { IsPresent = true, Service = original, DriverInfPath = "input.inf" };
            }
        }
    }

    public IReadOnlyList<PublishedInf> GetPublishedInfs() => Infs.Select(pair => new PublishedInf(pair.Key, pair.Value)).ToArray();

    public string? GetDriverStoreInfPath(string publishedInfName) =>
        Infs.ContainsKey(publishedInfName) ? $@"C:\Windows\System32\DriverStore\FileRepository\{publishedInfName}\usb_device.inf" : null;

    public DriverUpdateOutcome UpdateDriver(string hardwareId, string driverStoreInfPath)
    {
        Updates.Add((hardwareId, driverStoreInfPath));
        var inf = Path.GetFileName(Path.GetDirectoryName(driverStoreInfPath));
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].IsPresent && $"USB\\{Devices[i].HardwareKey}".Equals(hardwareId, StringComparison.OrdinalIgnoreCase))
            {
                Devices[i] = Devices[i] with
                {
                    Service = "WinUSB",
                    DriverInfPath = inf,
                    ProblemCode = 0,
                    DevNodeStatus = UpdateLeavesRestartPending ? NeedRestart : 0
                };
            }
        }

        return new DriverUpdateOutcome(true, UpdateLeavesRestartPending, 0);
    }

    public Task<ProcessOutcome> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct, ProcessCompletionProbe? completion = null)
    {
        Commands.Add($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
        if (fileName.EndsWith("wdi-simple.exe", StringComparison.OrdinalIgnoreCase))
        {
            WdiRuns++;
            return Task.FromResult(OnWdi?.Invoke(arguments, completion) ?? new ProcessOutcome(0, false, string.Empty));
        }

        switch (arguments[0])
        {
            case "/remove-device":
                for (int i = 0; i < Devices.Count; i++)
                {
                    if (Devices[i].InstanceId == arguments[1])
                    {
                        Devices[i] = Devices[i] with { IsPresent = false };
                    }
                }

                return Task.FromResult(new ProcessOutcome(0, false, string.Empty));
            case "/delete-driver":
                if (DeleteDriverExitCode is 0 or 3010)
                {
                    Infs.Remove(arguments[1]);
                }

                return Task.FromResult(new ProcessOutcome(DeleteDriverExitCode, false, string.Empty));
            case "/restart-device":
                if (!RestartSucceeds)
                {
                    return Task.FromResult(new ProcessOutcome(1, false, "Failed to restart device: vetoed."));
                }

                for (int i = 0; i < Devices.Count; i++)
                {
                    if (Devices[i].InstanceId == arguments[1] || Devices[i].Parent == arguments[1])
                    {
                        Devices[i] = Devices[i] with { DevNodeStatus = Devices[i].DevNodeStatus & ~NeedRestart };
                    }
                }

                var failedParent = Devices.FirstOrDefault(device => device.InstanceId == arguments[1] && device.ProblemCode != 0);
                if (failedParent != null)
                {
                    if (!RestartRecoversParent)
                    {
                        return Task.FromResult(new ProcessOutcome(1, false, "Failed to restart device."));
                    }

                    Devices[Devices.IndexOf(failedParent)] = failedParent with { ProblemCode = 0 };
                    foreach (var mi in new[] { 0, 3, 4 })
                    {
                        Devices.Add(new PnpDevice($@"USB\VID_{failedParent.VendorId:X4}&PID_{failedParent.ProductId:X4}&MI_{mi:X2}\7&1&0&000{mi}",
                            failedParent.VendorId, failedParent.ProductId, mi, true, mi == 0 ? "usbaudio" : "HidUsb", "input.inf", 0, 0, failedParent.InstanceId, null));
                    }
                }

                return Task.FromResult(new ProcessOutcome(0, false, string.Empty));
            default:
                return Task.FromResult(new ProcessOutcome(0, false, string.Empty));
        }
    }

    public TimeSpan Now { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Now += delay;
        return Task.CompletedTask;
    }

    public CorsairState StopCorsair(Action<string> log)
    {
        CorsairStopped = true;
        return new CorsairState(new[] { "CorsairService" }, true, @"C:\iCUE.exe");
    }

    public void RestoreCorsair(CorsairState state, Action<string> log) => CorsairRestored = true;
}
