using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ZeroCue.DataProbe.Views;

public partial class RemappingOverlayView : UserControl
{
    public RemappingOverlayView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
