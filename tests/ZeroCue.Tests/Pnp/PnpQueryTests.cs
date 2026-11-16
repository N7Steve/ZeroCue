using ZeroCue.DataProbe.Services.Drivers;
using ZeroCue.DataProbe.Services.Pnp;
using Xunit;

namespace ZeroCue.Tests.Pnp;

public class PnpQueryTests
{
    [Theory]
    [InlineData(@"USB\VID_1B1C&PID_3A08&MI_04\7&2AB3E7C1&6&0004", 0x1B1C, 0x3A08, 4)]
    [InlineData(@"USB\VID_1B1C&PID_3A09\1E03B15300050217", 0x1B1C, 0x3A09, null)]
    public void Parses_usb_instance_ids(string instanceId, int vid, int pid, int? mi)
    {
        Assert.True(PnpIdentity.TryParseUsbInstanceId(instanceId, out var parsedVid, out var parsedPid, out var parsedMi));
        Assert.Equal((vid, pid, mi), (parsedVid, parsedPid, parsedMi));
    }

    [Theory]
    [InlineData(@"HID\VID_1B1C&PID_3A09&COL01\7&2A3D7C28&9&0000")]
    [InlineData(@"USB\ROOT_HUB30\4&1")]
    public void Ignores_non_usb_function_ids(string instanceId)
    {
        Assert.False(PnpIdentity.TryParseUsbInstanceId(instanceId, out _, out _, out _));
    }

    /// <summary>Read-only smoke test against the real PnP manager of the machine running the tests.</summary>
    [Fact]
    public void Enumerates_real_usb_devices_quickly()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var devices = PnpQuery.GetUsbDevices(DriverCatalog.AllVendorIds, includePhantoms: true);
        timer.Stop();

        Assert.All(devices, device => Assert.Contains(device.VendorId, DriverCatalog.AllVendorIds));
        Assert.True(timer.ElapsedMilliseconds < 2000, $"Enumeration took {timer.ElapsedMilliseconds} ms");
    }
}
