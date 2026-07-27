using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ZeroCue.DataProbe;

public enum CloseApplicationChoice
{
    Cancel,
    MinimizeToTray,
    Exit
}

public readonly record struct CloseDialogResult(
    CloseApplicationChoice Choice,
    bool RememberSelection);

public partial class CloseConfirmationDialog : Window
{
    public CloseConfirmationDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Clicked(object? sender, RoutedEventArgs e)
    {
        Close(new CloseDialogResult(CloseApplicationChoice.Cancel, false));
    }

    private void Minimize_Clicked(object? sender, RoutedEventArgs e)
    {
        Close(new CloseDialogResult(
            CloseApplicationChoice.MinimizeToTray,
            RememberSelectionCheckBox.IsChecked == true));
    }

    private void Exit_Clicked(object? sender, RoutedEventArgs e)
    {
        Close(new CloseDialogResult(
            CloseApplicationChoice.Exit,
            RememberSelectionCheckBox.IsChecked == true));
    }
}
