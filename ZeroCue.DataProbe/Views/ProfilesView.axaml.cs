using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System;
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

        private async void ImportProfileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            viewModel.ProfileActionError = string.Empty;
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null)
                {
                    return;
                }

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = LocalizationService.Get("ImportProfileDialogTitle"),
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        CreateProfileFileType()
                    }
                });

                var selectedFile = files.FirstOrDefault();
                if (selectedFile != null)
                {
                    viewModel.BeginProfileImport(selectedFile.Path.LocalPath);
                }
            }
            catch (Exception ex)
            {
                viewModel.ProfileActionError = string.Format(LocalizationService.Get("ProfilePickerFailedFormat"), ex.Message);
            }
        }

        private async void ExportProfileMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: ProfileItemViewModel profile }
                || DataContext is not MainViewModel viewModel)
            {
                return;
            }

            viewModel.ProfileActionError = string.Empty;
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null)
                {
                    return;
                }

                var destination = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = LocalizationService.Get("ExportProfileDialogTitle"),
                    SuggestedFileName = $"{profile.Name}.json",
                    DefaultExtension = "json",
                    FileTypeChoices = new[]
                    {
                        CreateProfileFileType()
                    }
                });

                if (destination != null)
                {
                    viewModel.ExportProfile(profile, destination.Path.LocalPath);
                }
            }
            catch (Exception ex)
            {
                viewModel.ProfileActionError = string.Format(LocalizationService.Get("ProfilePickerFailedFormat"), ex.Message);
            }
        }

        private static FilePickerFileType CreateProfileFileType()
        {
            return new FilePickerFileType(LocalizationService.Get("ProfileJsonFiles"))
            {
                Patterns = new[] { "*.json" }
            };
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
