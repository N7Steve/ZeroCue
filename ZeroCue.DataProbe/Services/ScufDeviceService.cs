using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using ZeroCue.DataProbe.Models;

namespace ZeroCue.DataProbe.Services
{
    public enum ConnectionStatusState
    {
        None,
        ReceiverOnly,
        WirelessConnecting,
        WirelessConnected,
        UsbConnecting,
        UsbConnected,
        Error
    }

    public partial class ScufDeviceService : INotifyPropertyChanged
    {
        private static ScufDeviceService? _instance;
        public static ScufDeviceService Instance => _instance ??= new ScufDeviceService();

        private static readonly SupportedScufDeviceProfile DeviceProfile = SupportedScufDeviceProfile.ScufEnvisionPro;
        private const int DATA_IFACE = 3;
        private const int HANDSHAKE_IFACE = 4;
        private static readonly int FRAME_SIZE = DeviceProfile.ReportSize;

        private const string MAPPING_FILE = "scuf_mapping.json";

        // LibUSB reference state
        private UsbContext? _context;
        private UsbDevice? _device;
        private UsbEndpointReader? _reader;
        private UsbEndpointReader? _reader2;

        private IScufRawTransport? _transport;
        private ScufAckReader? _ackReader;
        private DeviceModeController? _modeController;
        private WirelessDongleWinUsbTransport? _wirelessWinUsbTransport;
        private WirelessDongleWinUsbTransport? _wirelessWinUsbAuxRadioTransport;
        private CancellationTokenSource? _wirelessWinUsbAuxRadioCts;
        private Task? _wirelessWinUsbAuxRadioTask;
        private WirelessSessionController? _wirelessSessionController;
        private CancellationTokenSource? _wirelessSessionCts;
        private CancellationTokenSource? _wirelessWiredUsbMonitorCts;
        private Task? _wirelessWiredUsbMonitorTask;
        private CancellationTokenSource? _wirelessSoftwareModeRefreshCts;
        private Task? _wirelessSoftwareModeRefreshTask;
        private readonly SemaphoreSlim _wirelessSoftwareModeGate = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _wirelessForegroundMonitorCts;
        private Task? _wirelessForegroundMonitorTask;
        private long _lastWirelessSoftwareModeRefreshMs;
        private int _wirelessG5SuppressorActive;
        private string _lastWirelessForegroundPath = string.Empty;

        private UsbEndpointWriter? _rumbleWriter;
        private UsbEndpointWriter? _writer2;
        private readonly byte[] _rumbleBuffer = new byte[13] { 0x09, 0x00, 0x6A, 0x09, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0xEB };
        private readonly object _rumbleDispatchLock = new object();
        private byte _pendingLargeMotor;
        private byte _pendingSmallMotor;
        private long _rumbleRequestVersion;
        private bool _rumbleDispatchRunning;
        private Task _rumbleDispatchTask = Task.CompletedTask;
        private CancellationTokenSource? _cts;
        private Task? _pollingTask;
        private int _disconnecting;
        private const long FrameProcessedEventMinIntervalMs = 10;
        private long _lastFrameProcessedEventMs;
        private const long WirelessAnalogProcessMinIntervalMs = 4;
        private long _lastWirelessAnalogProcessMs;
        private int _lastWirelessDigitalSignature = -1;
        private readonly object _wirelessInputFrameLock = new object();
        private byte[]? _lastWirelessRadioInputFrame;
        private long _wirelessRuntimeG4LastSeenMs;
        private long _wirelessRuntimeG5LastSeenMs;
        private const long WirelessRuntimeGKeyHoldMs = 220;

        private long _guidePressedAt = 0;

        // USB synchronization lock for Ep02/Ep82 commands vs Keepalive vs Ep82 Polling
        public readonly SemaphoreSlim UsbLock = new SemaphoreSlim(1, 1);

        // ViGEm states
        private ViGEmClient? _client;
        private IXbox360Controller? _xbox;

        private class VigemStateTracker
        {
            public bool A, B, X, Y, LB, RB, Back, Start, Up, Down, Left, Right, L3, R3, Guide;
        }
        private readonly VigemStateTracker _vigemTracker = new VigemStateTracker();

        // Configuration
        private MappingFile? _mapping;
        public Dictionary<string, string> PaddleRemapTable { get; private set; }

        // Paddle state
        private readonly Dictionary<string, long> _paddleLastSeen = new Dictionary<string, long>();
        private const long PADDLE_HOLD_MS = 20;
        private readonly HashSet<string> _activePaddleTargets = new HashSet<string>();

        // Button remap (full suppression: ALL hw buttons go through this table)
        public Dictionary<string, string> ButtonRemapTable { get; private set; }
        private readonly HashSet<string> _frameTargets = new HashSet<string>();
        private readonly HashSet<string> _previousFrameTargets = new HashSet<string>();

        // G-Key remap and state
        public Dictionary<string, string> GKeyRemapTable { get; private set; }
        public Dictionary<string, MacroDefinition> Macros { get; private set; } = new Dictionary<string, MacroDefinition>();
        public Dictionary<string, MacroDefinition> MacroLibrary { get; private set; } = new Dictionary<string, MacroDefinition>();

        // Shift remap tables
        public Dictionary<string, string> ShiftPaddleRemapTable { get; private set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ShiftGKeyRemapTable { get; private set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ShiftButtonRemapTable { get; private set; } = new Dictionary<string, string>();

        public string ShiftModifierButton = "SAX_L";
        public bool IsShiftHeld { get; private set; }

        private readonly HashSet<string> _activeGKeyTargets = new HashSet<string>();
        private string? _activeGKeyName = null;
        private bool _g5Active = false;
        private bool _g1Active = false;

        // G5 = Guide in firmware. No phantom map needed because G5 is detected
        // directly from Ep01 bd & 0x80 and Guide is NEVER passed as hardware button.

        private byte _rgbRed = 0; public byte RgbRed { get => _rgbRed; set => SetProperty(ref _rgbRed, value); }
        private byte _rgbGreen = 255; public byte RgbGreen { get => _rgbGreen; set => SetProperty(ref _rgbGreen, value); }
        private byte _rgbBlue = 255; public byte RgbBlue { get => _rgbBlue; set => SetProperty(ref _rgbBlue, value); }
        private ushort _rgbBrightness = 750; public ushort RgbBrightness { get => _rgbBrightness; set => SetProperty(ref _rgbBrightness, value); }
        private byte _rumbleIntensity = 100; public byte RumbleIntensity { get => _rumbleIntensity; set => SetProperty(ref _rumbleIntensity, Math.Clamp(value, (byte)0, (byte)100)); }
        private bool _ecoMode = false; public bool EcoMode { get => _ecoMode; set => SetProperty(ref _ecoMode, value); }
        private string _triggerCurve = "Lineal";
        public string TriggerCurve { get => _triggerCurve; set => SetProperty(ref _triggerCurve, value); }
        private double[] _customCurveX = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double[] CustomCurveX { get => _customCurveX; set => SetProperty(ref _customCurveX, value); }
        private double[] _customCurveY = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double[] CustomCurveY { get => _customCurveY; set => SetProperty(ref _customCurveY, value); }
        private double _stickDeadzoneMinPercent = 8.0;
        public double StickDeadzoneMinPercent
        {
            get => _stickDeadzoneMinPercent;
            set
            {
                double clamped = Math.Clamp(value, 0.0, 95.0);
                if (clamped >= _stickDeadzoneMaxPercent)
                {
                    clamped = Math.Max(0.0, _stickDeadzoneMaxPercent - 1.0);
                }
                SetProperty(ref _stickDeadzoneMinPercent, clamped);
            }
        }

        private double _stickDeadzoneMaxPercent = 100.0;
        public double StickDeadzoneMaxPercent
        {
            get => _stickDeadzoneMaxPercent;
            set
            {
                double clamped = Math.Clamp(value, 5.0, 100.0);
                if (clamped <= _stickDeadzoneMinPercent)
                {
                    clamped = Math.Min(100.0, _stickDeadzoneMinPercent + 1.0);
                }
                SetProperty(ref _stickDeadzoneMaxPercent, clamped);
            }
        }

        private string _stickCurve = "Lineal";
        public string StickCurve { get => _stickCurve; set => SetProperty(ref _stickCurve, string.IsNullOrWhiteSpace(value) ? "Lineal" : value); }
        private double[] _stickCustomCurveX = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double[] StickCustomCurveX { get => _stickCustomCurveX; set => SetProperty(ref _stickCustomCurveX, value); }
        private double[] _stickCustomCurveY = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0 };
        public double[] StickCustomCurveY { get => _stickCustomCurveY; set => SetProperty(ref _stickCustomCurveY, value); }

        public bool EnableWirelessHeartbeat { get; set; } = true;

        private string _languageCode = LocalizationService.English;
        public string LanguageCode
        {
            get => _languageCode;
            set
            {
                var normalized = LocalizationService.NormalizeLanguageCode(value);
                if (_languageCode != normalized)
                {
                    SetProperty(ref _languageCode, normalized);
                    SaveAppSettings();
                }
            }
        }

        private string _themeName = "DefaultTheme";
        public string ThemeName
        {
            get => _themeName;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "DefaultTheme" : value;
                if (_themeName != normalized)
                {
                    SetProperty(ref _themeName, normalized);
                    SaveAppSettings();
                }
            }
        }

        private bool _startWithWindows;
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (_startWithWindows != value)
                {
                    SetProperty(ref _startWithWindows, value);
                    WindowsStartupService.Configure(_startWithWindows, _startMinimized);
                    SaveAppSettings();
                }
            }
        }

        private bool _startMinimized;
        public bool StartMinimized
        {
            get => _startMinimized;
            set
            {
                if (_startMinimized != value)
                {
                    SetProperty(ref _startMinimized, value);
                    if (_startWithWindows)
                    {
                        WindowsStartupService.Configure(_startWithWindows, _startMinimized);
                    }
                    SaveAppSettings();
                }
            }
        }

        private ApplicationCloseBehavior _closeBehavior = ApplicationCloseBehavior.MinimizeToTray;
        public ApplicationCloseBehavior CloseBehavior
        {
            get => _closeBehavior;
            set
            {
                if (_closeBehavior != value)
                {
                    SetProperty(ref _closeBehavior, value);
                    SaveAppSettings();
                }
            }
        }

        private bool _askBeforeClosing = true;
        public bool AskBeforeClosing
        {
            get => _askBeforeClosing;
            set
            {
                if (_askBeforeClosing != value)
                {
                    SetProperty(ref _askBeforeClosing, value);
                    SaveAppSettings();
                }
            }
        }

        private string _defaultProfileName = "Default";
        public string DefaultProfileName
        {
            get => _defaultProfileName;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();
                if (_defaultProfileName != normalized)
                {
                    SetProperty(ref _defaultProfileName, normalized);
                    SaveAppSettings();
                }
            }
        }

        // Events
        public event Action<string>? OnInputEvent;
        public event Action<MacroInputEvent>? OnMacroControllerInput;
        public event Action? OnFrameProcessed;
        public event Action? OnProfileLoaded;
        public event PropertyChangedEventHandler? PropertyChanged;

        // Observable properties
        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set => SetProperty(ref _isConnected, value);
        }

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            private set => SetProperty(ref _isConnecting, value);
        }

        private bool _isViGEmActive;
        public bool IsViGEmActive
        {
            get => _isViGEmActive;
            private set => SetProperty(ref _isViGEmActive, value);
        }

        private ScufConnectionKind _connectionKind = ScufConnectionKind.Wired;
        public ScufConnectionKind ConnectionKind
        {
            get => _connectionKind;
            private set => SetProperty(ref _connectionKind, value);
        }

        private string _transportName = "Wired USB";
        public string TransportName
        {
            get => _transportName;
            private set
            {
                if (_transportName == value)
                {
                    return;
                }

                SetProperty(ref _transportName, value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionMode)));
            }
        }

        public string ConnectionMode => ConnectionKind == ScufConnectionKind.Wireless ? TransportName : "Wired USB";

        private ConnectionStatusState _connectionStatusState = ConnectionStatusState.None;
        public ConnectionStatusState ConnectionStatusState
        {
            get => _connectionStatusState;
            private set => SetProperty(ref _connectionStatusState, value);
        }

        private string _statusText = LocalizationService.Get("StatusWaitingController");
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string _statusDetail = LocalizationService.Get("StatusConnectControllerHint");
        public string StatusDetail
        {
            get => _statusDetail;
            private set => SetProperty(ref _statusDetail, value);
        }

        private void SetConnectionStatus(ConnectionStatusState state, string title, string detail)
        {
            ConnectionStatusState = state;
            StatusText = title;
            StatusDetail = detail;
        }

        private void SetWaitingForControllerStatus()
        {
            SetConnectionStatus(
                ConnectionStatusState.ReceiverOnly,
                LocalizationService.Get("StatusWaitingController"),
                LocalizationService.Get("StatusConnectControllerHint"));
        }

        private void BeginWirelessHandshakeStatus()
        {
            if (IsConnected || ConnectionStatusState == ConnectionStatusState.WirelessConnecting)
            {
                return;
            }

            IsConnecting = true;
            SetConnectionStatus(
                ConnectionStatusState.WirelessConnecting,
                LocalizationService.Get("StatusConnecting"),
                LocalizationService.Get("StatusPreparingController"));
        }

        public void RefreshLocalizedConnectionStatus()
        {
            switch (ConnectionStatusState)
            {
                case ConnectionStatusState.ReceiverOnly:
                case ConnectionStatusState.None:
                    SetWaitingForControllerStatus();
                    break;
                case ConnectionStatusState.WirelessConnecting:
                case ConnectionStatusState.UsbConnecting:
                    SetConnectionStatus(
                        ConnectionStatusState,
                        LocalizationService.Get("StatusConnecting"),
                        LocalizationService.Get("StatusPreparingController"));
                    break;
                case ConnectionStatusState.WirelessConnected:
                case ConnectionStatusState.UsbConnected:
                    SetConnectionStatus(
                        ConnectionStatusState,
                        LocalizationService.Get("StatusControllerConnected"),
                        LocalizationService.Get("StatusControllerActive"));
                    break;
            }
        }

        // Live input properties
        private short _leftStickX; public short LeftStickX { get => _leftStickX; set => SetProperty(ref _leftStickX, value); }
        private short _leftStickY; public short LeftStickY { get => _leftStickY; set => SetProperty(ref _leftStickY, value); }
        private short _rightStickX; public short RightStickX { get => _rightStickX; set => SetProperty(ref _rightStickX, value); }
        private short _rightStickY; public short RightStickY { get => _rightStickY; set => SetProperty(ref _rightStickY, value); }

        private ushort _leftTrigger; public ushort LeftTrigger { get => _leftTrigger; set => SetProperty(ref _leftTrigger, value); }
        private ushort _rightTrigger; public ushort RightTrigger { get => _rightTrigger; set => SetProperty(ref _rightTrigger, value); }

        private bool _buttonA; public bool ButtonA { get => _buttonA; set => SetProperty(ref _buttonA, value); }
        private bool _buttonB; public bool ButtonB { get => _buttonB; set => SetProperty(ref _buttonB, value); }
        private bool _buttonX; public bool ButtonX { get => _buttonX; set => SetProperty(ref _buttonX, value); }
        private bool _buttonY; public bool ButtonY { get => _buttonY; set => SetProperty(ref _buttonY, value); }
        private bool _buttonLB; public bool ButtonLB { get => _buttonLB; set => SetProperty(ref _buttonLB, value); }
        private bool _buttonRB; public bool ButtonRB { get => _buttonRB; set => SetProperty(ref _buttonRB, value); }
        private bool _buttonBack; public bool ButtonBack { get => _buttonBack; set => SetProperty(ref _buttonBack, value); }
        private bool _buttonStart; public bool ButtonStart { get => _buttonStart; set => SetProperty(ref _buttonStart, value); }
        private bool _buttonL3; public bool ButtonL3 { get => _buttonL3; set => SetProperty(ref _buttonL3, value); }
        private bool _buttonR3; public bool ButtonR3 { get => _buttonR3; set => SetProperty(ref _buttonR3, value); }
        private bool _buttonGuide; public bool ButtonGuide { get => _buttonGuide; set => SetProperty(ref _buttonGuide, value); }

        private string _dPadState = "Neutral";
        public string DPadState
        {
            get => _dPadState;
            set => SetProperty(ref _dPadState, value);
        }

        private string _lastPaddleAction = "";
        public string LastPaddleAction
        {
            get => _lastPaddleAction;
            set => SetProperty(ref _lastPaddleAction, value);
        }

        private string _lastGKeyAction = "";
        public string LastGKeyAction
        {
            get => _lastGKeyAction;
            set => SetProperty(ref _lastGKeyAction, value);
        }

        private ScufDeviceService()
        {
            // Initial remappings
            PaddleRemapTable = new Dictionary<string, string>
            {
                { "Paddle_R4", "A" },
                { "Paddle_R5", "B" },
                { "Paddle_L4", "X" },
                { "Paddle_L5", "Y" },
                { "SAX_L",     "Left" },
                { "SAX_R",     "Up" }
            };

            GKeyRemapTable = new Dictionary<string, string>
            {
                { "G1", "LeftShoulder" },
                { "G2", "RightShoulder" },
                { "G3", "Back" },
                { "G4", "Start" },
                { "G5", "Guide" }
            };

            // Full hardware suppression: ALL buttons pass through this table
            // Identity mapping = same behavior as before, but we OWN the output
            ButtonRemapTable = new Dictionary<string, string>
            {
                { "A",              "A" },
                { "B",              "B" },
                { "X",              "X" },
                { "Y",              "Y" },
                { "LeftShoulder",   "LeftShoulder" },
                { "RightShoulder",  "RightShoulder" },
                { "Back",           "Back" },
                { "Start",          "Start" },
                { "LeftThumb",      "LeftThumb" },
                { "RightThumb",     "RightThumb" },
                { "Up",             "Up" },
                { "Down",           "Down" },
                { "Left",           "Left" },
                { "Right",          "Right" },
                { "Guide",          "Guide" }
                // Guide NO está — nunca viene de hardware (bd & 0x80 = G5)
            };

            // Initialize shift tables with defaults from normal
            ShiftPaddleRemapTable = new Dictionary<string, string>(PaddleRemapTable);
            ShiftGKeyRemapTable = new Dictionary<string, string>(GKeyRemapTable);
            ShiftButtonRemapTable = new Dictionary<string, string>(ButtonRemapTable);

            ZeroCuePaths.EnsureInitialized();
            LoadAppSettings();
            LoadProfile("Default");
            LoadMapping(ZeroCuePaths.GetAppPath(MAPPING_FILE));

            // Initialize telemetry — clear previous logs on every app start
            InitTelemetry();

            StartAutoPolling();
        }

        private void TrySendClassRequest(int iface, byte bRequest, int wValue)
        {
            if (_device == null) return;
            try
            {
                var setup = new UsbSetupPacket((byte)0x21, bRequest, (short)wValue, (short)iface, 0);
                _device.ControlTransfer(setup, Array.Empty<byte>(), 0, 0);
            }
            catch { }
        }

        private static string HexDump(byte[] data, int len)
        {
            var sb = new System.Text.StringBuilder(len * 3);
            for (int i = 0; i < len; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        private void SetXboxButton(IXbox360Controller xbox, string target, bool state)
        {
            if (target == "A") xbox.SetButtonState(Xbox360Button.A, state);
            else if (target == "B") xbox.SetButtonState(Xbox360Button.B, state);
            else if (target == "X") xbox.SetButtonState(Xbox360Button.X, state);
            else if (target == "Y") xbox.SetButtonState(Xbox360Button.Y, state);
            else if (target == "LeftShoulder") xbox.SetButtonState(Xbox360Button.LeftShoulder, state);
            else if (target == "RightShoulder") xbox.SetButtonState(Xbox360Button.RightShoulder, state);
            else if (target == "Back") xbox.SetButtonState(Xbox360Button.Back, state);
            else if (target == "Start") xbox.SetButtonState(Xbox360Button.Start, state);
            else if (target == "LeftThumb") xbox.SetButtonState(Xbox360Button.LeftThumb, state);
            else if (target == "RightThumb") xbox.SetButtonState(Xbox360Button.RightThumb, state);
            else if (target == "Guide") xbox.SetButtonState(Xbox360Button.Guide, state);
            else if (target == "Up") xbox.SetButtonState(Xbox360Button.Up, state);
            else if (target == "Down") xbox.SetButtonState(Xbox360Button.Down, state);
            else if (target == "Left") xbox.SetButtonState(Xbox360Button.Left, state);
            else if (target == "Right") xbox.SetButtonState(Xbox360Button.Right, state);
        }

        public bool IsPaddleActive(string paddleName)
        {
            return _paddleLastSeen.TryGetValue(paddleName, out var lastSeen)
                   && (Environment.TickCount64 - lastSeen) < PADDLE_HOLD_MS;
        }

        public bool IsGKeyActive(string gkeyName)
        {
            if (gkeyName == "G1") return _g1Active;
            if (gkeyName == "G5") return _g5Active;
            return _activeGKeyName == gkeyName;
        }

        private void LogInput(string text)
        {
            OnInputEvent?.Invoke(text);
            if (text.StartsWith("[INPUT]") || text.StartsWith("[PADDLE]") || text.StartsWith("[G-KEY]") || text.StartsWith("[BUTTON]"))
            {
                WriteTelemetryInput(text);
            }
            else
            {
                WriteTelemetryComm(text);
            }
        }

        private void WriteTelemetryComm(string text)
        {
            ZeroCueLog.Communication(text);
        }

        private void WriteTelemetryInput(string text)
        {
            ZeroCueLog.InputMapping(text);
        }

        private void RaiseFrameProcessedThrottled()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastFrameProcessedEventMs);
            if (now - last < FrameProcessedEventMinIntervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastFrameProcessedEventMs, now, last) == last)
            {
                OnFrameProcessed?.Invoke();
            }
        }

        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public byte GetTriggerOutputByte(ushort rawValue)
        {
            return (byte)Math.Clamp(Math.Round(ApplyTriggerCurveNormalized(rawValue / 1023.0) * 255.0), 0, 255);
        }

        public ushort GetTriggerOutputRaw(ushort rawValue)
        {
            return (ushort)Math.Clamp(Math.Round(ApplyTriggerCurveNormalized(rawValue / 1023.0) * 1023.0), 0, 1023);
        }

        public double GetTriggerOutputPercent(ushort rawValue)
        {
            return ApplyTriggerCurveNormalized(rawValue / 1023.0) * 100.0;
        }

        private static double EvaluateCurve(double x, double[]? curveX, double[]? curveY)
        {
            if (curveX == null || curveY == null || curveX.Length < 2) return x;

            int n = Math.Min(curveX.Length, curveY.Length);
            if (n < 2) return x;
            if (x <= curveX[0]) return curveY[0];
            if (x >= curveX[n - 1]) return curveY[n - 1];

            // Monotone Cubic Spline Interpolation
            double[] dx = new double[n - 1];
            double[] dy = new double[n - 1];
            double[] m = new double[n];

            for (int i = 0; i < n - 1; i++)
            {
                dx[i] = curveX[i + 1] - curveX[i];
                dy[i] = curveY[i + 1] - curveY[i];
                if (dx[i] == 0) dx[i] = 0.0001;
            }

            for (int i = 1; i < n - 1; i++)
            {
                if (dy[i - 1] * dy[i] <= 0)
                    m[i] = 0;
                else
                {
                    double d1 = dy[i - 1] / dx[i - 1];
                    double d2 = dy[i] / dx[i];
                    m[i] = 2.0 / (1.0 / d1 + 1.0 / d2);
                }
            }
            m[0] = dy[0] / dx[0];
            m[n - 1] = dy[n - 2] / dx[n - 2];

            for (int i = 0; i < n - 1; i++)
            {
                double d = dy[i] / dx[i];
                if (d == 0)
                {
                    m[i] = 0;
                    m[i + 1] = 0;
                }
                else
                {
                    double a = m[i] / d;
                    double b = m[i + 1] / d;
                    if (a * a + b * b > 9.0)
                    {
                        double tau = 3.0 / System.Math.Sqrt(a * a + b * b);
                        m[i] = tau * a * d;
                        m[i + 1] = tau * b * d;
                    }
                }
            }

            for (int i = 0; i < n - 1; i++)
            {
                if (x >= curveX[i] && x <= curveX[i + 1])
                {
                    double h = dx[i];
                    double t = (x - curveX[i]) / h;
                    double t2 = t * t;
                    double t3 = t2 * t;

                    double h00 = 2 * t3 - 3 * t2 + 1;
                    double h10 = t3 - 2 * t2 + t;
                    double h01 = -2 * t3 + 3 * t2;
                    double h11 = t3 - t2;

                    double y = h00 * curveY[i] + h10 * h * m[i] + h01 * curveY[i + 1] + h11 * h * m[i + 1];
                    return System.Math.Clamp(y, 0.0, 1.0);
                }
            }
            return x;
        }

        public double EvaluateCustomCurve(double x) => EvaluateCurve(x, CustomCurveX, CustomCurveY);

        public double EvaluateStickCustomCurve(double x) => EvaluateCurve(x, StickCustomCurveX, StickCustomCurveY);

        private static double ApplyDynamicCurveNormalized(double x)
        {
            double cubic = Math.Clamp(4.0 * (x - 0.5) * (x - 0.5) * (x - 0.5) + 0.5, 0.0, 1.0);
            return Math.Clamp((x * 0.45) + (cubic * 0.55), 0.0, 1.0);
        }

        public double ApplyTriggerCurveNormalized(double x)
        {
            x = Math.Clamp(x, 0.0, 1.0);
            if (TriggerCurve == "Custom")
            {
                return EvaluateCustomCurve(x);
            }

            return TriggerCurve switch
            {
                "Exponencial" => Math.Pow(x, 1.75),
                "Dinamica" => ApplyDynamicCurveNormalized(x),
                "Agresiva" => 1.0 - Math.Pow(1.0 - x, 2.35),
                _ => x
            };
        }

        public double ApplyStickCurveNormalized(double x)
        {
            x = Math.Clamp(x, 0.0, 1.0);
            return StickCurve switch
            {
                "Precisa" => Math.Pow(x, 1.65),
                "Dinamica" => ApplyDynamicCurveNormalized(x),
                "Agresiva" => 1.0 - Math.Pow(1.0 - x, 2.2),
                "Custom" => EvaluateStickCustomCurve(x),
                _ => x
            };
        }

        public (short X, short Y) ApplyStickOutput(short rawX, short rawY)
        {
            const double axisMax = 32767.0;
            double x = Math.Clamp(rawX / axisMax, -1.0, 1.0);
            double y = Math.Clamp(rawY / axisMax, -1.0, 1.0);
            double magnitude = Math.Clamp(Math.Sqrt((x * x) + (y * y)), 0.0, 1.0);
            if (magnitude <= 0.0001)
            {
                return (0, 0);
            }

            double min = Math.Clamp(StickDeadzoneMinPercent / 100.0, 0.0, 0.95);
            double max = Math.Clamp(StickDeadzoneMaxPercent / 100.0, min + 0.01, 1.0);
            if (magnitude <= min)
            {
                return (0, 0);
            }

            double scaledMagnitude = magnitude >= max
                ? 1.0
                : (magnitude - min) / (max - min);
            double curvedMagnitude = ApplyStickCurveNormalized(scaledMagnitude);
            double directionX = x / magnitude;
            double directionY = y / magnitude;

            short outputX = (short)Math.Clamp(Math.Round(directionX * curvedMagnitude * axisMax), short.MinValue + 1, short.MaxValue);
            short outputY = (short)Math.Clamp(Math.Round(directionY * curvedMagnitude * axisMax), short.MinValue + 1, short.MaxValue);
            return (outputX, outputY);
        }

        // --- Software Mode Control Interface ---

        public async Task InitializeDeviceInSoftwareModeAsync(CancellationToken ct)
        {
            if (_modeController != null)
            {
                await _modeController.InitializeDeviceInSoftwareModeAsync(ct);
                CurrentOperatingState = "SoftwareIcueSession";

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250, ct);
                        await SetEcoModeAsync(EcoMode, ct);

                        await Task.Delay(50, ct);
                        await SetStaticRgbAsync(RgbRed, RgbGreen, RgbBlue, ct);
                        await SetBrightnessAsync(RgbBrightness, ct);
                        await SetRumbleIntensityAsync(RumbleIntensity, ct);
                        await SuppressG5HardwareEcoToggleAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        LogInput($"[WARN] Could not apply startup runtime profile: {ex.Message}");
                    }
                });
            }
        }

        public Task SetStaticRgbAsync(byte r, byte g, byte b, CancellationToken ct)
        {
            double factor = RgbBrightness / 1000.0;
            byte scaledR = (byte)(r * factor);
            byte scaledG = (byte)(g * factor);
            byte scaledB = (byte)(b * factor);
            return _modeController?.SetStaticRgbAsync(scaledR, scaledG, scaledB, ct) ?? Task.CompletedTask;
        }

        public async Task SetBrightnessAsync(ushort value, CancellationToken ct)
        {
            if (_modeController == null) return;
            await _modeController.SetBrightnessAsync(1000, ct);

            double factor = value / 1000.0;
            byte scaledR = (byte)(RgbRed * factor);
            byte scaledG = (byte)(RgbGreen * factor);
            byte scaledB = (byte)(RgbBlue * factor);
            await _modeController.SetStaticRgbAsync(scaledR, scaledG, scaledB, ct);
        }

        public Task SetEcoModeAsync(bool enabled, CancellationToken ct)
        {
            return _modeController?.SetEcoModeAsync(enabled, ct) ?? Task.CompletedTask;
        }

        public Task SetRumbleIntensityAsync(byte value, CancellationToken ct)
        {
            return _modeController?.SetRumbleIntensityAsync(value, ct) ?? Task.CompletedTask;
        }

        public Task SuppressG5HardwareEcoToggleAsync(CancellationToken ct)
        {
            return _modeController?.SuppressG5HardwareEcoToggleAsync(ct) ?? Task.CompletedTask;
        }

        public async Task ResetLightsAsync(CancellationToken ct)
        {
            if (_modeController == null) return;
            await _modeController.SetStaticRgbAsync(255, 255, 255, ct);
            await _modeController.SetBrightnessAsync(1000, ct);
        }

        private string _currentOperatingState = "Unknown";
        public string CurrentOperatingState
        {
            get => _currentOperatingState;
            set => SetProperty(ref _currentOperatingState, value);
        }
    }
}
