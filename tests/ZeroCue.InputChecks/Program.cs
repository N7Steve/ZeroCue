using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using ZeroCue.DataProbe.Services;

// Hardware-free checks against the real input loops. Never construct the singleton:
// its constructor starts device discovery and loads the user's profiles.
const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}

static ScufDeviceService CreateService()
{
    var service = (ScufDeviceService)RuntimeHelpers.GetUninitializedObject(typeof(ScufDeviceService));
    typeof(ScufDeviceService).GetField("UsbLock")!.SetValue(service, new SemaphoreSlim(1, 1));
    typeof(ScufDeviceService).GetField("_activeGKeyTargets", PrivateInstance)!
        .SetValue(service, new HashSet<string>());
    return service;
}

static void Invoke(ScufDeviceService service, string name, params object?[] arguments)
{
    try { typeof(ScufDeviceService).GetMethod(name, PrivateInstance)!.Invoke(service, arguments); }
    catch (TargetInvocationException ex) { throw ex.InnerException!; }
}

static Channel<byte[]> Queue(int capacity = 128) => Channel.CreateBounded<byte[]>(
    new BoundedChannelOptions(capacity)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });

static byte[] GKey(byte state) => new byte[] { 0x11, state };
static byte[] Paddle(byte state) => new byte[] { 0x03, 0, 0x02, 0, 0, state, 0 };

// Holding the command/ACK lock must not prevent primary reports from arriving.
{
    var service = CreateService();
    using var cancellation = new CancellationTokenSource();
    var queue = Queue();
    var primary = new FakeReader();
    service.UsbLock.Wait();
    var worker = Task.Factory.StartNew(
        () => Invoke(service, "PollingLoop", primary, queue.Reader, cancellation.Token),
        TaskCreationOptions.LongRunning);
    var progressed = SpinWait.SpinUntil(() => Volatile.Read(ref primary.Reads) >= 20, 3000);
    cancellation.Cancel();
    service.UsbLock.Release();
    Check(worker.Wait(2000), "Primary reader stops on cancellation");
    Check(progressed, "Primary input continues while the command/ACK lock is held");
}

// The auxiliary worker filters telemetry and malformed frames, deduplicates
// each report type independently, and preserves press/release ordering.
{
    var service = CreateService();
    using var cancellation = new CancellationTokenSource();
    var queue = Queue();
    var auxiliary = new FakeReader(new[]
    {
        GKey(1), Paddle(0x20), GKey(1), Paddle(0x20),
        new byte[] { 0x11, 1, 0xAA },
        new byte[] { 0x03, 0, 0x02, 0xAA, 0xBB, 0x20, 0 },
        new byte[] { 0x03, 0, 0x0A, 0, 0, 0, 0 },
        new byte[] { 0x11 }, new byte[] { 0x03, 0, 0x02 },
        GKey(0), Paddle(0)
    });
    var worker = Task.Factory.StartNew(
        () => Invoke(service, "PollAuxiliaryInput", auxiliary, queue.Writer, cancellation.Token),
        TaskCreationOptions.LongRunning);
    Check(SpinWait.SpinUntil(() => Volatile.Read(ref auxiliary.Reads) >= 12, 3000),
        "Auxiliary reader consumes the scripted frames");
    cancellation.Cancel();
    Check(worker.Wait(2000), "Auxiliary reader stops on cancellation");
    var actual = new List<byte[]>();
    while (queue.Reader.TryRead(out var frame)) actual.Add(frame);
    var expected = new[] { GKey(1), Paddle(0x20), GKey(0), Paddle(0) };
    Check(actual.Count == expected.Length && actual.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second)),
        "Press/release order survives telemetry, malformed input and duplicates with changing reserved bytes");
    Check(queue.Reader.Completion.IsCompletedSuccessfully, "Auxiliary queue completes after the reader stops");
}

// A full queue must apply backpressure outside UsbLock and remain cancellable.
{
    var service = CreateService();
    using var cancellation = new CancellationTokenSource();
    var queue = Queue(1);
    var auxiliary = new FakeReader(new[] { GKey(1), GKey(0) });
    var worker = Task.Factory.StartNew(
        () => Invoke(service, "PollAuxiliaryInput", auxiliary, queue.Writer, cancellation.Token),
        TaskCreationOptions.LongRunning);
    Check(SpinWait.SpinUntil(() => Volatile.Read(ref auxiliary.Reads) >= 2, 3000),
        "Auxiliary reader reaches a full queue");
    var lockAvailable = service.UsbLock.Wait(1000);
    if (lockAvailable) service.UsbLock.Release();
    cancellation.Cancel();
    Check(worker.Wait(2000), "Cancellation releases a reader blocked by queue backpressure");
    Check(lockAvailable, "Queue backpressure does not retain the command/ACK lock");
}

{
    var service = CreateService();
    var queue = Queue();
    Invoke(service, "PollAuxiliaryInput", null, queue.Writer, CancellationToken.None);
    Check(queue.Reader.Completion.IsCompletedSuccessfully, "A missing optional auxiliary endpoint completes cleanly");
}

{
    using var logger = new WirelessWinUsbInputMapLogger(_ => throw new Exception("Disabled logger callback"), false);
    logger.ObserveFrame(new byte[] { 0x06, 1, 2, 3 }, 4);
    logger.LogStop(1, 0);
    var previous = (byte[])typeof(WirelessWinUsbInputMapLogger)
        .GetField("_lastFrame", PrivateInstance)!.GetValue(logger)!;
    Check(previous.Length == 0, "Disabled wireless capture skips per-frame formatting, copying and logging");
}

// Inject timestamps into the real wireless gate: changed analog packets near
// the old 4 ms boundary must never be discarded because of arrival jitter.
{
    var service = CreateService();
    var gate = typeof(ScufDeviceService).GetMethod("ShouldProcessWirelessInputFrame", PrivateInstance)!;
    var frame = new byte[16];
    frame[0] = 0x06;
    frame[11] = 0x80;
    long Timestamp(double milliseconds) => Stopwatch.Frequency + (long)Math.Round(Stopwatch.Frequency * milliseconds / 1000);
    bool Accept(double milliseconds) => (bool)gate.Invoke(service, new object[] { frame, Timestamp(milliseconds) })!;

    int accepted = 0;
    for (int index = 0; index < 50; index++)
    {
        frame[1]++; // New analog position every nominally 4 ms, with jitter.
        if (Accept(index * 4 + (index % 2 == 0 ? 0 : -0.2))) accepted++;
    }
    Check(accepted == 50, "Wireless forwards every changed analog state despite 4 ms arrival jitter");
    Check(!Accept(196), "Wireless suppresses immediate duplicate states");
    Check(Accept(200), "Wireless reprocesses unchanged held states for timed gestures and macros");
    frame[12] = 1;
    Check(Accept(201), "Wireless digital press bypasses duplicate throttling");
    frame[12] = 0;
    Check(Accept(202), "Wireless digital release bypasses duplicate throttling");
    frame[9] = 1;
    Check(Accept(203), "Wireless trigger changes bypass duplicate throttling");
    Check(!Accept(205) && Accept(207), "Wireless duplicate timing uses high-resolution timestamps");

    var reset = typeof(ScufDeviceService).GetMethod("ResetWirelessRuntimeInputState", PrivateInstance)!;
    typeof(ScufDeviceService).GetField("_wirelessInputProcessingLock", PrivateInstance)!.SetValue(service, new object());
    typeof(ScufDeviceService).GetField("_wirelessInputFrameLock", PrivateInstance)!.SetValue(service, new object());
    reset.Invoke(service, null);
    Check(Accept(207.1), "A new wireless session immediately processes its first state");
}

// Diagnose processing rate versus distinct final output without native ViGEm.
{
    var trackerType = typeof(ScufDeviceService).Assembly
        .GetType("ZeroCue.DataProbe.Services.OutputPerformanceTracker")!;
    var tracker = Activator.CreateInstance(trackerType, "Wireless")!;
    var observe = trackerType.GetMethod("Observe")!;
    long Timestamp(double seconds) => Stopwatch.Frequency + (long)(Stopwatch.Frequency * seconds);
    string? Observe(ulong digital, ulong axes, double seconds) => (string?)observe.Invoke(tracker,
        new object[] { digital, axes, Timestamp(seconds), Stopwatch.Frequency / 1000 });
    var initial = Observe(1, 2, 0);
    var duplicate = Observe(1, 2, 2.5);
    var axesChanged = Observe(1, 3, 5);
    var digitalChanged = Observe(2, 3, 7.5);
    Check(initial == null && duplicate == null && axesChanged == null && digitalChanged == null,
        "Output telemetry formats no summaries before the 10 second window");
    var summary = Observe(2, 3, 10)!;
    Check(summary.Contains("reports=5 changes=3"),
        "Output telemetry distinguishes repeated reports from exact digital/axis changes");
    Check(summary.Contains("leftStickChangedHz=0.2 rightStickChangedHz=0.1"),
        "Output telemetry isolates left/right stick rates from button changes");
    Check(summary.Contains("avgSubmitMs=1.000 maxSubmitMs=1.000"),
        "Output telemetry reports average and maximum submit duration");
    Check(summary.Contains("maxGapMs=2500.000"),
        "Output telemetry measures gaps between report submissions");
    Check(Observe(2, 3, 20)!.Contains("reports=1 changes=0"),
        "Output telemetry preserves the previous state across aggregation windows");
}

// Distinguish repeated radio states, stick movement and trigger movement. The
// D-pad shares a packed byte with triggers and must not count as trigger motion.
{
    var type = typeof(ScufDeviceService).Assembly.GetType("ZeroCue.DataProbe.Services.RadioPerformanceTracker")!;
    var tracker = Activator.CreateInstance(type)!;
    var observe = type.GetMethod("Observe")!;
    var frame = new byte[16];
    frame[0] = 0x06;
    frame[11] = 0x80;
    string? Observe(double ms) => (string?)observe.Invoke(tracker,
        new object[] { frame, Stopwatch.Frequency + (long)Math.Round(Stopwatch.Frequency * ms / 1000) });
    Check(Observe(0) == null && Observe(0.5) == null, "Radio telemetry does not log every packet");
    frame[1] = 1;
    Observe(4);
    frame[12] = 1;
    Observe(13);
    frame[11] = 0x20; // D-pad only, trigger bits unchanged.
    Observe(17);
    frame[9] = 1;
    Observe(21);
    var summary = Observe(10000)!;
    Check(summary.Contains("frames=7 changes=5"), "Radio telemetry counts changed physical states before mapping");
    Check(summary.Contains("sticksChangedHz=0.2 triggersChangedHz=0.2"),
        "Radio telemetry separates stick/trigger changes and excludes D-pad trigger false positives");
    Check(summary.Contains("leftStickChangedHz=0.2 rightStickChangedHz=0.1"),
        "Radio telemetry isolates left/right stick rates from other physical state changes");
    Check(summary.Contains("gapsUnder1Ms=1 gapsOver8Ms=2") && summary.Contains("maxGapMs=9979.000"),
        "Radio telemetry identifies bursts and long inter-state gaps");
    Check(Observe(20000)!.Contains("frames=1 changes=0"),
        "Radio telemetry preserves its snapshot across reporting windows");
}

// Separate the costs of queue/lock waiting and I/O/processing with known times.
{
    var type = typeof(ScufDeviceService).Assembly.GetType("ZeroCue.DataProbe.Services.StageTimingTracker")!;
    var tracker = Activator.CreateInstance(type, "TEST", "Wait", "Work")!;
    var observe = type.GetMethod("Observe")!;
    string? Observe(int seconds, int waitMs, int workMs) => (string?)observe.Invoke(tracker,
        new object[] { Stopwatch.Frequency * (seconds + 1L), Stopwatch.Frequency * waitMs / 1000,
            Stopwatch.Frequency * workMs / 1000 });
    Check(Observe(0, 1, 2) == null && Observe(5, 3, 4) == null,
        "Stage timing aggregates samples without per-call logging");
    var summary = Observe(10, 2, 3)!;
    Check(summary.Contains("samples=3 avgWaitMs=2.000 maxWaitMs=3.000 avgWorkMs=3.000 maxWorkMs=4.000"),
        "Stage timing keeps waiting and work durations separate");
    Check(Observe(20, 5, 6)!.Contains("samples=1 avgWaitMs=5.000 maxWaitMs=5.000 avgWorkMs=6.000 maxWorkMs=6.000"),
        "Stage timing resets aggregate durations between windows");
}

Console.WriteLine("All hardware-free input checks passed.");

sealed class FakeReader : UsbEndpointReader
{
    private readonly ConcurrentQueue<byte[]>? _frames;
    public int Reads;

    public FakeReader(IEnumerable<byte[]>? frames = null)
        : base(null!, 64, 0, ReadEndpointID.Ep01, EndpointType.Interrupt)
    {
        if (frames != null) _frames = new ConcurrentQueue<byte[]>(frames);
    }

    public override Error Read(Span<byte> buffer, int timeout, out int transferLength)
    {
        if (_frames == null)
        {
            Thread.Sleep(1);
            buffer.Clear();
            buffer[0] = 0x06;
            buffer[11] = 0x80;
            transferLength = 64;
            Interlocked.Increment(ref Reads);
            return Error.Success;
        }
        if (_frames.TryDequeue(out var frame))
        {
            buffer.Clear();
            frame.CopyTo(buffer);
            transferLength = frame.Length;
            Interlocked.Increment(ref Reads);
            return Error.Success;
        }
        Thread.Sleep(1);
        transferLength = 0;
        Interlocked.Increment(ref Reads);
        return Error.Timeout;
    }
}
