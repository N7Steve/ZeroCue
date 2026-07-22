using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views
{
    public partial class SticksView : UserControl
    {
        private const double CurveGraphWidth = 220.0;
        private const double CurveGraphHeight = 96.0;
        private const double CurveThumbRadius = 6.0;

        private readonly Thumb?[] _stickCurveThumbs;

        public SticksView()
        {
            InitializeComponent();
            _stickCurveThumbs = new[]
            {
                this.FindControl<Thumb>("StickCurveThumb0"),
                this.FindControl<Thumb>("StickCurveThumb1"),
                this.FindControl<Thumb>("StickCurveThumb2"),
                this.FindControl<Thumb>("StickCurveThumb3"),
                this.FindControl<Thumb>("StickCurveThumb4")
            };

            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.StickCustomCurveX) ||
                        args.PropertyName == nameof(MainViewModel.StickCustomCurveY) ||
                        args.PropertyName == nameof(MainViewModel.SelectedStickCurve))
                    {
                        UpdateThumbsFromViewModel(vm);
                    }
                };
                UpdateThumbsFromViewModel(vm);
            }
        }

        private void UpdateThumbsFromViewModel(MainViewModel vm)
        {
            if (vm.StickCustomCurveX == null || vm.StickCustomCurveY == null || vm.StickCustomCurveX.Length < 5) return;
            if (_stickCurveThumbs[0] == null) return;

            for (int i = 0; i < 5; i++)
            {
                double px = vm.StickCustomCurveX[i] * CurveGraphWidth;
                double py = CurveGraphHeight - (vm.StickCustomCurveY[i] * CurveGraphHeight);
                Canvas.SetLeft(_stickCurveThumbs[i]!, px - CurveThumbRadius);
                Canvas.SetTop(_stickCurveThumbs[i]!, py - CurveThumbRadius);
            }
        }

        private void OnStickCurveThumbDragDelta(object sender, VectorEventArgs e)
        {
            if (sender is Thumb thumb && DataContext is MainViewModel vm)
            {
                int index = System.Array.IndexOf(_stickCurveThumbs, thumb);
                if (index >= 0)
                {
                    double currentX = Canvas.GetLeft(thumb) + CurveThumbRadius;
                    double currentY = Canvas.GetTop(thumb) + CurveThumbRadius;
                    double newX = currentX + e.Vector.X;
                    double newY = currentY + e.Vector.Y;

                    double normX = newX / CurveGraphWidth;
                    double normY = 1.0 - (newY / CurveGraphHeight);

                    vm.UpdateStickCustomCurvePoint(index, normX, normY);
                }
            }
        }
    }
}
