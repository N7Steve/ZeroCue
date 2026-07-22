using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views
{
    public partial class TriggersView : UserControl
    {
        private const double CurveGraphWidth = 240.0;
        private const double CurveGraphHeight = 120.0;
        private const double CurveThumbRadius = 6.0;

        private Thumb?[] _thumbs;

        public TriggersView()
        {
            InitializeComponent();
            _thumbs = new[] {
                this.FindControl<Thumb>("Thumb0"),
                this.FindControl<Thumb>("Thumb1"),
                this.FindControl<Thumb>("Thumb2"),
                this.FindControl<Thumb>("Thumb3"),
                this.FindControl<Thumb>("Thumb4")
            };

            this.DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (this.DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.CustomCurveX) ||
                        args.PropertyName == nameof(MainViewModel.CustomCurveY) ||
                        args.PropertyName == nameof(MainViewModel.SelectedTriggerCurve))
                    {
                        UpdateThumbsFromViewModel(vm);
                    }
                };
                UpdateThumbsFromViewModel(vm);
            }
        }

        private void UpdateThumbsFromViewModel(MainViewModel vm)
        {
            if (vm.CustomCurveX == null || vm.CustomCurveY == null || vm.CustomCurveX.Length < 5) return;
            if (_thumbs[0] == null) return; // Not loaded yet

            for (int i = 0; i < 5; i++)
            {
                double px = vm.CustomCurveX[i] * CurveGraphWidth;
                double py = CurveGraphHeight - (vm.CustomCurveY[i] * CurveGraphHeight);
                Canvas.SetLeft(_thumbs[i]!, px - CurveThumbRadius);
                Canvas.SetTop(_thumbs[i]!, py - CurveThumbRadius);
            }
        }

        private void OnThumbDragDelta(object sender, VectorEventArgs e)
        {
            if (sender is Thumb thumb && this.DataContext is MainViewModel vm)
            {
                int index = System.Array.IndexOf(_thumbs, thumb);
                if (index >= 0)
                {
                    double currentX = Canvas.GetLeft(thumb) + CurveThumbRadius;
                    double currentY = Canvas.GetTop(thumb) + CurveThumbRadius;

                    double newX = currentX + e.Vector.X;
                    double newY = currentY + e.Vector.Y;

                    double normX = newX / CurveGraphWidth;
                    double normY = 1.0 - (newY / CurveGraphHeight);

                    vm.UpdateCustomCurvePoint(index, normX, normY);
                }
            }
        }
    }
}
