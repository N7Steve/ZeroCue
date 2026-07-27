using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZeroCue.DataProbe.Models;
using ZeroCue.DataProbe.Services;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe;

public partial class MainWindow : Window
{
    private WindowState _lastVisibleWindowState = WindowState.Maximized;
    private bool _isHiddenInTray;
    private bool _hideOnFirstOpen;
    private bool _isCloseDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        this.KeyDown += MainWindow_KeyDown;
        this.KeyUp += MainWindow_KeyUp;
        AddHandler(InputElement.PointerPressedEvent, MainWindow_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerReleasedEvent, MainWindow_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerWheelChangedEvent, MainWindow_PointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
        PropertyChanged += MainWindow_PropertyChanged;
    }

    public void StartHiddenInTray()
    {
        _hideOnFirstOpen = true;
        WindowState = WindowState.Minimized;
    }

    public void MinimizeToTray()
    {
        if (!_isHiddenInTray)
        {
            if (WindowState != WindowState.Minimized)
            {
                _lastVisibleWindowState = WindowState;
            }

            _isHiddenInTray = true;
            Hide();
        }
    }

    public void RestoreFromTray()
    {
        _hideOnFirstOpen = false;
        _isHiddenInTray = false;
        WindowState = _lastVisibleWindowState;
        Show();
        Activate();
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);

        if (_hideOnFirstOpen)
        {
            _hideOnFirstOpen = false;
            Dispatcher.UIThread.Post(MinimizeToTray);
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            base.OnClosing(e);

            var service = ScufDeviceService.Instance;
            if (!service.AskBeforeClosing)
            {
                var rememberedChoice = service.CloseBehavior == ApplicationCloseBehavior.MinimizeToTray
                    ? CloseApplicationChoice.MinimizeToTray
                    : CloseApplicationChoice.Exit;
                Dispatcher.UIThread.Post(() => ApplyCloseChoice(rememberedChoice));
                return;
            }

            if (!_isCloseDialogOpen)
            {
                _isCloseDialogOpen = true;
                try
                {
                    var dialog = new CloseConfirmationDialog();
                    var result = await dialog.ShowDialog<CloseDialogResult>(this);

                    if (result.RememberSelection &&
                        result.Choice != CloseApplicationChoice.Cancel)
                    {
                        service.CloseBehavior = result.Choice == CloseApplicationChoice.MinimizeToTray
                            ? ApplicationCloseBehavior.MinimizeToTray
                            : ApplicationCloseBehavior.ExitApplication;
                        service.AskBeforeClosing = false;
                    }

                    ApplyCloseChoice(result.Choice);
                }
                finally
                {
                    _isCloseDialogOpen = false;
                }
            }

            return;
        }

        base.OnClosing(e);
    }

    private void ApplyCloseChoice(CloseApplicationChoice choice)
    {
        if (choice == CloseApplicationChoice.MinimizeToTray)
        {
            MinimizeToTray();
        }
        else if (choice == CloseApplicationChoice.Exit &&
                 Application.Current is App app)
        {
            app.ShutdownApplication();
        }
    }

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty)
        {
            return;
        }

        if (WindowState == WindowState.Minimized &&
            (!_hideOnFirstOpen || IsVisible))
        {
            Dispatcher.UIThread.Post(MinimizeToTray);
        }
        else if (!_isHiddenInTray)
        {
            _lastVisibleWindowState = WindowState;
        }
    }

    private void MainWindow_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (System.Math.Abs(e.Delta.X) > System.Math.Abs(e.Delta.Y) && System.Math.Abs(e.Delta.X) > 0.01)
        {
            e.Handled = true;
        }
    }

    private void MainWindow_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsRemapping)
        {
            if (vm.IsMacroRecording)
            {
                vm.RecordMacroKeyboardEvent(ToMacroKeyName(e.Key), true);
                e.Handled = true;
                return;
            }

            if (e.Key == Avalonia.Input.Key.Escape)
            {
                if (vm.CancelRemapCommand.CanExecute(null))
                    vm.CancelRemapCommand.Execute(null);
                e.Handled = true;
            }
            // Other keys are ignored for automatic remapping
        }
    }

    private void MainWindow_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.IsRemapping && vm.IsMacroRecording)
        {
            vm.RecordMacroKeyboardEvent(ToMacroKeyName(e.Key), false);
            e.Handled = true;
        }
    }

    private void MainWindow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        RecordMacroMouseButton(e, true);
    }

    private void MainWindow_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        RecordMacroMouseButton(e, false);
    }

    private void RecordMacroMouseButton(PointerEventArgs e, bool isDown)
    {
        if (DataContext is not MainViewModel vm || !vm.IsRemapping || !vm.IsMacroRecording)
        {
            return;
        }

        if (IsInteractiveMacroUiSource(e.Source))
        {
            return;
        }

        var buttonName = ToMacroMouseButtonName(e.GetCurrentPoint(this).Properties.PointerUpdateKind, isDown);
        if (string.IsNullOrWhiteSpace(buttonName))
        {
            return;
        }

        vm.RecordMacroMouseEvent(buttonName, isDown);
        e.Handled = true;
    }

    private static bool IsInteractiveMacroUiSource(object? source)
    {
        if (source is not Avalonia.Visual visual)
        {
            return false;
        }

        for (var current = visual; current != null; current = current.GetVisualParent())
        {
            if (current is Button or TextBox or CheckBox)
            {
                return true;
            }
        }

        return false;
    }

    private static string ToMacroMouseButtonName(PointerUpdateKind updateKind, bool isDown)
    {
        return updateKind switch
        {
            PointerUpdateKind.LeftButtonPressed when isDown => "MouseLeft",
            PointerUpdateKind.LeftButtonReleased when !isDown => "MouseLeft",
            PointerUpdateKind.RightButtonPressed when isDown => "MouseRight",
            PointerUpdateKind.RightButtonReleased when !isDown => "MouseRight",
            PointerUpdateKind.MiddleButtonPressed when isDown => "MouseMiddle",
            PointerUpdateKind.MiddleButtonReleased when !isDown => "MouseMiddle",
            PointerUpdateKind.XButton1Pressed when isDown => "MouseX1",
            PointerUpdateKind.XButton1Released when !isDown => "MouseX1",
            PointerUpdateKind.XButton2Pressed when isDown => "MouseX2",
            PointerUpdateKind.XButton2Released when !isDown => "MouseX2",
            _ => string.Empty
        };
    }

    private static string ToMacroKeyName(Avalonia.Input.Key key)
    {
        var keyName = key.ToString();
        if (keyName.Length == 1 && char.IsLetter(keyName[0]))
        {
            return $"Key{char.ToUpperInvariant(keyName[0])}";
        }

        return keyName switch
        {
            "D0" or "D1" or "D2" or "D3" or "D4" or "D5" or "D6" or "D7" or "D8" or "D9" => keyName,
            "NumPad0" or "NumPad1" or "NumPad2" or "NumPad3" or "NumPad4" or "NumPad5" or "NumPad6" or "NumPad7" or "NumPad8" or "NumPad9" => keyName,
            "LeftShift" or "RightShift" or "LeftCtrl" or "RightCtrl" or "LeftAlt" or "RightAlt" => keyName,
            "Return" => "Return",
            "Escape" => "Escape",
            "Back" => "Backspace",
            "Space" => "Space",
            "Tab" => "Tab",
            "Delete" => "Delete",
            "Insert" => "Insert",
            "Home" => "Home",
            "End" => "End",
            "PageUp" => "PageUp",
            "PageDown" => "PageDown",
            "Up" => "Up",
            "Down" => "Down",
            "Left" => "Left",
            "Right" => "Right",
            _ => keyName
        };
    }

}
