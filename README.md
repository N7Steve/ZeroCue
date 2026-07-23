# ZeroCue

ZeroCue is a free, experimental Windows application for using a SCUF Envision
Pro without keeping Corsair iCUE active. It reads the controller in wired or
wireless-receiver mode, exposes an Xbox-compatible virtual controller, and lets
you manage mappings and device settings from a single interface.

> [!WARNING]
> ZeroCue is alpha software. ViGEmBus must be installed before installing the
> ZeroCue WinUSB driver. ZeroCue replaces the Windows driver for the selected SCUF
> interfaces, so without ViGEmBus the physical Xbox-compatible controller
> disappears and ZeroCue cannot create the virtual Xbox 360 replacement.
> Installing the wired controller driver, the wireless receiver driver, or both
> requires administrator privileges. While a ZeroCue driver is installed, iCUE
> cannot detect the controller through those interfaces.

> [!CAUTION]
> Hardware compatibility is currently guaranteed only for the SCUF Envision Pro
> V2. Wired SCUF Envision V2 devices using `VID_1B1C / PID_3A04` are detected as
> an experimental, unvalidated profile that reuses the Pro V2 protocol. The SCUF
> Envision Pro V1 has not been tested. ZeroCue is early alpha software: bugs,
> failed connections, crashes, and unexpected behavior may occur, and reliable
> operation is not guaranteed.

ZeroCue is an independent project and is not affiliated with or endorsed by
Corsair or SCUF Gaming.

<img width="1703" height="984" alt="image" src="https://github.com/user-attachments/assets/191d9456-e69b-4c07-9940-c2e11104ffaa" />

## Current features

- Wired and wireless-receiver controller communication.
- Xbox-compatible virtual controller output through ViGEm.
- Face-button, paddle, G-key, keyboard, mouse, macro, and shifted mappings.
- Per-profile stick curves, deadzones, trigger curves, rumble, RGB, eco mode,
  and automatic application linking.
- Portable, self-contained Windows x64 build: no separate .NET installation.
- Two centralized diagnostic logs beside the executable: communication and
  input/mapping activity.
- Restricted driver recovery based on packages created and recorded by ZeroCue.

## Requirements

- Windows 10 or Windows 11 on an x64 PC.
- [ViGEmBus 1.22.0](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0)
  installed and working. ZeroCue includes the ViGEm client library, but it does
  not include or install the ViGEmBus system driver.

ViGEmBus is what allows ZeroCue to expose the SCUF controller as a virtual Xbox
360 controller. Without it, ZeroCue may still read the physical controller after
WinUSB is installed, but games and gamepad testers will not see an Xbox
controller. ZeroCue checks this requirement at startup and displays a warning if
ViGEmBus is missing or unavailable.

You can verify the installation in Windows Device Manager by selecting **View >
Devices by connection** and looking for **Nefarius Virtual Gamepad Emulation
Bus**. Its device status should report that it is working correctly. See the
[official ViGEmBus installation and troubleshooting guide](https://docs.nefarius.at/projects/ViGEm/How-to-Install/).

## Install an alpha build

1. Install ViGEmBus 1.22.0 from the official release linked above. Restart
   Windows if the installer requests it.
2. Download the portable ZeroCue ZIP from the GitHub release.
3. Extract the complete ZIP to a normal writable folder. Do not run the
   executable from inside the ZIP.
4. Run `zerocue.exe`. If ZeroCue reports that ViGEmBus is unavailable, do not
   install WinUSB until ViGEmBus is installed and working.
5. Connect the controller by cable or its supported receiver.
6. Install the required driver from ZeroCue: choose the controller driver for a
   wired USB connection or the receiver driver for a wireless connection. Install
   both if you plan to use both connection modes. Windows will show an
   administrator prompt. ZeroCue closes iCUE during the operation and reopens it
   when the operation ends.
7. After installation, iCUE will no longer detect the controller through the
   modified connection mode. Restore the original driver before using that mode
   with iCUE again.

Windows SmartScreen may warn about the unsigned alpha. Verify the SHA-256 shown
in the GitHub release notes before running it. Never download a build from an
unofficial mirror.

## Driver safety and recovery

ZeroCue records the driver packages it installs and its restore option only
removes verified packages for the selected SCUF controller or receiver. Restoring
the original driver allows iCUE to detect the device again.

ZeroCue is not required for recovery. You can use Windows Device Manager as an
administrator, locate only the SCUF controller or receiver interfaces modified by
ZeroCue, choose **Uninstall device**, and select the option to remove the driver
when Windows offers it. Then disconnect and reconnect the device, or reinstall
iCUE, so Windows can restore the original driver. Do not uninstall unrelated USB
devices or delete files manually from `C:\Windows\INF`.

## Files created at runtime

- Settings, profiles, and driver manifests: `%APPDATA%\ZeroCue`
- Communication log: `logs\zerocue-communication.log` beside `zerocue.exe`
- Input and mapping log: `logs\zerocue-input-mappings.log` beside `zerocue.exe`

Logs can contain device identifiers and local diagnostic details. Review and
redact them before posting publicly.

## Reporting bugs

If ZeroCue fails, disconnects, or behaves unexpectedly, please
[open a GitHub issue](https://github.com/N7Steve/ZeroCue/issues/new) and attach
both logs listed above so the problem can be analyzed and fixed. Include the
exact controller version, connection mode, Windows version, and the steps that
triggered the problem. Review the logs first and redact any identifiers or local
details you do not want to share publicly.

## Alpha limitations

- Only Windows x64 is supported.
- `wdi-simple.exe` is the existing unsigned alpha helper. It is pinned by
  SHA-256, but its exact historical build recipe has not been reconstructed.
- The application executable is not yet Authenticode-signed.

See [SECURITY.md](SECURITY.md) for private vulnerability reporting.

## License

ZeroCue is licensed under the [Apache License 2.0](LICENSE). You may use, modify,
and redistribute it, including commercially. Redistributed modifications must
retain the license and attribution notices and must clearly state which files
were changed. See [NOTICE](NOTICE) and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for attribution and dependency
terms.
