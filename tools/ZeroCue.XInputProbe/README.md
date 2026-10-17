# ZeroCue XInput Probe

This Windows x64 console utility reads `XInputGetState` directly from
`xinput1_4.dll`. It does not send input, vibration or driver commands, or change
ZeroCue settings. It writes a timestamped results text file beside its executable.

1. Connect the controller wirelessly through ZeroCue and wait for it to connect.
2. Run `ZeroCue.XInputProbe.exe`. During the five-second countdown, prepare to
   move the **left stick** continuously in circles away from the center deadzone.
3. Keep moving for the 30-second measurement, without switching profiles or
   connections. Results appear once per second and are saved beside the tool.
4. Compare the matching slot's `leftChangesHz`, `allChangesHz` and interval
   measurements with ZeroCue's logs from the same capture.

All connected XInput slots are measured. If none are present at launch, connect
ZeroCue and launch the utility again. The browser can remain closed during this
native measurement; this isolates the game-facing XInput path from browser APIs.

The nominal read rate is 2000 samples/s, paced by QPC rather than millisecond
sleeps. This temporarily uses one CPU core, as native polling-rate testers do.
The reported `samplesHz` shows the actual read rate achieved. Observed changes
are not the USB transfer rate or button-to-screen latency. Packet-number gaps
are diagnostic only because numbering behavior is driver-specific.

`--list` only lists connected slots. `--duration N` selects 1–120 seconds.
`--self-test` verifies counters, duplicate suppression, per-stick changes,
packet-number gaps, interval calculations and native structure sizes without
accessing a controller.

## Reproduce the build

Use the repository's pinned .NET SDK:

```powershell
dotnet run --project tools/ZeroCue.XInputProbe/ZeroCue.XInputProbe.csproj -c Release -- --self-test
dotnet publish tools/ZeroCue.XInputProbe/ZeroCue.XInputProbe.csproj -c Release -o artifacts/xinput-probe
```
