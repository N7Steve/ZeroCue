using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ZeroCue.DataProbe.Services;

namespace ZeroCue.DataProbe.ViewModels
{
    public class ProfileItemViewModel : ViewModelBase
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private string _originalName = string.Empty;
        public string OriginalName
        {
            get => _originalName;
            set => SetProperty(ref _originalName, value);
        }

        private string _linkedAppPath = string.Empty;
        public string LinkedAppPath
        {
            get => _linkedAppPath;
            set
            {
                var normalizedValue = value ?? string.Empty;
                if (SetProperty(ref _linkedAppPath, normalizedValue))
                {
                    LinkedAppPaths = string.IsNullOrWhiteSpace(normalizedValue)
                        ? new List<string>()
                        : new List<string> { normalizedValue };
                    OnPropertyChanged(nameof(LinkedAppName));
                    OnPropertyChanged(nameof(LinkedAppDisplay));
                    OnPropertyChanged(nameof(LinkedAppPathDisplay));
                    OnPropertyChanged(nameof(HasLinkedApp));
                }
            }
        }

        private List<string> _linkedAppPaths = new List<string>();
        public List<string> LinkedAppPaths
        {
            get => _linkedAppPaths;
            set
            {
                var paths = value?
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                if (SetProperty(ref _linkedAppPaths, paths))
                {
                    _linkedAppPath = paths.FirstOrDefault() ?? string.Empty;
                    LinkedAppItems = paths.Select(path => new LinkedAppItemViewModel(path)).ToList();
                    OnPropertyChanged(nameof(LinkedAppPath));
                    OnPropertyChanged(nameof(LinkedAppName));
                    OnPropertyChanged(nameof(LinkedAppDisplay));
                    OnPropertyChanged(nameof(LinkedAppPathDisplay));
                    OnPropertyChanged(nameof(PreviewLinkedAppItems));
                    OnPropertyChanged(nameof(LinkedAppCountText));
                    OnPropertyChanged(nameof(LinkedAppOverflowText));
                    OnPropertyChanged(nameof(HasLinkedApp));
                    OnPropertyChanged(nameof(HasLinkedAppOverflow));
                }
            }
        }

        private List<LinkedAppItemViewModel> _linkedAppItems = new List<LinkedAppItemViewModel>();
        public List<LinkedAppItemViewModel> LinkedAppItems
        {
            get => _linkedAppItems;
            private set => SetProperty(ref _linkedAppItems, value);
        }

        public IEnumerable<LinkedAppItemViewModel> PreviewLinkedAppItems => LinkedAppItems.Take(2);

        public string LinkedAppName => LinkedAppPaths.Count == 0
            ? LocalizationService.Get("None")
            : Path.GetFileName(LinkedAppPaths[0]);

        public string LinkedAppDisplay
        {
            get
            {
                if (LinkedAppPaths.Count == 0) return LinkedAppName;
                if (LinkedAppPaths.Count == 1) return Path.GetFileName(LinkedAppPaths[0]);
                return $"{Path.GetFileName(LinkedAppPaths[0])} + {LinkedAppPaths.Count - 1}";
            }
        }

        public string LinkedAppPathDisplay => LinkedAppPaths.Count == 0
            ? LocalizationService.Get("ProfileNoLinkedAppHint")
            : string.Join("; ", LinkedAppPaths);

        public string LinkedAppCountText => string.Format(LocalizationService.Get("LinkedAppCountFormat"), LinkedAppPaths.Count);

        public string LinkedAppOverflowText => string.Format(LocalizationService.Get("LinkedAppOverflowFormat"), Math.Max(LinkedAppPaths.Count - 2, 0));

        public bool HasLinkedApp => LinkedAppPaths.Count > 0;
        public bool HasLinkedAppOverflow => LinkedAppPaths.Count > 2;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    OnPropertyChanged(nameof(IsNotSelected));
                }
            }
        }
        public bool IsNotSelected => !IsSelected;

        private bool _isDefault;
        public bool IsDefault
        {
            get => _isDefault;
            set
            {
                if (SetProperty(ref _isDefault, value))
                {
                    OnPropertyChanged(nameof(IsNotDefault));
                }
            }
        }

        public bool IsNotDefault => !IsDefault;

        private bool _canDelete = true;
        public bool CanDelete
        {
            get => _canDelete;
            set => SetProperty(ref _canDelete, value);
        }

        public void NotifyLocalizedTextChanged()
        {
            OnPropertyChanged(nameof(LinkedAppName));
            OnPropertyChanged(nameof(LinkedAppDisplay));
            OnPropertyChanged(nameof(LinkedAppPathDisplay));
            OnPropertyChanged(nameof(LinkedAppCountText));
            OnPropertyChanged(nameof(LinkedAppOverflowText));
        }

        public ICommand RenameProfileCommand { get; }
        public ICommand ActivateProfileCommand { get; }
        public ICommand SelectProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand DuplicateProfileCommand { get; }
        public ICommand SetDefaultProfileCommand { get; }
        public ICommand ManageLinkedAppsCommand { get; }
        public ICommand UnlinkAppCommand { get; }

        public ProfileItemViewModel(
            string name,
            bool isDefault,
            string linkedAppPath,
            IEnumerable<string>? linkedAppPaths,
            Action<ProfileItemViewModel> onRename,
            Action<ProfileItemViewModel> onDelete,
            Action<ProfileItemViewModel> onDuplicate,
            Action<ProfileItemViewModel> onSetDefault,
            Action<ProfileItemViewModel> onActivate,
            Action<ProfileItemViewModel> onSelect,
            Action<ProfileItemViewModel> onManageLinkedApps,
            Action<ProfileItemViewModel> onUnlink)
        {
            Name = name;
            OriginalName = name;
            IsDefault = isDefault;
            LinkedAppPaths = (linkedAppPaths ?? Array.Empty<string>()).ToList();
            if (LinkedAppPaths.Count == 0)
            {
                LinkedAppPath = linkedAppPath;
            }

            RenameProfileCommand = new RelayCommand(() => onRename(this));
            ActivateProfileCommand = new RelayCommand(() => onActivate(this));
            SelectProfileCommand = new RelayCommand(() => onSelect(this));
            DeleteProfileCommand = new RelayCommand(() => onDelete(this));
            DuplicateProfileCommand = new RelayCommand(() => onDuplicate(this));
            SetDefaultProfileCommand = new RelayCommand(() => onSetDefault(this));
            ManageLinkedAppsCommand = new RelayCommand(() => onManageLinkedApps(this));
            UnlinkAppCommand = new RelayCommand(() => onUnlink(this));
        }
    }

    public class LinkedAppItemViewModel
    {
        public string Path { get; }
        public string Name { get; }
        public string DirectoryPath { get; }

        public LinkedAppItemViewModel(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            DirectoryPath = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }
    }
}
