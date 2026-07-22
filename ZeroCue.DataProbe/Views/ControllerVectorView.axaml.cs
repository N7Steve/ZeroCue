using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using Avalonia.VisualTree;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views
{
    public partial class ControllerVectorView : UserControl
    {
        private const double UsbCableHiddenY = -1650;
        private const double UsbCableTouchY = 0;
        private const double UsbCableConnectedY = 38;
        private const double RfDongleHiddenTop = 1510.53;
        private const double RfDonglePlugTouchTop = 955;
        private const double RfDongleConnectedTop = 850;
        private const double RfDongleSimpleConnectedTop = RfDongleConnectedTop + 4;

        private readonly List<Button> _silhouetteButtons = new();
        private MainViewModel? _subscribedViewModel;
        private CancellationTokenSource? _usbCableAnimationCts;
        private CancellationTokenSource? _rfDongleAnimationCts;
        private bool? _lastUsbConnected;
        private bool? _lastRfConnected;

        public ControllerVectorView()
        {
            InitializeComponent();
            SetUsbCableY(UsbCableHiddenY);
            SetRfDongleTop(RfDongleHiddenTop);
            RfDongleViewbox.Opacity = 0;
            Loaded += (_, _) =>
            {
                ApplySilhouetteMode();
                SubscribeToViewModel();
                InitializeConnectionVisualStates();
            };
            DataContextChanged += (_, _) => SubscribeToViewModel();
        }

        private void ApplySilhouetteMode()
        {
            if (!Classes.Contains("controllerSilhouette"))
            {
                return;
            }

            string[] overlayClasses =
            {
                "labelBtn",
                "circleLabelBtn",
                "mapLine",
                "mapDot",
                "modeToggle"
            };

            foreach (var control in this.GetVisualDescendants().OfType<Control>())
            {
                if (overlayClasses.Any(control.Classes.Contains))
                {
                    control.IsVisible = false;
                }
            }

            ControllerCropPanel.Width = 2508;
            ControllerCropPanel.Height = 2508;
            ControllerOuterCanvas.Margin = new Avalonia.Thickness(-1350, 46, 0, 0);

            foreach (var button in this.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("pathRemapBtn")))
            {
                button.PointerEntered += HandleSilhouetteButtonPointerEntered;
                button.PointerExited += HandleSilhouetteButtonPointerExited;
                button.PointerPressed += HandleSilhouetteButtonPointerPressed;
                if (!_silhouetteButtons.Contains(button))
                {
                    _silhouetteButtons.Add(button);
                }
            }

            SubscribeToViewModel();
            UpdateLinkedHoverButtons();
        }

        private void HandleSilhouetteButtonPointerEntered(object? sender, PointerEventArgs e)
        {
            SetHoveredDetailsInput(sender);
        }

        private void HandleSilhouetteButtonPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            SetHoveredDetailsInput(sender);
        }

        private void HandleSilhouetteButtonPointerExited(object? sender, PointerEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || sender is not Button button)
            {
                return;
            }

            var inputName = button.CommandParameter as string;
            if (viewModel.HoveredDetailsInput == NormalizeDetailsInputName(inputName))
            {
                viewModel.SetDetailsHoveredInput(null);
            }
        }

        private void SetHoveredDetailsInput(object? sender)
        {
            if (DataContext is MainViewModel viewModel && sender is Button button)
            {
                viewModel.SetDetailsHoveredInput(button.CommandParameter as string);
            }
        }

        private void SubscribeToViewModel()
        {
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
                _subscribedViewModel = null;
            }

            if (DataContext is MainViewModel viewModel)
            {
                _subscribedViewModel = viewModel;
                _subscribedViewModel.PropertyChanged += HandleViewModelPropertyChanged;
                InitializeConnectionVisualStates();
            }
        }

        private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsStatusUsbConnected) && _subscribedViewModel != null)
            {
                UpdateUsbCableConnectionState(_subscribedViewModel.IsStatusUsbConnected);
            }

            if (e.PropertyName == nameof(MainViewModel.IsStatusWirelessConnected) && _subscribedViewModel != null)
            {
                UpdateRfDongleConnectionState(_subscribedViewModel.IsStatusWirelessConnected);
            }

            if (e.PropertyName == nameof(MainViewModel.HoveredDetailsInput))
            {
                UpdateLinkedHoverButtons();
            }
        }

        private void InitializeConnectionVisualStates()
        {
            InitializeUsbCableState();
            InitializeRfDongleState();
        }

        private void InitializeUsbCableState()
        {
            if (DataContext is not MainViewModel viewModel)
            {
                SetUsbCableY(UsbCableHiddenY);
                _lastUsbConnected = false;
                return;
            }

            _usbCableAnimationCts?.Cancel();
            _lastUsbConnected = viewModel.IsStatusUsbConnected;
            SetUsbCableY(viewModel.IsStatusUsbConnected ? UsbCableConnectedY : UsbCableHiddenY);
        }

        private void InitializeRfDongleState()
        {
            if (DataContext is not MainViewModel viewModel)
            {
                SetRfDongleTop(RfDongleHiddenTop);
                RfDongleViewbox.Opacity = 0;
                _lastRfConnected = false;
                return;
            }

            _rfDongleAnimationCts?.Cancel();
            _lastRfConnected = viewModel.IsStatusWirelessConnected;
            SetRfDongleTop(viewModel.IsStatusWirelessConnected ? GetRfDongleConnectedTop() : RfDongleHiddenTop);
            RfDongleViewbox.Opacity = viewModel.IsStatusWirelessConnected ? 1 : 0;
        }

        private void UpdateUsbCableConnectionState(bool isConnected)
        {
            if (_lastUsbConnected == isConnected)
            {
                return;
            }

            _lastUsbConnected = isConnected;

            Dispatcher.UIThread.Post(() =>
            {
                _ = isConnected
                    ? AnimateUsbCableConnectionAsync()
                    : AnimateUsbCableDisconnectionAsync();
            });
        }

        private void UpdateRfDongleConnectionState(bool isConnected)
        {
            if (_lastRfConnected == isConnected)
            {
                return;
            }

            _lastRfConnected = isConnected;

            Dispatcher.UIThread.Post(() =>
            {
                _ = isConnected
                    ? AnimateRfDongleConnectionAsync()
                    : AnimateRfDongleDisconnectionAsync();
            });
        }

        private async Task AnimateUsbCableConnectionAsync()
        {
            var animation = StartUsbCableAnimation();

            try
            {
                SetUsbCableY(UsbCableHiddenY);
                await AnimateUsbCableYAsync(UsbCableHiddenY, UsbCableTouchY, 800, EaseOutCubic, animation.Token);
                await Task.Delay(100, animation.Token);
                await AnimateUsbCableYAsync(UsbCableTouchY, UsbCableConnectedY, 150, EaseOutCubic, animation.Token);
                SetUsbCableY(UsbCableConnectedY);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ClearUsbCableAnimation(animation);
            }
        }

        private async Task AnimateUsbCableDisconnectionAsync()
        {
            var animation = StartUsbCableAnimation();

            try
            {
                await AnimateUsbCableYAsync(GetUsbCableY(), UsbCableTouchY, 120, EaseOutCubic, animation.Token);
                await AnimateUsbCableYAsync(UsbCableTouchY, UsbCableHiddenY, 450, EaseInCubic, animation.Token);
                SetUsbCableY(UsbCableHiddenY);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ClearUsbCableAnimation(animation);
            }
        }

        private CancellationTokenSource StartUsbCableAnimation()
        {
            _usbCableAnimationCts?.Cancel();
            _usbCableAnimationCts?.Dispose();
            _usbCableAnimationCts = new CancellationTokenSource();
            return _usbCableAnimationCts;
        }

        private void ClearUsbCableAnimation(CancellationTokenSource animation)
        {
            if (ReferenceEquals(_usbCableAnimationCts, animation))
            {
                _usbCableAnimationCts = null;
            }

            animation.Dispose();
        }

        private async Task AnimateRfDongleConnectionAsync()
        {
            var animation = StartRfDongleAnimation();

            try
            {
                RfDongleViewbox.Opacity = 1;
                SetRfDongleTop(RfDongleHiddenTop);
                await AnimateRfDongleTopAsync(RfDongleHiddenTop, RfDonglePlugTouchTop, 720, EaseOutCubic, animation.Token);
                await Task.Delay(100, animation.Token);
                await AnimateRfDongleTopAsync(RfDonglePlugTouchTop, GetRfDongleConnectedTop(), 150, EaseOutCubic, animation.Token);
                SetRfDongleTop(GetRfDongleConnectedTop());
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ClearRfDongleAnimation(animation);
            }
        }

        private async Task AnimateRfDongleDisconnectionAsync()
        {
            var animation = StartRfDongleAnimation();

            try
            {
                RfDongleViewbox.Opacity = 1;
                await AnimateRfDongleTopAsync(GetRfDongleTop(), RfDonglePlugTouchTop, 120, EaseOutCubic, animation.Token);
                await AnimateRfDongleTopAsync(RfDonglePlugTouchTop, RfDongleHiddenTop, 480, EaseInCubic, animation.Token);
                SetRfDongleTop(RfDongleHiddenTop);
                RfDongleViewbox.Opacity = 0;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ClearRfDongleAnimation(animation);
            }
        }

        private CancellationTokenSource StartRfDongleAnimation()
        {
            _rfDongleAnimationCts?.Cancel();
            _rfDongleAnimationCts?.Dispose();
            _rfDongleAnimationCts = new CancellationTokenSource();
            return _rfDongleAnimationCts;
        }

        private void ClearRfDongleAnimation(CancellationTokenSource animation)
        {
            if (ReferenceEquals(_rfDongleAnimationCts, animation))
            {
                _rfDongleAnimationCts = null;
            }

            animation.Dispose();
        }

        private async Task AnimateUsbCableYAsync(double from, double to, int durationMs, Func<double, double> easing, CancellationToken cancellationToken)
        {
            var start = DateTime.UtcNow;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var progress = Math.Clamp(elapsed / durationMs, 0, 1);
                var easedProgress = easing(progress);

                SetUsbCableY(from + ((to - from) * easedProgress));

                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(16, cancellationToken);
            }

            SetUsbCableY(to);
        }

        private double GetUsbCableY()
        {
            return UsbCableCanvas.RenderTransform is TranslateTransform transform
                ? transform.Y
                : UsbCableHiddenY;
        }

        private void SetUsbCableY(double y)
        {
            if (UsbCableCanvas.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform();
                UsbCableCanvas.RenderTransform = transform;
            }

            transform.Y = y;
        }

        private async Task AnimateRfDongleTopAsync(double from, double to, int durationMs, Func<double, double> easing, CancellationToken cancellationToken)
        {
            var start = DateTime.UtcNow;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var progress = Math.Clamp(elapsed / durationMs, 0, 1);
                var easedProgress = easing(progress);

                SetRfDongleTop(from + ((to - from) * easedProgress));

                if (progress >= 1)
                {
                    break;
                }

                await Task.Delay(16, cancellationToken);
            }

            SetRfDongleTop(to);
        }

        private double GetRfDongleTop()
        {
            return Canvas.GetTop(RfDongleViewbox);
        }

        private void SetRfDongleTop(double top)
        {
            Canvas.SetTop(RfDongleViewbox, top);
        }

        private double GetRfDongleConnectedTop()
        {
            return Classes.Contains("controllerSilhouette")
                ? RfDongleConnectedTop
                : RfDongleSimpleConnectedTop;
        }

        private static double EaseOutCubic(double progress)
        {
            return 1 - Math.Pow(1 - progress, 3);
        }

        private static double EaseInCubic(double progress)
        {
            return progress * progress * progress;
        }

        private void UpdateLinkedHoverButtons()
        {
            if (_subscribedViewModel == null)
            {
                return;
            }

            foreach (var button in _silhouetteButtons)
            {
                var isHovered = _subscribedViewModel.HoveredDetailsInput == NormalizeDetailsInputName(button.CommandParameter as string);
                button.Classes.Set("linkedHover", isHovered);
            }
        }

        private static string NormalizeDetailsInputName(string? inputName)
        {
            return inputName switch
            {
                "LeftThumb" => "L3",
                "RightThumb" => "R3",
                null => string.Empty,
                _ => inputName
            };
        }
    }
}
