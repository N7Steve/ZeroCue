using System;
using System.Collections.Generic;
using System.Linq;
using ZeroCue.DataProbe.Services.Pnp;

namespace ZeroCue.DataProbe.Services.Drivers
{
    public enum DriverTarget
    {
        Gamepad,
        Receiver
    }

    /// <summary>One WinUSB binding: a whole device (no interface) or one interface of a composite device.</summary>
    public sealed record DriverBinding(int ProductId, int? InterfaceNumber, string Name)
    {
        public string HardwareKey(int vendorId) => PnpIdentity.FormatHardwareKey(vendorId, ProductId, InterfaceNumber);

        public bool Matches(PnpDevice device, int vendorId) =>
            device.VendorId == vendorId &&
            device.ProductId == ProductId &&
            device.InterfaceNumber == InterfaceNumber;
    }

    public sealed record DriverVariant(
        string Key,
        string Name,
        string DeviceName,
        int VendorId,
        bool IsExperimental,
        IReadOnlyList<DriverBinding> Bindings,
        IReadOnlyList<DriverBinding> ObsoleteBindings)
    {
        public IReadOnlyList<int> ProductIds => Bindings.Select(binding => binding.ProductId).Distinct().ToArray();

        public IEnumerable<string> HardwareKeys => Bindings.Concat(ObsoleteBindings).Select(binding => binding.HardwareKey(VendorId));
    }

    public sealed record DriverTargetDefinition(
        DriverTarget Target,
        string LogName,
        string DisplayName,
        IReadOnlyList<DriverVariant> Variants)
    {
        /// <summary>
        /// Receivers switch between a composite base PID and a whole-device active PID,
        /// so every state is prepared in one operation. Wired controllers only bind
        /// the PID that is actually connected.
        /// </summary>
        public bool PreparesAllVariantStates => Target == DriverTarget.Receiver;

        public IReadOnlyList<int> VendorIds => Variants.Select(variant => variant.VendorId).Distinct().ToArray();

        public IEnumerable<string> AllHardwareKeys => Variants.SelectMany(variant => variant.HardwareKeys).Distinct(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> DeviceIdentityKeys => Variants
            .SelectMany(variant => variant.ProductIds.Select(pid => PnpIdentity.FormatHardwareKey(variant.VendorId, pid, null)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        public DriverVariant? FindVariant(int vendorId, int productId) =>
            Variants.FirstOrDefault(variant => variant.VendorId == vendorId && variant.ProductIds.Contains(productId));

        /// <summary>Bindings that an install of the selected identity must converge.</summary>
        public IReadOnlyList<DriverBinding> BindingsFor(DriverVariant variant, int selectedProductId) =>
            PreparesAllVariantStates
                ? variant.Bindings
                : variant.Bindings.Where(binding => binding.ProductId == selectedProductId).ToArray();

        /// <summary>True when the device node belongs to any identity managed by this target.</summary>
        public bool OwnsDeviceNode(PnpDevice device) =>
            Variants.Any(variant => variant.VendorId == device.VendorId && variant.ProductIds.Contains(device.ProductId));
    }

    /// <summary>
    /// Single source of truth for the USB identities that ZeroCue binds to WinUSB.
    /// Protocol details (pipes, report sizes) live in <see cref="SupportedScufDeviceProfile"/>.
    /// </summary>
    public static class DriverCatalog
    {
        public const int CorsairVendorId = 0x1B1C;
        public const int ScufVendorId = 0x2E95;

        public static IReadOnlyList<int> AllVendorIds { get; } = new[] { CorsairVendorId, ScufVendorId };

        // Wired bindings are applied MI_04, MI_03 then MI_00: replacing MI_00 makes the
        // composite device re-enumerate, so it goes last and is followed by convergence.
        public static DriverTargetDefinition Gamepad { get; } = new(
            DriverTarget.Gamepad,
            "gamepad",
            "SCUF Envision wired controller",
            new[]
            {
                new DriverVariant(
                    "wired-v2",
                    "SCUF Envision wired controller",
                    "ZeroCue SCUF Envision Wired",
                    CorsairVendorId,
                    false,
                    new[]
                    {
                        new DriverBinding(0x3A05, 4, "interface 4 (MI_04)"),
                        new DriverBinding(0x3A05, 3, "interface 3 (MI_03)"),
                        new DriverBinding(0x3A05, 0, "interface 0 (MI_00)"),
                        new DriverBinding(0x3A04, 4, "interface 4 (MI_04)"),
                        new DriverBinding(0x3A04, 3, "interface 3 (MI_03)"),
                        new DriverBinding(0x3A04, 0, "interface 0 (MI_00)")
                    },
                    Array.Empty<DriverBinding>()),
                new DriverVariant(
                    "wired-v1",
                    "SCUF Envision wired controller V1",
                    "ZeroCue SCUF Envision Wired V1",
                    ScufVendorId,
                    true,
                    new[]
                    {
                        new DriverBinding(0x434D, 4, "experimental V1 Pro interface 4 (MI_04)"),
                        new DriverBinding(0x434D, 3, "experimental V1 Pro interface 3 (MI_03)"),
                        new DriverBinding(0x434D, 0, "experimental V1 Pro interface 0 (MI_00)"),
                        new DriverBinding(0x434B, 4, "experimental V1 interface 4 (MI_04)"),
                        new DriverBinding(0x434B, 3, "experimental V1 interface 3 (MI_03)"),
                        new DriverBinding(0x434B, 0, "experimental V1 interface 0 (MI_00)")
                    },
                    Array.Empty<DriverBinding>())
            });

        public static DriverTargetDefinition Receiver { get; } = new(
            DriverTarget.Receiver,
            "receiver",
            "SCUF RF receiver",
            new[]
            {
                new DriverVariant(
                    "v2",
                    "Envision Pro Wireless USB Receiver V2",
                    "ZeroCue SCUF Envision Pro Receiver V2",
                    CorsairVendorId,
                    false,
                    new[]
                    {
                        new DriverBinding(0x3A08, 4, "V2 interface 4 (MI_04)"),
                        new DriverBinding(0x3A08, 3, "V2 interface 3 (MI_03)"),
                        new DriverBinding(0x3A09, null, "V2 active receiver device")
                    },
                    new[]
                    {
                        new DriverBinding(0x3A08, 0, "obsolete V2 interface 0 (MI_00)")
                    }),
                new DriverVariant(
                    "v1",
                    "SCUF PC Controller Dongle V1",
                    "ZeroCue SCUF PC Controller Dongle V1",
                    ScufVendorId,
                    true,
                    new[]
                    {
                        new DriverBinding(0x434E, 4, "experimental V1 interface 4 (MI_04)"),
                        new DriverBinding(0x434E, 3, "experimental V1 interface 3 (MI_03)"),
                        new DriverBinding(0x5046, null, "experimental V1 active receiver device")
                    },
                    Array.Empty<DriverBinding>())
            });

        public static DriverTargetDefinition For(DriverTarget target) =>
            target == DriverTarget.Receiver ? Receiver : Gamepad;

        public static IEnumerable<DriverTargetDefinition> All
        {
            get
            {
                yield return Gamepad;
                yield return Receiver;
            }
        }
    }
}
