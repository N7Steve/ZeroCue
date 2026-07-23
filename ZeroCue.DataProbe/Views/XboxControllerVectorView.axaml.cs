using Avalonia.Controls;
using Avalonia.Input;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views
{
    public partial class XboxControllerVectorView : UserControl
    {
        public XboxControllerVectorView()
        {
            InitializeComponent();
        }

        private void TriggerButton_OnPointerEntered(object? sender, PointerEventArgs e)
        {
            if (DataContext is MainViewModel viewModel
                && sender is Button button
                && button.CommandParameter is string target)
            {
                viewModel.BeginTriggerOutputSelection(target);
            }
        }

        private void TriggerButton_OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.EndTriggerOutputSelection();
            }
        }

        private void TriggerButton_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.AdjustTriggerOutputSelection(e.Delta.Y);
                e.Handled = true;
            }
        }
    }
}
