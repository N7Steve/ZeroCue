# ZeroCue — agent guide

## What this is

Windows x64 app (.NET 8, Avalonia 11) that reads a SCUF Envision controller over
WinUSB (cable or RF receiver), exposes it as a virtual Xbox 360 pad through
ViGEmBus and applies remaps/macros. It installs WinUSB drivers with elevation.
Mistakes here mean stuck keys on the user's PC, an unusable controller or a
broken driver binding — treat device I/O, threading and driver code as
safety-critical.

## Commands

- Restore: `dotnet restore ZeroCue.sln --locked-mode` (update lock files only when you change packages)
- Build: `dotnet build ZeroCue.sln -c Release` — warnings are errors (`Directory.Build.props`)
- Unit tests: `dotnet test tests/ZeroCue.Tests/ZeroCue.Tests.csproj -c Release`
- Hardware-free transport checks: `dotnet run --project tests/ZeroCue.InputChecks/ZeroCue.InputChecks.csproj -c Release`
- Repository checks: `scripts/verify-runtime-assets.ps1`, `scripts/verify-driver-safety.ps1`

Run build + both test suites + the scripts before declaring work done.

## Code map (`ZeroCue.DataProbe/`, the app project; the name is historical)

- `Services/Drivers/` — driver install/restore.
  - `DriverCatalog` is the single source of truth for VID/PID/interface bindings.
  - `InfClassifier` decides package ownership from INF content (exact hardware-ID tokens).
  - `DriverOperationEngine` is the install/restore logic behind `IDriverSystem` (fakeable).
  - `ElevatedDriverHelper` is the only elevated code path (`zerocue.exe --driver-helper …`).
  - `DriverAutomationService` (in `Services/`) is the unelevated side: UAC launch, named-pipe log/result, iCUE relaunch.
- `Services/Pnp/` — CfgMgr32 enumeration (`PnpQuery`), presence evaluation
  (`ScufPresenceSnapshot`: Ready / Settling / MissingDriver / DeviceError) and
  hot-plug notifications (`DevicePresenceMonitor`).
- `Services/ScufDeviceService*.cs` — singleton device service split in partials:
  connection loop (`.Usb.cs`), wireless session (`.Wireless.cs`, `.WirelessInput.cs`),
  remapping/gestures/macros (`.RemapGestures.cs`), ViGEm/rumble (`.Emulation.cs`), profiles.
- `Services/WirelessDongleWinUsbTransport.cs` + `WinUsbInterop.cs` — WinUSB transport.
- `ViewModels/MainViewModel.cs` — the (oversized) main view model.
- `docs/audit/` — technical audit and the phased refactor plan. Read it before structural work.

## Non-negotiable rules

1. **Never run driver operations, wdi-simple, pnputil or anything elevated, and never
   write to the real controller.** Ask the user to test with hardware.
2. **Elevation:** only `ElevatedDriverHelper` runs elevated. The unelevated process must
   never write anything the elevated side executes. wdi-simple is hash-verified in memory
   (`WdiSimpleSha256`, keep in sync with `scripts/verify-runtime-assets.ps1`).
3. **Driver packages are removed only after `InfClassifier` classifies them as owned.**
   Never match hardware IDs with substrings or regexes outside `InfClassifier`.
4. **Device identities live in `DriverCatalog` (bindings) and `SupportedScufDeviceProfile`
   (protocol).** `DriverCatalogTests` keeps them consistent; don't duplicate VID/PIDs elsewhere.
5. **Threads:** no `.Wait()`/`.Result` on async code from the UI thread (wrap in `Task.Run`
   if a sync API is unavoidable); no `async void` except UI event handlers with try/catch;
   no fire-and-forget without a cancellation token owned by the session.
6. **Native handles:** never free a WinUSB/libusb handle while I/O can be in flight —
   cancel/abort first, wait for workers, then free (see the lifetime gate in the transport).
7. **Every teardown path calls `ReleaseAllSyntheticOutputs`.** A new exit path without it
   leaves keys held down in Windows.
8. **Profiles:** schema changes bump `FormatVersion` and need a migration plus a test.

## Conventions

- New code, identifiers, comments and logs in English; UI text only through
  `LocalizationService` with both `en` and `es` keys.
- Log with a category prefix (`[USB]`, `[DETECT]`, `[DRIVER]`, `[INPUT]`…). Never log per
  packet on the hot path; use the `*-PERF` aggregate trackers.
- Time measurements: `Stopwatch`, not `Environment.TickCount64` or `DateTime.Now`.
- Prefer small focused classes; do not grow `MainViewModel` or `ScufDeviceService` further —
  extract instead.
- Put new testable logic behind interfaces and cover it in `tests/ZeroCue.Tests`
  (xUnit). Use fakes (see `FakeDriverSystem`) instead of real devices.

## Working method

- Fix root causes. For concurrency/lifetime bugs, reproduce with a fake first.
- Report clearly what still needs hardware validation (wired V2, receiver V2 base/active,
  experimental V1) — do not claim device behaviour you could not observe.
- Do not commit build artifacts; `artifacts/` is local output only.

## Hardware validation checklist (for the human)

Cable and receiver separately: connect, hot-unplug, switch cable↔receiver, G1–G5, paddles,
SAX, D-pad diagonals, macros (tap/hold/repeat), Shift, profile switch while a button is held,
closing the app while a mapped key is held (nothing may stay pressed), rumble, RGB.
Drivers: install and restore for controller and receiver, with iCUE open and closed.
