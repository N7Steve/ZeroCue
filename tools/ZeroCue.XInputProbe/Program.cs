using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Contains("--self-test"))
{
    if (Marshal.SizeOf<Native.State>() != 16 || Marshal.SizeOf<Native.Gamepad>() != 12)
        throw new Exception("Unexpected XInput ABI structure sizes.");
    var check = new ProbeCounter();
    var state = new Native.State();
    check.Observe(state, 100);
    state.Gamepad.LeftX = 1;
    state.PacketNumber = 1;
    check.Observe(state, 200);
    check.Observe(state, 300);
    state.Gamepad.RightY = 2;
    state.Gamepad.LeftTrigger = 3;
    state.PacketNumber = 2;
    check.Observe(state, 400);
    state.Gamepad.LeftY = 4;
    state.PacketNumber = 4;
    check.Observe(state, 500);
    if (check.Changes != 3 || check.LeftChanges != 2 || check.RightChanges != 1 ||
        check.TriggerChanges != 1 || check.PacketChanges != 3 || check.MissedPacketNumbers != 1 ||
        check.LeftIntervals != 1 || check.LeftIntervalTicks != 300)
        throw new Exception("Probe counter self-test failed.");
    Console.WriteLine("PASS: exact changes, duplicates, per-stick counts, interval timing and packet-number gaps.");
    return;
}

var connected = new List<uint>();
for (uint slot = 0; slot < 4; slot++)
    if (Native.XInputGetState(slot, out _) == 0) connected.Add(slot);
Console.WriteLine("Read-only XInput 1.4 probe. No driver, mapping or vibration changes.");
Console.WriteLine("Connected XInput slots: " + (connected.Count == 0 ? "none" : string.Join(", ", connected)));
if (args.Contains("--list")) return;

int duration = 30;
int durationOption = Array.IndexOf(args, "--duration");
if (durationOption >= 0 && durationOption + 1 < args.Length && int.TryParse(args[durationOption + 1], out var requestedDuration))
    duration = Math.Clamp(requestedDuration, 1, 120);

if (connected.Count == 0)
{
    Console.WriteLine("Connect ZeroCue wirelessly first, then run this probe again.");
    if (!Console.IsInputRedirected) Console.ReadLine();
    return;
}

Console.WriteLine($"Measuring for {duration} seconds after a 5-second preparation period.");
Console.WriteLine("Move the LEFT stick continuously in circles, away from the center deadzone.");
Console.WriteLine("Keep the same profile and wireless connection. Ctrl+C stops and saves a partial capture.");
for (int countdown = 5; countdown > 0; countdown--)
{
    Console.WriteLine($"Starting in {countdown}...");
    Thread.Sleep(1000);
}

bool cancelled = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
var counters = connected.ToDictionary(slot => slot, _ => new ProbeCounter());
var lines = new List<string>
{
    $"XInput read-only capture {DateTimeOffset.Now:O}",
    "API=xinput1_4.dll XInputGetState; sampleTargetHz=2000; no input writes",
    "Rates count observed state transitions. Packet-number gaps can indicate transitions missed between samples."
};
var watch = Stopwatch.StartNew();
long nextPoll = Stopwatch.GetTimestamp();
long pollStep = Math.Max(1, Stopwatch.Frequency / 2000);
int nextSecond = 1;
var previousLeftCounts = connected.ToDictionary(slot => slot, _ => 0L);
long previousWindowTicks = 0;

while (!cancelled && watch.Elapsed.TotalSeconds < duration)
{
    foreach (uint slot in connected)
    {
        var counter = counters[slot];
        if (Native.XInputGetState(slot, out var state) == 0)
            counter.Observe(state, Stopwatch.GetTimestamp());
        else
            counter.Failures++;
    }

    if (watch.Elapsed.TotalSeconds >= nextSecond)
    {
        long windowTicks = watch.ElapsedTicks;
        double seconds = (windowTicks - previousWindowTicks) / (double)Stopwatch.Frequency;
        foreach (uint slot in connected)
        {
            var counter = counters[slot];
            double leftHz = (counter.LeftChanges - previousLeftCounts[slot]) / seconds;
            var line = FormattableString.Invariant($"t={watch.Elapsed.TotalSeconds:F1}s slot={slot} leftChangesHz={leftHz:F1}");
            Console.WriteLine(line);
            lines.Add(line);
            previousLeftCounts[slot] = counter.LeftChanges;
        }
        previousWindowTicks = windowTicks;
        nextSecond = (int)watch.Elapsed.TotalSeconds + 1;
    }

    // QPC-based pacing avoids Windows' coarse sleep clock. The probe uses one
    // CPU core temporarily, like native polling testers, and never writes input.
    nextPoll += pollStep;
    long now = Stopwatch.GetTimestamp();
    if (nextPoll < now) nextPoll = now;
    while (!cancelled && Stopwatch.GetTimestamp() < nextPoll) Thread.SpinWait(16);
}

double elapsed = watch.Elapsed.TotalSeconds;
foreach (uint slot in connected)
{
    var counter = counters[slot];
    double avgLeftInterval = counter.LeftIntervals == 0 ? 0 :
        counter.LeftIntervalTicks * 1000.0 / Stopwatch.Frequency / counter.LeftIntervals;
    var summary = FormattableString.Invariant($"SUMMARY slot={slot} seconds={elapsed:F3} samplesHz={counter.Samples / elapsed:F1} allChangesHz={counter.Changes / elapsed:F1} leftChangesHz={counter.LeftChanges / elapsed:F1} rightChangesHz={counter.RightChanges / elapsed:F1} triggerChangesHz={counter.TriggerChanges / elapsed:F1} packetChangesHz={counter.PacketChanges / elapsed:F1} avgLeftIntervalMs={avgLeftInterval:F3} maxLeftIntervalMs={counter.MaximumLeftIntervalTicks * 1000.0 / Stopwatch.Frequency:F3} missedPacketNumbers={counter.MissedPacketNumbers} failures={counter.Failures}");
    Console.WriteLine(summary);
    lines.Add(summary);
}
string resultPath = Path.Combine(AppContext.BaseDirectory, $"xinput-results-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
File.WriteAllLines(resultPath, lines);
Console.WriteLine("Saved: " + resultPath);
if (!Console.IsInputRedirected)
{
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
}

sealed class ProbeCounter
{
    private Native.State _previous;
    private bool _started;
    private long _previousLeftChangeTimestamp;
    public long Samples, Changes, LeftChanges, RightChanges, TriggerChanges, PacketChanges, Failures;
    public long MissedPacketNumbers, LeftIntervals, LeftIntervalTicks, MaximumLeftIntervalTicks;

    public void Observe(Native.State state, long timestamp)
    {
        Samples++;
        if (!_started)
        {
            _started = true;
            _previous = state;
            return;
        }
        bool left = state.Gamepad.LeftX != _previous.Gamepad.LeftX || state.Gamepad.LeftY != _previous.Gamepad.LeftY;
        bool right = state.Gamepad.RightX != _previous.Gamepad.RightX || state.Gamepad.RightY != _previous.Gamepad.RightY;
        bool triggers = state.Gamepad.LeftTrigger != _previous.Gamepad.LeftTrigger || state.Gamepad.RightTrigger != _previous.Gamepad.RightTrigger;
        if (left || right || triggers || state.Gamepad.Buttons != _previous.Gamepad.Buttons) Changes++;
        if (left)
        {
            LeftChanges++;
            if (_previousLeftChangeTimestamp != 0)
            {
                long interval = timestamp - _previousLeftChangeTimestamp;
                LeftIntervals++;
                LeftIntervalTicks += interval;
                MaximumLeftIntervalTicks = Math.Max(MaximumLeftIntervalTicks, interval);
            }
            _previousLeftChangeTimestamp = timestamp;
        }
        if (right) RightChanges++;
        if (triggers) TriggerChanges++;
        if (state.PacketNumber != _previous.PacketNumber)
        {
            PacketChanges++;
            uint delta = unchecked(state.PacketNumber - _previous.PacketNumber);
            // Ignore driver resets/backwards jumps rather than treating them as
            // billions of lost samples. Consecutive numbering is driver-specific.
            if (delta > 1 && delta < 100000) MissedPacketNumbers += delta - 1;
        }
        _previous = state;
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    public static extern uint XInputGetState(uint dwUserIndex, out State pState);
}
