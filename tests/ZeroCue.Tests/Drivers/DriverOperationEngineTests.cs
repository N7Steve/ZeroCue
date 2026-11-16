using ZeroCue.DataProbe.Services.Drivers;
using Xunit;

namespace ZeroCue.Tests.Drivers;

public class DriverOperationEngineTests
{
    private const string ActiveReceiver = @"USB\VID_1B1C&PID_3A09\1E03B15300050217";

    private static DriverOperationEngine CreateEngine(FakeDriverSystem system, string? wdi = @"C:\work\wdi-simple.exe") =>
        new(system, _ => { }, Path.GetTempPath(), wdi);

    private static void StageReceiverPackages(FakeDriverSystem system)
    {
        system.AddInf("oem32.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A08&MI_04"));
        system.AddInf("oem34.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A08&MI_03"));
        system.AddInf("oem55.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A09"));
    }

    [Fact]
    public async Task Install_applies_staged_package_without_regenerating_it()
    {
        // The exact state observed on real hardware: the active receiver kept the
        // inbox HID driver although ZeroCue's 3A09 package was already staged.
        var system = new FakeDriverSystem();
        system.AddDevice(ActiveReceiver, "HidUsb", "input.inf");
        StageReceiverPackages(system);

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Receiver, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Equal(0, system.WdiRuns);
        Assert.Single(system.Updates);
        Assert.Equal(@"USB\VID_1B1C&PID_3A09", system.Updates[0].HardwareId);
        Assert.Contains("oem55.inf", result.OwnedPackages);
        Assert.True(system.CorsairStopped && system.CorsairRestored);
        Assert.True(result.IcueWasRunning);
    }

    [Fact]
    public async Task Install_without_device_reports_no_device_and_leaves_icue_alone()
    {
        var system = new FakeDriverSystem();

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.NoDevice, result.Status);
        Assert.False(system.CorsairStopped);
    }

    [Fact]
    public async Task Install_already_bound_device_is_a_fast_no_op()
    {
        var system = new FakeDriverSystem();
        system.AddDevice(@"USB\VID_1B1C&PID_3A05\SERIAL", "usbccgp");
        system.AddDevice(@"USB\VID_1B1C&PID_3A05&MI_00\6&1&0&0000", "WinUSB", "oem91.inf");
        system.AddDevice(@"USB\VID_1B1C&PID_3A05&MI_03\6&1&0&0003", "WinUSB", "oem93.inf");
        system.AddDevice(@"USB\VID_1B1C&PID_3A05&MI_04\6&1&0&0004", "WinUSB", "oem94.inf");

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Equal(0, system.WdiRuns);
        Assert.Empty(system.Updates);
    }

    [Fact]
    public async Task Install_without_verified_wdi_fails_instead_of_running_unverified_binary()
    {
        var system = new FakeDriverSystem();
        system.AddDevice(@"USB\VID_1B1C&PID_3A05\SERIAL", "usbccgp");
        system.AddDevice(@"USB\VID_1B1C&PID_3A05&MI_04\6&1&0&0004", "HidUsb");

        var result = await CreateEngine(system, wdi: null).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Failed, result.Status);
        Assert.Equal(0, system.WdiRuns);
        Assert.True(system.CorsairRestored);
    }

    [Fact]
    public async Task Restore_removes_only_verified_packages_and_waits_for_original_driver()
    {
        var system = new FakeDriverSystem();
        system.AddDevice(ActiveReceiver, "WinUSB", "oem55.inf");
        system.OriginalServices[ActiveReceiver] = "HidUsb";
        StageReceiverPackages(system);
        system.AddInf("oem70.inf", InfClassifierTests.LibWdiInf("Other tool", "VID_1B1C&PID_3A09", sourceName: "Other Disk"));

        var result = await CreateEngine(system).RestoreAsync(DriverTarget.Receiver, new[] { "oem55.inf" }, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Equal(new[] { "oem32.inf", "oem34.inf", "oem55.inf" }, result.RemovedPackages.OrderBy(name => name));
        Assert.True(system.Infs.ContainsKey("oem70.inf"));
        Assert.Equal("HidUsb", system.Devices.Single().Service);
    }

    [Fact]
    public async Task Restore_treats_reboot_required_as_success()
    {
        var system = new FakeDriverSystem { DeleteDriverExitCode = 3010 };
        system.AddDevice(ActiveReceiver, "WinUSB", "oem55.inf");
        system.OriginalServices[ActiveReceiver] = "HidUsb";
        system.AddInf("oem55.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A09"));

        var result = await CreateEngine(system).RestoreAsync(DriverTarget.Receiver, Array.Empty<string>(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.RebootRequired);
    }

    [Fact]
    public async Task Restore_refuses_when_winusb_comes_from_an_unverified_package()
    {
        var system = new FakeDriverSystem();
        system.AddDevice(ActiveReceiver, "WinUSB", "oem70.inf");
        system.AddInf("oem70.inf", InfClassifierTests.LibWdiInf("Other tool", "VID_1B1C&PID_3A09", sourceName: "Other Disk"));

        var result = await CreateEngine(system).RestoreAsync(DriverTarget.Receiver, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Failed, result.Status);
        Assert.True(system.Infs.ContainsKey("oem70.inf"));
        Assert.DoesNotContain(system.Commands, command => command.Contains("/remove-device"));
    }

    private const string WiredParent = @"USB\VID_1B1C&PID_3A05\1C555B6300060217";
    private const string WiredMi03 = @"USB\VID_1B1C&PID_3A05&MI_03\6&1D6D9941&0&0003";
    private const string WiredMi04 = @"USB\VID_1B1C&PID_3A05&MI_04\6&1D6D9941&0&0004";

    [Fact]
    public async Task Install_restarts_a_receiver_left_pending_by_the_driver_update()
    {
        // Real hardware: UpdateDriverForPlugAndPlayDevices returned reboot=true and the
        // receiver kept answering CreateFile with ERROR_GEN_FAILURE until reconnected.
        var system = new FakeDriverSystem { UpdateLeavesRestartPending = true };
        system.AddDevice(ActiveReceiver, "HidUsb", "input.inf");
        StageReceiverPackages(system);

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Receiver, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.False(result.ReconnectRequired);
        Assert.Equal(0, system.WdiRuns);
        Assert.Contains(system.Commands, command => command == $"pnputil.exe /restart-device {ActiveReceiver}");
        Assert.DoesNotContain(system.Devices, device => device.IsRestartPending);
    }

    [Fact]
    public async Task Install_asks_for_a_reconnect_when_the_restart_is_vetoed()
    {
        var system = new FakeDriverSystem { UpdateLeavesRestartPending = true, RestartSucceeds = false };
        system.AddDevice(ActiveReceiver, "HidUsb", "input.inf");
        StageReceiverPackages(system);

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Receiver, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.SuccessWithWarnings, result.Status);
        Assert.True(result.ReconnectRequired);
        // Reconnecting is enough; the UI must not also ask for a Windows restart.
        Assert.False(result.RebootRequired);
        // A pending restart must never trigger a new package.
        Assert.Equal(0, system.WdiRuns);
        Assert.Contains("oem55.inf", result.OwnedPackages);
    }

    [Fact]
    public async Task Pending_interfaces_restart_through_their_composite_parent_only()
    {
        var system = new FakeDriverSystem();
        system.AddDevice(WiredParent, "usbccgp", "usb.inf", parent: @"USB\VID_2109&PID_2813\HUB");
        system.AddDevice(@"USB\VID_1B1C&PID_3A05&MI_00\6&1D6D9941&0&0000", "WinUSB", "oem94.inf", parent: WiredParent);
        system.AddDevice(WiredMi03, "WinUSB", "oem93.inf", parent: WiredParent, status: FakeDriverSystem.NeedRestart);
        system.AddDevice(WiredMi04, "WinUSB", "oem91.inf", parent: WiredParent, status: FakeDriverSystem.NeedRestart);

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Equal(0, system.WdiRuns);
        var restarts = system.Commands.Where(command => command.Contains("/restart-device")).ToArray();
        Assert.Equal(new[] { $"pnputil.exe /restart-device {WiredParent}" }, restarts);
    }

    [Fact]
    public async Task Wdi_probe_reports_completion_only_after_a_new_package_is_assigned()
    {
        var system = new FakeDriverSystem();
        system.AddDevice(WiredParent, "usbccgp", "usb.inf");
        system.AddDevice(@"USB\VID_1B1C&PID_3A05&MI_00\6&1D6D9941&0&0000", "WinUSB", "oem94.inf", parent: WiredParent);
        system.AddDevice(WiredMi03, "WinUSB", "oem93.inf", parent: WiredParent);
        system.AddDevice(WiredMi04, "HidUsb", "input.inf", parent: WiredParent);
        system.AddInf("oem94.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A05&MI_00"));
        system.AddInf("oem93.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A05&MI_03"));

        var probeResults = new List<bool>();
        system.OnWdi = (arguments, completion) =>
        {
            Assert.NotNull(completion);
            Assert.Equal(DriverOperationEngine.WdiCompletionGrace, completion!.Grace);
            probeResults.Add(completion.IsComplete());

            // wdi-simple publishes the package and Windows assigns it; the restart is vetoed.
            system.AddInf("oem99.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A05&MI_04"));
            int index = system.Devices.FindIndex(device => device.InstanceId == WiredMi04);
            system.Devices[index] = system.Devices[index] with { Service = "WinUSB", DriverInfPath = "oem99.inf", DevNodeStatus = FakeDriverSystem.NeedRestart };
            probeResults.Add(completion.IsComplete());
            return new ProcessOutcome(null, false, string.Empty, StoppedEarly: true);
        };

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(new[] { false, true }, probeResults);
        Assert.Equal(1, system.WdiRuns);
        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Contains("oem99.inf", result.OwnedPackages);
        Assert.Contains(system.Commands, command => command == $"pnputil.exe /restart-device {WiredParent}");
    }

    [Fact]
    public async Task Wdi_probe_completes_absent_receiver_states_once_their_package_is_staged()
    {
        // Real hardware: with the receiver in active mode, wdi-simple spent ~39 s on each
        // base-mode interface that was not even connected.
        var system = new FakeDriverSystem();
        system.AddDevice(ActiveReceiver, "HidUsb", "input.inf");
        int nextInf = 60;
        var probeResults = new List<(string Target, bool Before, bool After)>();
        system.OnWdi = (arguments, completion) =>
        {
            int interfaceIndex = arguments.ToList().IndexOf("-i");
            string pid = arguments[arguments.ToList().IndexOf("-p") + 1][2..];
            string key = interfaceIndex >= 0 ? $"VID_1B1C&PID_{pid}&MI_{int.Parse(arguments[interfaceIndex + 1]):X2}" : $"VID_1B1C&PID_{pid}";
            bool before = completion!.IsComplete();
            string inf = $"oem{nextInf++}.inf";
            system.AddInf(inf, InfClassifierTests.LibWdiInf("ZeroCue", key));
            int index = system.Devices.FindIndex(device => device.HardwareKey == key);
            if (index >= 0)
            {
                system.Devices[index] = system.Devices[index] with { Service = "WinUSB", DriverInfPath = inf };
            }

            probeResults.Add((key, before, completion.IsComplete()));
            return new ProcessOutcome(null, false, string.Empty, StoppedEarly: true);
        };

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Receiver, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Equal(3, probeResults.Count);
        Assert.All(probeResults, probe => Assert.False(probe.Before));
        Assert.All(probeResults, probe => Assert.True(probe.After));
        Assert.Contains(probeResults, probe => probe.Target == "VID_1B1C&PID_3A08&MI_04");
    }

    [Fact]
    public async Task Install_on_a_failed_composite_parent_stops_quickly_without_wdi()
    {
        // Real hardware: the wired controller showed Code 10 on usbccgp, exposed no
        // interfaces, and wdi-simple retried 3 x 39 s per interface.
        var system = new FakeDriverSystem();
        system.AddDevice(WiredParent, "usbccgp", "usb.inf", problem: 10);

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.DeviceError, result.Status);
        Assert.Equal(10u, result.ProblemCode);
        Assert.Equal(0, system.WdiRuns);
        Assert.Contains(system.Commands, command => command == $"pnputil.exe /restart-device {WiredParent}");
        Assert.False(system.CorsairStopped);
    }

    [Fact]
    public async Task Install_continues_when_restarting_the_failed_parent_recovers_it()
    {
        var system = new FakeDriverSystem { RestartRecoversParent = true };
        system.AddDevice(WiredParent, "usbccgp", "usb.inf", problem: 10);
        system.AddInf("oem91.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A05&MI_04"));
        system.AddInf("oem93.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A05&MI_03"));
        system.AddInf("oem94.inf", InfClassifierTests.LibWdiInf("ZeroCue", "VID_1B1C&PID_3A05&MI_00"));

        var result = await CreateEngine(system).InstallAsync(DriverTarget.Gamepad, CancellationToken.None);

        Assert.Equal(DriverOperationStatus.Success, result.Status);
        Assert.Equal(0, system.WdiRuns);
        Assert.Equal(3, system.Updates.Count);
    }
}
