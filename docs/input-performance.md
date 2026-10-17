# Input performance validation

ZeroCue forwards physical input to a virtual Xbox controller. A polling-rate
tester measures distinct virtual states visible through XInput, rather than the
USB link speed or full button-to-screen latency. Move a stick continuously while
measuring; an idle stick does not provide enough distinct states.

## Changes under test

- Wireless auxiliary input reads no longer sleep after each successful packet.
  Short `Task.Delay` calls can take a full Windows timer tick instead of the
  requested number of milliseconds. Reads themselves provide the pacing.
- ACK wait loops immediately resume reading after receiving unrelated input on
  a shared wireless endpoint. Command timeouts and failure recovery remain.
- Wired primary and auxiliary endpoints have separate dedicated reader workers.
  The primary input reader does not acquire the control/ACK lock. Auxiliary
  reads remain serialized with command ACKs on their own shared endpoint.
- A bounded auxiliary queue preserves digital state transitions in order. The
  consumer applies one transition per primary report, so queued presses and
  releases are not collapsed into one virtual state. Duplicate states are
  suppressed independently for the two auxiliary report types; non-input
  telemetry and incomplete input reports do not enter the queue.
- Disconnect waits for both wired readers before releasing USB resources.
  Queue backpressure is cancellable and never retains the control/ACK lock.
- XInput diagnostic read-back runs at most every 50 ms, independently of virtual
  report submission. It retains the existing persistent-mismatch diagnostics.
- Disabled wireless packet capture skips frame formatting, copying and writes.
- The wireless analog gate uses high-resolution `Stopwatch` timestamps rather
  than `Environment.TickCount64`. The latter can advance in roughly 15.6 ms
  steps on Windows, accidentally turning the intended 4 ms interval into a
  roughly 64 Hz limit. A local isolated 500 ms timing probe reproduced 64 Hz
  with the old clock and approximately 248 Hz with the high-resolution clock.
  This probe measures the gate alone, not hardware throughput.
- Every changed wireless state now passes immediately, including stick and
  trigger changes. Applying a 4 ms minimum to new states could drop alternating
  packets when a nominally 4 ms stream arrived with small timing variations.
  Only identical states are throttled, while periodic processing of held states
  remains available for timed gestures, macros and paddle expiry.
- `[WIRELESS-PERF]` logs aggregate normalized and processed input rates every
  10 seconds while connected, without logging each packet's payload.
- `[OUTPUT-PERF]` logs virtual report calls and exact final state changes every
  10 seconds, after curves, deadzones and remapping. It also reports average and
  maximum ViGEm submission duration and maximum time between submissions.
  Wireless output is submitted before scheduling the UI frame refresh.
- `[RADIO-PERF]` counts normalized radio states and distinct stick/trigger
  changes before curves, deadzones, mappings and runtime G-key overlays. It
  records average/max arrival gaps and counts arrivals less than 1 ms apart or
  more than 8 ms apart. Counts exclude runtime G-key-only overlay reports.
- `[USB-READ-PERF]` separates thread-pool queue delay from pipe setup/native read
  time. The pipe ID identifies the transport: in dual-interface mode, `0x81`
  carries radio input and `0x82` carries control/ACK traffic. I/O timings include
  timeouts, so slow idle/control reads alone do not imply delayed gameplay input.
- `[RADIO-PUMP-PERF]` measures the auxiliary reader's completion-to-continuation
  delay and observer duration. `[PROCESS-PERF]` separately measures waiting for
  the shared input processing lock and time inside normalization/remapping.

The wireless duplicate processing interval remains 4 ms; changed analog and
digital states bypass that interval. UI refresh throttling is separate from
virtual output. These changes do not promise a specific polling rate.

## Automated checks (Windows, .NET SDK from global.json)

```powershell
dotnet run --project tests/ZeroCue.InputChecks/ZeroCue.InputChecks.csproj --configuration Release
```

These checks use scripted endpoint readers against the actual service loops.
They never start device discovery, construct the service singleton, change
drivers, or load the user's settings. They cover primary progress while the
command lock is held, auxiliary ordering and filtering, bounded-queue
cancellation, command-lock availability during backpressure, an absent optional
endpoint, disabled capture, analog arrival jitter, immediate digital and trigger
changes, high-resolution duplicate gating, and session reset. They do not measure hardware throughput or
exercise the native USB/ViGEm drivers.

## Hardware comparison

1. Keep the controller, connection mode, tester, mapping profile and stick
   settings the same for both builds. Close one ZeroCue build before starting
   the other; do not run two instances together.
2. Record the existing build's XInput rate and average/max update intervals over
   three 30-second runs, moving a stick continuously. Repeat with the local
   performance build. Test cable and receiver separately.
3. For wireless, note `topology=dual-interface` or `topology=unified-active` in
   the communication log. The removed auxiliary pause applies to the former;
   ACK-draining improvements apply to shared control/input reads.
4. Verify G1â€“G5, paddles, SAX, regular buttons, triggers, sticks, shifted mappings
   and macros, including short taps and release back to neutral.
5. While moving a stick, change brightness/RGB and test rumble. Check for stuck
   inputs, freezes or persistent `[VIGEM-XINPUT]` warnings.
6. Disconnect/reconnect the controller, switch between cable and receiver, and
   exit the app while input is active. Verify clean shutdown and recovery.

Record rates from the tester alongside the build and connection topology.
After at least 10 seconds connected, also record the `[WIRELESS-PERF]` lines in
the communication log. `normalizedHz` counts normalized controller frames and
`processedHz` counts frames admitted to mapping/gesture processing. They include
duplicate held-state reports and runtime G-key overlays, so neither value is
the same as distinct virtual states observed by XInput. A gap between these
rates can help distinguish duplicate suppression from slow input arrival.
Build success and hardware-free checks alone cannot establish the real polling
rate or guarantee firmware-specific behavior.

Compare `[RADIO-PERF] leftStickChangedHz` and `[OUTPUT-PERF] leftStickChangedHz`
with a native tester during the same left-stick movement. The chrizonix
XInputTest source measures changes in the left stick only; total `changedHz`
also includes other controls and is not an equivalent comparison. Left and
right stick rates are counted separately in both diagnostic stages. `reportsHz` includes calls with identical output; `changedHz` counts
transitions in the final button, trigger and axis values. Successful submission
does not guarantee that another process samples every transition, particularly
when reports arrive in bursts. A low `changedHz` despite faster input requires
checking repeated physical values and the mapping profile before attributing
the gap to USB or ViGEm. Submit durations and inter-report gaps help locate
blocking or scheduling delays.

For the next diagnostic capture, leave the build connected wirelessly and move
one stick continuously in circles for at least 30 seconds after connection is
complete. Avoid holding the stick still or repeatedly crossing the center
deadzone. Keep the profile and tester unchanged and avoid driver changes or
connection switching during the capture. Compare radio stick changes with final
output changes, then inspect queue, continuation, lock-wait and processing times.
The counters locate software delays and repeated states; they do not prove the
receiver's ultimate hardware/firmware capability.

## Browser versus native XInput measurements

ControllerTest.io's polling page counts Gamepad API timestamp changes in a
MessageChannel loop, or WebHID `inputreport` events when that mode is selected.
Its browser-visible frequency is not a direct XInputGetState measurement. The
same website measuring iCUE faster than ZeroCue is a useful comparison, but
alone does not identify whether the difference is in physical input, virtual
output delivery or the browser's handling of those devices.

Use the read-only [native probe](../tools/ZeroCue.XInputProbe/README.md) with
ZeroCue connected wirelessly to measure actual XInput state changes. Keep the
profile, connection and movement consistent. Match slots and time windows;
compare left-stick rates separately from aggregate state changes. This also
avoids treating browser refresh limits as an established diagnosis without
direct measurement.
