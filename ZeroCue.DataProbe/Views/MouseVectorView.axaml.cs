using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ZeroCue.DataProbe.Views
{
    public partial class MouseVectorView : UserControl
    {
        public MouseVectorView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
