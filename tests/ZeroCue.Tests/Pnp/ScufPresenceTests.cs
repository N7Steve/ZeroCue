using ZeroCue.DataProbe.Services.Pnp;
using Xunit;
using Xunit.Abstractions;

namespace ZeroCue.Tests.Pnp;

public class ScufPresenceTests
{
    private readonly ITestOutputHelper _output;

    public ScufPresenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static PnpDevice Node(string instanceId, string? service, uint problem = 0)
    {
        Assert.True(PnpIdentity.TryParseUsbInstanceId(instanceId, out var vid, out var pid, out var mi));
        return new PnpDevice(instanceId, vid, pid, mi, true, service, null, problem, 0, null, null);
    }

    [Fact]
    public void Active_receiver_on_hid_driver_needs_the_zerocue_driver()
    {
        var snapshot = ScufPresenceSnapshot.Evaluate(new[] { Node(@"USB\VID_1B1C&PID_3A09\1E03", "HidUsb") });

        Assert.Equal(ScufDeviceReadiness.MissingDriver, snapshot.Receiver?.Readiness);
        Assert.False(snapshot.IsReceiverReady);
        Assert.Null(snapshot.Wired);
    }

    [Fact]
    public void Base_receiver_with_both_interfaces_on_winusb_is_ready()
    {
        var snapshot = ScufPresenceSnapshot.Evaluate(new[]
        {
            Node(@"USB\VID_1B1C&PID_3A08\1E03", "usbccgp"),
            Node(@"USB\VID_1B1C&PID_3A08&MI_00\7&1&0000", "usbaudio"),
            Node(@"USB\VID_1B1C&PID_3A08&MI_03\7&1&0003", "WinUSB"),
            Node(@"USB\VID_1B1C&PID_3A08&MI_04\7&1&0004", "WinUSB")
        });

        Assert.True(snapshot.IsReceiverReady);
    }

    [Fact]
    public void Failed_composite_parent_is_reported_as_device_error()
    {
        // Observed on real hardware: Code 10 / STATUS_INSUFFICIENT_RESOURCES on a hub.
        var snapshot = ScufPresenceSnapshot.Evaluate(new[] { Node(@"USB\VID_1B1C&PID_3A08\1E03", "usbccgp", problem: 10) });

        Assert.Equal(ScufDeviceReadiness.DeviceError, snapshot.Receiver?.Readiness);
        Assert.Equal(10u, snapshot.Receiver?.ProblemCode);
    }

    [Fact]
    public void Wired_controller_with_missing_interface_is_settling()
    {
        var snapshot = ScufPresenceSnapshot.Evaluate(new[]
        {
            Node(@"USB\VID_1B1C&PID_3A05\SERIAL", "usbccgp"),
            Node(@"USB\VID_1B1C&PID_3A05&MI_00\6&1&0000", "WinUSB"),
            Node(@"USB\VID_1B1C&PID_3A05&MI_04\6&1&0004", "WinUSB")
        });

        Assert.Equal(ScufDeviceReadiness.Settling, snapshot.Wired?.Readiness);
    }

    [Fact]
    public void Wired_controller_partially_on_hid_needs_driver()
    {
        var snapshot = ScufPresenceSnapshot.Evaluate(new[]
        {
            Node(@"USB\VID_1B1C&PID_3A05\SERIAL", "usbccgp"),
            Node(@"USB\VID_1B1C&PID_3A05&MI_00\6&1&0000", "WinUSB"),
            Node(@"USB\VID_1B1C&PID_3A05&MI_03\6&1&0003", "HidUsb"),
            Node(@"USB\VID_1B1C&PID_3A05&MI_04\6&1&0004", "WinUSB")
        });

        Assert.Equal(ScufDeviceReadiness.MissingDriver, snapshot.Wired?.Readiness);
    }

    /// <summary>Read-only: prints what ZeroCue would detect on this machine right now.</summary>
    [Fact]
    public void Captures_live_presence()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = ScufPresenceSnapshot.Capture();
        _output.WriteLine($"{timer.ElapsedMilliseconds} ms: {snapshot.Signature}");
    }

    [Fact]
    public void Winusb_node_waiting_for_a_device_restart_is_not_ready()
    {
        Assert.True(PnpIdentity.TryParseUsbInstanceId(@"USB\VID_1B1C&PID_3A09\1E03", out var vid, out var pid, out var mi));
        var flagged = new PnpDevice(@"USB\VID_1B1C&PID_3A09\1E03", vid, pid, mi, true, "WinUSB", "oem55.inf", 0, 0, null, null, DevNodeStatus: 0x100);
        var noInterface = flagged with { DevNodeStatus = 0x0A, WinUsbInterfaceActive = false };
        var running = flagged with { DevNodeStatus = 0x0A, WinUsbInterfaceActive = true };
        var unknown = flagged with { DevNodeStatus = 0x0A, WinUsbInterfaceActive = null };

        Assert.Equal(ScufDeviceReadiness.RestartPending, ScufPresenceSnapshot.Evaluate(new[] { flagged }).Receiver?.Readiness);
        Assert.Equal(ScufDeviceReadiness.RestartPending, ScufPresenceSnapshot.Evaluate(new[] { noInterface }).Receiver?.Readiness);
        Assert.Equal(ScufDeviceReadiness.Ready, ScufPresenceSnapshot.Evaluate(new[] { running }).Receiver?.Readiness);
        Assert.Equal(ScufDeviceReadiness.Ready, ScufPresenceSnapshot.Evaluate(new[] { unknown }).Receiver?.Readiness);
        Assert.False(ScufPresenceSnapshot.Evaluate(new[] { flagged }).IsReceiverReady);
    }
}
