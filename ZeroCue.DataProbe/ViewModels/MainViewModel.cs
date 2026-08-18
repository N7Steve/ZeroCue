using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ZeroCue.DataProbe.Models;
using ZeroCue.DataProbe.Services;

namespace ZeroCue.DataProbe.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ScufDeviceService _service;
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> AdvancedMappingIconCache = new();
        private readonly DispatcherTimer _profileLinkTimer;
        private string _lastForegroundAppPath = string.Empty;
        private string? _profileBeforeLinkedForegroundApp;
        private string? _activeLinkedForegroundProfile;

        // Command definitions
        public ICommand ConnectCommand { get; }
        private bool _isProcessing;
        public bool IsProcessing { get => _isProcessing; set => SetProperty(ref _isProcessing, value); }

        private string _processingMessage = "";
        public string ProcessingMessage { get => _processingMessage; set => SetProperty(ref _processingMessage, value); }

        private bool _isResetToDefaultDialogOpen;
        public bool IsResetToDefaultDialogOpen
        {
            get => _isResetToDefaultDialogOpen;
            set => SetProperty(ref _isResetToDefaultDialogOpen, value);
        }

        public ICommand InstallDriverCommand { get; }
        public ICommand RestoreDriverCommand { get; }
        public ICommand InstallReceiverDriverCommand { get; }
        public ICommand RestoreReceiverDriverCommand { get; }
        public ICommand ListWirelessReceiverPnpInstancesCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand ResetToDefaultCommand { get; }
        public ICommand ConfirmResetToDefaultCommand { get; }
        public ICommand CancelResetToDefaultCommand { get; }
        public ICommand ConfirmProfileEditorCommand { get; }
        public ICommand CancelProfileEditorCommand { get; }
        public ICommand ConfirmProfileDeleteCommand { get; }
        public ICommand CancelProfileDeleteCommand { get; }
        public ICommand ConfirmProfileLinkCommand { get; }
        public ICommand CancelProfileLinkCommand { get; }
        public ICommand ConfirmProfileUnlinkCommand { get; }
        public ICommand CancelProfileUnlinkCommand { get; }
        public ICommand CloseLinkedAppsManagerCommand { get; }
        public ICommand RemoveLinkedAppCommand { get; }
        public ICommand ClearManagedLinkedAppsCommand { get; }

        public ICommand SelectRemapInputTabCommand { get; }
        public ICommand BeginRemapCommand { get; }
        public ICommand CancelRemapCommand { get; }
        public ICommand RestoreRemapCommand { get; }
        public ICommand UnmapRemapCommand { get; }
        public ICommand AddRemapGestureCommand { get; }
        public ICommand RemoveRemapGestureCommand { get; }
        public ICommand SelectRemapGestureCommand { get; }
        public ICommand OpenDetailsRemapCommand { get; }
        public ICommand AddDetailsDoubleTapCommand { get; }
        public ICommand AddDetailsHoldCommand { get; }
        public ICommand AddDetailsPressStartCommand { get; }
        public ICommand AddDetailsPressReleaseCommand { get; }
        public ICommand RemoveDetailsRemapGestureCommand { get; }
        public ICommand StartMacroRecordingCommand { get; }
        public ICommand StopMacroRecordingCommand { get; }
        public ICommand ToggleMacroRecordingCommand { get; }
        public ICommand OpenSaveMacroDialogCommand { get; }
        public ICommand ConfirmSaveMacroCommand { get; }
        public ICommand CancelSaveMacroCommand { get; }
        public ICommand OpenLoadMacroDialogCommand { get; }
        public ICommand CancelLoadMacroCommand { get; }
        public ICommand LoadMacroCommand { get; }
        public ICommand DeleteSavedMacroCommand { get; }
        public ICommand ClearMacroCommand { get; }
        public ICommand RemoveMacroStepCommand { get; }
        public ICommand OpenShiftModifierPickerCommand { get; }
        public ICommand CloseShiftModifierPickerCommand { get; }
        public ICommand AcceptShiftModifierCommand { get; }
        public ICommand ClearShiftModifierCommand { get; }
        public ICommand SetSectionCommand { get; }
        public ICommand SetTabCommand { get; }
        public ICommand RefreshAdvancedMappingsCommand { get; }
        public ICommand SetTriggerCurveCommand { get; }
        public ICommand SetStickCurveCommand { get; }
        public ICommand SetStandardModeCommand { get; }
        public ICommand SetShiftModeCommand { get; }

        public ICommand SetRgbRedCommand { get; }
        public ICommand SetRgbGreenCommand { get; }
        public ICommand SetRgbBlueCommand { get; }
        public ICommand SetRgbWhiteCommand { get; }
        public ICommand SetBrightnessMaxCommand { get; }
        public ICommand SetBrightnessZeroCommand { get; }

        public ICommand ResetLightsCommand { get; }
        public ICommand LightsOffCommand { get; }

        private byte _rgbRed = 0;
        public byte RgbRed
        {
            get => _rgbRed;
            set
            {
                if (SetProperty(ref _rgbRed, value))
                {
                    OnPropertyChanged(nameof(CurrentColorHex));
                    DebounceRgbUpdate();
                }
            }
        }

        private byte _rgbGreen = 255;
        public byte RgbGreen
        {
            get => _rgbGreen;
            set
            {
                if (SetProperty(ref _rgbGreen, value))
                {
                    OnPropertyChanged(nameof(CurrentColorHex));
                    DebounceRgbUpdate();
                }
            }
        }

        private byte _rgbBlue = 255;
        public byte RgbBlue
        {
            get => _rgbBlue;
            set
            {
                if (SetProperty(ref _rgbBlue, value))
                {
                    OnPropertyChanged(nameof(CurrentColorHex));
                    DebounceRgbUpdate();
                }
            }
        }

        public string CurrentColorHex
        {
            get
            {
                double factor = RgbBrightness / 1000.0;
                byte r = (byte)(RgbRed * factor);
                byte g = (byte)(RgbGreen * factor);
                byte b = (byte)(RgbBlue * factor);
                return $"#FF{r:X2}{g:X2}{b:X2}";
            }
        }

        private CancellationTokenSource? _rgbDebounceCts;
        private void DebounceRgbUpdate()
        {
            _rgbDebounceCts?.Cancel();
            _rgbDebounceCts = new CancellationTokenSource();
            var ct = _rgbDebounceCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, ct);
                    if (!ct.IsCancellationRequested)
                    {
                        await _service.SetStaticRgbAsync(RgbRed, RgbGreen, RgbBlue, CancellationToken.None);
                        _service.RgbRed = RgbRed;
                        _service.RgbGreen = RgbGreen;
                        _service.RgbBlue = RgbBlue;
                        _service.SaveProfile(SelectedProfile);
                    }
                }
                catch { }
            }, ct);
        }

        private ushort _rgbBrightness = 750;
        public ushort RgbBrightness
        {
            get => _rgbBrightness;
            set
            {
                if (SetProperty(ref _rgbBrightness, value))
                {
                    OnPropertyChanged(nameof(CurrentColorHex));
                    DebounceBrightnessUpdate();
                }
            }
        }

        private CancellationTokenSource? _brightnessDebounceCts;
        private void DebounceBrightnessUpdate()
        {
            _brightnessDebounceCts?.Cancel();
            _brightnessDebounceCts = new CancellationTokenSource();
            var ct = _brightnessDebounceCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, ct);
                    if (!ct.IsCancellationRequested)
                    {
                        await _service.SetBrightnessAsync(RgbBrightness, CancellationToken.None);
                        _service.RgbBrightness = RgbBrightness;
                        _service.SaveProfile(SelectedProfile);
                    }
                }
                catch { }
            }, ct);
        }

        private byte _rumbleIntensity = 100;
        public byte RumbleIntensity
        {
            get => _rumbleIntensity;
            set
            {
                var clamped = Math.Clamp(value, (byte)0, (byte)100);
                if (SetProperty(ref _rumbleIntensity, clamped))
                {
                    OnPropertyChanged(nameof(RumbleIntensityText));
                    DebounceRumbleIntensityUpdate();
                }
            }
        }

        public string RumbleIntensityText => $"{RumbleIntensity}%";

        private CancellationTokenSource? _rumbleDebounceCts;
        private void DebounceRumbleIntensityUpdate()
        {
            _rumbleDebounceCts?.Cancel();
            _rumbleDebounceCts = new CancellationTokenSource();
            var ct = _rumbleDebounceCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, ct);
                    if (!ct.IsCancellationRequested)
                    {
                        await _service.SetRumbleIntensityAsync(RumbleIntensity, CancellationToken.None);
                        _service.RumbleIntensity = RumbleIntensity;
                        _service.SaveProfile(SelectedProfile);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => EventLog.Add($"[WARN] Rumble command failed: {ex.Message}"));
                }
            }, ct);
        }

        private readonly string[] _themeResourceNames =
        {
            "DefaultTheme",
            "ScufTheme",
            "AuroraTheme",
            "CrimsonNoirTheme",
            "PolarTheme",
            "VaporwaveTheme",
            "VoltTheme"
        };

        public ObservableCollection<string> ThemeOptions { get; } = new ObservableCollection<string>
        {
            "Default",
            "Scuf",
            "Aurora",
            "Crimson Noir",
            "Polar",
            "Vaporwave",
            "Volt"
        };

        public ObservableCollection<string> LanguageOptions { get; } = new ObservableCollection<string>();

        private int _selectedLanguageIndex;
        public int SelectedLanguageIndex
        {
            get => _selectedLanguageIndex;
            set
            {
                var languageIndex = Math.Clamp(value, 0, LocalizationService.LanguageCodes.Count - 1);
                if (SetProperty(ref _selectedLanguageIndex, languageIndex))
                {
                    ApplyLanguage(LocalizationService.GetLanguageCode(languageIndex));
                }
            }
        }

        public ObservableCollection<string> IconVariantOptions { get; } = new ObservableCollection<string>
        {
            "Default",
            "Light",
            "Retro",
            "Alt",
            "Alt 2"
        };

        public ObservableCollection<string> KeyboardMouseIconVariantOptions { get; } = new ObservableCollection<string>
        {
            "Dark",
            "White",
            "Alt",
            "Retro",
            "Vintage"
        };

        public bool StartWithWindows
        {
            get => _service.StartWithWindows;
            set
            {
                if (_service.StartWithWindows != value)
                {
                    _service.StartWithWindows = value;
                    OnPropertyChanged(nameof(StartWithWindows));
                }
            }
        }

        public bool StartMinimized
        {
            get => _service.StartMinimized;
            set
            {
                if (_service.StartMinimized != value)
                {
                    _service.StartMinimized = value;
                    OnPropertyChanged(nameof(StartMinimized));
                }
            }
        }

        public ObservableCollection<string> CloseBehaviorOptions { get; } = new ObservableCollection<string>();

        private int _selectedCloseBehaviorIndex;
        public int SelectedCloseBehaviorIndex
        {
            get => _selectedCloseBehaviorIndex;
            set
            {
                var behaviorIndex = Math.Clamp(value, 0, 1);
                if (SetProperty(ref _selectedCloseBehaviorIndex, behaviorIndex))
                {
                    _service.CloseBehavior = behaviorIndex == 0
                        ? ApplicationCloseBehavior.MinimizeToTray
                        : ApplicationCloseBehavior.ExitApplication;
                }
            }
        }

        public bool AskBeforeClosing
        {
            get => _service.AskBeforeClosing;
            set
            {
                if (_service.AskBeforeClosing != value)
                {
                    _service.AskBeforeClosing = value;
                    OnPropertyChanged(nameof(AskBeforeClosing));
                }
            }
        }

        private int _selectedThemeIndex = 0;
        public int SelectedThemeIndex
        {
            get => _selectedThemeIndex;
            set
            {
                int themeIndex = Math.Clamp(value, 0, _themeResourceNames.Length - 1);
                if (SetProperty(ref _selectedThemeIndex, themeIndex))
                {
                    string themeName = _themeResourceNames[themeIndex];
                    _service.ThemeName = themeName;
                    App.ChangeTheme(themeName);
                }
            }
        }

        private int GetThemeIndex(string? themeName)
        {
            var index = Array.FindIndex(_themeResourceNames, theme =>
                string.Equals(theme, themeName, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : 0;
        }

        private int _selectedIconVariantIndex;
        public int SelectedIconVariantIndex
        {
            get => _selectedIconVariantIndex;
            set
            {
                var variantIndex = Math.Clamp(value, 0, IconVariantOptions.Count - 1);
                if (SetProperty(ref _selectedIconVariantIndex, variantIndex))
                {
                    MappingIconCatalog.XGamepadVariant = IconVariantOptions[variantIndex];
                    NotifyIconVariantChanged();
                }
            }
        }

        private int _selectedKeyboardMouseIconVariantIndex;
        public int SelectedKeyboardMouseIconVariantIndex
        {
            get => _selectedKeyboardMouseIconVariantIndex;
            set
            {
                var variantIndex = Math.Clamp(value, 0, KeyboardMouseIconVariantOptions.Count - 1);
                if (SetProperty(ref _selectedKeyboardMouseIconVariantIndex, variantIndex))
                {
                    MappingIconCatalog.KeyboardMouseVariant = KeyboardMouseIconVariantOptions[variantIndex];
                    NotifyIconVariantChanged();
                }
            }
        }

        private bool _isRemapping;
        public bool IsRemapping { get => _isRemapping; set => SetProperty(ref _isRemapping, value); }

        private string _remappingTargetName = string.Empty;
        public string RemappingTargetName
        {
            get => _remappingTargetName;
            set
            {
                if (SetProperty(ref _remappingTargetName, value))
                {
                    OnPropertyChanged(nameof(RemappingTargetDisplayName));
                    OnPropertyChanged(nameof(RemappingTargetIcon));
                    OnPropertyChanged(nameof(RemappingTargetFallbackText));
                    OnPropertyChanged(nameof(HasRemappingTargetIcon));
                    OnPropertyChanged(nameof(HasRemappingTargetFallback));
                    NotifyProfileActionStateChanged();
                }
            }
        }

        public string RemappingTargetDisplayName => string.IsNullOrWhiteSpace(RemappingTargetName)
            ? "-"
            : GetButtonDisplayName(RemappingTargetName);
        public Avalonia.Media.Imaging.Bitmap? RemappingTargetIcon => GetAdvancedMappingSourceIcon(RemappingTargetName);
        public string RemappingTargetFallbackText => GetMappingFallbackText(RemappingTargetName);
        public bool HasRemappingTargetIcon => RemappingTargetIcon != null;
        public bool HasRemappingTargetFallback => RemappingTargetIcon == null && !string.IsNullOrWhiteSpace(RemappingTargetFallbackText);

        private void NotifyIconVariantChanged()
        {
            RemapVersion++;
            RefreshAdvancedMappings();
            OnPropertyChanged(nameof(DetectedTargetIcon));
            OnPropertyChanged(nameof(RemappingTargetIcon));
            OnPropertyChanged(nameof(HasRemappingTargetIcon));
            OnPropertyChanged(nameof(HasRemappingTargetFallback));
            OnPropertyChanged(nameof(SelectedShiftModifierIcon));
            OnPropertyChanged(nameof(HasSelectedShiftModifierIcon));
            OnPropertyChanged(nameof(HasSelectedShiftModifierFallback));
        }

        private readonly System.Collections.Generic.HashSet<string> _visibleRemapGestureTabs = new();
        private int _remapInputTabIndex;
        public int RemapInputTabIndex
        {
            get => _remapInputTabIndex;
            set
            {
                if (value == 3 && !IsActionInputTabEnabled)
                {
                    value = 0;
                }

                int oldIndex = _remapInputTabIndex;
                if (SetProperty(ref _remapInputTabIndex, value))
                {
                    RemapInputTabTransitionDirection = value > oldIndex ? 1 : -1;
                    OnPropertyChanged(nameof(IsRemapInputTabXbox));
                    OnPropertyChanged(nameof(IsRemapInputTabKeyboard));
                    OnPropertyChanged(nameof(IsRemapInputTabMouse));
                    OnPropertyChanged(nameof(IsRemapInputTabAction));
                    OnPropertyChanged(nameof(IsRemapInputTabMacro));

                    if (IsRemapInputTabMacro)
                    {
                        LoadMacroEditorForCurrentTarget();
                    }
                    if (oldIndex == 4 && value != 4)
                    {
                        StopMacroRecordingCommand.Execute(null);
                    }
                }
            }
        }

        public static int RemapInputTabTransitionDirection { get; set; } = 1;
        public static bool SuppressRemapInputTabTransition { get; set; }

        public bool IsRemapInputTabXbox => RemapInputTabIndex == 0;
        public bool IsRemapInputTabKeyboard => RemapInputTabIndex == 1;
        public bool IsRemapInputTabMouse => RemapInputTabIndex == 2;
        public bool IsRemapInputTabAction => RemapInputTabIndex == 3;
        public bool IsRemapInputTabMacro => RemapInputTabIndex == 4;
        public bool IsActionInputTabEnabled => CanUseActionsForGesture(SelectedRemapGestureType);

        private bool _isMacroRecording;
        private long _macroRecordingLastEventMs;
        private readonly HashSet<string> _macroRecordingHeldKeys = new();
        private bool _isLoadingMacroEditor;

        public bool IsMacroRecording
        {
            get => _isMacroRecording;
            set
            {
                if (SetProperty(ref _isMacroRecording, value))
                {
                    OnPropertyChanged(nameof(MacroRecordingStatus));
                }
            }
        }

        private bool _macroRepeatWhileHeld;
        public bool MacroRepeatWhileHeld
        {
            get => _macroRepeatWhileHeld;
            set
            {
                if (SetProperty(ref _macroRepeatWhileHeld, value))
                {
                    SaveCurrentMacro();
                }
            }
        }

        private bool _macroRecordFixedDelay;
        public bool MacroRecordFixedDelay
        {
            get => _macroRecordFixedDelay;
            set => SetProperty(ref _macroRecordFixedDelay, value);
        }

        private int _macroFixedDelayMs = 50;
        public int MacroFixedDelayMs
        {
            get => _macroFixedDelayMs;
            set => SetProperty(ref _macroFixedDelayMs, value);
        }

        public string MacroRecordingStatus => IsMacroRecording ? "Grabando" : "Edicion";
        public ObservableCollection<MacroStepViewModel> MacroSteps { get; } = new ObservableCollection<MacroStepViewModel>();
        public ObservableCollection<MacroFlowItemViewModel> MacroFlowItems { get; } = new ObservableCollection<MacroFlowItemViewModel>();

        public ObservableCollection<MacroDefinition> SavedMacros { get; } = new ObservableCollection<MacroDefinition>();
        public bool HasSavedMacros => SavedMacros.Count > 0;

        private bool _isSaveMacroDialogOpen;
        public bool IsSaveMacroDialogOpen
        {
            get => _isSaveMacroDialogOpen;
            set => SetProperty(ref _isSaveMacroDialogOpen, value);
        }

        private bool _isLoadMacroDialogOpen;
        public bool IsLoadMacroDialogOpen
        {
            get => _isLoadMacroDialogOpen;
            set => SetProperty(ref _isLoadMacroDialogOpen, value);
        }

        private string _newMacroName = string.Empty;
        public string NewMacroName
        {
            get => _newMacroName;
            set => SetProperty(ref _newMacroName, value);
        }


        private string _detectedTarget = string.Empty;
        public string DetectedTarget
        {
            get => _detectedTarget;
            set
            {
                if (SetProperty(ref _detectedTarget, value))
                {
                    OnPropertyChanged(nameof(DetectedTargetIcon));
                    if (AcceptRemapCommand is RelayCommand cmd)
                    {
                        cmd.RaiseCanExecuteChanged();
                    }
                }
            }
        }

        public Avalonia.Media.Imaging.Bitmap? DetectedTargetIcon
        {
            get
            {
                var inputKind = RemapInputTabIndex switch
                {
                    1 => MappingIconInputKind.Keyboard,
                    2 => MappingIconInputKind.Mouse,
                    _ => MappingIconInputKind.Gamepad
                };

                return MappingIconCatalog.GetBitmap(_detectedTarget, inputKind);
            }
        }

        private bool _isTriggerOutputDialVisible;
        private bool _isTriggerOutputDialMounted;
        private double _triggerOutputDialOpacity;
        private int _triggerOutputDialFadeGeneration;
        private string _hoveredTriggerOutputTarget = string.Empty;
        private int _triggerOutputPercent = 100;

        public bool IsTriggerOutputDialVisible
        {
            get => _isTriggerOutputDialVisible;
            private set => SetProperty(ref _isTriggerOutputDialVisible, value);
        }

        public bool IsTriggerOutputDialMounted
        {
            get => _isTriggerOutputDialMounted;
            private set => SetProperty(ref _isTriggerOutputDialMounted, value);
        }

        public double TriggerOutputDialOpacity
        {
            get => _triggerOutputDialOpacity;
            private set => SetProperty(ref _triggerOutputDialOpacity, Math.Clamp(value, 0.0, 1.0));
        }

        public int TriggerOutputPercent
        {
            get => _triggerOutputPercent;
            private set
            {
                if (SetProperty(ref _triggerOutputPercent, Math.Clamp(value, 0, 100)))
                {
                    OnPropertyChanged(nameof(TriggerOutputPercentText));
                }
            }
        }

        public string TriggerOutputPercentText => $"{TriggerOutputPercent}%";

        public void BeginTriggerOutputSelection(string target)
        {
            if (!VirtualTarget.IsTriggerTarget(target))
            {
                return;
            }

            _hoveredTriggerOutputTarget = VirtualTarget.GetBaseTarget(
                VirtualTarget.WithTriggerOutputPercent(target, 100));
            var detectedBaseTarget = VirtualTarget.GetBaseTarget(DetectedTarget);
            TriggerOutputPercent = detectedBaseTarget == _hoveredTriggerOutputTarget
                ? VirtualTarget.GetTriggerOutputPercent(DetectedTarget)
                : 100;

            var fadeGeneration = ++_triggerOutputDialFadeGeneration;
            IsTriggerOutputDialMounted = true;
            IsTriggerOutputDialVisible = true;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (fadeGeneration == _triggerOutputDialFadeGeneration && IsTriggerOutputDialVisible)
                    {
                        TriggerOutputDialOpacity = 1.0;
                    }
                },
                DispatcherPriority.Background);
        }

        public void AdjustTriggerOutputSelection(double wheelDelta)
        {
            if (!IsTriggerOutputDialVisible || Math.Abs(wheelDelta) < 0.01)
            {
                return;
            }

            var nextValue = TriggerOutputPercent + (wheelDelta > 0 ? 5 : -5);
            TriggerOutputPercent = nextValue <= 0 ? 0 : nextValue;
        }

        public void EndTriggerOutputSelection()
        {
            IsTriggerOutputDialVisible = false;
            _hoveredTriggerOutputTarget = string.Empty;
            TriggerOutputDialOpacity = 0.0;
            var fadeGeneration = ++_triggerOutputDialFadeGeneration;
            DispatcherTimer.RunOnce(
                () =>
                {
                    if (fadeGeneration == _triggerOutputDialFadeGeneration && !IsTriggerOutputDialVisible)
                    {
                        IsTriggerOutputDialMounted = false;
                    }
                },
                TimeSpan.FromMilliseconds(180));
        }

        private string ApplyPendingTriggerOutputPercent(string target)
        {
            if (!VirtualTarget.IsTriggerTarget(target))
            {
                return target;
            }

            var baseTarget = VirtualTarget.GetBaseTarget(
                VirtualTarget.WithTriggerOutputPercent(target, 100));
            return IsTriggerOutputDialVisible && baseTarget == _hoveredTriggerOutputTarget
                ? VirtualTarget.WithTriggerOutputPercent(baseTarget, TriggerOutputPercent)
                : VirtualTarget.WithTriggerOutputPercent(target, VirtualTarget.GetTriggerOutputPercent(target));
        }

        private string GetGamepadIconName(string target) => target switch
        {
            "A" => "T_X_A_Color.png",
            "B" => "T_X_B_Color.png",
            "X" => "T_X_X_Color.png",
            "Y" => "T_X_Y_Color.png",
            "LB" => "T_X_LB.png",
            "LeftShoulder" => "T_X_LB.png",
            "LT" => "T_X_LT.png",
            "LeftTrigger" => "T_X_LT.png",
            "RB" => "T_X_RB.png",
            "RightShoulder" => "T_X_RB.png",
            "RT" => "T_X_RT.png",
            "RightTrigger" => "T_X_RT.png",
            "Up" => "T_X_Dpad_Up.png",
            "Down" => "T_X_Dpad_Down.png",
            "Left" => "T_X_Dpad_Left.png",
            "Right" => "T_X_Dpad_Right.png",
            "L3" => "T_X_Left_Stick_Click.png",
            "LeftThumb" => "T_X_Left_Stick_Click.png",
            "R3" => "T_X_Right_Stick_Click.png",
            "RightThumb" => "T_X_Right_Stick_Click.png",
            "Start" => "T_X_Share.png",
            "Back" => "T_X_Share-1.png",
            "LS_Up" => "T_X_L_UP.png",
            "LS_Down" => "T_X_L_Down.png",
            "LS_Left" => "T_X_L_Left.png",
            "LS_Right" => "T_X_L_Right.png",
            "RS_Up" => "T_X_R_UP.png",
            "RS_Down" => "T_X_R_Down.png",
            "RS_Left" => "T_X_R_Left.png",
            "RS_Right" => "T_X_R_Right.png",
            _ => ""
        };

        private string GetKeyboardIconName(string target) => target switch
        {
            "Escape" => "T_Esc_Key_Dark.png",
            "F1" => "T_F1_Key_Dark.png",
            "F2" => "T_F2_Key_Dark.png",
            "F3" => "T_F3_Key_Dark.png",
            "F4" => "T_F4_Key_Dark.png",
            "F5" => "T_F5_Key_Dark.png",
            "F6" => "T_F6_Key_Dark.png",
            "F7" => "T_F7_Key_Dark.png",
            "F8" => "T_F8_Key_Dark.png",
            "F9" => "T_F9_Key_Dark.png",
            "F10" => "T_F10_Key_Dark.png",
            "F11" => "T_F11_Key_Dark.png",
            "F12" => "T_F12_Key_Dark.png",
            "Delete" => "T_Del_Key_Dark.png",
            "OemTilde" => "T_Tilde_Key_Dark.png",
            "D1" => "T_1_Key_Dark.png",
            "D2" => "T_2_Key_Dark.png",
            "D3" => "T_3_Key_Dark.png",
            "D4" => "T_4_Key_Dark.png",
            "D5" => "T_5_Key_Dark.png",
            "D6" => "T_6_Key_Dark.png",
            "D7" => "T_7_Key_Dark.png",
            "D8" => "T_8_Key_Dark.png",
            "D9" => "T_9_Key_Dark.png",
            "D0" => "T_0_Key_Dark.png",
            "Subtract" => "T_Minus_Key_Dark.png",
            "Add" => "T_Plus_Key_Dark.png",
            "Back" => "T_BackSpace_Key_Dark.png",
            "Tab" => "T_Tab_Key_Dark.png",
            "Q" => "T_Q_Key_Dark.png",
            "W" => "T_W_Key_Dark.png",
            "E" => "T_E_Key_Dark.png",
            "R" => "T_R_Key_Dark.png",
            "T" => "T_T_Key_Dark.png",
            "Y" => "T_Y_Key_Dark.png",
            "U" => "T_U_Key_Dark.png",
            "I" => "T_I_Key_Dark.png",
            "O" => "T_O_Key_Dark.png",
            "P" => "T_P_Key_Dark.png",
            "OemOpenBrackets" => "T_Brackets_L_Key_Dark.png",
            "OemCloseBrackets" => "T_Brackets_R_Key_Dark.png",
            "OemPipe" => "T_Slash_Key_Dark.png",
            "Capital" => "T_CapsLock_Key_Dark.png",
            "A" => "T_A_Key_Dark.png",
            "S" => "T_S_Key_Dark.png",
            "D" => "T_D_Key_Dark.png",
            "F" => "T_F_Key_Dark.png",
            "G" => "T_G_Key_Dark.png",
            "H" => "T_H_Key_Dark.png",
            "J" => "T_J_Key_Dark.png",
            "K" => "T_K_Key_Dark.png",
            "L" => "T_L_Key_Dark.png",
            "OemSemicolon" => "T_Semicolon_Key_Dark.png",
            "OemQuotes" => "T_Quotation_Key_Dark.png",
            "Return" => "T_Enter_Key_Dark.png",
            "LeftShift" => "T_Shift_Key_Dark.png",
            "Z" => "T_Z_Key_Dark.png",
            "X" => "T_X_Key_Dark.png",
            "C" => "T_C_Key_Dark.png",
            "V" => "T_V_Key_Dark.png",
            "B" => "T_B_Key_Dark.png",
            "N" => "T_N_Key_Dark.png",
            "M" => "T_M_Key_Dark.png",
            "OemComma" => "T_Keyboard_R_Key_Dark.png",
            "OemPeriod" => "T_Keyboard_R_Key_Dark-1.png",
            "OemQuestion" => "T_Question_Mark_Key_Dark.png",
            "RightShift" => "T_Shift_Key_Dark.png",
            "LeftCtrl" => "T_Crtl_Key_Dark.png",
            "LWin" => "T_Keyboard_Mouse_Key_Sprite.png",
            "LeftAlt" => "T_Alt_Key_Dark.png",
            "Space" => "T_Space_Key_Dark.png",
            "RightAlt" => "T_Alt_Key_Dark.png",
            "RightCtrl" => "T_Crtl_Key_Dark.png",
            "Insert" => "T_Ins_Key_Dark.png",
            "Home" => "T_Home_Key_Dark.png",
            "PageUp" => "T_PageUp_Key_Dark.png",
            "PageDown" => "T_PageDown_Key_Dark.png",
            "NumLock" => "T_NumLock_Key_Dark.png",
            "Divide" => "T_Slash_Key_Dark.png",
            "Multiply" => "T_Asterisk_Key_Dark.png",
            "NumPad7" => "T_7_Key_Dark.png",
            "NumPad8" => "T_8_Key_Dark.png",
            "NumPad9" => "T_9_Key_Dark.png",
            "NumPad4" => "T_4_Key_Dark.png",
            "NumPad5" => "T_5_Key_Dark.png",
            "NumPad6" => "T_6_Key_Dark.png",
            "NumPad1" => "T_1_Key_Dark.png",
            "NumPad2" => "T_2_Key_Dark.png",
            "NumPad3" => "T_3_Key_Dark.png",
            "NumPad0" => "T_0_Key_Dark.png",
            "Decimal" => "T_Keyboard_R_Key_Dark-1.png",
            "Up" => "T_Up_Key_Dark.png",
            "Down" => "T_Down_Key_Dark.png",
            "Left" => "T_Left_Key_Dark.png",
            "Right" => "T_Right_Key_Dark.png",
            _ => ""
        };

        private string GetMouseIconName(string target) => target switch
        {
            "MouseLeft" => "T_Mouse_Left_Key_Dark.png",
            "MouseRight" => "T_Mouse_Right_Key_Dark.png",
            "MouseMiddle" => "T_Mouse_Middle_Key_Dark.png",
            "MouseX1" => "T_Mouse_X_Key_Dark.png",
            "MouseX2" => "T_Mouse_Y_Key_Dark.png",
            "ScrollUp" => "T_Mouse_Scroll_Up_Key_Dark_Key_Dark.png",
            "ScrollDown" => "T_Mouse_Scroll_Down_Key_Dark_Key_Dark.png",
            _ => ""
        };

        private string _selectedRemapGestureType = RemapGestureTypes.Simple;
        public string SelectedRemapGestureType
        {
            get => _selectedRemapGestureType;
            set
            {
                if (SetProperty(ref _selectedRemapGestureType, string.IsNullOrWhiteSpace(value) ? RemapGestureTypes.Simple : value))
                {
                    if (!CanUseSelectedGesture())
                    {
                        _selectedRemapGestureType = RemapGestureTypes.Simple;
                        OnPropertyChanged(nameof(SelectedRemapGestureType));
                    }

                    if (ActionProfileWhileHeld && !IsProfileWhileHeldOptionEnabled)
                    {
                        ActionProfileWhileHeld = false;
                    }

                    UpdateDetectedTargetForSelectedGesture();
                    LoadActionModalStateFromTarget(DetectedTarget);
                    LoadGestureDelayForSelectedGesture();
                    if (!IsActionInputTabEnabled && RemapInputTabIndex == 3)
                    {
                        RemapInputTabIndex = 0;
                    }

                    LoadMacroEditorForCurrentTarget();
                    NotifyRemapGestureStateChanged();
                }
            }
        }

        public string SelectedRemapGestureLabel => RemapGestureTypes.GetLabel(SelectedRemapGestureType);
        public string SelectedRemapActionDisplayName => SelectedRemapGestureType == RemapGestureTypes.Simple
            ? LocalizationService.Get("PrimaryAction")
            : SelectedRemapGestureLabel;
        private bool _isLoadingGestureDelay;
        private int _selectedGestureDelayMs;
        public int SelectedGestureDelayMs
        {
            get => _selectedGestureDelayMs;
            set
            {
                var clampedValue = IsGestureDelayEditorVisible ? ClampGestureDelayMs(SelectedRemapGestureType, value) : 0;
                if (SetProperty(ref _selectedGestureDelayMs, clampedValue) && !_isLoadingGestureDelay)
                {
                    SaveSelectedGestureDelay();
                }
            }
        }

        public bool IsGestureDelayEditorVisible =>
            SelectedRemapGestureType == RemapGestureTypes.DoubleTap || SelectedRemapGestureType == RemapGestureTypes.Hold;
        public string SelectedGestureDelayLabel => SelectedRemapGestureType == RemapGestureTypes.Hold
            ? LocalizationService.Get("HoldDelayLabel")
            : LocalizationService.Get("DoubleTapDelayLabel");
        public bool IsSimpleGestureSelected => SelectedRemapGestureType == RemapGestureTypes.Simple;
        public bool IsDoubleTapGestureSelected => SelectedRemapGestureType == RemapGestureTypes.DoubleTap;
        public bool IsHoldGestureSelected => SelectedRemapGestureType == RemapGestureTypes.Hold;
        public bool IsPressStartGestureSelected => SelectedRemapGestureType == RemapGestureTypes.PressStart;
        public bool IsPressReleaseGestureSelected => SelectedRemapGestureType == RemapGestureTypes.PressRelease;
        public bool IsDoubleTapTabVisible => IsRemapping && IsGestureTabVisible(RemapGestureTypes.DoubleTap);
        public bool IsHoldTabVisible => IsRemapping && IsGestureTabVisible(RemapGestureTypes.Hold);
        public bool IsPressStartTabVisible => IsRemapping && IsGestureTabVisible(RemapGestureTypes.PressStart);
        public bool IsPressReleaseTabVisible => IsRemapping && IsGestureTabVisible(RemapGestureTypes.PressRelease);

        // Navigation State
        private int _selectedSectionIndex = 0;
        public int SelectedSectionIndex
        {
            get => _selectedSectionIndex;
            set
            {
                var clampedValue = Math.Clamp(value, 0, 2);
                if (SetProperty(ref _selectedSectionIndex, clampedValue))
                {
                    if (clampedValue == 0) SelectedSection = "Mapping";
                    else if (clampedValue == 1) SelectedSection = "Profiles";
                    else if (clampedValue == 2) SelectedSection = "Settings";
                }
            }
        }

        private string _selectedSection = "Mapping";
        public string SelectedSection
        {
            get => _selectedSection;
            set
            {
                if (SetProperty(ref _selectedSection, value))
                {
                    if (value == "Mapping") SelectedSectionIndex = 0;
                    else if (value == "Profiles") SelectedSectionIndex = 1;
                    else if (value == "Settings") SelectedSectionIndex = 2;

                    OnPropertyChanged(nameof(IsMappingSectionSelected));
                    OnPropertyChanged(nameof(IsProfilesSectionSelected));
                    OnPropertyChanged(nameof(IsSettingsSectionSelected));
                }
            }
        }

        public bool IsMappingSectionSelected => SelectedSection == "Mapping";
        public bool IsProfilesSectionSelected => SelectedSection == "Profiles";
        public bool IsSettingsSectionSelected => SelectedSection == "Settings";

        public ObservableCollection<string> Profiles { get; } = new ObservableCollection<string>();
        public ObservableCollection<ProfileItemViewModel> ProfileCards { get; } = new ObservableCollection<ProfileItemViewModel>();

        private string _selectedProfile = string.Empty;
        public string SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (SetProperty(ref _selectedProfile, value))
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        _service.LoadProfile(value);
                        OnPropertyChanged(nameof(SelectedShiftModifier));
                        InitializePaddleRemaps();
                        InitializeGKeyRemaps();
                        if (IsMappingModeAdvanced)
                        {
                            RefreshAdvancedMappings();
                        }
                        RefreshSelectedProfileCards();
                        RemapVersion++;
                    }
                }
            }
        }

        private string _selectedTab = "Mapping";
        public string SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(IsMappingTabSelected));
                    OnPropertyChanged(nameof(IsTriggersTabSelected));
                    OnPropertyChanged(nameof(IsSticksTabSelected));
                    OnPropertyChanged(nameof(IsDetailsTabSelected));
                }
            }
        }

        public bool IsMappingTabSelected => SelectedTab == "Mapping";
        public bool IsTriggersTabSelected => SelectedTab == "Triggers";
        public bool IsSticksTabSelected => SelectedTab == "Sticks";
        public bool IsDetailsTabSelected => SelectedTab == "Details";

        private bool _isMappingModeSimple = true;
        public bool IsMappingModeSimple
        {
            get => _isMappingModeSimple;
            set
            {
                if (SetProperty(ref _isMappingModeSimple, value))
                {
                    OnPropertyChanged(nameof(IsMappingModeAdvanced));
                    OnPropertyChanged(nameof(MappingModeIndex));
                    if (!value) RefreshAdvancedMappings();
                }
            }
        }

        public bool IsMappingModeAdvanced
        {
            get => !_isMappingModeSimple;
            set
            {
                if (value)
                {
                    IsMappingModeSimple = false;
                }
                else
                {
                    IsMappingModeSimple = true;
                }
            }
        }

        public int MappingModeIndex
        {
            get => IsMappingModeSimple ? 0 : 1;
            set => IsMappingModeSimple = value <= 0;
        }

        public ObservableCollection<string> ShiftModifierOptions { get; } = new ObservableCollection<string>
        {
            "Paddle_R4", "Paddle_R5", "Paddle_L4", "Paddle_L5", "SAX_L", "SAX_R",
            "G1", "G2", "G3", "G4", "G5",
            "LeftShoulder", "RightShoulder", "LeftThumb", "RightThumb", "A", "B", "X", "Y"
        };

        private bool _isShiftModifierPickerOpen;
        public bool IsShiftModifierPickerOpen
        {
            get => _isShiftModifierPickerOpen;
            set
            {
                if (SetProperty(ref _isShiftModifierPickerOpen, value))
                {
                    NotifySelectedShiftModifierChanged();
                }
            }
        }

        private string _pendingShiftModifier = string.Empty;
        public string PendingShiftModifier
        {
            get => _pendingShiftModifier;
            set
            {
                SetProperty(ref _pendingShiftModifier, value);
                NotifySelectedShiftModifierChanged();
            }
        }

        public string SelectedShiftModifier
        {
            get => _service.ShiftModifierButton;
            set => ApplyShiftModifierSelection(value, saveProfile: true);
        }

        private string CurrentShiftModifierName => IsShiftModifierPickerOpen && !string.IsNullOrEmpty(PendingShiftModifier)
            ? PendingShiftModifier
            : _service.ShiftModifierButton;

        public string SelectedShiftModifierDisplayName
        {
            get
            {
                return string.IsNullOrWhiteSpace(CurrentShiftModifierName)
                    ? LocalizationService.Get("NoModifier")
                    : GetButtonDisplayName(CurrentShiftModifierName);
            }
        }

        public Avalonia.Media.Imaging.Bitmap? SelectedShiftModifierIcon => GetAdvancedMappingSourceIcon(CurrentShiftModifierName);
        public string SelectedShiftModifierFallbackText => GetMappingFallbackText(CurrentShiftModifierName);
        public bool HasSelectedShiftModifier => !string.IsNullOrWhiteSpace(CurrentShiftModifierName);
        public bool HasSelectedShiftModifierIcon => SelectedShiftModifierIcon != null;
        public bool HasSelectedShiftModifierFallback => SelectedShiftModifierIcon == null && !string.IsNullOrWhiteSpace(SelectedShiftModifierFallbackText);

        private bool _isShiftMode;
        private bool _wasShiftModifierHeld;
        private bool _shiftModeActivatedByHeldModifier;
        public bool IsShiftMode
        {
            get => _isShiftMode;
            set
            {
                if (SetProperty(ref _isShiftMode, value))
                {
                    ZeroCue.DataProbe.Converters.RemapContext.IsShiftModeUi = value;
                    InitializePaddleRemaps();
                    InitializeGKeyRemaps();
                    RemapVersion++;
                    OnPropertyChanged(nameof(VisibleAdvancedMappingGroups));
                    OnPropertyChanged(nameof(HasVisibleAdvancedMappings));
                    NotifyProfileActionStateChanged();
                }
            }
        }

        private int _remapVersion;
        public int RemapVersion { get => _remapVersion; set => SetProperty(ref _remapVersion, value); }

        public ICommand AcceptRemapCommand { get; }
        public ICommand MapActionCommand { get; }
        public ICommand ManualMapCommand { get; }
        public ICommand TestRumbleCommand { get; }
        public ICommand CreateProfileCommand { get; }

        // Observable log list
        public ObservableCollection<string> EventLog { get; } = new ObservableCollection<string>();

        // Remapping collection (6 entries for paddles, 5 entries for G-Keys)
        public ObservableCollection<PaddleRemapEntry> PaddleRemaps { get; } = new ObservableCollection<PaddleRemapEntry>();
        public ObservableCollection<GKeyRemapEntry> GKeyRemaps { get; } = new ObservableCollection<GKeyRemapEntry>();
        public ObservableCollection<AdvancedMappingGroup> AdvancedMappingGroups { get; } = new ObservableCollection<AdvancedMappingGroup>();
        public ObservableCollection<AdvancedMappingGroup> StandardAdvancedMappingGroups { get; } = new ObservableCollection<AdvancedMappingGroup>();
        public ObservableCollection<AdvancedMappingGroup> ShiftAdvancedMappingGroups { get; } = new ObservableCollection<AdvancedMappingGroup>();
        public ObservableCollection<AdvancedMappingGroup> VisibleAdvancedMappingGroups => IsShiftMode ? ShiftAdvancedMappingGroups : StandardAdvancedMappingGroups;
        public bool HasAdvancedMappings => AdvancedMappingGroups.Count > 0;
        public bool HasStandardAdvancedMappings => StandardAdvancedMappingGroups.Count > 0;
        public bool HasShiftAdvancedMappings => ShiftAdvancedMappingGroups.Count > 0;
        public bool HasVisibleAdvancedMappings => VisibleAdvancedMappingGroups.Count > 0;
        public int StandardAdvancedMappingCount => StandardAdvancedMappingGroups.Sum(group => group.Items.Count(item => item.HasAnyMapping));
        public int ShiftAdvancedMappingCount => ShiftAdvancedMappingGroups.Sum(group => group.Items.Count(item => item.HasAnyMapping));

        private string _hoveredDetailsInput = string.Empty;
        public string HoveredDetailsInput
        {
            get => _hoveredDetailsInput;
            private set
            {
                if (SetProperty(ref _hoveredDetailsInput, NormalizeDetailsInputName(value)))
                {
                    UpdateDetailsInputHighlights();
                }
            }
        }

        // Observable status properties mirroring the service
        public bool IsConnected => _service.IsConnected;
        public bool IsConnecting => _service.IsConnecting;
        public bool IsWaitingForConnection => !IsConnected && !IsConnecting;
        public bool IsViGEmActive => _service.IsViGEmActive;
        public string StatusText => _service.StatusText;
        public string StatusDetail => _service.StatusDetail;
        public ConnectionStatusState ConnectionStatusState => _service.ConnectionStatusState;
        public bool IsStatusNone => ConnectionStatusState == ConnectionStatusState.None;
        public bool IsStatusReceiverOnly => ConnectionStatusState == ConnectionStatusState.ReceiverOnly;
        public bool IsStatusWaiting => IsStatusNone || IsStatusReceiverOnly;
        public bool IsStatusWirelessConnecting => ConnectionStatusState == ConnectionStatusState.WirelessConnecting;
        public bool IsStatusWirelessConnected => ConnectionStatusState == ConnectionStatusState.WirelessConnected;
        public bool IsStatusUsbConnecting => ConnectionStatusState == ConnectionStatusState.UsbConnecting;
        public bool IsStatusUsbConnected => ConnectionStatusState == ConnectionStatusState.UsbConnected;
        public bool IsStatusConnecting => IsStatusWirelessConnecting || IsStatusUsbConnecting;
        public bool IsStatusConnected => IsStatusWirelessConnected || IsStatusUsbConnected;
        public bool IsStatusError => ConnectionStatusState == ConnectionStatusState.Error;

        private bool _isEcoModeEnabled;
        public bool IsEcoModeEnabled
        {
            get => _isEcoModeEnabled;
            set
            {
                if (SetProperty(ref _isEcoModeEnabled, value))
                {
                    _ = ApplyEcoModeAsync(value);
                    _service.EcoMode = value;
                    _service.SaveProfile(SelectedProfile);
                }
            }
        }

        private async Task ApplyEcoModeAsync(bool value)
        {
            try
            {
                await _service.SetEcoModeAsync(value, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => EventLog.Add($"[WARN] Eco mode command failed: {ex.Message}"));
            }
        }

        // Digital buttons states
        private bool _buttonA; public bool ButtonA { get => _buttonA; private set => SetProperty(ref _buttonA, value); }
        private bool _buttonB; public bool ButtonB { get => _buttonB; private set => SetProperty(ref _buttonB, value); }
        private bool _buttonX; public bool ButtonX { get => _buttonX; private set => SetProperty(ref _buttonX, value); }
        private bool _buttonY; public bool ButtonY { get => _buttonY; private set => SetProperty(ref _buttonY, value); }
        private bool _buttonLB; public bool ButtonLB { get => _buttonLB; private set => SetProperty(ref _buttonLB, value); }
        private bool _buttonRB; public bool ButtonRB { get => _buttonRB; private set => SetProperty(ref _buttonRB, value); }
        private bool _buttonBack; public bool ButtonBack { get => _buttonBack; private set => SetProperty(ref _buttonBack, value); }
        private bool _buttonStart; public bool ButtonStart { get => _buttonStart; private set => SetProperty(ref _buttonStart, value); }
        private bool _buttonL3; public bool ButtonL3 { get => _buttonL3; private set => SetProperty(ref _buttonL3, value); }
        private bool _buttonR3; public bool ButtonR3 { get => _buttonR3; private set => SetProperty(ref _buttonR3, value); }
        private bool _buttonGuide; public bool ButtonGuide { get => _buttonGuide; private set => SetProperty(ref _buttonGuide, value); }

        private string _dPadState = "Neutral";
        public string DPadState
        {
            get => _dPadState;
            private set
            {
                if (SetProperty(ref _dPadState, value))
                {
                    OnPropertyChanged(nameof(DPadUp));
                    OnPropertyChanged(nameof(DPadDown));
                    OnPropertyChanged(nameof(DPadLeft));
                    OnPropertyChanged(nameof(DPadRight));
                }
            }
        }

        public bool DPadUp => DPadState == "Up";
        public bool DPadDown => DPadState == "Down";
        public bool DPadLeft => DPadState == "Left";
        public bool DPadRight => DPadState == "Right";

        // Normalized sticks values (0 to 100 range, center = 50)
        public double LeftStickXNormalized => 50.0 + (_service.LeftStickX / 32768.0) * 40.0;
        public double LeftStickYNormalized => 50.0 - (_service.LeftStickY / 32768.0) * 40.0;
        public double RightStickXNormalized => 50.0 + (_service.RightStickX / 32768.0) * 40.0;
        public double RightStickYNormalized => 50.0 - (_service.RightStickY / 32768.0) * 40.0;

        // Visual sticks properties for UI overlay (Avalonia)
        // Center offsets calculated based on UI layout
        public double LeftStickKnobX => 804.87 + (_service.LeftStickX / 32768.0) * 80;
        public double LeftStickKnobY => 1217.19 - (_service.LeftStickY / 32768.0) * 80;
        public double RightStickKnobX => 1584.07 + (_service.RightStickX / 32768.0) * 80;
        public double RightStickKnobY => 1216.94 - (_service.RightStickY / 32768.0) * 80;

        // Raw stick positions for text display
        public short LeftStickX => _service.LeftStickX;
        public short LeftStickY => _service.LeftStickY;
        public short RightStickX => _service.RightStickX;
        public short RightStickY => _service.RightStickY;

        public double LeftStickOpacity => (Math.Abs(_service.LeftStickX) > 4000 || Math.Abs(_service.LeftStickY) > 4000) ? 0.8 : 0.0;
        public double RightStickOpacity => (Math.Abs(_service.RightStickX) > 4000 || Math.Abs(_service.RightStickY) > 4000) ? 0.8 : 0.0;

        private const double StickGraphCenter = 120.0;
        private const double StickGraphRadius = 88.0;
        private const double StickInputDotRadius = 5.0;
        private const double StickOutputDotRadius = 4.0;
        private const double StickCurveGraphWidth = 220.0;
        private const double StickCurveGraphHeight = 96.0;
        private const double StickCurveMarkerRadius = 4.0;

        public ObservableCollection<string> StickCurveOptions { get; } = new ObservableCollection<string>
        {
            "Lineal", "Precisa", "Dinamica", "Agresiva", "Custom"
        };

        public string SelectedStickCurve
        {
            get => _service.StickCurve;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && _service.StickCurve != value)
                {
                    _service.StickCurve = value;
                    NotifyStickSettingsChanged();
                    _service.SaveProfile(SelectedProfile);
                }
            }
        }

        public bool IsStickCurveLineal => SelectedStickCurve == "Lineal";
        public bool IsStickCurvePrecisa => SelectedStickCurve == "Precisa";
        public bool IsStickCurveDinamica => SelectedStickCurve == "Dinamica";
        public bool IsStickCurveAgresiva => SelectedStickCurve == "Agresiva";
        public bool IsStickCurveCustom => SelectedStickCurve == "Custom";

        public string StickCurveDescription => SelectedStickCurve switch
        {
            "Precisa" => LocalizationService.Get("StickCurvePreciseDescription"),
            "Dinamica" => LocalizationService.Get("StickCurveDynamicDescription"),
            "Agresiva" => LocalizationService.Get("StickCurveAggressiveDescription"),
            "Custom" => LocalizationService.Get("StickCurveCustomDescription"),
            _ => LocalizationService.Get("StickCurveLinearDescription")
        };

        public double[] StickCustomCurveX
        {
            get => _service.StickCustomCurveX;
            set
            {
                if (_service.StickCustomCurveX != value)
                {
                    _service.StickCustomCurveX = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StickCurvePoints));
                    NotifyStickSettingsChanged();
                }
            }
        }

        public double[] StickCustomCurveY
        {
            get => _service.StickCustomCurveY;
            set
            {
                if (_service.StickCustomCurveY != value)
                {
                    _service.StickCustomCurveY = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StickCurvePoints));
                    NotifyStickSettingsChanged();
                }
            }
        }

        public void UpdateStickCustomCurvePoint(int index, double x, double y)
        {
            if (index >= 0 && index < 5)
            {
                _service.StickCustomCurveX[index] = System.Math.Clamp(x, 0.0, 1.0);
                _service.StickCustomCurveY[index] = System.Math.Clamp(y, 0.0, 1.0);

                if (index > 0 && _service.StickCustomCurveX[index] < _service.StickCustomCurveX[index - 1])
                    _service.StickCustomCurveX[index] = _service.StickCustomCurveX[index - 1];
                if (index < 4 && _service.StickCustomCurveX[index] > _service.StickCustomCurveX[index + 1])
                    _service.StickCustomCurveX[index] = _service.StickCustomCurveX[index + 1];

                OnPropertyChanged(nameof(StickCustomCurveX));
                OnPropertyChanged(nameof(StickCustomCurveY));
                OnPropertyChanged(nameof(StickCurvePoints));
                NotifyStickSettingsChanged();
                _service.SaveProfile(SelectedProfile);
            }
        }

        public System.Collections.Generic.IEnumerable<Avalonia.Point> StickCurvePoints
        {
            get
            {
                var points = new System.Collections.Generic.List<Avalonia.Point>();
                for (int i = 0; i < 49; i++)
                {
                    double px = i * (StickCurveGraphWidth / 48.0);
                    double x = px / StickCurveGraphWidth;
                    double y = _service.ApplyStickCurveNormalized(x);
                    double py = StickCurveGraphHeight - (y * StickCurveGraphHeight);
                    points.Add(new Avalonia.Point(px, py));
                }
                return points;
            }
        }

        private double StickCurveGraphInput(short x, short y)
        {
            double magnitude = StickMagnitudePercent(x, y) / 100.0;
            double min = Math.Clamp(StickDeadzoneMinPercent / 100.0, 0.0, 0.95);
            double max = Math.Clamp(StickDeadzoneMaxPercent / 100.0, min + 0.01, 1.0);
            if (magnitude <= min) return 0.0;
            if (magnitude >= max) return 1.0;
            return Math.Clamp((magnitude - min) / (max - min), 0.0, 1.0);
        }

        private double StickCurveMarkerLeft(short x, short y)
        {
            return (StickCurveGraphInput(x, y) * StickCurveGraphWidth) - StickCurveMarkerRadius;
        }

        private double StickCurveMarkerTop(short x, short y)
        {
            double curveInput = StickCurveGraphInput(x, y);
            double curveOutput = _service.ApplyStickCurveNormalized(curveInput);
            return StickCurveGraphHeight - (curveOutput * StickCurveGraphHeight) - StickCurveMarkerRadius;
        }

        public double LeftStickCurveMarkerX => StickCurveMarkerLeft(LeftStickX, LeftStickY);
        public double LeftStickCurveMarkerY => StickCurveMarkerTop(LeftStickX, LeftStickY);
        public double RightStickCurveMarkerX => StickCurveMarkerLeft(RightStickX, RightStickY);
        public double RightStickCurveMarkerY => StickCurveMarkerTop(RightStickX, RightStickY);

        public double StickDeadzoneMinPercent
        {
            get => _service.StickDeadzoneMinPercent;
            set
            {
                double oldValue = _service.StickDeadzoneMinPercent;
                _service.StickDeadzoneMinPercent = value;
                if (Math.Abs(oldValue - _service.StickDeadzoneMinPercent) > 0.001)
                {
                    NotifyStickSettingsChanged();
                    _service.SaveProfile(SelectedProfile);
                }
            }
        }

        public double StickDeadzoneMaxPercent
        {
            get => _service.StickDeadzoneMaxPercent;
            set
            {
                double oldValue = _service.StickDeadzoneMaxPercent;
                _service.StickDeadzoneMaxPercent = value;
                if (Math.Abs(oldValue - _service.StickDeadzoneMaxPercent) > 0.001)
                {
                    NotifyStickSettingsChanged();
                    _service.SaveProfile(SelectedProfile);
                }
            }
        }

        private static double NormalizeStickAxis(short value) => Math.Clamp(value / 32767.0, -1.0, 1.0);

        private static double StickMagnitudePercent(short x, short y)
        {
            double nx = NormalizeStickAxis(x);
            double ny = NormalizeStickAxis(y);
            return Math.Clamp(Math.Sqrt((nx * nx) + (ny * ny)), 0.0, 1.0) * 100.0;
        }

        private static (double X, double Y) NormalizeStickVectorForGraph(short x, short y)
        {
            double nx = NormalizeStickAxis(x);
            double ny = NormalizeStickAxis(y);
            double magnitude = Math.Sqrt((nx * nx) + (ny * ny));
            if (magnitude > 1.0)
            {
                nx /= magnitude;
                ny /= magnitude;
            }

            return (nx, ny);
        }

        private static double StickDotLeft(short x, short y, double dotRadius)
        {
            var normalized = NormalizeStickVectorForGraph(x, y);
            return StickGraphCenter + (normalized.X * StickGraphRadius) - dotRadius;
        }

        private static double StickDotTop(short x, short y, double dotRadius)
        {
            var normalized = NormalizeStickVectorForGraph(x, y);
            return StickGraphCenter - (normalized.Y * StickGraphRadius) - dotRadius;
        }

        public short LeftStickOutputX => _service.ApplyStickOutput(LeftStickX, LeftStickY).X;
        public short LeftStickOutputY => _service.ApplyStickOutput(LeftStickX, LeftStickY).Y;
        public short RightStickOutputX => _service.ApplyStickOutput(RightStickX, RightStickY).X;
        public short RightStickOutputY => _service.ApplyStickOutput(RightStickX, RightStickY).Y;

        public double LeftStickInputMagnitudePercent => StickMagnitudePercent(LeftStickX, LeftStickY);
        public double RightStickInputMagnitudePercent => StickMagnitudePercent(RightStickX, RightStickY);
        public double LeftStickOutputMagnitudePercent => StickMagnitudePercent(LeftStickOutputX, LeftStickOutputY);
        public double RightStickOutputMagnitudePercent => StickMagnitudePercent(RightStickOutputX, RightStickOutputY);
        public string LeftStickOutputText => string.Format(LocalizationService.Get("OutputSuffixFormat"), LeftStickOutputMagnitudePercent);
        public string RightStickOutputText => string.Format(LocalizationService.Get("OutputSuffixFormat"), RightStickOutputMagnitudePercent);

        public double LeftStickInputDotX => StickDotLeft(LeftStickX, LeftStickY, StickInputDotRadius);
        public double LeftStickInputDotY => StickDotTop(LeftStickX, LeftStickY, StickInputDotRadius);
        public double RightStickInputDotX => StickDotLeft(RightStickX, RightStickY, StickInputDotRadius);
        public double RightStickInputDotY => StickDotTop(RightStickX, RightStickY, StickInputDotRadius);
        public double LeftStickOutputDotX => StickDotLeft(LeftStickOutputX, LeftStickOutputY, StickOutputDotRadius);
        public double LeftStickOutputDotY => StickDotTop(LeftStickOutputX, LeftStickOutputY, StickOutputDotRadius);
        public double RightStickOutputDotX => StickDotLeft(RightStickOutputX, RightStickOutputY, StickOutputDotRadius);
        public double RightStickOutputDotY => StickDotTop(RightStickOutputX, RightStickOutputY, StickOutputDotRadius);

        public double StickMinDeadzoneRingSize => StickGraphRadius * 2.0 * Math.Clamp(StickDeadzoneMinPercent / 100.0, 0.0, 1.0);
        public double StickMinDeadzoneRingOffset => StickGraphCenter - (StickMinDeadzoneRingSize / 2.0);
        public double StickMaxDeadzoneRingSize => StickGraphRadius * 2.0 * Math.Clamp(StickDeadzoneMaxPercent / 100.0, 0.0, 1.0);
        public double StickMaxDeadzoneRingOffset => StickGraphCenter - (StickMaxDeadzoneRingSize / 2.0);

        // Triggers (0-1023, 10-bit)
        public ObservableCollection<string> TriggerCurveOptions { get; } = new ObservableCollection<string>
        {
            "Lineal", "Exponencial", "Dinamica", "Agresiva"
        };

        public string SelectedTriggerCurve
        {
            get => _service.TriggerCurve;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && _service.TriggerCurve != value)
                {
                    _service.TriggerCurve = value;
                    NotifyTriggerCurveChanged();
                    _service.SaveProfile(SelectedProfile);
                }
            }
        }

        public bool IsTriggerCurveLineal => SelectedTriggerCurve == "Lineal";
        public bool IsTriggerCurveExponencial => SelectedTriggerCurve == "Exponencial";
        public bool IsTriggerCurveDinamica => SelectedTriggerCurve == "Dinamica";
        public bool IsTriggerCurveAgresiva => SelectedTriggerCurve == "Agresiva";
        public bool IsTriggerCurveCustom => SelectedTriggerCurve == "Custom";
        public string SelectedTriggerCurveDisplayName => GetCurveDisplayName(SelectedTriggerCurve);

        public double[] CustomCurveX
        {
            get => _service.CustomCurveX;
            set
            {
                if (_service.CustomCurveX != value)
                {
                    _service.CustomCurveX = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CustomCurvePoints));
                    NotifyTriggerCurveChanged();
                }
            }
        }

        public double[] CustomCurveY
        {
            get => _service.CustomCurveY;
            set
            {
                if (_service.CustomCurveY != value)
                {
                    _service.CustomCurveY = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CustomCurvePoints));
                    NotifyTriggerCurveChanged();
                }
            }
        }

        public void UpdateCustomCurvePoint(int index, double x, double y)
        {
            if (index >= 0 && index < 5)
            {
                _service.CustomCurveX[index] = System.Math.Clamp(x, 0.0, 1.0);
                _service.CustomCurveY[index] = System.Math.Clamp(y, 0.0, 1.0);

                // Force X monotonicity
                if (index > 0 && _service.CustomCurveX[index] < _service.CustomCurveX[index - 1])
                    _service.CustomCurveX[index] = _service.CustomCurveX[index - 1];
                if (index < 4 && _service.CustomCurveX[index] > _service.CustomCurveX[index + 1])
                    _service.CustomCurveX[index] = _service.CustomCurveX[index + 1];

                OnPropertyChanged(nameof(CustomCurveX));
                OnPropertyChanged(nameof(CustomCurveY));
                OnPropertyChanged(nameof(CustomCurvePoints));
            }
        }

        public System.Collections.Generic.IEnumerable<Avalonia.Point> CustomCurvePoints
        {
            get
            {
                var points = new System.Collections.Generic.List<Avalonia.Point>();
                for (int i = 0; i < 49; i++)
                {
                    double px = i * 5;
                    double x = px / 240.0;

                    double ty = _service.EvaluateCustomCurve(x);

                    double py = 120.0 - (ty * 120.0);
                    points.Add(new Avalonia.Point(px, py));
                }
                return points;
            }
        }

        public string TriggerCurveDescription => SelectedTriggerCurve switch
        {
            "Exponencial" => LocalizationService.Get("TriggerCurveExponentialDescription"),
            "Dinamica" => LocalizationService.Get("TriggerCurveDynamicDescription"),
            "Agresiva" => LocalizationService.Get("TriggerCurveAggressiveDescription"),
            _ => LocalizationService.Get("TriggerCurveLinearDescription")
        };

        private static string GetCurveDisplayName(string curveName) => curveName switch
        {
            "Lineal" => LocalizationService.Get("LinearCurve"),
            "Exponencial" => LocalizationService.Get("ExponentialCurve"),
            "Dinamica" => LocalizationService.Get("DynamicCurve"),
            "Agresiva" => LocalizationService.Get("AggressiveCurve"),
            "Precisa" => LocalizationService.Get("PreciseCurve"),
            "Custom" => LocalizationService.Get("CustomCurve"),
            _ => curveName
        };

        public ushort LeftTrigger => _service.LeftTrigger;
        public ushort RightTrigger => _service.RightTrigger;
        public bool LeftTriggerActive => LeftTrigger > 30;
        public bool RightTriggerActive => RightTrigger > 30;
        public double LeftTriggerFill => (LeftTrigger / 1023.0);
        public double RightTriggerFill => (RightTrigger / 1023.0);
        public double LeftTriggerPercent => (LeftTrigger / 1023.0) * 100.0;
        public double RightTriggerPercent => (RightTrigger / 1023.0) * 100.0;
        public ushort LeftTriggerOutput => _service.GetTriggerOutputRaw(LeftTrigger);
        public ushort RightTriggerOutput => _service.GetTriggerOutputRaw(RightTrigger);
        public byte LeftTriggerOutputByte => _service.GetTriggerOutputByte(LeftTrigger);
        public byte RightTriggerOutputByte => _service.GetTriggerOutputByte(RightTrigger);
        public double LeftTriggerOutputPercent => _service.GetTriggerOutputPercent(LeftTrigger);
        public double RightTriggerOutputPercent => _service.GetTriggerOutputPercent(RightTrigger);
        public double TriggerDeltaPercent => ((LeftTriggerOutputPercent - LeftTriggerPercent) + (RightTriggerOutputPercent - RightTriggerPercent)) / 2.0;
        public string TriggerDeltaText => string.Format(LocalizationService.Get("DeltaFormat"), TriggerDeltaPercent);
        public string LeftTriggerInputText => string.Format(LocalizationService.Get("InputFormat"), LeftTriggerPercent);
        public string RightTriggerInputText => string.Format(LocalizationService.Get("InputFormat"), RightTriggerPercent);
        public double LeftTriggerMarkerX => (Math.Clamp(LeftTriggerPercent / 100.0, 0.0, 1.0) * 240.0) - 5.0;
        public double LeftTriggerMarkerY => 120.0 - (Math.Clamp(LeftTriggerOutputPercent / 100.0, 0.0, 1.0) * 120.0) - 5.0;
        public double RightTriggerMarkerX => (Math.Clamp(RightTriggerPercent / 100.0, 0.0, 1.0) * 240.0) - 5.0;
        public double RightTriggerMarkerY => 120.0 - (Math.Clamp(RightTriggerOutputPercent / 100.0, 0.0, 1.0) * 120.0) - 5.0;

        private double _smoothedLeftTriggerPercent;
        public double SmoothedLeftTriggerPercent { get => _smoothedLeftTriggerPercent; set => SetProperty(ref _smoothedLeftTriggerPercent, value); }
        private double _smoothedRightTriggerPercent;
        public double SmoothedRightTriggerPercent { get => _smoothedRightTriggerPercent; set => SetProperty(ref _smoothedRightTriggerPercent, value); }
        private double _smoothedLeftTriggerOutputPercent;
        public double SmoothedLeftTriggerOutputPercent { get => _smoothedLeftTriggerOutputPercent; set => SetProperty(ref _smoothedLeftTriggerOutputPercent, value); }
        private double _smoothedRightTriggerOutputPercent;
        public double SmoothedRightTriggerOutputPercent { get => _smoothedRightTriggerOutputPercent; set => SetProperty(ref _smoothedRightTriggerOutputPercent, value); }
        private double _smoothedLeftTriggerMarkerX;
        public double SmoothedLeftTriggerMarkerX { get => _smoothedLeftTriggerMarkerX; set => SetProperty(ref _smoothedLeftTriggerMarkerX, value); }
        private double _smoothedLeftTriggerMarkerY;
        public double SmoothedLeftTriggerMarkerY { get => _smoothedLeftTriggerMarkerY; set => SetProperty(ref _smoothedLeftTriggerMarkerY, value); }
        private double _smoothedRightTriggerMarkerX;
        public double SmoothedRightTriggerMarkerX { get => _smoothedRightTriggerMarkerX; set => SetProperty(ref _smoothedRightTriggerMarkerX, value); }
        private double _smoothedRightTriggerMarkerY;
        public double SmoothedRightTriggerMarkerY { get => _smoothedRightTriggerMarkerY; set => SetProperty(ref _smoothedRightTriggerMarkerY, value); }

        private double GetTriggerCurveOutputPercent(double inputPercent)
        {
            double normalizedInput = Math.Clamp(inputPercent / 100.0, 0.0, 1.0);
            return _service.ApplyTriggerCurveNormalized(normalizedInput) * 100.0;
        }

        private void RefreshSmoothedTriggerCurveOutputs()
        {
            SmoothedLeftTriggerOutputPercent = GetTriggerCurveOutputPercent(SmoothedLeftTriggerPercent);
            SmoothedRightTriggerOutputPercent = GetTriggerCurveOutputPercent(SmoothedRightTriggerPercent);

            SmoothedLeftTriggerMarkerX = (Math.Clamp(SmoothedLeftTriggerPercent / 100.0, 0.0, 1.0) * 240.0) - 5.0;
            SmoothedLeftTriggerMarkerY = 120.0 - (Math.Clamp(SmoothedLeftTriggerOutputPercent / 100.0, 0.0, 1.0) * 120.0) - 5.0;
            SmoothedRightTriggerMarkerX = (Math.Clamp(SmoothedRightTriggerPercent / 100.0, 0.0, 1.0) * 240.0) - 5.0;
            SmoothedRightTriggerMarkerY = 120.0 - (Math.Clamp(SmoothedRightTriggerOutputPercent / 100.0, 0.0, 1.0) * 120.0) - 5.0;
        }

        private void UpdateSmoothedTriggerValues()
        {
            double alpha = 0.35;
            SmoothedLeftTriggerPercent += alpha * (LeftTriggerPercent - SmoothedLeftTriggerPercent);
            SmoothedRightTriggerPercent += alpha * (RightTriggerPercent - SmoothedRightTriggerPercent);
            RefreshSmoothedTriggerCurveOutputs();
        }

        public MainViewModel()
        {
            _service = ScufDeviceService.Instance;
            LocalizationService.SetLanguage(_service.LanguageCode);
            RefreshLanguageOptions();
            RefreshCloseBehaviorOptions();
            _selectedLanguageIndex = LocalizationService.GetLanguageIndex(_service.LanguageCode);
            _selectedThemeIndex = GetThemeIndex(_service.ThemeName);
            _selectedCloseBehaviorIndex = _service.CloseBehavior == ApplicationCloseBehavior.MinimizeToTray ? 0 : 1;

            ConnectCommand = new RelayCommand(async () => await ConnectAsync());
            InstallDriverCommand = new RelayCommand(ExecuteInstallDriverAsync);
            RestoreDriverCommand = new RelayCommand(ExecuteRestoreDriverAsync);
            InstallReceiverDriverCommand = new RelayCommand(ExecuteInstallReceiverDriverAsync);
            RestoreReceiverDriverCommand = new RelayCommand(ExecuteRestoreReceiverDriverAsync);
            ListWirelessReceiverPnpInstancesCommand = new RelayCommand(ExecuteListWirelessReceiverPnpInstancesAsync);
            ConfirmDriverCommand = new AsyncRelayCommand(ConfirmDriverActionAsync);
            CloseDriverModalCommand = new RelayCommand(() => IsDriverModalOpen = false);
            DisconnectCommand = new RelayCommand(Disconnect);
            ClearLogCommand = new RelayCommand(ClearLog);
            ResetToDefaultCommand = new RelayCommand(() => IsResetToDefaultDialogOpen = true);
            ConfirmResetToDefaultCommand = new RelayCommand(ResetToDefaults);
            CancelResetToDefaultCommand = new RelayCommand(() => IsResetToDefaultDialogOpen = false);

            SelectRemapInputTabCommand = new RelayCommand<string>(SelectRemapInputTab);
            BeginRemapCommand = new RelayCommand<string>(BeginRemap);
            CancelRemapCommand = new RelayCommand(CancelRemap);
            RestoreRemapCommand = new RelayCommand(RestoreRemap);
            UnmapRemapCommand = new RelayCommand(UnmapRemap);
            AddRemapGestureCommand = new RelayCommand<string>(AddRemapGesture);
            RemoveRemapGestureCommand = new RelayCommand<string>(RemoveRemapGesture);
            SelectRemapGestureCommand = new RelayCommand<string>(SelectRemapGesture);
            OpenDetailsRemapCommand = new RelayCommand<AdvancedMappingCommand>(OpenDetailsRemap);
            AddDetailsDoubleTapCommand = new RelayCommand<AdvancedMappingItem>(item => AddDetailsRemapGesture(item, "DoubleTap"));
            AddDetailsHoldCommand = new RelayCommand<AdvancedMappingItem>(item => AddDetailsRemapGesture(item, "Hold"));
            AddDetailsPressStartCommand = new RelayCommand<AdvancedMappingItem>(item => AddDetailsRemapGesture(item, "PressStart"));
            AddDetailsPressReleaseCommand = new RelayCommand<AdvancedMappingItem>(item => AddDetailsRemapGesture(item, "PressRelease"));
            RemoveDetailsRemapGestureCommand = new RelayCommand<AdvancedMappingCommand>(RemoveDetailsRemapGesture);
            StartMacroRecordingCommand = new RelayCommand(StartMacroRecording);
            StopMacroRecordingCommand = new RelayCommand(StopMacroRecording);
            ToggleMacroRecordingCommand = new RelayCommand(ToggleMacroRecording);
            OpenSaveMacroDialogCommand = new RelayCommand(OpenSaveMacroDialog);
            ConfirmSaveMacroCommand = new RelayCommand(ConfirmSaveMacro);
            CancelSaveMacroCommand = new RelayCommand(CancelSaveMacro);
            OpenLoadMacroDialogCommand = new RelayCommand(OpenLoadMacroDialog);
            CancelLoadMacroCommand = new RelayCommand(CancelLoadMacro);
            LoadMacroCommand = new RelayCommand<MacroDefinition>(LoadMacroFromLibrary);
            DeleteSavedMacroCommand = new RelayCommand<MacroDefinition>(DeleteSavedMacro);
            ClearMacroCommand = new RelayCommand(ClearMacro);
            RemoveMacroStepCommand = new RelayCommand<MacroStepViewModel>(RemoveMacroStep);
            AcceptRemapCommand = new RelayCommand(AcceptRemap);
            MapActionCommand = new RelayCommand<string>(HandleMapAction);
            ManualMapCommand = new RelayCommand<string>(HandleKeyPress);
            TestRumbleCommand = new RelayCommand(() =>
            {
                byte motor = (byte)Math.Round(RumbleIntensity / 100.0 * byte.MaxValue);
                _service.TestRumble(motor, motor);
            });
            CreateProfileCommand = new RelayCommand(OpenCreateProfileDialog);
            ConfirmProfileEditorCommand = new RelayCommand(ConfirmProfileEditor);
            CancelProfileEditorCommand = new RelayCommand(CloseProfileEditorDialog);
            ConfirmProfileDeleteCommand = new RelayCommand(ConfirmProfileDelete);
            CancelProfileDeleteCommand = new RelayCommand(CloseProfileDeleteDialog);
            ConfirmProfileLinkCommand = new RelayCommand(ConfirmProfileLink);
            CancelProfileLinkCommand = new RelayCommand(CloseProfileLinkDialog);
            ConfirmProfileUnlinkCommand = new RelayCommand(ConfirmProfileUnlink);
            CancelProfileUnlinkCommand = new RelayCommand(CloseProfileUnlinkDialog);
            CloseLinkedAppsManagerCommand = new RelayCommand(CloseLinkedAppsManager);
            RemoveLinkedAppCommand = new RelayCommand<LinkedAppItemViewModel>(RemoveLinkedApp);
            ClearManagedLinkedAppsCommand = new RelayCommand(ClearManagedLinkedApps);
            SetSectionCommand = new RelayCommand<string>(s => { if (s != null) SelectedSection = s; });
            SetTabCommand = new RelayCommand<string>(s => { if (s != null) { SelectedTab = s; if (s == "Details") RefreshAdvancedMappings(); } });
            RefreshAdvancedMappingsCommand = new RelayCommand(RefreshAdvancedMappings);
            SetTriggerCurveCommand = new RelayCommand<string>(s => { if (!string.IsNullOrWhiteSpace(s)) SelectedTriggerCurve = s; });
            SetStickCurveCommand = new RelayCommand<string>(s => { if (!string.IsNullOrWhiteSpace(s)) SelectedStickCurve = s; });
            SetStandardModeCommand = new RelayCommand(() => IsShiftMode = false);
            SetShiftModeCommand = new RelayCommand(() => IsShiftMode = true);
            OpenShiftModifierPickerCommand = new RelayCommand(() =>
            {
                PendingShiftModifier = _service.ShiftModifierButton;
                IsShiftModifierPickerOpen = true;
            });
            CloseShiftModifierPickerCommand = new RelayCommand(() => IsShiftModifierPickerOpen = false);
            AcceptShiftModifierCommand = new RelayCommand(AcceptShiftModifier);
            ClearShiftModifierCommand = new RelayCommand(ClearShiftModifier);
            SetRgbRedCommand = new AsyncRelayCommand(async () => await _service.SetStaticRgbAsync(255, 0, 0, CancellationToken.None));
            SetRgbGreenCommand = new AsyncRelayCommand(async () => await _service.SetStaticRgbAsync(0, 255, 0, CancellationToken.None));
            SetRgbBlueCommand = new AsyncRelayCommand(async () => await _service.SetStaticRgbAsync(0, 0, 255, CancellationToken.None));
            SetRgbWhiteCommand = new AsyncRelayCommand(async () => await _service.SetStaticRgbAsync(255, 255, 255, CancellationToken.None));
            SetBrightnessMaxCommand = new AsyncRelayCommand(async () => await _service.SetBrightnessAsync(1000, CancellationToken.None));
            SetBrightnessZeroCommand = new AsyncRelayCommand(async () => await _service.SetBrightnessAsync(0, CancellationToken.None));

            ResetLightsCommand = new AsyncRelayCommand(async () => await _service.ResetLightsAsync(CancellationToken.None));
            LightsOffCommand = new AsyncRelayCommand(async () => await _service.SetBrightnessAsync(0, CancellationToken.None));

            // Set up remappings collection
            InitializePaddleRemaps();
            InitializeGKeyRemaps();


            // Hook service events
            _service.OnInputEvent += HandleInputEvent;
            _service.OnMacroControllerInput += HandleMacroControllerInput;
            _service.OnFrameProcessed += HandleFrameProcessed;
            _service.OnProfileLoaded += HandleProfileLoaded;
            _service.OnActionTriggered += Service_OnActionTriggered;

            LoadProfilesList(SelectedProfile);
            _profileLinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _profileLinkTimer.Tick += (_, _) => ActivateLinkedProfileForForegroundApp();
            _profileLinkTimer.Start();


            // Property change propagation
            _service.PropertyChanged += Service_PropertyChanged;

            if (!ViGEmBusDetector.IsAvailable())
            {
                ShowMissingViGEmBusModal();
            }
        }

        private void RefreshLanguageOptions()
        {
            if (LanguageOptions.Count == 0)
            {
                LanguageOptions.Add(LocalizationService.Get("LanguageEnglish"));
                LanguageOptions.Add(LocalizationService.Get("LanguageSpanish"));
                return;
            }

            LanguageOptions[0] = LocalizationService.Get("LanguageEnglish");
            LanguageOptions[1] = LocalizationService.Get("LanguageSpanish");
        }

        private void RefreshCloseBehaviorOptions()
        {
            if (CloseBehaviorOptions.Count == 0)
            {
                CloseBehaviorOptions.Add(LocalizationService.Get("CloseBehaviorMinimize"));
                CloseBehaviorOptions.Add(LocalizationService.Get("CloseBehaviorExit"));
                return;
            }

            CloseBehaviorOptions[0] = LocalizationService.Get("CloseBehaviorMinimize");
            CloseBehaviorOptions[1] = LocalizationService.Get("CloseBehaviorExit");
        }

        private void ApplyLanguage(string languageCode)
        {
            LocalizationService.SetLanguage(languageCode);
            _service.LanguageCode = languageCode;
            _service.RefreshLocalizedConnectionStatus();
            RefreshLanguageOptions();
            RefreshCloseBehaviorOptions();
            NotifyLocalizedTextChanged();
        }

        private void NotifyLocalizedTextChanged()
        {
            NotifyTriggerCurveChanged();
            NotifyStickSettingsChanged();
            NotifyRemapGestureStateChanged();
            NotifySelectedShiftModifierChanged();
            RefreshAdvancedMappings();
            foreach (var profileCard in ProfileCards)
            {
                profileCard.NotifyLocalizedTextChanged();
            }
            RemapVersion++;
        }

        private ProfileItemViewModel? _profileEditorTarget;
        private ProfileItemViewModel? _profileDuplicateSource;
        private PreparedProfileImport? _pendingProfileImport;
        private ProfileItemViewModel? _pendingProfileActionTarget;
        private ProfileItemViewModel? _managedLinkedAppsProfile;
        private List<string> _pendingLinkedAppPaths = new List<string>();
        public ObservableCollection<LinkedAppItemViewModel> ManagedLinkedAppItems { get; } = new ObservableCollection<LinkedAppItemViewModel>();

        private bool _isProfileEditorDialogOpen;
        public bool IsProfileEditorDialogOpen
        {
            get => _isProfileEditorDialogOpen;
            set => SetProperty(ref _isProfileEditorDialogOpen, value);
        }

        private bool _isProfileEditorRenameMode;
        public bool IsProfileEditorRenameMode
        {
            get => _isProfileEditorRenameMode;
            set => SetProperty(ref _isProfileEditorRenameMode, value);
        }

        private string _profileEditorTitle = string.Empty;
        public string ProfileEditorTitle
        {
            get => _profileEditorTitle;
            set => SetProperty(ref _profileEditorTitle, value);
        }

        private string _profileEditorDescription = string.Empty;
        public string ProfileEditorDescription
        {
            get => _profileEditorDescription;
            set => SetProperty(ref _profileEditorDescription, value);
        }

        private string _pendingProfileName = string.Empty;
        public string PendingProfileName
        {
            get => _pendingProfileName;
            set => SetProperty(ref _pendingProfileName, value);
        }

        private string _profileEditorError = string.Empty;
        public string ProfileEditorError
        {
            get => _profileEditorError;
            set
            {
                if (SetProperty(ref _profileEditorError, value))
                {
                    OnPropertyChanged(nameof(HasProfileEditorError));
                }
            }
        }

        public bool HasProfileEditorError => !string.IsNullOrWhiteSpace(ProfileEditorError);

        private bool _isProfileDeleteDialogOpen;
        public bool IsProfileDeleteDialogOpen
        {
            get => _isProfileDeleteDialogOpen;
            set => SetProperty(ref _isProfileDeleteDialogOpen, value);
        }

        private string _profileDeleteTitle = string.Empty;
        public string ProfileDeleteTitle
        {
            get => _profileDeleteTitle;
            set => SetProperty(ref _profileDeleteTitle, value);
        }

        private string _profileDeleteDescription = string.Empty;
        public string ProfileDeleteDescription
        {
            get => _profileDeleteDescription;
            set => SetProperty(ref _profileDeleteDescription, value);
        }

        private string _profileActionError = string.Empty;
        public string ProfileActionError
        {
            get => _profileActionError;
            set
            {
                if (SetProperty(ref _profileActionError, value))
                {
                    OnPropertyChanged(nameof(HasProfileActionError));
                }
            }
        }

        public bool HasProfileActionError => !string.IsNullOrWhiteSpace(ProfileActionError);

        private bool _isLinkedAppsManagerOpen;
        public bool IsLinkedAppsManagerOpen
        {
            get => _isLinkedAppsManagerOpen;
            set => SetProperty(ref _isLinkedAppsManagerOpen, value);
        }

        public ProfileItemViewModel? ManagedLinkedAppsProfile
        {
            get => _managedLinkedAppsProfile;
            private set => SetProperty(ref _managedLinkedAppsProfile, value);
        }

        private string _linkedAppsManagerTitle = string.Empty;
        public string LinkedAppsManagerTitle
        {
            get => _linkedAppsManagerTitle;
            set => SetProperty(ref _linkedAppsManagerTitle, value);
        }

        private string _linkedAppsManagerDescription = string.Empty;
        public string LinkedAppsManagerDescription
        {
            get => _linkedAppsManagerDescription;
            set => SetProperty(ref _linkedAppsManagerDescription, value);
        }

        public bool HasManagedLinkedApps => ManagedLinkedAppItems.Count > 0;

        private bool _isProfileLinkDialogOpen;
        public bool IsProfileLinkDialogOpen
        {
            get => _isProfileLinkDialogOpen;
            set => SetProperty(ref _isProfileLinkDialogOpen, value);
        }

        private string _profileLinkTitle = string.Empty;
        public string ProfileLinkTitle
        {
            get => _profileLinkTitle;
            set => SetProperty(ref _profileLinkTitle, value);
        }

        private string _profileLinkDescription = string.Empty;
        public string ProfileLinkDescription
        {
            get => _profileLinkDescription;
            set => SetProperty(ref _profileLinkDescription, value);
        }

        private bool _isProfileUnlinkDialogOpen;
        public bool IsProfileUnlinkDialogOpen
        {
            get => _isProfileUnlinkDialogOpen;
            set => SetProperty(ref _isProfileUnlinkDialogOpen, value);
        }

        private string _profileUnlinkTitle = string.Empty;
        public string ProfileUnlinkTitle
        {
            get => _profileUnlinkTitle;
            set => SetProperty(ref _profileUnlinkTitle, value);
        }

        private string _profileUnlinkDescription = string.Empty;
        public string ProfileUnlinkDescription
        {
            get => _profileUnlinkDescription;
            set => SetProperty(ref _profileUnlinkDescription, value);
        }

        private string _pendingLinkedAppPath = string.Empty;
        public string PendingLinkedAppPath
        {
            get => _pendingLinkedAppPath;
            set
            {
                if (SetProperty(ref _pendingLinkedAppPath, value))
                {
                    OnPropertyChanged(nameof(PendingLinkedAppName));
                }
            }
        }

        public string PendingLinkedAppName => string.IsNullOrWhiteSpace(PendingLinkedAppPath)
            ? LocalizationService.Get("None")
            : System.IO.Path.GetFileName(PendingLinkedAppPath);

        private sealed class HeldProfileActionState
        {
            public string Action { get; init; } = string.Empty;
            public string TargetProfile { get; init; } = string.Empty;
        }

        private string? _profileBeforeHeldActions = null;
        private readonly List<HeldProfileActionState> _heldProfileActions = new();

        private void Service_OnActionTriggered(string action, bool isPressed)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (action == "Action:EcoMode" && isPressed)
                {
                    IsEcoModeEnabled = !IsEcoModeEnabled;
                }
                else if (action.StartsWith("Action:LoadProfile:"))
                {
                    if (isPressed)
                    {
                        var profileName = action.Substring("Action:LoadProfile:".Length);
                        var matchedProfile = Profiles.FirstOrDefault(p => string.Equals(p, profileName, StringComparison.OrdinalIgnoreCase));
                        if (matchedProfile != null)
                        {
                            SelectedProfile = matchedProfile;
                        }
                    }
                }
                else if (action.StartsWith("Action:LoadProfileHeld:"))
                {
                    var profileName = action.Substring("Action:LoadProfileHeld:".Length);
                    if (isPressed)
                    {
                        var matchedProfile = Profiles.FirstOrDefault(p => string.Equals(p, profileName, StringComparison.OrdinalIgnoreCase));
                        if (matchedProfile != null)
                        {
                            if (_heldProfileActions.Count == 0)
                            {
                                _profileBeforeHeldActions = SelectedProfile;
                            }

                            _heldProfileActions.RemoveAll(held => string.Equals(held.Action, action, StringComparison.Ordinal));
                            _heldProfileActions.Add(new HeldProfileActionState
                            {
                                Action = action,
                                TargetProfile = matchedProfile
                            });
                            SelectedProfile = matchedProfile;
                        }
                    }
                    else
                    {
                        _heldProfileActions.RemoveAll(held => string.Equals(held.Action, action, StringComparison.Ordinal));
                        var profileToRestore = _heldProfileActions.Count > 0
                            ? _heldProfileActions[^1].TargetProfile
                            : _profileBeforeHeldActions;

                        if (!string.IsNullOrEmpty(profileToRestore))
                        {
                            var matchedProfile = Profiles.FirstOrDefault(p => string.Equals(p, profileToRestore, StringComparison.OrdinalIgnoreCase));
                            if (matchedProfile != null)
                            {
                                SelectedProfile = matchedProfile;
                            }
                        }

                        if (_heldProfileActions.Count == 0)
                        {
                            _profileBeforeHeldActions = null;
                        }
                    }
                }
            });
        }

        private void HandleProfileLoaded()
        {
            RgbRed = _service.RgbRed;
            RgbGreen = _service.RgbGreen;
            RgbBlue = _service.RgbBlue;
            RgbBrightness = _service.RgbBrightness;
            RumbleIntensity = _service.RumbleIntensity;
            IsEcoModeEnabled = _service.EcoMode;
            if (SynchronizeShiftModifierMappings() && !string.IsNullOrWhiteSpace(SelectedProfile))
            {
                _service.SaveProfile(SelectedProfile);
            }
            OnPropertyChanged(nameof(SelectedShiftModifier));
            NotifySelectedShiftModifierChanged();
            NotifyTriggerCurveChanged();
            NotifyStickSettingsChanged();
            RefreshSavedMacros();
        }

        private void LoadProfilesList(string? preferredProfile = null)
        {
            Profiles.Clear();
            ProfileCards.Clear();

            var profilesDirectory = ZeroCuePaths.ProfilesDirectory;
            if (System.IO.Directory.Exists(profilesDirectory))
            {
                var files = System.IO.Directory.GetFiles(profilesDirectory, "*.json")
                    .OrderBy(file => System.IO.Path.GetFileNameWithoutExtension(file) == "Default" ? 0 : 1)
                    .ThenBy(file => System.IO.Path.GetFileNameWithoutExtension(file), StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(file);
                    var profile = _service.TryReadProfile(name);
                    Profiles.Add(name);
                    ProfileCards.Add(new ProfileItemViewModel(
                        name,
                        IsDefaultProfileName(name),
                        profile?.LinkedAppPath ?? string.Empty,
                        profile?.GetLinkedAppPaths(),
                        OpenRenameProfileDialog,
                        OpenProfileDeleteDialog,
                        DuplicateProfile,
                        SetDefaultProfile,
                        ActivateProfileFromCard,
                        OnProfileSelectedForEdit,
                        OpenLinkedAppsManager,
                        OpenProfileUnlinkDialog));
                }
            }

            if (Profiles.Count == 0)
            {
                _service.SaveProfile("Default");
                Profiles.Add("Default");
                ProfileCards.Add(new ProfileItemViewModel(
                    "Default",
                    IsDefaultProfileName("Default"),
                    string.Empty,
                    Array.Empty<string>(),
                    OpenRenameProfileDialog,
                    OpenProfileDeleteDialog,
                    DuplicateProfile,
                    SetDefaultProfile,
                    ActivateProfileFromCard,
                    OnProfileSelectedForEdit,
                    OpenLinkedAppsManager,
                    OpenProfileUnlinkDialog));
            }

            var profileToSelect = !string.IsNullOrWhiteSpace(preferredProfile)
                ? preferredProfile
                : _service.DefaultProfileName;

            var nextSelectedProfile = !string.IsNullOrWhiteSpace(profileToSelect)
                    ? Profiles.FirstOrDefault(profile => string.Equals(profile, profileToSelect, StringComparison.OrdinalIgnoreCase))
                    : null;
            nextSelectedProfile = !string.IsNullOrWhiteSpace(nextSelectedProfile)
                ? nextSelectedProfile
                : Profiles[0];

            SelectedProfile = nextSelectedProfile;
            RefreshSelectedProfileCards();
        }

        public void BeginProfileImport(string sourcePath)
        {
            ProfileActionError = string.Empty;
            try
            {
                _pendingProfileImport = ProfileTransferService.ReadImport(sourcePath);
                _profileEditorTarget = null;
                _profileDuplicateSource = null;
                string suggestedName = string.IsNullOrWhiteSpace(_pendingProfileImport.SuggestedName)
                    ? LocalizationService.Get("ImportedProfileDefaultName")
                    : _pendingProfileImport.SuggestedName;
                PendingProfileName = GetAvailableProfileName(suggestedName);
                ProfileEditorError = string.Empty;
                IsProfileEditorRenameMode = false;
                ProfileEditorTitle = LocalizationService.Get("ImportProfileTitle");
                ProfileEditorDescription = LocalizationService.Get("ImportProfileDescription");
                IsProfileEditorDialogOpen = true;
            }
            catch (ProfileImportException ex)
            {
                _pendingProfileImport = null;
                ProfileActionError = GetProfileImportError(ex);
            }
            catch (Exception ex)
            {
                _pendingProfileImport = null;
                ProfileActionError = string.Format(LocalizationService.Get("ProfileImportReadFailedFormat"), ex.Message);
            }
        }

        public void ExportProfile(ProfileItemViewModel profile, string destinationPath)
        {
            ProfileActionError = string.Empty;
            try
            {
                var sourceProfile = _service.TryReadProfile(profile.OriginalName);
                if (sourceProfile == null)
                {
                    ProfileActionError = LocalizationService.Get("ProfileExportSourceMissing");
                    return;
                }

                ProfileTransferService.Export(sourceProfile, profile.Name, destinationPath);
            }
            catch (ProfileImportException)
            {
                ProfileActionError = LocalizationService.Get("ProfileExportInvalid");
            }
            catch (Exception ex)
            {
                ProfileActionError = string.Format(LocalizationService.Get("ProfileExportFailedFormat"), ex.Message);
            }
        }

        private void RefreshSelectedProfileCards()
        {
            var canDeleteProfiles = Profiles.Count > 1;
            foreach (var card in ProfileCards)
            {
                card.IsSelected = string.Equals(card.Name, SelectedProfile, StringComparison.OrdinalIgnoreCase);
                card.IsDefault = IsDefaultProfileName(card.Name);
                card.CanDelete = canDeleteProfiles;
            }
        }

        private bool IsDefaultProfileName(string profileName)
        {
            return string.Equals(profileName, _service.DefaultProfileName, StringComparison.OrdinalIgnoreCase);
        }

        private string GetNextProfileName()
        {
            int nextId = 1;
            while (ProfileNameExists($"Profile{nextId}")) nextId++;
            return $"Profile{nextId}";
        }

        private void OpenCreateProfileDialog()
        {
            _profileEditorTarget = null;
            _profileDuplicateSource = null;
            _pendingProfileImport = null;
            ProfileActionError = string.Empty;
            PendingProfileName = GetNextProfileName();
            ProfileEditorError = string.Empty;
            IsProfileEditorRenameMode = false;
            ProfileEditorTitle = LocalizationService.Get("CreateProfileTitle");
            ProfileEditorDescription = LocalizationService.Get("CreateProfileDescription");
            IsProfileEditorDialogOpen = true;
        }

        private void OpenRenameProfileDialog(ProfileItemViewModel vm)
        {
            _profileEditorTarget = vm;
            _profileDuplicateSource = null;
            _pendingProfileImport = null;
            ProfileActionError = string.Empty;
            PendingProfileName = vm.Name;
            ProfileEditorError = string.Empty;
            IsProfileEditorRenameMode = true;
            ProfileEditorTitle = LocalizationService.Get("RenameProfileTitle");
            ProfileEditorDescription = string.Format(LocalizationService.Get("RenameProfileDescriptionFormat"), vm.Name);
            IsProfileEditorDialogOpen = true;
        }

        private void CloseProfileEditorDialog()
        {
            IsProfileEditorDialogOpen = false;
            _profileEditorTarget = null;
            _profileDuplicateSource = null;
            _pendingProfileImport = null;
            ProfileEditorError = string.Empty;
        }

        private void ConfirmProfileEditor()
        {
            var requestedName = (PendingProfileName ?? string.Empty).Trim();
            var originalName = _profileEditorTarget?.OriginalName;
            var validationError = ValidateProfileName(requestedName, originalName);
            if (!string.IsNullOrEmpty(validationError))
            {
                ProfileEditorError = validationError;
                return;
            }

            if (_profileEditorTarget == null)
            {
                if (_pendingProfileImport != null)
                {
                    try
                    {
                        ProfileTransferService.SaveImport(_pendingProfileImport.Profile, requestedName);
                    }
                    catch (ProfileImportException)
                    {
                        ProfileEditorError = LocalizationService.Get("ProfileImportInvalid");
                        return;
                    }
                    catch (Exception ex)
                    {
                        ProfileEditorError = string.Format(LocalizationService.Get("ProfileImportSaveFailedFormat"), ex.Message);
                        return;
                    }

                    CloseProfileEditorDialog();
                    LoadProfilesList(requestedName);
                    SelectedProfile = requestedName;
                    return;
                }

                if (_profileDuplicateSource != null)
                {
                    CreateProfileDuplicate(_profileDuplicateSource, requestedName);
                    CloseProfileEditorDialog();
                    LoadProfilesList(requestedName);
                    SelectedProfile = requestedName;
                    return;
                }

                _service.SaveProfile(requestedName);
                CloseProfileEditorDialog();
                LoadProfilesList(requestedName);
                SelectedProfile = requestedName;
                return;
            }

            if (!string.Equals(originalName, requestedName, StringComparison.Ordinal))
            {
                RenameProfileFile(originalName!, requestedName);
                if (string.Equals(_service.DefaultProfileName, originalName, StringComparison.OrdinalIgnoreCase))
                {
                    _service.DefaultProfileName = requestedName;
                }
            }

            CloseProfileEditorDialog();
            LoadProfilesList(requestedName);
            SelectedProfile = requestedName;
        }

        private string ValidateProfileName(string profileName, string? currentName = null)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return LocalizationService.Get("ProfileNameRequired");
            }

            if (profileName.Length > 80
                || profileName.EndsWith(".", StringComparison.Ordinal)
                || profileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsFileName(profileName))
            {
                return LocalizationService.Get("ProfileNameInvalid");
            }

            bool sameAsCurrent = !string.IsNullOrWhiteSpace(currentName)
                && string.Equals(profileName, currentName, StringComparison.OrdinalIgnoreCase);
            bool duplicate = ProfileNameExists(profileName) && !sameAsCurrent;

            return duplicate ? LocalizationService.Get("ProfileNameDuplicate") : string.Empty;
        }

        private bool ProfileNameExists(string profileName)
        {
            if (Profiles.Any(profile => string.Equals(profile, profileName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            try
            {
                var profilePath = ZeroCuePaths.GetProfileFile(profileName);
                var profileDirectory = System.IO.Path.GetDirectoryName(profilePath);
                var fileName = System.IO.Path.GetFileName(profilePath);
                return !string.IsNullOrWhiteSpace(profileDirectory)
                    && System.IO.Directory.Exists(profileDirectory)
                    && System.IO.Directory.EnumerateFiles(profileDirectory, "*.json")
                        .Any(file => string.Equals(System.IO.Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private string GetAvailableProfileName(string baseName)
        {
            if (!ProfileNameExists(baseName))
            {
                return baseName;
            }

            int suffix = 2;
            string candidate;
            do
            {
                string suffixText = $" ({suffix})";
                int baseLength = Math.Min(baseName.Length, 80 - suffixText.Length);
                candidate = baseName[..baseLength].TrimEnd() + suffixText;
                suffix++;
            }
            while (ProfileNameExists(candidate));

            return candidate;
        }

        private static bool IsReservedWindowsFileName(string profileName)
        {
            string name = profileName.Trim().TrimEnd('.');
            return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (name.Length == 4
                    && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && name[3] >= '1'
                    && name[3] <= '9');
        }

        private static string GetProfileImportError(ProfileImportException exception)
        {
            return exception.Failure switch
            {
                ProfileImportFailure.TooLarge => LocalizationService.Get("ProfileImportTooLarge"),
                ProfileImportFailure.UnsupportedVersion => string.Format(
                    LocalizationService.Get("ProfileImportUnsupportedVersionFormat"),
                    exception.FormatVersion ?? 0,
                    ScufProfile.CurrentFormatVersion),
                _ => LocalizationService.Get("ProfileImportInvalid")
            };
        }

        private void RenameProfileFile(string oldName, string newName)
        {
            string oldPath = ZeroCuePaths.GetProfileFile(oldName);
            string newPath = ZeroCuePaths.GetProfileFile(newName);
            if (System.IO.File.Exists(oldPath) && !string.Equals(System.IO.Path.GetFullPath(oldPath), System.IO.Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
            {
                System.IO.File.Move(oldPath, newPath);
            }

            var renamedProfile = _service.TryReadProfile(System.IO.File.Exists(newPath) ? newName : oldName);
            if (renamedProfile != null)
            {
                renamedProfile.Name = newName;
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                AtomicFile.WriteAllText(newPath, System.Text.Json.JsonSerializer.Serialize(renamedProfile, options));
            }
        }

        private void DuplicateProfile(ProfileItemViewModel vm)
        {
            _profileEditorTarget = null;
            _profileDuplicateSource = vm;
            _pendingProfileImport = null;
            ProfileActionError = string.Empty;
            PendingProfileName = GetDuplicateProfileName(vm.Name);
            ProfileEditorError = string.Empty;
            IsProfileEditorRenameMode = false;
            ProfileEditorTitle = LocalizationService.Get("DuplicateProfileTitle");
            ProfileEditorDescription = string.Format(LocalizationService.Get("DuplicateProfileDescriptionFormat"), vm.Name);
            IsProfileEditorDialogOpen = true;
        }

        private void CreateProfileDuplicate(ProfileItemViewModel source, string duplicateName)
        {
            var sourceProfile = _service.TryReadProfile(source.OriginalName);
            if (sourceProfile == null)
            {
                return;
            }

            sourceProfile.Name = duplicateName;
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            AtomicFile.WriteAllText(
                ZeroCuePaths.GetProfileFile(duplicateName),
                System.Text.Json.JsonSerializer.Serialize(sourceProfile, options));
        }

        private string GetDuplicateProfileName(string sourceName)
        {
            var baseName = $"{sourceName} Copy";
            if (!ProfileNameExists(baseName))
            {
                return baseName;
            }

            var nextId = 2;
            string candidate;
            do
            {
                candidate = $"{baseName} {nextId}";
                nextId++;
            }
            while (ProfileNameExists(candidate));

            return candidate;
        }

        private void SetDefaultProfile(ProfileItemViewModel vm)
        {
            if (IsDefaultProfileName(vm.Name))
            {
                return;
            }

            _service.DefaultProfileName = vm.Name;
            RefreshSelectedProfileCards();
        }

        private void OpenProfileDeleteDialog(ProfileItemViewModel vm)
        {
            if (Profiles.Count <= 1)
            {
                return;
            }

            _pendingProfileActionTarget = vm;
            ProfileActionError = Profiles.Count <= 1 ? LocalizationService.Get("CannotDeleteLastProfile") : string.Empty;
            ProfileDeleteTitle = LocalizationService.Get("DeleteProfileTitle");
            ProfileDeleteDescription = string.Format(LocalizationService.Get("DeleteProfileDescriptionFormat"), vm.Name);
            IsProfileDeleteDialogOpen = true;
        }

        private void CloseProfileDeleteDialog()
        {
            IsProfileDeleteDialogOpen = false;
            _pendingProfileActionTarget = null;
            ProfileActionError = string.Empty;
        }

        private void ConfirmProfileDelete()
        {
            if (_pendingProfileActionTarget == null) return;
            if (Profiles.Count <= 1)
            {
                ProfileActionError = LocalizationService.Get("CannotDeleteLastProfile");
                return;
            }

            var deletedName = _pendingProfileActionTarget.OriginalName;
            string path = ZeroCuePaths.GetProfileFile(deletedName);
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }

            var fallbackProfile = Profiles.FirstOrDefault(profile => !string.Equals(profile, deletedName, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_service.DefaultProfileName, deletedName, StringComparison.OrdinalIgnoreCase))
            {
                _service.DefaultProfileName = fallbackProfile ?? "Default";
            }
            CloseProfileDeleteDialog();
            LoadProfilesList(fallbackProfile);
        }

        private void OpenLinkedAppsManager(ProfileItemViewModel vm)
        {
            ManagedLinkedAppsProfile = vm;
            RefreshManagedLinkedApps(vm);
            LinkedAppsManagerTitle = string.Format(LocalizationService.Get("ManageLinkedAppsTitleFormat"), vm.Name);
            LinkedAppsManagerDescription = string.Format(LocalizationService.Get("ManageLinkedAppsDescriptionFormat"), vm.LinkedAppPaths.Count);
            IsLinkedAppsManagerOpen = true;
        }

        private void CloseLinkedAppsManager()
        {
            IsLinkedAppsManagerOpen = false;
            ManagedLinkedAppsProfile = null;
            ManagedLinkedAppItems.Clear();
            OnPropertyChanged(nameof(HasManagedLinkedApps));
        }

        private void RefreshManagedLinkedApps(ProfileItemViewModel vm)
        {
            ManagedLinkedAppItems.Clear();
            foreach (var app in vm.LinkedAppItems)
            {
                ManagedLinkedAppItems.Add(app);
            }

            LinkedAppsManagerDescription = string.Format(LocalizationService.Get("ManageLinkedAppsDescriptionFormat"), vm.LinkedAppPaths.Count);
            OnPropertyChanged(nameof(HasManagedLinkedApps));
        }

        private void RefreshOpenLinkedAppsManager(string profileName)
        {
            if (!IsLinkedAppsManagerOpen) return;

            var refreshedProfile = ProfileCards.FirstOrDefault(profile =>
                string.Equals(profile.OriginalName, profileName, StringComparison.OrdinalIgnoreCase));
            if (refreshedProfile == null)
            {
                CloseLinkedAppsManager();
                return;
            }

            ManagedLinkedAppsProfile = refreshedProfile;
            LinkedAppsManagerTitle = string.Format(LocalizationService.Get("ManageLinkedAppsTitleFormat"), refreshedProfile.Name);
            RefreshManagedLinkedApps(refreshedProfile);
        }

        private void RemoveLinkedApp(LinkedAppItemViewModel app)
        {
            if (ManagedLinkedAppsProfile == null || app == null) return;

            var profileName = ManagedLinkedAppsProfile.OriginalName;
            if (_service.TryRemoveProfileLinkedApp(profileName, app.Path))
            {
                LoadProfilesList(SelectedProfile);
                var refreshedProfile = ProfileCards.FirstOrDefault(profile =>
                    string.Equals(profile.OriginalName, profileName, StringComparison.OrdinalIgnoreCase));

                if (refreshedProfile == null)
                {
                    CloseLinkedAppsManager();
                    return;
                }

                ManagedLinkedAppsProfile = refreshedProfile;
                RefreshManagedLinkedApps(refreshedProfile);
            }
        }

        private void ClearManagedLinkedApps()
        {
            if (ManagedLinkedAppsProfile == null || !ManagedLinkedAppsProfile.HasLinkedApp) return;
            OpenProfileUnlinkDialog(ManagedLinkedAppsProfile);
        }

        public void StageProfileAppLink(ProfileItemViewModel vm, string appPath)
        {
            StageProfileAppLinks(vm, new[] { appPath });
        }

        public void StageProfileAppLinks(ProfileItemViewModel vm, IEnumerable<string> appPaths)
        {
            var validPaths = appPaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                .Select(System.IO.Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validPaths.Count == 0)
            {
                return;
            }

            _pendingProfileActionTarget = vm;
            _pendingLinkedAppPaths = validPaths;
            PendingLinkedAppPath = string.Join("; ", validPaths);
            ProfileLinkTitle = LocalizationService.Get("LinkProfileTitle");
            ProfileLinkDescription = string.Format(
                LocalizationService.Get("LinkProfileDescriptionFormat"),
                vm.Name,
                validPaths.Count == 1
                    ? System.IO.Path.GetFileName(validPaths[0])
                    : string.Format(LocalizationService.Get("MultipleApplicationsFormat"), validPaths.Count));
            IsProfileLinkDialogOpen = true;
        }

        private void CloseProfileLinkDialog()
        {
            IsProfileLinkDialogOpen = false;
            _pendingProfileActionTarget = null;
            _pendingLinkedAppPaths.Clear();
            PendingLinkedAppPath = string.Empty;
        }

        private void ConfirmProfileLink()
        {
            if (_pendingProfileActionTarget == null || string.IsNullOrWhiteSpace(PendingLinkedAppPath)) return;

            var profileName = _pendingProfileActionTarget.OriginalName;
            if (_service.TryAddProfileLinkedApps(profileName, _pendingLinkedAppPaths))
            {
                CloseProfileLinkDialog();
                LoadProfilesList(SelectedProfile);
                RefreshOpenLinkedAppsManager(profileName);
            }
        }

        private void OpenProfileUnlinkDialog(ProfileItemViewModel vm)
        {
            if (!vm.HasLinkedApp) return;

            _pendingProfileActionTarget = vm;
            _pendingLinkedAppPaths.Clear();
            PendingLinkedAppPath = vm.LinkedAppPathDisplay;
            ProfileUnlinkTitle = LocalizationService.Get("UnlinkProfileTitle");
            ProfileUnlinkDescription = string.Format(
                LocalizationService.Get("UnlinkProfileDescriptionFormat"),
                vm.Name,
                vm.LinkedAppDisplay);
            IsProfileUnlinkDialogOpen = true;
        }

        private void CloseProfileUnlinkDialog()
        {
            IsProfileUnlinkDialogOpen = false;
            _pendingProfileActionTarget = null;
            _pendingLinkedAppPaths.Clear();
            PendingLinkedAppPath = string.Empty;
        }

        private void ConfirmProfileUnlink()
        {
            if (_pendingProfileActionTarget == null) return;

            var profileName = _pendingProfileActionTarget.OriginalName;
            if (_service.TryClearProfileLinkedApps(profileName))
            {
                CloseProfileUnlinkDialog();
                LoadProfilesList(SelectedProfile);
                RefreshOpenLinkedAppsManager(profileName);
            }
        }

        private void ActivateProfileFromCard(ProfileItemViewModel vm)
        {
            _profileBeforeLinkedForegroundApp = null;
            _activeLinkedForegroundProfile = null;
            SelectedProfile = vm.Name;
        }

        private void OnProfileSelectedForEdit(ProfileItemViewModel vm)
        {
            _profileBeforeLinkedForegroundApp = null;
            _activeLinkedForegroundProfile = null;
            SelectedProfile = vm.Name;
            SelectedSectionIndex = 0; // Go to mapping section
        }

        private void ActivateLinkedProfileForForegroundApp()
        {
            var foregroundPath = ForegroundApplicationService.GetForegroundProcessPath();
            if (string.Equals(foregroundPath, _lastForegroundAppPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _lastForegroundAppPath = foregroundPath;
            var matchingProfile = ProfileCards.FirstOrDefault(card =>
                card.LinkedAppPaths.Any(path => PathsMatch(path, foregroundPath)));

            if (matchingProfile != null)
            {
                if (!string.Equals(_activeLinkedForegroundProfile, matchingProfile.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (_activeLinkedForegroundProfile == null)
                    {
                        _profileBeforeLinkedForegroundApp = SelectedProfile;
                    }

                    _activeLinkedForegroundProfile = matchingProfile.Name;
                    if (!string.Equals(SelectedProfile, matchingProfile.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedProfile = matchingProfile.Name;
                    }
                }

                return;
            }

            if (_activeLinkedForegroundProfile != null)
            {
                var profileToRestore = _profileBeforeLinkedForegroundApp;
                _activeLinkedForegroundProfile = null;
                _profileBeforeLinkedForegroundApp = null;

                if (!string.IsNullOrWhiteSpace(profileToRestore)
                    && Profiles.Contains(profileToRestore)
                    && !string.Equals(SelectedProfile, profileToRestore, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedProfile = profileToRestore;
                }
            }
        }

        private static bool PathsMatch(string linkedPath, string foregroundPath)
        {
            if (string.IsNullOrWhiteSpace(linkedPath) || string.IsNullOrWhiteSpace(foregroundPath))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    System.IO.Path.GetFullPath(linkedPath),
                    System.IO.Path.GetFullPath(foregroundPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(linkedPath, foregroundPath, StringComparison.OrdinalIgnoreCase);
            }
        }


        private void SelectShiftModifier(string buttonName)
        {
            var normalizedButtonName = NormalizeShiftModifierName(buttonName);
            if (string.IsNullOrWhiteSpace(normalizedButtonName))
            {
                return;
            }

            PendingShiftModifier = normalizedButtonName;
            AcceptShiftModifier();
        }

        private void AcceptShiftModifier()
        {
            if (string.IsNullOrWhiteSpace(PendingShiftModifier)) return;

            ApplyShiftModifierSelection(PendingShiftModifier, saveProfile: false);
            IsShiftModifierPickerOpen = false;

            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);
        }

        private void ClearShiftModifier()
        {
            string oldShiftModifier = SelectedShiftModifier;
            if (!string.IsNullOrWhiteSpace(oldShiftModifier))
            {
                RestoreDefaultMapping(oldShiftModifier);
            }

            RestoreShiftAssignmentsExcept(string.Empty);
            PendingShiftModifier = string.Empty;
            _service.ShiftModifierButton = string.Empty;
            IsShiftModifierPickerOpen = false;

            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            RefreshAdvancedMappings();
            OnPropertyChanged(nameof(SelectedShiftModifier));
            NotifySelectedShiftModifierChanged();
            _service.SaveProfile(SelectedProfile);
        }

        private bool ApplyShiftModifierSelection(string buttonName, bool saveProfile)
        {
            var normalizedButtonName = NormalizeShiftModifierName(buttonName);
            if (string.IsNullOrWhiteSpace(normalizedButtonName))
            {
                return false;
            }

            var oldShiftModifier = NormalizeShiftModifierName(_service.ShiftModifierButton);
            var changed = _service.ShiftModifierButton != normalizedButtonName;
            _service.ShiftModifierButton = normalizedButtonName;

            changed |= RestoreShiftAssignmentsExcept(normalizedButtonName);
            if (!string.IsNullOrEmpty(oldShiftModifier) && oldShiftModifier != normalizedButtonName)
            {
                changed |= RestoreDefaultMapping(oldShiftModifier);
            }
            changed |= ClearShiftModifierMappings(normalizedButtonName);

            OnPropertyChanged(nameof(SelectedShiftModifier));
            NotifySelectedShiftModifierChanged();

            if (changed && saveProfile)
            {
                _service.SaveProfile(SelectedProfile);
            }

            return changed;
        }

        private bool SynchronizeShiftModifierMappings()
        {
            var normalizedShiftModifier = NormalizeShiftModifierName(_service.ShiftModifierButton);
            var changed = _service.ShiftModifierButton != normalizedShiftModifier;
            _service.ShiftModifierButton = normalizedShiftModifier;

            if (string.IsNullOrWhiteSpace(normalizedShiftModifier))
            {
                return changed | RestoreShiftAssignmentsExcept(string.Empty);
            }

            changed |= RestoreShiftAssignmentsExcept(normalizedShiftModifier);
            changed |= ClearShiftModifierMappings(normalizedShiftModifier);
            return changed;
        }

        private bool RestoreDefaultMapping(string buttonName)
        {
            var normalizedButtonName = NormalizeShiftModifierName(buttonName);
            if (string.IsNullOrWhiteSpace(normalizedButtonName))
            {
                return false;
            }

            var changed = false;
            foreach (var isShiftLayer in new[] { false, true })
            {
                changed |= RemoveAdvancedMappings(normalizedButtonName, isShiftLayer);
                string defaultMapping = GetDefaultMappingFor(normalizedButtonName);
                if (_service.GetRemapTarget(normalizedButtonName, RemapGestureTypes.Simple, isShiftLayer) != defaultMapping)
                {
                    changed = true;
                }
                _service.SetRemapTarget(normalizedButtonName, RemapGestureTypes.Simple, defaultMapping, isShiftLayer);
            }

            return changed;
        }

        private bool ClearShiftModifierMappings(string buttonName)
        {
            var normalizedButtonName = NormalizeShiftModifierName(buttonName);
            if (string.IsNullOrWhiteSpace(normalizedButtonName))
            {
                return false;
            }

            var changed = false;
            foreach (var isShiftLayer in new[] { false, true })
            {
                changed |= RemoveAdvancedMappings(normalizedButtonName, isShiftLayer);
                if (_service.GetRemapTarget(normalizedButtonName, RemapGestureTypes.Simple, isShiftLayer) != "Shift")
                {
                    changed = true;
                }
                _service.SetRemapTarget(normalizedButtonName, RemapGestureTypes.Simple, "Shift", isShiftLayer);
            }

            return changed;
        }

        private bool RemoveAdvancedMappings(string buttonName, bool isShiftLayer)
        {
            var changed = _service.GetConfiguredAdvancedGestures(buttonName, isShiftLayer).Count > 0;
            _service.RemoveRemapTarget(buttonName, RemapGestureTypes.DoubleTap, isShiftLayer);
            _service.RemoveRemapTarget(buttonName, RemapGestureTypes.Hold, isShiftLayer);
            _service.RemoveRemapTarget(buttonName, RemapGestureTypes.PressStart, isShiftLayer);
            _service.RemoveRemapTarget(buttonName, RemapGestureTypes.PressRelease, isShiftLayer);
            return changed;
        }

        private bool RestoreShiftAssignmentsExcept(string selectedShiftModifier)
        {
            var changed = false;
            var selected = NormalizeShiftModifierName(selectedShiftModifier);
            var sourcesToRestore = GetShiftAssignedSources()
                .Where(source => !IsSamePhysicalButton(source, selected))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var sourceName in sourcesToRestore)
            {
                changed |= RestoreDefaultMapping(sourceName);
            }

            return changed;
        }

        private IEnumerable<string> GetShiftAssignedSources()
        {
            foreach (var sourceName in GetShiftAssignedSources(_service.PaddleRemapTable)) yield return sourceName;
            foreach (var sourceName in GetShiftAssignedSources(_service.GKeyRemapTable)) yield return sourceName;
            foreach (var sourceName in GetShiftAssignedSources(_service.ButtonRemapTable)) yield return sourceName;
            foreach (var sourceName in GetShiftAssignedSources(_service.ShiftPaddleRemapTable)) yield return sourceName;
            foreach (var sourceName in GetShiftAssignedSources(_service.ShiftGKeyRemapTable)) yield return sourceName;
            foreach (var sourceName in GetShiftAssignedSources(_service.ShiftButtonRemapTable)) yield return sourceName;
        }

        private static IEnumerable<string> GetShiftAssignedSources(Dictionary<string, string> table)
        {
            return table
                .Where(kvp => kvp.Value == "Shift")
                .Select(kvp => kvp.Key);
        }

        private static bool IsSamePhysicalButton(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && NormalizeShiftModifierName(left) == NormalizeShiftModifierName(right);
        }

        private static string NormalizeShiftModifierName(string buttonName)
        {
            return buttonName switch
            {
                "LB" => "LeftShoulder",
                "RB" => "RightShoulder",
                "L3" => "LeftThumb",
                "R3" => "RightThumb",
                _ => buttonName
            };
        }

        private void NotifySelectedShiftModifierChanged()
        {
            OnPropertyChanged(nameof(SelectedShiftModifierDisplayName));
            OnPropertyChanged(nameof(SelectedShiftModifierIcon));
            OnPropertyChanged(nameof(SelectedShiftModifierFallbackText));
            OnPropertyChanged(nameof(HasSelectedShiftModifier));
            OnPropertyChanged(nameof(HasSelectedShiftModifierIcon));
            OnPropertyChanged(nameof(HasSelectedShiftModifierFallback));
        }

        private void InitializePaddleRemaps()
        {
            PaddleRemaps.Clear();
            var paddles = new[] { "Paddle_R4", "Paddle_R5", "Paddle_L4", "Paddle_L5", "SAX_L", "SAX_R" };
            var table = IsShiftMode ? _service.ShiftPaddleRemapTable : _service.PaddleRemapTable;
            foreach (var paddle in paddles)
            {
                string systemTarget = "Sin Mapeo";
                if (table.TryGetValue(paddle, out var t))
                {
                    systemTarget = t;
                }

                var entry = new PaddleRemapEntry(paddle, systemTarget, table, SaveRemap);
                PaddleRemaps.Add(entry);
            }
        }

        private void InitializeGKeyRemaps()
        {
            GKeyRemaps.Clear();
            var gkeys = new[] { "G1", "G2", "G3", "G4", "G5" };
            var table = IsShiftMode ? _service.ShiftGKeyRemapTable : _service.GKeyRemapTable;
            foreach (var gkey in gkeys)
            {
                string systemTarget = "Sin Mapeo";
                if (table.TryGetValue(gkey, out var t))
                {
                    systemTarget = t;
                }

                var entry = new GKeyRemapEntry(gkey, systemTarget, table, SaveRemap);
                GKeyRemaps.Add(entry);
            }
        }

        private void Service_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScufDeviceService.IsConnected))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsWaitingForConnection));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.IsConnecting))
            {
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsWaitingForConnection));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.IsViGEmActive))
            {
                OnPropertyChanged(nameof(IsViGEmActive));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.StatusText))
            {
                OnPropertyChanged(nameof(StatusText));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.StatusDetail))
            {
                OnPropertyChanged(nameof(StatusDetail));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.ConnectionStatusState))
            {
                OnPropertyChanged(nameof(ConnectionStatusState));
                OnPropertyChanged(nameof(IsStatusNone));
                OnPropertyChanged(nameof(IsStatusReceiverOnly));
                OnPropertyChanged(nameof(IsStatusWaiting));
                OnPropertyChanged(nameof(IsStatusWirelessConnecting));
                OnPropertyChanged(nameof(IsStatusWirelessConnected));
                OnPropertyChanged(nameof(IsStatusUsbConnecting));
                OnPropertyChanged(nameof(IsStatusUsbConnected));
                OnPropertyChanged(nameof(IsStatusConnecting));
                OnPropertyChanged(nameof(IsStatusConnected));
                OnPropertyChanged(nameof(IsStatusError));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.TriggerCurve))
            {
                NotifyTriggerCurveChanged();
            }
            else if (e.PropertyName == nameof(ScufDeviceService.CloseBehavior))
            {
                _selectedCloseBehaviorIndex = _service.CloseBehavior == ApplicationCloseBehavior.MinimizeToTray ? 0 : 1;
                OnPropertyChanged(nameof(SelectedCloseBehaviorIndex));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.AskBeforeClosing))
            {
                OnPropertyChanged(nameof(AskBeforeClosing));
            }
            else if (e.PropertyName == nameof(ScufDeviceService.StickCurve) ||
                     e.PropertyName == nameof(ScufDeviceService.StickDeadzoneMinPercent) ||
                     e.PropertyName == nameof(ScufDeviceService.StickDeadzoneMaxPercent) ||
                     e.PropertyName == nameof(ScufDeviceService.StickCustomCurveX) ||
                     e.PropertyName == nameof(ScufDeviceService.StickCustomCurveY))
            {
                NotifyStickSettingsChanged();
            }
        }

        private void NotifyTriggerCurveChanged()
        {
            OnPropertyChanged(nameof(SelectedTriggerCurve));
            OnPropertyChanged(nameof(SelectedTriggerCurveDisplayName));
            OnPropertyChanged(nameof(IsTriggerCurveLineal));
            OnPropertyChanged(nameof(IsTriggerCurveExponencial));
            OnPropertyChanged(nameof(IsTriggerCurveDinamica));
            OnPropertyChanged(nameof(IsTriggerCurveAgresiva));
            OnPropertyChanged(nameof(IsTriggerCurveCustom));
            OnPropertyChanged(nameof(CustomCurvePoints));
            OnPropertyChanged(nameof(TriggerCurveDescription));
            NotifyTriggerOutputChanged();
            RefreshSmoothedTriggerCurveOutputs();
        }

        private void NotifyStickSettingsChanged()
        {
            OnPropertyChanged(nameof(SelectedStickCurve));
            OnPropertyChanged(nameof(IsStickCurveLineal));
            OnPropertyChanged(nameof(IsStickCurvePrecisa));
            OnPropertyChanged(nameof(IsStickCurveDinamica));
            OnPropertyChanged(nameof(IsStickCurveAgresiva));
            OnPropertyChanged(nameof(IsStickCurveCustom));
            OnPropertyChanged(nameof(StickCurveDescription));
            OnPropertyChanged(nameof(StickCustomCurveX));
            OnPropertyChanged(nameof(StickCustomCurveY));
            OnPropertyChanged(nameof(StickCurvePoints));
            OnPropertyChanged(nameof(StickDeadzoneMinPercent));
            OnPropertyChanged(nameof(StickDeadzoneMaxPercent));
            OnPropertyChanged(nameof(StickMinDeadzoneRingSize));
            OnPropertyChanged(nameof(StickMinDeadzoneRingOffset));
            OnPropertyChanged(nameof(StickMaxDeadzoneRingSize));
            OnPropertyChanged(nameof(StickMaxDeadzoneRingOffset));
            NotifyStickOutputChanged();
        }

        private void NotifyStickOutputChanged()
        {
            OnPropertyChanged(nameof(LeftStickOutputX));
            OnPropertyChanged(nameof(LeftStickOutputY));
            OnPropertyChanged(nameof(RightStickOutputX));
            OnPropertyChanged(nameof(RightStickOutputY));
            OnPropertyChanged(nameof(LeftStickInputMagnitudePercent));
            OnPropertyChanged(nameof(RightStickInputMagnitudePercent));
            OnPropertyChanged(nameof(LeftStickOutputMagnitudePercent));
            OnPropertyChanged(nameof(RightStickOutputMagnitudePercent));
            OnPropertyChanged(nameof(LeftStickOutputText));
            OnPropertyChanged(nameof(RightStickOutputText));
            OnPropertyChanged(nameof(LeftStickInputDotX));
            OnPropertyChanged(nameof(LeftStickInputDotY));
            OnPropertyChanged(nameof(RightStickInputDotX));
            OnPropertyChanged(nameof(RightStickInputDotY));
            OnPropertyChanged(nameof(LeftStickOutputDotX));
            OnPropertyChanged(nameof(LeftStickOutputDotY));
            OnPropertyChanged(nameof(RightStickOutputDotX));
            OnPropertyChanged(nameof(RightStickOutputDotY));
            OnPropertyChanged(nameof(LeftStickCurveMarkerX));
            OnPropertyChanged(nameof(LeftStickCurveMarkerY));
            OnPropertyChanged(nameof(RightStickCurveMarkerX));
            OnPropertyChanged(nameof(RightStickCurveMarkerY));
        }

        private void NotifyTriggerOutputChanged()
        {
            OnPropertyChanged(nameof(LeftTriggerPercent));
            OnPropertyChanged(nameof(RightTriggerPercent));
            OnPropertyChanged(nameof(LeftTriggerOutput));
            OnPropertyChanged(nameof(RightTriggerOutput));
            OnPropertyChanged(nameof(LeftTriggerOutputByte));
            OnPropertyChanged(nameof(RightTriggerOutputByte));
            OnPropertyChanged(nameof(LeftTriggerOutputPercent));
            OnPropertyChanged(nameof(RightTriggerOutputPercent));
            OnPropertyChanged(nameof(TriggerDeltaPercent));
            OnPropertyChanged(nameof(TriggerDeltaText));
            OnPropertyChanged(nameof(LeftTriggerInputText));
            OnPropertyChanged(nameof(RightTriggerInputText));
            OnPropertyChanged(nameof(LeftTriggerMarkerX));
            OnPropertyChanged(nameof(LeftTriggerMarkerY));
            OnPropertyChanged(nameof(RightTriggerMarkerX));
            OnPropertyChanged(nameof(RightTriggerMarkerY));
        }

        // Modal Properties
        private bool _isDriverModalOpen;
        public bool IsDriverModalOpen { get => _isDriverModalOpen; set => SetProperty(ref _isDriverModalOpen, value); }

        private bool _isDriverModalConfirmationState;
        public bool IsDriverModalConfirmationState { get => _isDriverModalConfirmationState; set => SetProperty(ref _isDriverModalConfirmationState, value); }

        private bool _isDriverModalProcessingState;
        public bool IsDriverModalProcessingState { get => _isDriverModalProcessingState; set => SetProperty(ref _isDriverModalProcessingState, value); }

        private bool _isDriverModalResultState;
        public bool IsDriverModalResultState { get => _isDriverModalResultState; set => SetProperty(ref _isDriverModalResultState, value); }

        private bool _isDriverModalResultSuccess;
        public bool IsDriverModalResultSuccess { get => _isDriverModalResultSuccess; set => SetProperty(ref _isDriverModalResultSuccess, value); }

        private bool _isDriverModalResultError;
        public bool IsDriverModalResultError { get => _isDriverModalResultError; set => SetProperty(ref _isDriverModalResultError, value); }

        private string _driverModalTitle = "";
        public string DriverModalTitle { get => _driverModalTitle; set => SetProperty(ref _driverModalTitle, value); }

        private string _driverModalDescription = "";
        public string DriverModalDescription { get => _driverModalDescription; set => SetProperty(ref _driverModalDescription, value); }

        public ICommand ConfirmDriverCommand { get; }
        public ICommand CloseDriverModalCommand { get; }

        private enum PendingDriverAction
        {
            Install,
            Restore,
            InstallReceiver,
            RestoreReceiver,
            None
        }
        private PendingDriverAction _pendingAction = PendingDriverAction.None;


        private void SetDriverModalState(bool confirmation, bool processing, bool result)
        {
            IsDriverModalConfirmationState = confirmation;
            IsDriverModalProcessingState = processing;
            IsDriverModalResultState = result;
        }

        private void ShowMissingViGEmBusModal()
        {
            _pendingAction = PendingDriverAction.None;
            SetDriverModalState(false, false, true);
            IsDriverModalResultSuccess = false;
            IsDriverModalResultError = true;
            DriverModalTitle = LocalizationService.Get("ViGEmBusRequiredTitle");
            DriverModalDescription = LocalizationService.Get("ViGEmBusRequiredDescription");
            IsDriverModalOpen = true;
        }

        private void ExecuteInstallDriverAsync()
        {
            if (IsProcessing) return;
            _pendingAction = PendingDriverAction.Install;
            SetDriverModalState(true, false, false);
            DriverModalTitle = LocalizationService.Get("DriverInstallTitle");
            DriverModalDescription = LocalizationService.Get("DriverInstallDescription");
            IsDriverModalOpen = true;
        }

        private void ExecuteRestoreDriverAsync()
        {
            if (IsProcessing) return;
            _pendingAction = PendingDriverAction.Restore;
            SetDriverModalState(true, false, false);
            DriverModalTitle = LocalizationService.Get("DriverRestoreTitle");
            DriverModalDescription = LocalizationService.Get("DriverRestoreDescription");
            IsDriverModalOpen = true;
        }

        private void ExecuteInstallReceiverDriverAsync()
        {
            if (IsProcessing) return;
            _pendingAction = PendingDriverAction.InstallReceiver;
            SetDriverModalState(true, false, false);
            DriverModalTitle = LocalizationService.Get("ReceiverInstallTitle");
            DriverModalDescription = LocalizationService.Get("ReceiverInstallDescription");
            IsDriverModalOpen = true;
        }

        private void ExecuteRestoreReceiverDriverAsync()
        {
            if (IsProcessing) return;
            _pendingAction = PendingDriverAction.RestoreReceiver;
            SetDriverModalState(true, false, false);
            DriverModalTitle = LocalizationService.Get("ReceiverRestoreTitle");
            DriverModalDescription = LocalizationService.Get("ReceiverRestoreDescription");
            IsDriverModalOpen = true;
        }

        private async void ExecuteListWirelessReceiverPnpInstancesAsync()
        {
            if (IsProcessing) return;
            SetDriverModalState(false, true, false);
            DriverModalTitle = LocalizationService.Get("PnpDiagnosticTitle");
            DriverModalDescription = LocalizationService.Get("QueryingInstances");
            IsDriverModalOpen = true;

            var service = new Services.DriverAutomationService();
            string result = await service.GetWirelessReceiverPnpInstancesAsync();

            SetDriverModalState(false, false, true);
            IsDriverModalResultSuccess = true;
            IsDriverModalResultError = false;
            DriverModalTitle = LocalizationService.Get("ReceiverPnpInstancesTitle");
            DriverModalDescription = result;
        }

        private async Task ConfirmDriverActionAsync()
        {
            if (IsProcessing || _pendingAction == PendingDriverAction.None) return;

            SetDriverModalState(false, true, false);
            IsProcessing = true;

            try
            {
                using var connectionSuspension = await _service.SuspendConnectionsAsync();

                await Task.Delay(500);

                var service = new Services.DriverAutomationService();
                bool success = false;

                if (_pendingAction == PendingDriverAction.Install)
                {
                    ProcessingMessage = LocalizationService.Get("ProcessingInstallDriver");
                    success = await service.InstallWinUsbDriversAsync();
                }
                else if (_pendingAction == PendingDriverAction.Restore)
                {
                    ProcessingMessage = LocalizationService.Get("ProcessingRestoreDriver");
                    success = await service.RestoreDefaultDriversAsync();
                }
                else if (_pendingAction == PendingDriverAction.InstallReceiver)
                {
                    ProcessingMessage = LocalizationService.Get("ProcessingInstallReceiver");
                    success = await service.InstallReceiverWinUsbDriversAsync();
                }
                else if (_pendingAction == PendingDriverAction.RestoreReceiver)
                {
                    ProcessingMessage = LocalizationService.Get("ProcessingRestoreReceiver");
                    success = await service.RestoreReceiverDefaultDriversAsync();
                }

                IsProcessing = false;
                SetDriverModalState(false, false, true);

                if (success)
                {
                    IsDriverModalResultSuccess = true;
                    IsDriverModalResultError = false;
                    DriverModalTitle = LocalizationService.Get("OperationCompleted");
                    if (_pendingAction == PendingDriverAction.Install)
                        DriverModalDescription = LocalizationService.Get("DriverInstalledSuccess");
                    else
                        DriverModalDescription = LocalizationService.Get("DriversRemovedSuccess");
                    if (_pendingAction == PendingDriverAction.InstallReceiver)
                        DriverModalDescription = LocalizationService.Get("ReceiverDriverInstalledSuccess");
                    else if (_pendingAction == PendingDriverAction.RestoreReceiver)
                        DriverModalDescription = LocalizationService.Get("ReceiverDriversRemovedSuccess");
                }
                else
                {
                    IsDriverModalResultSuccess = false;
                    IsDriverModalResultError = true;
                    DriverModalTitle = LocalizationService.Get("OperationFailed");
                    DriverModalDescription = LocalizationService.Get("OperationFailedDescription");
                }
            }
            catch (Exception ex)
            {
                IsProcessing = false;
                SetDriverModalState(false, false, true);
                IsDriverModalResultSuccess = false;
                IsDriverModalResultError = true;
                DriverModalTitle = LocalizationService.Get("UnexpectedError");
                DriverModalDescription = string.Format(LocalizationService.Get("UnexpectedErrorDescription"), ex.Message);
            }
            finally
            {
                _pendingAction = PendingDriverAction.None;
            }
        }

        private async Task ConnectAsync()
        {
            await _service.ConnectAsync();
        }

        private void Disconnect()
        {
            _service.Disconnect();
        }

        private void SaveRemap()
        {
            _service.SaveProfile(SelectedProfile);
        }

        private void BeginRemap(string buttonName)
        {
            if (string.IsNullOrEmpty(buttonName)) return;

            if (IsShiftModifierPickerOpen)
            {
                SelectShiftModifier(buttonName);
                return;
            }

            if (IsRemapping && !string.IsNullOrEmpty(RemappingTargetName))
            {
                HandleKeyPress(buttonName);
                return;
            }

            _visibleRemapGestureTabs.Clear();
            EndTriggerOutputSelection();
            RemappingTargetName = buttonName;
            SelectedRemapGestureType = RemapGestureTypes.Simple;
            UpdateDetectedTargetForSelectedGesture();
            BeginRemapModalInitialization();
            ResetRemapModalState();
            IsRemapping = true;
            EndRemapModalInitialization();
            LoadMacroEditorForCurrentTarget();
            NotifyRemapGestureStateChanged();
        }

        private void BeginRemap(string buttonName, string gestureType, bool isShiftLayer)
        {
            if (string.IsNullOrWhiteSpace(buttonName) || string.IsNullOrWhiteSpace(gestureType))
            {
                return;
            }

            _visibleRemapGestureTabs.Clear();
            EndTriggerOutputSelection();
            IsShiftMode = isShiftLayer;
            RemappingTargetName = buttonName;
            SelectedRemapGestureType = gestureType;
            if (gestureType != RemapGestureTypes.Simple)
            {
                _visibleRemapGestureTabs.Add(gestureType);
            }

            UpdateDetectedTargetForSelectedGesture();
            BeginRemapModalInitialization();
            ResetRemapModalState();
            IsRemapping = true;
            EndRemapModalInitialization();
            LoadMacroEditorForCurrentTarget();
            NotifyRemapGestureStateChanged();
        }

        private static void BeginRemapModalInitialization()
        {
            SuppressRemapInputTabTransition = true;
        }

        private static void EndRemapModalInitialization()
        {
            Dispatcher.UIThread.Post(
                () => SuppressRemapInputTabTransition = false,
                DispatcherPriority.Background);
        }

        private void ResetRemapModalState()
        {
            RemapInputTabIndex = GetInitialRemapInputTabIndex(DetectedTarget);
            LoadActionModalStateFromTarget(DetectedTarget);
            LoadGestureDelayForSelectedGesture();
        }

        private void LoadActionModalStateFromTarget(string target)
        {
            ActionSelectedProfile = null;
            ActionProfileWhileHeld = SelectedRemapGestureType == RemapGestureTypes.Hold;

            if (TryParseLoadProfileActionTarget(target, out var profileName, out var whileHeld))
            {
                ActionSelectedProfile = Profiles.FirstOrDefault(profile =>
                    string.Equals(profile, profileName, StringComparison.OrdinalIgnoreCase));
                ActionProfileWhileHeld = whileHeld || SelectedRemapGestureType == RemapGestureTypes.Hold;
            }
        }

        private int GetInitialRemapInputTabIndex(string target)
        {
            if (string.IsNullOrWhiteSpace(target) || target == "Sin Mapeo" || target == "Original")
            {
                return 0;
            }

            if (MacroTarget.IsMacroTarget(target))
            {
                return 4;
            }

            if (target == "Shift" || IsActionTarget(target))
            {
                return IsActionInputTabEnabled ? 3 : 0;
            }

            if (IsMouseMappingTarget(target))
            {
                return 2;
            }

            if (IsKeyboardMappingTarget(target))
            {
                return 1;
            }

            return 0;
        }

        private static bool IsMouseMappingTarget(string target)
        {
            return target.StartsWith("Mouse", StringComparison.Ordinal)
                || target is "ScrollUp" or "ScrollDown";
        }

        private static bool IsKeyboardMappingTarget(string target)
        {
            return target.StartsWith("Key", StringComparison.Ordinal)
                || target.StartsWith("NumPad", StringComparison.Ordinal)
                || target.StartsWith("Oem", StringComparison.Ordinal)
                || IsFunctionKey(target)
                || IsTopRowNumberKey(target)
                || target is "Escape" or "Delete" or "Backspace" or "BackSpace" or "Tab"
                    or "Capital" or "Return" or "LeftShift" or "RightShift"
                    or "LeftCtrl" or "RightCtrl" or "LWin" or "LeftAlt" or "RightAlt"
                    or "Space" or "Insert" or "Home" or "End" or "PageUp" or "PageDown"
                    or "NumLock" or "Divide" or "Multiply" or "Subtract" or "Add"
                    or "Decimal" or "NumpadDecimal";
        }

        private static bool IsFunctionKey(string target)
        {
            return target.Length >= 2
                && target[0] == 'F'
                && int.TryParse(target.Substring(1), out var keyNumber)
                && keyNumber >= 1
                && keyNumber <= 24;
        }

        private static bool IsTopRowNumberKey(string target)
        {
            return target.Length == 2 && target[0] == 'D' && char.IsDigit(target[1]);
        }

        private void OpenDetailsRemap(AdvancedMappingCommand command)
        {
            if (command == null || command.GestureType == "None") return;
            if (!CanUseGesture(command.SourceName, command.GestureType, command.IsShiftLayer))
            {
                return;
            }

            BeginRemap(command.SourceName, command.GestureType, command.IsShiftLayer);
        }

        private void AddDetailsRemapGesture(AdvancedMappingItem item, string gestureType)
        {
            if (item == null || string.IsNullOrWhiteSpace(gestureType)) return;
            if (!CanUseGesture(item.SourceName, gestureType, item.IsShiftLayer))
            {
                return;
            }

            BeginRemap(item.SourceName, gestureType, item.IsShiftLayer);
        }

        private void RemoveDetailsRemapGesture(AdvancedMappingCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.SourceName) || !command.CanRemove) return;
            _service.RemoveRemapTarget(command.SourceName, command.GestureType, command.IsShiftLayer);
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);
        }

        private void CancelRemap()
        {
            StopMacroRecording();
            EndTriggerOutputSelection();
            IsRemapping = false;
            _visibleRemapGestureTabs.Clear();
            NotifyRemapGestureStateChanged();
        }

        private void AcceptRemap()
        {
            StopMacroRecording();
            EndTriggerOutputSelection();
            if (RemapInputTabIndex == 3
                && !string.IsNullOrWhiteSpace(ActionSelectedProfile)
                && CanAssignLoadProfileAction)
            {
                HandleMapAction("LoadProfile");
                return;
            }

            IsRemapping = false;
            _visibleRemapGestureTabs.Clear();
        }

        private string CurrentMacroId => $"{(IsShiftMode ? "shift" : "standard")}:{RemappingTargetName}:{SelectedRemapGestureType}";

        private void LoadMacroEditorForCurrentTarget()
        {
            if (string.IsNullOrWhiteSpace(RemappingTargetName))
            {
                return;
            }

            _isLoadingMacroEditor = true;
            try
            {
                MacroSteps.Clear();
                var target = _service.GetRemapTarget(RemappingTargetName, SelectedRemapGestureType, IsShiftMode);
                var macroId = MacroTarget.IsMacroTarget(target)
                    ? MacroTarget.GetId(target)
                    : CurrentMacroId;
                var macro = _service.GetMacroDefinition(macroId);
                MacroRepeatWhileHeld = macro?.RepeatWhileHeld ?? false;

                if (macro?.Steps != null)
                {
                    foreach (var step in macro.Steps)
                    {
                        AddMacroStepViewModel(step);
                    }
                }

                RebuildMacroFlow();
            }
            finally
            {
                _isLoadingMacroEditor = false;
            }
        }
        private void RefreshSavedMacros()
        {
            SavedMacros.Clear();
            foreach (var macro in _service.MacroLibrary.Values.OrderBy(m => m.Name))
            {
                SavedMacros.Add(macro);
            }
            OnPropertyChanged(nameof(HasSavedMacros));
        }

        private void LoadMacroFromLibrary(MacroDefinition? libraryMacro)
        {
            if (libraryMacro == null || string.IsNullOrWhiteSpace(RemappingTargetName)) return;

            MacroSteps.Clear();
            MacroFlowItems.Clear();

            MacroRepeatWhileHeld = libraryMacro.RepeatWhileHeld;

            foreach (var step in libraryMacro.Steps)
            {
                AddMacroStepViewModel(new MacroStep
                {
                    InputKind = step.InputKind,
                    Target = step.Target,
                    Action = step.Action,
                    DelayMs = step.DelayMs
                });
            }

            SaveCurrentMacro();
            IsLoadMacroDialogOpen = false;
        }

        private void OpenSaveMacroDialog()
        {
            if (MacroSteps.Count == 0) return;
            NewMacroName = string.Empty;
            IsSaveMacroDialogOpen = true;
        }

        private void ConfirmSaveMacro()
        {
            if (string.IsNullOrWhiteSpace(NewMacroName)) return;

            var macro = new MacroDefinition
            {
                Id = Guid.NewGuid().ToString(),
                Name = NewMacroName.Trim(),
                RepeatWhileHeld = MacroRepeatWhileHeld,
                Steps = MacroSteps.Select(step => step.ToModel()).ToList()
            };

            _service.MacroLibrary[macro.Id] = macro;
            _service.SaveProfile(SelectedProfile);

            RefreshSavedMacros();

            IsSaveMacroDialogOpen = false;
        }

        private void CancelSaveMacro()
        {
            IsSaveMacroDialogOpen = false;
            NewMacroName = string.Empty;
        }

        private void OpenLoadMacroDialog()
        {
            IsLoadMacroDialogOpen = true;
        }

        private void CancelLoadMacro()
        {
            IsLoadMacroDialogOpen = false;
        }

        private void DeleteSavedMacro(MacroDefinition? macro)
        {
            if (macro != null && _service.MacroLibrary.ContainsKey(macro.Id))
            {
                _service.MacroLibrary.Remove(macro.Id);
                _service.SaveProfile(SelectedProfile);
                RefreshSavedMacros();

                if (SavedMacros.Count == 0)
                {
                    IsLoadMacroDialogOpen = false;
                }
            }
        }
        private void ToggleMacroRecording()
        {
            if (IsMacroRecording)
                StopMacroRecording();
            else
                StartMacroRecording();
        }

        private void StartMacroRecording()
        {
            if (!IsRemapping || string.IsNullOrWhiteSpace(RemappingTargetName))
            {
                return;
            }

            MacroSteps.Clear();
            MacroFlowItems.Clear();
            _macroRecordingHeldKeys.Clear();
            _macroRecordingLastEventMs = Environment.TickCount64;
            IsMacroRecording = true;

            SaveCurrentMacro();
        }

        private void StopMacroRecording()
        {
            if (!IsMacroRecording)
            {
                return;
            }

            IsMacroRecording = false;
            _macroRecordingHeldKeys.Clear();
            SaveCurrentMacro();
        }

        private void ClearMacro()
        {
            StopMacroRecording();
            MacroSteps.Clear();
            MacroFlowItems.Clear();

            SaveCurrentMacro();
        }

        private void RemoveMacroStep(MacroStepViewModel step)
        {
            if (step == null)
            {
                return;
            }

            step.PropertyChanged -= MacroStep_PropertyChanged;
            MacroSteps.Remove(step);
            RebuildMacroFlow();

            SaveCurrentMacro();
        }

        private void HandleMacroControllerInput(MacroInputEvent inputEvent)
        {
            if (!IsMacroRecording || inputEvent.InputKind != MacroInputKinds.Gamepad)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => RecordMacroEvent(inputEvent.InputKind, inputEvent.Target, inputEvent.Action));
        }

        public void RecordMacroKeyboardEvent(string keyName, bool isDown)
        {
            RecordMacroButtonEvent(MacroInputKinds.Keyboard, keyName, isDown);
        }

        public void RecordMacroMouseEvent(string mouseButtonName, bool isDown)
        {
            RecordMacroButtonEvent(MacroInputKinds.Mouse, mouseButtonName, isDown);
        }

        private void RecordMacroButtonEvent(string inputKind, string target, bool isDown)
        {
            if (!IsMacroRecording || string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var stateKey = $"{inputKind}:{target}";
            if (isDown)
            {
                if (!_macroRecordingHeldKeys.Add(stateKey))
                {
                    return;
                }
            }
            else if (!_macroRecordingHeldKeys.Remove(stateKey))
            {
                return;
            }

            RecordMacroEvent(inputKind, target, isDown ? MacroActions.Down : MacroActions.Up);
        }

        private void RecordMacroEvent(string inputKind, string target, string action)
        {
            if (!IsMacroRecording || string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var now = Environment.TickCount64;
            if (MacroSteps.Count > 0)
            {
                if (MacroRecordFixedDelay)
                {
                    MacroSteps[^1].DelayMs = MacroFixedDelayMs;
                }
                else
                {
                    MacroSteps[^1].DelayMs = (int)Math.Clamp(now - _macroRecordingLastEventMs, 0, 60000);
                }
            }

            _macroRecordingLastEventMs = now;
            AddMacroStepViewModel(new MacroStep
            {
                InputKind = inputKind,
                Target = target,
                Action = action,
                DelayMs = 0
            });
            SaveCurrentMacro();
        }

        private void AddMacroStepViewModel(MacroStep step)
        {
            var vm = new MacroStepViewModel(step);
            vm.PropertyChanged += MacroStep_PropertyChanged;
            MacroSteps.Add(vm);
            RebuildMacroFlow();
        }

        private void MacroStep_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            SaveCurrentMacro();
        }

        private void RebuildMacroFlow()
        {
            MacroFlowItems.Clear();
            for (var i = 0; i < MacroSteps.Count; i++)
            {
                var step = MacroSteps[i];
                MacroFlowItems.Add(MacroFlowItemViewModel.CreateAction(step, RemoveMacroStepCommand));
                if (i < MacroSteps.Count - 1)
                {
                    MacroFlowItems.Add(MacroFlowItemViewModel.CreateDelay(step, RemoveMacroStepCommand));
                }
            }
        }

        private void SaveCurrentMacro()
        {
            if (_isLoadingMacroEditor || string.IsNullOrWhiteSpace(RemappingTargetName))
            {
                return;
            }

            var macroId = CurrentMacroId;
            var macro = new MacroDefinition
            {
                Id = macroId,
                Name = $"{GetButtonDisplayName(RemappingTargetName)} {SelectedRemapActionDisplayName}",
                RepeatWhileHeld = MacroRepeatWhileHeld,
                Steps = MacroSteps.Select(step => step.ToModel()).ToList()
            };

            _service.SetMacroDefinition(macro);
            var macroTarget = MacroTarget.Create(macroId);
            ClearShiftModifierIfOverwritten(RemappingTargetName, SelectedRemapGestureType, macroTarget);
            _service.SetRemapTarget(RemappingTargetName, SelectedRemapGestureType, macroTarget, IsShiftMode);
            DetectedTarget = macroTarget;
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);
        }

        private void RestoreRemap()
        {
            if (string.IsNullOrEmpty(RemappingTargetName)) return;

            _service.RemoveRemapTarget(RemappingTargetName, "DoubleTap", IsShiftMode);
            _service.RemoveRemapTarget(RemappingTargetName, "Hold", IsShiftMode);
            _service.RemoveRemapTarget(RemappingTargetName, "PressStart", IsShiftMode);
            _service.RemoveRemapTarget(RemappingTargetName, "PressRelease", IsShiftMode);

            string defaultMapping = GetDefaultMappingFor(RemappingTargetName);
            ClearShiftModifierIfOverwritten(RemappingTargetName, "Simple", defaultMapping);
            _service.SetRemapTarget(RemappingTargetName, "Simple", defaultMapping, IsShiftMode);

            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;

            _visibleRemapGestureTabs.Clear();
            SelectedRemapGestureType = "Simple";
            UpdateDetectedTargetForSelectedGesture();
            NotifyRemapGestureStateChanged();
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);
        }

        private void UnmapRemap()
        {
            if (string.IsNullOrEmpty(RemappingTargetName)) return;

            ClearShiftModifierIfOverwritten(RemappingTargetName, SelectedRemapGestureType, "Sin Mapeo");
            _service.SetRemapTarget(RemappingTargetName, SelectedRemapGestureType, "Sin Mapeo", IsShiftMode);
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);

            IsRemapping = false;
            _visibleRemapGestureTabs.Clear();
            NotifyRemapGestureStateChanged();
        }

        private string GetDefaultMappingFor(string buttonName)
        {
            var standardButtons = new System.Collections.Generic.HashSet<string>
            {
                "A", "B", "X", "Y",
                "LB", "LT", "RB", "RT", "L3", "R3",
                "LeftShoulder", "LeftTrigger", "RightShoulder", "RightTrigger", "LeftThumb", "RightThumb",
                "Up", "Down", "Left", "Right",
                "LS_Up", "LS_Down", "LS_Left", "LS_Right", "RS_Up", "RS_Down", "RS_Left", "RS_Right",
                "Start", "Back", "Guide"
            };

            if (standardButtons.Contains(buttonName))
            {
                return buttonName;
            }

            return "Sin Mapeo";
        }

        private void ClearShiftModifierIfOverwritten(string buttonName, string gestureType, string newTarget)
        {
            if (gestureType == RemapGestureTypes.Simple
                && IsSamePhysicalButton(buttonName, _service.ShiftModifierButton)
                && newTarget != "Shift")
            {
                RestoreDefaultMapping(_service.ShiftModifierButton);
                _service.ShiftModifierButton = string.Empty;
                PendingShiftModifier = string.Empty;
                OnPropertyChanged(nameof(SelectedShiftModifier));
                NotifySelectedShiftModifierChanged();
            }
        }

        private string? _actionSelectedProfile;
        public string? ActionSelectedProfile
        {
            get => _actionSelectedProfile;
            set
            {
                if (SetProperty(ref _actionSelectedProfile, value))
                {
                    NotifyProfileActionStateChanged();
                }
            }
        }

        private bool _actionProfileWhileHeld;
        public bool ActionProfileWhileHeld
        {
            get => _actionProfileWhileHeld;
            set
            {
                var nextValue = SelectedRemapGestureType == RemapGestureTypes.Hold
                    || (value && IsProfileWhileHeldOptionEnabled);
                if (SetProperty(ref _actionProfileWhileHeld, nextValue))
                {
                    NotifyProfileActionStateChanged();
                }
            }
        }

        public bool IsProfileWhileHeldOptionEnabled => CanUseProfileWhileHeldForGesture(SelectedRemapGestureType);
        public bool IsProfileWhileHeldCheckboxEnabled => IsProfileWhileHeldOptionEnabled && SelectedRemapGestureType != RemapGestureTypes.Hold;
        public bool IsLoadProfileActionEnabled => !IsSelectedGestureBlockedByPrimaryProfileHeld;
        public bool CanAssignLoadProfileAction => IsLoadProfileActionEnabled && !string.IsNullOrWhiteSpace(ActionSelectedProfile);
        public bool HasProfileActionConstraint => !string.IsNullOrWhiteSpace(ProfileActionConstraintText);
        public string ProfileActionConstraintText
        {
            get
            {
                if (!IsActionInputTabEnabled)
                {
                    return LocalizationService.Get("ActionsDisabledOnPressRelease");
                }

                if (IsSelectedGestureBlockedByPrimaryProfileHeld)
                {
                    return LocalizationService.Get("PrimaryProfileHeldConflict");
                }

                if (!IsProfileWhileHeldOptionEnabled)
                {
                    return SelectedRemapGestureType == RemapGestureTypes.PressRelease
                        ? LocalizationService.Get("ProfileHeldPressReleaseUnavailable")
                        : LocalizationService.Get("ProfileHeldSecondaryConflict");
                }

                if (SelectedRemapGestureType == RemapGestureTypes.Hold)
                {
                    return LocalizationService.Get("HoldProfileAlwaysWhileHeld");
                }

                return string.Empty;
            }
        }

        private void HandleMapAction(string? actionType)
        {
            if (string.IsNullOrWhiteSpace(actionType) || string.IsNullOrWhiteSpace(RemappingTargetName))
                return;
            if (!IsActionInputTabEnabled)
                return;

            string targetString = "Sin Mapeo";
            if (actionType == "Shift")
            {
                SelectShiftModifier(RemappingTargetName);
                IsRemapping = false;
                _visibleRemapGestureTabs.Clear();
                return;
            }
            else if (actionType == "EcoMode")
            {
                targetString = "Action:EcoMode";
            }
            else if (actionType == "LoadProfile")
            {
                if (!IsLoadProfileActionEnabled)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(ActionSelectedProfile)) return;
                targetString = ShouldMapLoadProfileWhileHeld()
                    ? $"Action:LoadProfileHeld:{ActionSelectedProfile}"
                    : $"Action:LoadProfile:{ActionSelectedProfile}";
            }

            HandleKeyPress(targetString);
        }

        public void HandleKeyPress(string key)
        {
            if (!IsRemapping || string.IsNullOrEmpty(RemappingTargetName)) return;
            if (!CanMapTargetToSelectedGesture(key))
            {
                return;
            }

            key = ApplyPendingTriggerOutputPercent(key);
            DetectedTarget = key;
            ClearShiftModifierIfOverwritten(RemappingTargetName, SelectedRemapGestureType, DetectedTarget);
            _service.SetRemapTarget(RemappingTargetName, SelectedRemapGestureType, DetectedTarget, IsShiftMode);
            CleanupRemapConflictsAfterSet(RemappingTargetName, SelectedRemapGestureType, DetectedTarget, IsShiftMode);
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            NotifyRemapGestureStateChanged();
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);

            // Auto close modal
            EndTriggerOutputSelection();
            IsRemapping = false;
            _visibleRemapGestureTabs.Clear();
        }

        private void ApplyRemap(string buttonName, string targetKey)
        {
            if (!CanMapTargetToSelectedGesture(targetKey))
            {
                return;
            }

            ClearShiftModifierIfOverwritten(buttonName, SelectedRemapGestureType, targetKey);
            _service.SetRemapTarget(buttonName, SelectedRemapGestureType, targetKey, IsShiftMode);
            CleanupRemapConflictsAfterSet(buttonName, SelectedRemapGestureType, targetKey, IsShiftMode);
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            NotifyRemapGestureStateChanged();
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);
        }

        private void AddRemapGesture(string gestureType)
        {
            if (string.IsNullOrWhiteSpace(gestureType) || string.IsNullOrEmpty(RemappingTargetName)) return;
            if (!CanUseGesture(RemappingTargetName, gestureType, IsShiftMode))
            {
                return;
            }

            _visibleRemapGestureTabs.Add(gestureType);
            SelectedRemapGestureType = gestureType;
        }

        private void RemoveRemapGesture(string gestureType)
        {
            if (string.IsNullOrWhiteSpace(gestureType) || string.IsNullOrEmpty(RemappingTargetName)) return;
            _visibleRemapGestureTabs.Remove(gestureType);
            _service.RemoveRemapTarget(RemappingTargetName, gestureType, IsShiftMode);
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;

            if (SelectedRemapGestureType == gestureType)
            {
                SelectedRemapGestureType = RemapGestureTypes.Simple;
                UpdateDetectedTargetForSelectedGesture();
            }
            NotifyRemapGestureStateChanged();
            RefreshAdvancedMappings();
            _service.SaveProfile(SelectedProfile);
        }

        private void SelectRemapGesture(string gestureType)
        {
            if (string.IsNullOrWhiteSpace(gestureType) || string.IsNullOrEmpty(RemappingTargetName)) return;
            if (!CanUseGesture(RemappingTargetName, gestureType, IsShiftMode))
            {
                return;
            }

            SelectedRemapGestureType = gestureType;
        }

        private void SelectRemapInputTab(string tabIndex)
        {
            if (int.TryParse(tabIndex, out var index))
            {
                RemapInputTabIndex = index;
            }
        }

        private void LoadGestureDelayForSelectedGesture()
        {
            _isLoadingGestureDelay = true;
            try
            {
                SelectedGestureDelayMs = IsGestureDelayEditorVisible && !string.IsNullOrWhiteSpace(RemappingTargetName)
                    ? _service.GetRemapGestureDelayMs(RemappingTargetName, SelectedRemapGestureType, IsShiftMode)
                    : 0;
            }
            finally
            {
                _isLoadingGestureDelay = false;
            }
        }

        private void SaveSelectedGestureDelay()
        {
            if (!IsGestureDelayEditorVisible || string.IsNullOrWhiteSpace(RemappingTargetName))
            {
                return;
            }

            _service.SetRemapGestureDelayMs(RemappingTargetName, SelectedRemapGestureType, SelectedGestureDelayMs, IsShiftMode);
            _service.SaveProfile(SelectedProfile);
        }

        private static int ClampGestureDelayMs(string gestureType, int delayMs)
        {
            return gestureType == RemapGestureTypes.Hold
                ? Math.Clamp(delayMs, 100, 3000)
                : Math.Clamp(delayMs, 80, 1000);
        }

        private bool CanUseSelectedGesture()
        {
            return string.IsNullOrWhiteSpace(RemappingTargetName)
                || CanUseGesture(RemappingTargetName, SelectedRemapGestureType, IsShiftMode);
        }

        private bool CanUseGesture(string sourceName, string gestureType, bool isShiftLayer)
        {
            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(gestureType))
            {
                return false;
            }

            if (gestureType == RemapGestureTypes.Simple)
            {
                return true;
            }

            return !(IsPrimaryProfileWhileHeld(sourceName, isShiftLayer)
                && (gestureType == RemapGestureTypes.Hold || gestureType == RemapGestureTypes.PressRelease));
        }

        private bool CanMapTargetToSelectedGesture(string target)
        {
            if (!CanUseSelectedGesture())
            {
                return false;
            }

            if (IsActionTarget(target) && !IsActionInputTabEnabled)
            {
                return false;
            }

            if (!IsProfileWhileHeldTarget(target))
            {
                return true;
            }

            return IsProfileWhileHeldOptionEnabled;
        }

        private bool ShouldMapLoadProfileWhileHeld()
        {
            return SelectedRemapGestureType == RemapGestureTypes.Hold
                || (ActionProfileWhileHeld && IsProfileWhileHeldOptionEnabled);
        }

        private bool IsSelectedGestureBlockedByPrimaryProfileHeld =>
            !string.IsNullOrWhiteSpace(RemappingTargetName)
            && !CanUseGesture(RemappingTargetName, SelectedRemapGestureType, IsShiftMode);

        private bool CanUseProfileWhileHeldForGesture(string gestureType)
        {
            if (gestureType == RemapGestureTypes.PressRelease)
            {
                return false;
            }

            return gestureType == RemapGestureTypes.Simple || !IsCurrentPrimaryProfileWhileHeld;
        }

        private static bool CanUseActionsForGesture(string gestureType)
        {
            return gestureType != RemapGestureTypes.PressStart
                && gestureType != RemapGestureTypes.PressRelease;
        }

        private bool IsCurrentPrimaryProfileWhileHeld =>
            !string.IsNullOrWhiteSpace(RemappingTargetName) && IsPrimaryProfileWhileHeld(RemappingTargetName, IsShiftMode);

        private bool IsPrimaryProfileWhileHeld(string sourceName, bool isShiftLayer)
        {
            return IsProfileWhileHeldTarget(_service.GetRemapTarget(sourceName, RemapGestureTypes.Simple, isShiftLayer));
        }

        private static bool IsProfileWhileHeldTarget(string target)
        {
            return !string.IsNullOrWhiteSpace(target)
                && target.StartsWith("Action:LoadProfileHeld:", StringComparison.Ordinal);
        }

        private static bool IsActionTarget(string target)
        {
            return !string.IsNullOrWhiteSpace(target)
                && target.StartsWith("Action:", StringComparison.Ordinal);
        }

        private static bool TryParseLoadProfileActionTarget(string target, out string profileName, out bool whileHeld)
        {
            const string heldPrefix = "Action:LoadProfileHeld:";
            const string loadPrefix = "Action:LoadProfile:";

            profileName = string.Empty;
            whileHeld = false;

            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            if (target.StartsWith(heldPrefix, StringComparison.Ordinal))
            {
                profileName = target.Substring(heldPrefix.Length);
                whileHeld = true;
                return !string.IsNullOrWhiteSpace(profileName);
            }

            if (target.StartsWith(loadPrefix, StringComparison.Ordinal))
            {
                profileName = target.Substring(loadPrefix.Length);
                return !string.IsNullOrWhiteSpace(profileName);
            }

            return false;
        }

        private void CleanupRemapConflictsAfterSet(string sourceName, string gestureType, string target, bool isShiftLayer)
        {
            if (gestureType == RemapGestureTypes.Simple && IsProfileWhileHeldTarget(target))
            {
                _service.RemoveRemapTarget(sourceName, RemapGestureTypes.Hold, isShiftLayer);
                _service.RemoveRemapTarget(sourceName, RemapGestureTypes.PressRelease, isShiftLayer);
                _visibleRemapGestureTabs.Remove(RemapGestureTypes.Hold);
                _visibleRemapGestureTabs.Remove(RemapGestureTypes.PressRelease);
            }
        }

        private void NotifyProfileActionStateChanged()
        {
            OnPropertyChanged(nameof(IsProfileWhileHeldOptionEnabled));
            OnPropertyChanged(nameof(IsProfileWhileHeldCheckboxEnabled));
            OnPropertyChanged(nameof(IsLoadProfileActionEnabled));
            OnPropertyChanged(nameof(CanAssignLoadProfileAction));
            OnPropertyChanged(nameof(ProfileActionConstraintText));
            OnPropertyChanged(nameof(HasProfileActionConstraint));
            OnPropertyChanged(nameof(IsActionInputTabEnabled));
        }

        private bool IsGestureTabVisible(string gestureType)
        {
            return SelectedRemapGestureType == gestureType
                || _visibleRemapGestureTabs.Contains(gestureType)
                || (!string.IsNullOrEmpty(RemappingTargetName) && _service.HasAdvancedRemap(RemappingTargetName, gestureType, IsShiftMode));
        }

        private void UpdateDetectedTargetForSelectedGesture()
        {
            if (string.IsNullOrEmpty(RemappingTargetName)) return;
            DetectedTarget = _service.GetRemapTarget(RemappingTargetName, SelectedRemapGestureType, IsShiftMode);
        }

        private void NotifyRemapGestureStateChanged()
        {
            OnPropertyChanged(nameof(SelectedRemapGestureLabel));
            OnPropertyChanged(nameof(SelectedRemapActionDisplayName));
            OnPropertyChanged(nameof(IsGestureDelayEditorVisible));
            OnPropertyChanged(nameof(SelectedGestureDelayLabel));
            OnPropertyChanged(nameof(IsSimpleGestureSelected));
            OnPropertyChanged(nameof(IsDoubleTapGestureSelected));
            OnPropertyChanged(nameof(IsHoldGestureSelected));
            OnPropertyChanged(nameof(IsPressStartGestureSelected));
            OnPropertyChanged(nameof(IsPressReleaseGestureSelected));
            OnPropertyChanged(nameof(IsDoubleTapTabVisible));
            OnPropertyChanged(nameof(IsHoldTabVisible));
            OnPropertyChanged(nameof(IsPressStartTabVisible));
            OnPropertyChanged(nameof(IsPressReleaseTabVisible));
            OnPropertyChanged(nameof(IsActionInputTabEnabled));
            NotifyProfileActionStateChanged();
        }

        private void ResetToDefaults()
        {
            _service.ResetToDefaults();
            IsResetToDefaultDialogOpen = false;
            EndTriggerOutputSelection();
            IsRemapping = false;
            IsShiftModifierPickerOpen = false;
            _visibleRemapGestureTabs.Clear();
            IsShiftMode = false;
            InitializePaddleRemaps();
            InitializeGKeyRemaps();
            RemapVersion++;
            RefreshAdvancedMappings();

            OnPropertyChanged(nameof(SelectedShiftModifier));
            NotifySelectedShiftModifierChanged();
            OnPropertyChanged(nameof(VisibleAdvancedMappingGroups));
            OnPropertyChanged(nameof(HasVisibleAdvancedMappings));
            OnPropertyChanged(nameof(ButtonA));
            OnPropertyChanged(nameof(ButtonB));
            OnPropertyChanged(nameof(ButtonX));
            OnPropertyChanged(nameof(ButtonY));
            OnPropertyChanged(nameof(ButtonLB));
            OnPropertyChanged(nameof(ButtonRB));
            OnPropertyChanged(nameof(ButtonL3));
            OnPropertyChanged(nameof(ButtonR3));
            OnPropertyChanged(nameof(ButtonStart));
            OnPropertyChanged(nameof(ButtonBack));
            OnPropertyChanged(nameof(ButtonGuide));
            OnPropertyChanged(nameof(DPadUp));
            OnPropertyChanged(nameof(DPadDown));
            OnPropertyChanged(nameof(DPadLeft));
            OnPropertyChanged(nameof(DPadRight));
            OnPropertyChanged(nameof(LeftTriggerActive));
            OnPropertyChanged(nameof(RightTriggerActive));
            NotifyTriggerCurveChanged();
            NotifyStickSettingsChanged();
            NotifyRemapGestureStateChanged();
            _service.SaveProfile(SelectedProfile);
        }

        private void ClearLog()
        {
            Dispatcher.UIThread.Post(() => EventLog.Clear());
        }

        private void HandleInputEvent(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                EventLog.Add(message);
                if (EventLog.Count > 200)
                {
                    EventLog.RemoveAt(0);
                }
            });
        }

        private void HandleFrameProcessed()
        {
            Dispatcher.UIThread.Post(() =>
            {
                // Update sticks and trigger notification
                OnPropertyChanged(nameof(LeftStickXNormalized));
                OnPropertyChanged(nameof(LeftStickYNormalized));
                OnPropertyChanged(nameof(RightStickXNormalized));
                OnPropertyChanged(nameof(RightStickYNormalized));
                OnPropertyChanged(nameof(LeftStickKnobX));
                OnPropertyChanged(nameof(LeftStickKnobY));
                OnPropertyChanged(nameof(RightStickKnobX));
                OnPropertyChanged(nameof(RightStickKnobY));
                OnPropertyChanged(nameof(LeftStickX));
                OnPropertyChanged(nameof(LeftStickY));
                OnPropertyChanged(nameof(RightStickX));
                OnPropertyChanged(nameof(RightStickY));
                OnPropertyChanged(nameof(LeftStickOpacity));
                OnPropertyChanged(nameof(RightStickOpacity));
                NotifyStickOutputChanged();
                OnPropertyChanged(nameof(LeftTrigger));
                OnPropertyChanged(nameof(RightTrigger));
                OnPropertyChanged(nameof(LeftTriggerActive));
                OnPropertyChanged(nameof(RightTriggerActive));
                OnPropertyChanged(nameof(LeftTriggerFill));
                OnPropertyChanged(nameof(RightTriggerFill));
                NotifyTriggerOutputChanged();
                UpdateSmoothedTriggerValues();



                // Update digital buttons
                ButtonA = _service.ButtonA;
                ButtonB = _service.ButtonB;
                ButtonX = _service.ButtonX;
                ButtonY = _service.ButtonY;
                ButtonLB = _service.ButtonLB;
                ButtonRB = _service.ButtonRB;
                ButtonBack = _service.ButtonBack;
                ButtonStart = _service.ButtonStart;
                ButtonL3 = _service.ButtonL3;
                ButtonR3 = _service.ButtonR3;
                ButtonGuide = _service.ButtonGuide;

                DPadState = _service.DPadState;

                foreach (var entry in PaddleRemaps)
                {
                    entry.IsPressed = _service.IsPaddleActive(entry.PaddleName);
                }
                foreach (var entry in GKeyRemaps)
                {
                    entry.IsPressed = _service.IsGKeyActive(entry.GKeyName);
                }

                UpdateShiftTabFromHeldModifier();
                UpdateDetailsInputHighlights();
            });
        }

        private void UpdateShiftTabFromHeldModifier()
        {
            bool isShiftHeld = _service.IsShiftHeld;

            if (isShiftHeld && !_wasShiftModifierHeld)
            {
                _shiftModeActivatedByHeldModifier = !IsShiftMode && !IsRemapping && !IsShiftModifierPickerOpen;
                if (_shiftModeActivatedByHeldModifier)
                {
                    IsShiftMode = true;
                }
            }
            else if (!isShiftHeld && _wasShiftModifierHeld)
            {
                if (_shiftModeActivatedByHeldModifier)
                {
                    IsShiftMode = false;
                }

                _shiftModeActivatedByHeldModifier = false;
            }

            _wasShiftModifierHeld = isShiftHeld;
        }

        public void SetDetailsHoveredInput(string? inputName)
        {
            HoveredDetailsInput = inputName ?? string.Empty;
        }

        private static string NormalizeDetailsInputName(string? inputName)
        {
            return inputName switch
            {
                "LeftShoulder" => "LB",
                "RightShoulder" => "RB",
                "LeftTrigger" => "LT",
                "RightTrigger" => "RT",
                "LeftThumb" => "L3",
                "RightThumb" => "R3",
                null => string.Empty,
                _ => inputName
            };
        }

        private void UpdateDetailsInputHighlights()
        {
            var activeInputs = GetActiveDetailsInputs();
            var activeGestures = GetActiveDetailsGestures();
            if (!string.IsNullOrEmpty(HoveredDetailsInput))
            {
                activeInputs.Add(HoveredDetailsInput);
            }

            foreach (var item in AdvancedMappingGroups.SelectMany(group => group.Items))
            {
                item.IsHighlighted = activeInputs.Contains(item.SourceName);
                activeGestures.TryGetValue(item.SourceName, out var activeGestureType);
                foreach (var command in item.Commands)
                {
                    command.IsHighlighted = !string.IsNullOrEmpty(activeGestureType)
                        && command.GestureType == activeGestureType;
                }
            }
        }

        private Dictionary<string, string> GetActiveDetailsGestures()
        {
            return _service.GetActiveGestureFeedback()
                .ToDictionary(kvp => NormalizeDetailsInputName(kvp.Key), kvp => kvp.Value);
        }

        private HashSet<string> GetActiveDetailsInputs()
        {
            var activeInputs = new HashSet<string>();

            if (ButtonA) activeInputs.Add("A");
            if (ButtonB) activeInputs.Add("B");
            if (ButtonX) activeInputs.Add("X");
            if (ButtonY) activeInputs.Add("Y");
            if (ButtonLB) activeInputs.Add("LB");
            if (ButtonRB) activeInputs.Add("RB");
            if (ButtonBack) activeInputs.Add("Back");
            if (ButtonStart) activeInputs.Add("Start");
            if (ButtonGuide) activeInputs.Add("Guide");
            if (ButtonL3) activeInputs.Add("L3");
            if (ButtonR3) activeInputs.Add("R3");
            if (DPadUp) activeInputs.Add("Up");
            if (DPadRight) activeInputs.Add("Right");
            if (DPadDown) activeInputs.Add("Down");
            if (DPadLeft) activeInputs.Add("Left");
            if (LeftTriggerActive) activeInputs.Add("LT");
            if (RightTriggerActive) activeInputs.Add("RT");

            foreach (var entry in PaddleRemaps.Where(entry => entry.IsPressed))
            {
                activeInputs.Add(entry.PaddleName);
            }

            foreach (var entry in GKeyRemaps.Where(entry => entry.IsPressed))
            {
                activeInputs.Add(entry.GKeyName);
            }

            return activeInputs;
        }

        private void RefreshAdvancedMappings()
        {
            AdvancedMappingGroups.Clear();
            StandardAdvancedMappingGroups.Clear();
            ShiftAdvancedMappingGroups.Clear();

            var groups = new List<AdvancedMappingGroup>
            {
                CreateAdvancedMappingGroup(LocalizationService.Get("Paddles"), "GamepadVariant", new[] { "Paddle_L4", "Paddle_L5", "Paddle_R4", "Paddle_R5" }, false),
                CreateAdvancedMappingGroup(LocalizationService.Get("GKeys"), "Keyboard", new[] { "G1", "G2", "G3", "G4", "G5" }, false),
                CreateAdvancedMappingGroup(LocalizationService.Get("SideButtons"), "GamepadCircle", new[] { "SAX_L", "SAX_R" }, false),
                CreateAdvancedMappingGroup(LocalizationService.Get("ActionButtons"), "AlphaA", new[] { "A", "B", "X", "Y" }, false),
                CreateAdvancedMappingGroup(LocalizationService.Get("TriggersAndBumpers"), "ControllerTrigger", new[] { "LB", "RB", "LT", "RT" }, false),
                CreateAdvancedMappingGroup(LocalizationService.Get("SticksAndDPad"), "Directions", new[] { "Up", "Down", "Left", "Right", "L3", "R3" }, false),
                CreateAdvancedMappingGroup(LocalizationService.Get("System"), "Cog", new[] { "Start", "Back", "Guide" }, false),

                // Shift Layer
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("Paddles")}", "GamepadVariantOutline", new[] { "Paddle_L4", "Paddle_L5", "Paddle_R4", "Paddle_R5" }, true),
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("GKeys")}", "KeyboardOutline", new[] { "G1", "G2", "G3", "G4", "G5" }, true),
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("SideButtons")}", "GamepadCircleOutline", new[] { "SAX_L", "SAX_R" }, true),
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("ActionButtons")}", "AlphaAOutline", new[] { "A", "B", "X", "Y" }, true),
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("TriggersAndBumpers")}", "ControllerTriggerOutline", new[] { "LB", "RB", "LT", "RT" }, true),
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("SticksAndDPad")}", "DirectionsFork", new[] { "Up", "Down", "Left", "Right", "L3", "R3" }, true),
                CreateAdvancedMappingGroup($"{LocalizationService.Get("Shift")} - {LocalizationService.Get("System")}", "CogOutline", new[] { "Start", "Back" }, true)
            };

            foreach (var group in groups)
            {
                if (group.Items.Count > 0)
                {
                    AdvancedMappingGroups.Add(group);
                }
            }

            AddAdvancedMappingGroups(StandardAdvancedMappingGroups, false);
            AddAdvancedMappingGroups(ShiftAdvancedMappingGroups, true);
            AdvancedMappingGroups.Clear();
            foreach (var group in StandardAdvancedMappingGroups.Concat(ShiftAdvancedMappingGroups))
                AdvancedMappingGroups.Add(group);

            OnPropertyChanged(nameof(HasAdvancedMappings));
            OnPropertyChanged(nameof(HasStandardAdvancedMappings));
            OnPropertyChanged(nameof(HasShiftAdvancedMappings));
            OnPropertyChanged(nameof(HasVisibleAdvancedMappings));
            OnPropertyChanged(nameof(StandardAdvancedMappingCount));
            OnPropertyChanged(nameof(ShiftAdvancedMappingCount));
            OnPropertyChanged(nameof(VisibleAdvancedMappingGroups));
            UpdateDetailsInputHighlights();
        }

        private void AddAdvancedMappingGroups(ObservableCollection<AdvancedMappingGroup> target, bool isShift)
        {
            var groups = new[]
            {
                CreateAdvancedMappingGroup(LocalizationService.Get("ActionButtons"), "AlphaA", new[] { "A", "B", "X", "Y" }, isShift),
                CreateAdvancedMappingGroup(LocalizationService.Get("ShouldersAndTriggers"), "ControllerTrigger", new[] { "LB", "RB", "LT", "RT" }, isShift),
                CreateAdvancedMappingGroup("D-Pad", "Directions", new[] { "Up", "Right", "Down", "Left" }, isShift),
                CreateAdvancedMappingGroup("Sticks", "GamepadCircle", new[] { "L3", "R3" }, isShift),
                CreateAdvancedMappingGroup(LocalizationService.Get("System"), "Cog", new[] { "Back", "Start", "Guide" }, isShift),
                CreateAdvancedMappingGroup(LocalizationService.Get("GKeys"), "Keyboard", new[] { "G1", "G2", "G3", "G4", "G5" }, isShift),
                CreateAdvancedMappingGroup(LocalizationService.Get("Paddles"), "GamepadVariant", new[] { "Paddle_L4", "Paddle_L5", "Paddle_R4", "Paddle_R5" }, isShift),
                CreateAdvancedMappingGroup(LocalizationService.Get("SideButtons"), "GamepadCircle", new[] { "SAX_L", "SAX_R" }, isShift)
            };

            foreach (var group in groups)
                target.Add(group);
        }

        private AdvancedMappingGroup CreateAdvancedMappingGroup(string name, string icon, string[] buttons, bool isShift)
        {
            var group = new AdvancedMappingGroup { GroupName = name, IconKind = icon, IsShiftLayer = isShift };
            foreach (var btn in buttons)
            {
                var item = new AdvancedMappingItem
                {
                    SourceName = btn,
                    DisplayName = GetButtonDisplayName(btn),
                    IconKind = GetButtonIcon(btn),
                    SourceIcon = GetAdvancedMappingSourceIcon(btn),
                    SourceFallbackText = GetMappingFallbackText(btn),
                    IsShiftLayer = isShift,
                    SimpleTarget = _service.GetRemapTarget(btn, RemapGestureTypes.Simple, isShift),
                    DoubleTapTarget = _service.GetRemapTarget(btn, RemapGestureTypes.DoubleTap, isShift),
                    HoldTarget = _service.GetRemapTarget(btn, RemapGestureTypes.Hold, isShift),
                    PressStartTarget = _service.GetRemapTarget(btn, RemapGestureTypes.PressStart, isShift),
                    PressReleaseTarget = _service.GetRemapTarget(btn, RemapGestureTypes.PressRelease, isShift)
                };
                item.Commands = CreateAdvancedMappingCommands(item);
                group.Items.Add(item);
            }
            return group;
        }

        private List<AdvancedMappingCommand> CreateAdvancedMappingCommands(AdvancedMappingItem item)
        {
            var commands = new List<AdvancedMappingCommand>();
            if (item.HasSimple)
            {
                AddAdvancedMappingCommand(commands, true, item.SourceName, item.IsShiftLayer, RemapGestureTypes.Simple, LocalizationService.Get("PrimaryAction"), item.SimpleTarget, "GestureTap", "#66B2FF");
            }
            else
            {
                AddAdvancedMappingCommand(commands, true, item.SourceName, item.IsShiftLayer, RemapGestureTypes.Simple, LocalizationService.Get("PrimaryAction"), string.Empty, "GestureTap", "#6F7E92");
            }

            AddAdvancedMappingCommand(commands, item.HasDoubleTap, item.SourceName, item.IsShiftLayer, RemapGestureTypes.DoubleTap, LocalizationService.Get("DoubleTap"), item.DoubleTapTarget, "GestureDoubleTap", "#FFD280");
            AddAdvancedMappingCommand(commands, item.HasHold, item.SourceName, item.IsShiftLayer, RemapGestureTypes.Hold, LocalizationService.Get("Hold"), item.HoldTarget, "GestureTapHold", "#DF80DF");
            AddAdvancedMappingCommand(commands, item.HasPressStart, item.SourceName, item.IsShiftLayer, RemapGestureTypes.PressStart, LocalizationService.Get("PressStart"), item.PressStartTarget, "ArrowDownBoldCircleOutline", "#66CCCC");
            AddAdvancedMappingCommand(commands, item.HasPressRelease, item.SourceName, item.IsShiftLayer, RemapGestureTypes.PressRelease, LocalizationService.Get("PressRelease"), item.PressReleaseTarget, "ArrowUpBoldCircleOutline", "#E58080");
            return commands;
        }

        private void AddAdvancedMappingCommand(List<AdvancedMappingCommand> commands, bool isVisible, string sourceName, bool isShiftLayer, string gestureType, string label, string target, string iconKind, string accentBrush)
        {
            if (!isVisible) return;

            commands.Add(new AdvancedMappingCommand
            {
                SourceName = sourceName,
                IsShiftLayer = isShiftLayer,
                GestureType = gestureType,
                Label = label,
                Target = target,
                IconKind = iconKind,
                AccentBrush = accentBrush,
                TargetIcon = GetAdvancedMappingSourceIcon(target),
                TargetFallbackText = GetMappingFallbackText(target)
            });
        }

        private string GetButtonDisplayName(string btn) => btn switch
        {
            "Paddle_L4" => LocalizationService.Get("ButtonPaddleL4"),
            "Paddle_L5" => LocalizationService.Get("ButtonPaddleL5"),
            "Paddle_R4" => LocalizationService.Get("ButtonPaddleR4"),
            "Paddle_R5" => LocalizationService.Get("ButtonPaddleR5"),
            "SAX_L" => LocalizationService.Get("ButtonSaxL"),
            "SAX_R" => LocalizationService.Get("ButtonSaxR"),
            "LeftShoulder" => "LB",
            "RightShoulder" => "RB",
            "LeftTrigger" => "LT",
            "RightTrigger" => "RT",
            "LeftThumb" => "L3",
            "RightThumb" => "R3",
            _ => btn
        };

        private string GetButtonIcon(string btn) => btn switch
        {
            "A" => "AlphaA",
            "B" => "AlphaB",
            "X" => "AlphaX",
            "Y" => "AlphaY",
            "LB" => "GamepadVariant",
            "RB" => "GamepadVariant",
            "LT" => "ControllerTrigger",
            "RT" => "ControllerTrigger",
            "Up" => "ArrowUp",
            "Down" => "ArrowDown",
            "Left" => "ArrowLeft",
            "Right" => "ArrowRight",
            "L3" => "GamepadCircle",
            "R3" => "GamepadCircle",
            "Start" => "Menu",
            "Back" => "Fullscreen",
            "Guide" => "Xbox",
            _ => "GamepadCircle"
        };

        private Avalonia.Media.Imaging.Bitmap? GetAdvancedMappingSourceIcon(string sourceName)
        {
            return MappingIconCatalog.GetBitmap(sourceName);
        }

        private static bool IsGKeyName(string value)
        {
            return value is "G1" or "G2" or "G3" or "G4" or "G5";
        }

        private string GetMappingFallbackText(string value)
        {
            if (MacroTarget.IsMacroTarget(value))
            {
                return "M";
            }

            return MappingIconCatalog.GetFallbackText(value);
        }
    }

    public class PaddleRemapEntry : ViewModelBase
    {
        private static readonly Dictionary<string, string> FriendlyToSystem = new Dictionary<string, string>
        {
            { "A", "A" }, { "B", "B" }, { "X", "X" }, { "Y", "Y" },
            { "LB", "LeftShoulder" }, { "RB", "RightShoulder" },
            { "Back", "Back" }, { "Start", "Start" },
            { "L3", "LeftThumb" }, { "R3", "RightThumb" },
            { "Guide", "Guide" },
            { "Up", "Up" }, { "Down", "Down" }, { "Left", "Left" }, { "Right", "Right" }
        };

        private static readonly Dictionary<string, string> SystemToFriendly = FriendlyToSystem.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        public string PaddleName { get; }
        public List<string> AvailableTargets => FriendlyToSystem.Keys.ToList();

        private readonly Dictionary<string, string> _targetDictionary;
        private readonly Action _onChanged;

        private string _selectedTarget = "A";
        public string SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                {
                    if (FriendlyToSystem.TryGetValue(value, out var sysTarget))
                    {
                        _targetDictionary[PaddleName] = sysTarget;
                        _onChanged?.Invoke();
                    }
                }
            }
        }

        private bool _isPressed;
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }

        public PaddleRemapEntry(string paddleName, string systemTarget, Dictionary<string, string> targetDictionary, Action onChanged)
        {
            PaddleName = paddleName;
            _targetDictionary = targetDictionary;
            _onChanged = onChanged;
            if (SystemToFriendly.TryGetValue(systemTarget, out var friendly))
            {
                _selectedTarget = friendly;
            }
            else
            {
                _selectedTarget = systemTarget;
            }
        }
    }

    public class GKeyRemapEntry : ViewModelBase
    {
        private static readonly Dictionary<string, string> FriendlyToSystem = new Dictionary<string, string>
        {
            { "A", "A" }, { "B", "B" }, { "X", "X" }, { "Y", "Y" },
            { "LB", "LeftShoulder" }, { "RB", "RightShoulder" },
            { "Back", "Back" }, { "Start", "Start" },
            { "L3", "LeftThumb" }, { "R3", "RightThumb" },
            { "Guide", "Guide" },
            { "Up", "Up" }, { "Down", "Down" }, { "Left", "Left" }, { "Right", "Right" }
        };

        private static readonly Dictionary<string, string> SystemToFriendly = FriendlyToSystem.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        public string GKeyName { get; }
        public List<string> AvailableTargets => FriendlyToSystem.Keys.ToList();

        private readonly Dictionary<string, string> _targetDictionary;
        private readonly Action _onChanged;

        private string _selectedTarget = "A";
        public string SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                {
                    if (FriendlyToSystem.TryGetValue(value, out var sysTarget))
                    {
                        _targetDictionary[GKeyName] = sysTarget;
                        _onChanged?.Invoke();
                    }
                }
            }
        }

        private bool _isPressed;
        public bool IsPressed
        {
            get => _isPressed;
            set => SetProperty(ref _isPressed, value);
        }

        public GKeyRemapEntry(string gkeyName, string systemTarget, Dictionary<string, string> targetDictionary, Action onChanged)
        {
            GKeyName = gkeyName;
            _targetDictionary = targetDictionary;
            _onChanged = onChanged;
            if (SystemToFriendly.TryGetValue(systemTarget, out var friendly))
            {
                _selectedTarget = friendly;
            }
            else
            {
                _selectedTarget = systemTarget;
            }
        }
    }

    public class MacroStepViewModel : ViewModelBase
    {
        private string _inputKind;
        private string _target;
        private string _action;
        private int _delayMs;

        public MacroStepViewModel(MacroStep step)
        {
            _inputKind = string.IsNullOrWhiteSpace(step.InputKind) ? MacroInputKinds.Keyboard : step.InputKind;
            _target = step.Target;
            _action = step.Action == MacroActions.Up ? MacroActions.Up : MacroActions.Down;
            _delayMs = Math.Clamp(step.DelayMs, 0, 60000);
        }

        public string InputKind
        {
            get => _inputKind;
            set
            {
                if (SetProperty(ref _inputKind, string.IsNullOrWhiteSpace(value) ? MacroInputKinds.Keyboard : value))
                {
                    OnPropertyChanged(nameof(InputIconKind));
                    OnPropertyChanged(nameof(TargetIcon));
                    OnPropertyChanged(nameof(HasTargetIcon));
                    OnPropertyChanged(nameof(TargetFallbackText));
                    OnPropertyChanged(nameof(HasTargetFallback));
                    OnPropertyChanged(nameof(IsTriggerOutputStep));
                }
            }
        }

        public string Target
        {
            get => _target;
            set
            {
                if (SetProperty(ref _target, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(TargetIcon));
                    OnPropertyChanged(nameof(HasTargetIcon));
                    OnPropertyChanged(nameof(TargetFallbackText));
                    OnPropertyChanged(nameof(HasTargetFallback));
                    OnPropertyChanged(nameof(IsTriggerOutputStep));
                    OnPropertyChanged(nameof(TriggerOutputPercent));
                }
            }
        }

        public string Action
        {
            get => _action;
            set
            {
                if (SetProperty(ref _action, value == MacroActions.Up ? MacroActions.Up : MacroActions.Down))
                {
                    OnPropertyChanged(nameof(ActionDisplay));
                    OnPropertyChanged(nameof(ActionIconKind));
                    OnPropertyChanged(nameof(IsTriggerOutputStep));
                }
            }
        }

        public string ActionDisplay => Action == MacroActions.Up ? "KeyUp" : "KeyDown";
        public string ActionIconKind => Action == MacroActions.Up ? "ArrowUpBold" : "ArrowDownBold";
        public string InputIconKind => InputKind switch
        {
            MacroInputKinds.Gamepad => "ControllerClassic",
            MacroInputKinds.Mouse => "Mouse",
            _ => "Keyboard"
        };

        public Avalonia.Media.Imaging.Bitmap? TargetIcon => MappingIconCatalog.GetBitmap(Target, GetMappingIconKind());
        public bool HasTargetIcon => TargetIcon != null;
        public string TargetFallbackText => MappingIconCatalog.GetFallbackText(Target);
        public bool HasTargetFallback => !HasTargetIcon && !string.IsNullOrWhiteSpace(TargetFallbackText);
        public bool IsTriggerOutputStep =>
            InputKind == MacroInputKinds.Gamepad
            && Action != MacroActions.Up
            && VirtualTarget.IsTriggerTarget(Target);

        public int TriggerOutputPercent
        {
            get => VirtualTarget.GetTriggerOutputPercent(Target);
            set
            {
                if (!VirtualTarget.IsTriggerTarget(Target))
                {
                    return;
                }

                var updatedTarget = VirtualTarget.WithTriggerOutputPercent(Target, Math.Clamp(value, 0, 100));
                if (updatedTarget == Target)
                {
                    return;
                }

                Target = updatedTarget;
                OnPropertyChanged();
            }
        }

        public int DelayMs
        {
            get => _delayMs;
            set => SetProperty(ref _delayMs, Math.Clamp(value, 0, 99999));
        }

        public MacroStep ToModel()
        {
            return new MacroStep
            {
                InputKind = InputKind,
                Target = Target,
                Action = Action,
                DelayMs = DelayMs
            };
        }

        private MappingIconInputKind GetMappingIconKind()
        {
            return InputKind switch
            {
                MacroInputKinds.Gamepad => MappingIconInputKind.Gamepad,
                MacroInputKinds.Mouse => MappingIconInputKind.Mouse,
                MacroInputKinds.Keyboard => MappingIconInputKind.Keyboard,
                _ => MappingIconInputKind.Any
            };
        }
    }

    public class MacroFlowItemViewModel : ViewModelBase
    {
        private MacroFlowItemViewModel(MacroStepViewModel step, bool isAction, System.Windows.Input.ICommand removeCommand)
        {
            Step = step;
            IsAction = isAction;
            IsDelay = !isAction;
            RemoveCommand = removeCommand;
        }

        public MacroStepViewModel Step { get; }
        public bool IsAction { get; }
        public bool IsDelay { get; }
        public System.Windows.Input.ICommand RemoveCommand { get; }

        public string TriggerOutputPercentText
        {
            get => Step.TriggerOutputPercent.ToString();
            set
            {
                if (int.TryParse(value, out var parsed))
                {
                    var clamped = Math.Clamp(parsed, 0, 100);
                    if (Step.TriggerOutputPercent != clamped)
                    {
                        Step.TriggerOutputPercent = clamped;
                        OnPropertyChanged();
                    }
                    else if (parsed != clamped)
                    {
                        OnPropertyChanged();
                    }
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    if (Step.TriggerOutputPercent != 0)
                    {
                        Step.TriggerOutputPercent = 0;
                        OnPropertyChanged();
                    }
                }
                else
                {
                    OnPropertyChanged();
                }
            }
        }

        public string DelayMsText
        {
            get => Step.DelayMs.ToString();
            set
            {
                if (int.TryParse(value, out int parsed))
                {
                    int clamped = Math.Clamp(parsed, 0, 99999);
                    if (Step.DelayMs != clamped)
                    {
                        Step.DelayMs = clamped;
                        OnPropertyChanged();
                    }
                    else if (parsed != clamped)
                    {
                        // Forced clamp update
                        OnPropertyChanged();
                    }
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    if (Step.DelayMs != 0)
                    {
                        Step.DelayMs = 0;
                        OnPropertyChanged();
                    }
                }
                else
                {
                    // Invalid input, force UI to revert to current valid value
                    OnPropertyChanged();
                }
            }
        }

        public static MacroFlowItemViewModel CreateAction(MacroStepViewModel step, System.Windows.Input.ICommand removeCommand)
        {
            return new MacroFlowItemViewModel(step, true, removeCommand);
        }

        public static MacroFlowItemViewModel CreateDelay(MacroStepViewModel step, System.Windows.Input.ICommand removeCommand)
        {
            return new MacroFlowItemViewModel(step, false, removeCommand);
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<Task> execute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !_isExecuting;

        public async void Execute(object? parameter)
        {
            if (_isExecuting) return;
            _isExecuting = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                ZeroCueLog.Communication($"[UI-COMMAND-ERROR] {ex}");
            }
            finally
            {
                _isExecuting = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool>? _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public bool CanExecute(object? parameter)
        {
            if (parameter == null && typeof(T).IsValueType) return _canExecute?.Invoke(default(T)!) ?? true;
            return _canExecute?.Invoke((T)parameter!) ?? true;
        }

        public void Execute(object? parameter)
        {
            if (parameter == null && typeof(T).IsValueType) _execute(default(T)!);
            else _execute((T)parameter!);
        }
    }
}
