using ZeroCue.DataProbe.Services;
using ZeroCue.DataProbe.Services.Drivers;
using Xunit;

namespace ZeroCue.Tests.Drivers;

/// <summary>
/// Hardware-model invariants that previously lived as source-text checks in
/// scripts/verify-driver-safety.ps1. Breaking any of them binds WinUSB to the
/// wrong interface of a real device.
/// </summary>
public class DriverCatalogTests
{
    private static DriverVariant Variant(DriverTargetDefinition definition, string key) =>
        definition.Variants.Single(variant => variant.Key == key);

    [Theory]
    [InlineData(0x3A05)]
    [InlineData(0x3A04)]
    public void Wired_v2_binds_mi04_mi03_then_mi00(int productId)
    {
        var bindings = Variant(DriverCatalog.Gamepad, "wired-v2").Bindings
            .Where(binding => binding.ProductId == productId)
            .Select(binding => binding.InterfaceNumber)
            .ToArray();

        Assert.Equal(new int?[] { 4, 3, 0 }, bindings);
    }

    [Theory]
    [InlineData(0x434D)]
    [InlineData(0x434B)]
    public void Experimental_wired_v1_is_scoped_to_mi00_mi03_mi04(int productId)
    {
        var interfaces = Variant(DriverCatalog.Gamepad, "wired-v1").Bindings
            .Where(binding => binding.ProductId == productId)
            .Select(binding => binding.InterfaceNumber)
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(new int?[] { 0, 3, 4 }, interfaces);
    }

    [Theory]
    [InlineData("v2", 0x3A08, 0x3A09)]
    [InlineData("v1", 0x434E, 0x5046)]
    public void Receiver_base_state_is_mi03_mi04_and_active_state_is_whole_device(string key, int basePid, int activePid)
    {
        var variant = Variant(DriverCatalog.Receiver, key);

        Assert.Equal(new int?[] { 3, 4 }, variant.Bindings.Where(b => b.ProductId == basePid).Select(b => b.InterfaceNumber).OrderBy(v => v));
        var active = Assert.Single(variant.Bindings, binding => binding.ProductId == activePid);
        Assert.Null(active.InterfaceNumber);
    }

    [Fact]
    public void Receiver_mi00_is_never_bound_and_only_v2_mi00_is_an_obsolete_migration()
    {
        Assert.All(DriverCatalog.Receiver.Variants, variant =>
            Assert.DoesNotContain(variant.Bindings, binding => binding.InterfaceNumber == 0));

        var obsolete = Assert.Single(Variant(DriverCatalog.Receiver, "v2").ObsoleteBindings);
        Assert.Equal((0x3A08, (int?)0), (obsolete.ProductId, obsolete.InterfaceNumber));
        Assert.Empty(Variant(DriverCatalog.Receiver, "v1").ObsoleteBindings);
    }

    [Fact]
    public void Only_receivers_prepare_every_state_in_one_operation()
    {
        Assert.True(DriverCatalog.Receiver.PreparesAllVariantStates);
        Assert.False(DriverCatalog.Gamepad.PreparesAllVariantStates);
    }

    [Fact]
    public void Driver_catalog_and_protocol_profile_describe_the_same_devices()
    {
        var profile = SupportedScufDeviceProfile.ScufEnvisionPro;

        var wiredCatalog = DriverCatalog.Gamepad.Variants.SelectMany(v => v.ProductIds.Select(pid => (v.VendorId, pid))).ToHashSet();
        var wiredProfile = profile.WiredDeviceIdentities.Select(identity => (identity.VendorId, identity.ProductId)).ToHashSet();
        Assert.Equal(wiredProfile, wiredCatalog);

        var receiverCatalog = DriverCatalog.Receiver.Variants.SelectMany(v => v.ProductIds.Select(pid => (v.VendorId, pid))).ToHashSet();
        var receiverProfile = profile.WirelessReceiverIdentities.Select(identity => (identity.VendorId, identity.ProductId)).ToHashSet();
        Assert.Equal(receiverProfile, receiverCatalog);
    }

    [Fact]
    public void Pinned_wdi_simple_hash_matches_the_bundled_binary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "wdi-simple.exe");
        Assert.True(File.Exists(path), $"Missing {path}");

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(ElevatedDriverHelper.WdiSimpleSha256, hash);
    }
}
