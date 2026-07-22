using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Linq;
using ZeroCue.DataProbe.Services;
using ZeroCue.DataProbe.ViewModels;

namespace ZeroCue.DataProbe.Views
{
    public partial class ProfilesView : UserControl
    {
        public ProfilesView()
        {
            InitializeComponent();
        }

        private async void LinkAppButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not Button { Tag: ProfileItemViewModel profile }
                || DataContext is not MainViewModel viewModel)
            {
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return;
            }

            var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Get("ChooseExeTitle"),
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(LocalizationService.Get("ApplicationFiles"))
                    {
                        Patterns = new[] { "*.exe" }
                    }
                }
            });

            var selectedPaths = result
                .Select(file => file.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            if (selectedPaths.Count > 0)
            {
                viewModel.StageProfileAppLinks(profile, selectedPaths);
            }
        }
    }
}
