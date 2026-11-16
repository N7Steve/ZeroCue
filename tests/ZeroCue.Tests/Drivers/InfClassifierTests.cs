using ZeroCue.DataProbe.Services.Drivers;
using Xunit;

namespace ZeroCue.Tests.Drivers;

public class InfClassifierTests
{
    internal static string LibWdiInf(string deviceName, string deviceId, string sourceName = "ZeroCue Install Disk", string service = "WinUSB") =>
        "; usb_device.inf\r\n" +
        "[Strings]\r\n" +
        $"DeviceName = \"{deviceName}\"\r\n" +
        "VendorName = \"Corsair\"\r\n" +
        $"SourceName = \"{sourceName}\"\r\n" +
        $"DeviceID   = \"{deviceId}\"\r\n" +
        "DeviceGUID = \"{E0DC1436-F377-4F24-9446-D6EE5F93E60F}\"\r\n" +
        "\r\n" +
        "[Version]\r\n" +
        "Signature   = \"$Windows NT$\"\r\n" +
        "Class       = \"USBDevice\"\r\n" +
        "Provider    = \"libwdi\"\r\n" +
        "CatalogFile = usb_device.cat\r\n" +
        "DriverVer   = 04/18/2019, 6.1.7600.16385\r\n" +
        "\r\n" +
        "[libusbDevice_WinUSB.NTamd64]\r\n" +
        "%DeviceName% = USB_Install, USB\\%DeviceID%\r\n" +
        "\r\n" +
        "[USB_Install.Services]\r\n" +
        "Include     = winusb.inf\r\n" +
        $"AddService  = {service},0x00000002,WinUSB_ServiceInstall\r\n";

    private static readonly string[] ReceiverKeys = DriverCatalog.Receiver.AllHardwareKeys.ToArray();

    [Fact]
    public void Parses_libwdi_package_identity()
    {
        var info = InfClassifier.Parse(LibWdiInf("ZeroCue", "VID_1B1C&PID_3A09"));

        Assert.Equal("libwdi", info.Provider);
        Assert.Equal("ZeroCue", info.DeviceName);
        Assert.True(info.InstallsWinUsb);
        Assert.Equal(new[] { "VID_1B1C&PID_3A09" }, info.HardwareKeys);
    }

    [Fact]
    public void ZeroCue_package_for_managed_identity_is_owned()
    {
        var info = InfClassifier.Parse(LibWdiInf("ZeroCue SCUF Envision Pro Receiver V2", "VID_1B1C&PID_3A08&MI_04"));
        Assert.Equal(InfOwnership.ZeroCue, InfClassifier.Classify(info, ReceiverKeys));
    }

    [Fact]
    public void Whole_device_key_does_not_match_interface_key()
    {
        var info = InfClassifier.Parse(LibWdiInf("ZeroCue", "VID_1B1C&PID_3A09&MI_00"));

        Assert.DoesNotContain("VID_1B1C&PID_3A09", info.HardwareKeys);
        Assert.Equal(InfOwnership.Foreign, InfClassifier.Classify(info, ReceiverKeys));
    }

    [Fact]
    public void Package_for_unmanaged_device_is_foreign_even_with_zerocue_name()
    {
        var info = InfClassifier.Parse(LibWdiInf("ZeroCue", "VID_1234&PID_5678"));
        Assert.Equal(InfOwnership.Foreign, InfClassifier.Classify(info, ReceiverKeys));
    }

    [Fact]
    public void Non_winusb_package_is_foreign()
    {
        var info = InfClassifier.Parse(LibWdiInf("ZeroCue", "VID_1B1C&PID_3A09", service: "libusbK"));
        Assert.Equal(InfOwnership.Foreign, InfClassifier.Classify(info, ReceiverKeys));
    }

    [Fact]
    public void User_zadig_package_without_scoped_fingerprint_is_foreign()
    {
        var info = InfClassifier.Parse(LibWdiInf("My gamepad", "VID_1B1C&PID_3A09", sourceName: "My Install Disk"));
        Assert.Equal(InfOwnership.Foreign, InfClassifier.Classify(info, ReceiverKeys));
    }

    [Fact]
    public void Legacy_scuf_fingerprint_is_recognised()
    {
        var info = InfClassifier.Parse(LibWdiInf("SCUF", "VID_1B1C&PID_3A05&MI_03", sourceName: "SCUF Install Disk"));
        Assert.Equal(InfOwnership.LegacyZeroCue, InfClassifier.Classify(info, DriverCatalog.Gamepad.AllHardwareKeys));
    }

    [Theory]
    [InlineData("oem55.inf", true)]
    [InlineData("OEM7.INF", true)]
    [InlineData("oem-1.inf", false)]
    [InlineData("oem 1 .inf", false)]
    [InlineData("oem.inf", false)]
    [InlineData("..\\oem1.inf", false)]
    public void Published_inf_names_are_strict(string name, bool expected)
    {
        Assert.Equal(expected, InfClassifier.IsPublishedInfName(name));
    }

    /// <summary>Reads the real ZeroCue packages on this machine (read-only) when present.</summary>
    [Fact]
    public void Classifies_real_published_packages_without_throwing()
    {
        var infs = new WindowsDriverSystem().GetPublishedInfs();
        var managed = DriverCatalog.All.SelectMany(definition => definition.AllHardwareKeys).ToArray();
        foreach (var inf in infs)
        {
            _ = InfClassifier.Classify(inf.Info, managed);
        }
    }
}
