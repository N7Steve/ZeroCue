using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using ZeroCue.DataProbe.Models;
using ZeroCue.DataProbe.Services;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views
{
    public partial class AdvancedMappingsView : UserControl
    {
        public AdvancedMappingsView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void MappingCard_PointerEntered(object? sender, PointerEventArgs e)
        {
            if (DataContext is MainViewModel viewModel && sender is Control { DataContext: AdvancedMappingItem item })
            {
                viewModel.SetDetailsHoveredInput(item.SourceName);
            }
        }

        private void MappingCard_PointerExited(object? sender, PointerEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || sender is not Control { DataContext: AdvancedMappingItem item })
            {
                return;
            }

            if (viewModel.HoveredDetailsInput == item.SourceName)
            {
                viewModel.SetDetailsHoveredInput(null);
            }
        }

        private void AddCommandButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel
                || sender is not Control anchor
                || anchor.DataContext is not AdvancedMappingItem item)
            {
                return;
            }

            var flyout = new MenuFlyout();
            flyout.Placement = PlacementMode.RightEdgeAlignedTop;
            flyout.HorizontalOffset = 10;
            AddMenuItem(flyout, LocalizationService.Get("DoubleTap"), item.CanAddDoubleTap, viewModel.AddDetailsDoubleTapCommand, item);
            AddMenuItem(flyout, LocalizationService.Get("Hold"), item.CanAddHold, viewModel.AddDetailsHoldCommand, item);
            AddMenuItem(flyout, LocalizationService.Get("PressStart"), item.CanAddPressStart, viewModel.AddDetailsPressStartCommand, item);
            AddMenuItem(flyout, LocalizationService.Get("PressRelease"), item.CanAddPressRelease, viewModel.AddDetailsPressReleaseCommand, item);

            if (flyout.Items.Count > 0)
            {
                flyout.ShowAt(anchor);
            }
        }

        private void RemoveCommandButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel
                || sender is not Control { DataContext: AdvancedMappingCommand command })
            {
                return;
            }

            if (viewModel.RemoveDetailsRemapGestureCommand.CanExecute(command))
            {
                viewModel.RemoveDetailsRemapGestureCommand.Execute(command);
            }
        }

        private static void AddMenuItem(MenuFlyout flyout, string header, bool isEnabled, System.Windows.Input.ICommand command, AdvancedMappingItem item)
        {
            if (!isEnabled)
            {
                return;
            }

            var menuItem = new MenuItem { Header = header };
            menuItem.Click += (_, _) =>
            {
                if (command.CanExecute(item))
                {
                    command.Execute(item);
                }
            };
            flyout.Items.Add(menuItem);
        }
    }
}
