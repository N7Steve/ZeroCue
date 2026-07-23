using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace ZeroCue.DataProbe.Models
{
    public class AdvancedMappingItem : INotifyPropertyChanged
    {
        private bool _isHighlighted;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SourceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string IconKind { get; set; } = "GamepadCircle";
        public Bitmap? SourceIcon { get; set; }
        public string SourceFallbackText { get; set; } = string.Empty;
        public bool HasSourceIcon => SourceIcon != null;
        public bool HasSourceFallback => SourceIcon == null && !string.IsNullOrWhiteSpace(SourceFallbackText);
        public bool IsShiftLayer { get; set; }

        public string SimpleTarget { get; set; } = "Sin Mapeo";
        public string DoubleTapTarget { get; set; } = "Sin Mapeo";
        public string HoldTarget { get; set; } = "Sin Mapeo";
        public string PressStartTarget { get; set; } = "Sin Mapeo";
        public string PressReleaseTarget { get; set; } = "Sin Mapeo";
        public List<AdvancedMappingCommand> Commands { get; set; } = new();

        public bool HasSimple => !string.IsNullOrEmpty(SimpleTarget) && SimpleTarget != "Sin Mapeo";
        public bool HasDoubleTap => !string.IsNullOrEmpty(DoubleTapTarget) && DoubleTapTarget != "Sin Mapeo";
        public bool HasHold => !string.IsNullOrEmpty(HoldTarget) && HoldTarget != "Sin Mapeo";
        public bool HasPressStart => !string.IsNullOrEmpty(PressStartTarget) && PressStartTarget != "Sin Mapeo";
        public bool HasPressRelease => !string.IsNullOrEmpty(PressReleaseTarget) && PressReleaseTarget != "Sin Mapeo";
        public bool CanAddDoubleTap => !HasDoubleTap;
        public bool IsSimpleProfileWhileHeld => IsProfileWhileHeldTarget(SimpleTarget);
        public bool CanAddHold => !HasHold && !IsSimpleProfileWhileHeld;
        public bool CanAddPressStart => !HasPressStart;
        public bool CanAddPressRelease => !HasPressRelease && !IsSimpleProfileWhileHeld;
        public bool IsShiftModifier => SimpleTarget == "Shift";
        public bool HasAddableCommands => !IsShiftModifier && (CanAddDoubleTap || CanAddHold || CanAddPressStart || CanAddPressRelease);

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value)
                {
                    return;
                }

                _isHighlighted = value;
                OnPropertyChanged();
            }
        }

        // Return true if any mapping is active on this button
        public bool HasAnyMapping => HasSimple || HasDoubleTap || HasHold || HasPressStart || HasPressRelease;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static bool IsProfileWhileHeldTarget(string target)
        {
            return !string.IsNullOrWhiteSpace(target)
                && target.StartsWith("Action:LoadProfileHeld:", System.StringComparison.Ordinal);
        }
    }

    public class AdvancedMappingGroup
    {
        public string GroupName { get; set; } = string.Empty;
        public string IconKind { get; set; } = "Folder";
        public bool IsShiftLayer { get; set; }
        public List<AdvancedMappingItem> Items { get; set; } = new();
    }

    public class AdvancedMappingCommand : INotifyPropertyChanged
    {
        private bool _isHighlighted;

        public string GestureType { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
        public bool IsShiftLayer { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string IconKind { get; set; } = "SubdirectoryArrowRight";
        public string AccentBrush { get; set; } = "#66B2FF";
        public Bitmap? TargetIcon { get; set; }
        public string TargetFallbackText { get; set; } = string.Empty;
        public bool CanRemove => GestureType != "Simple" && GestureType != "None";
        public bool HasTargetIcon => TargetIcon != null;
        public bool HasTargetFallback => TargetIcon == null && !string.IsNullOrWhiteSpace(TargetFallbackText);
        public bool HasTargetText => TargetIcon == null && string.IsNullOrWhiteSpace(TargetFallbackText) && !string.IsNullOrWhiteSpace(Target);
        public bool HasTargetContent => HasTargetIcon || HasTargetFallback || HasTargetText;
        public bool HasTriggerOutputPercent =>
            VirtualTarget.IsTriggerTarget(Target)
            && VirtualTarget.GetTriggerOutputPercent(Target) < 100;
        public string TriggerOutputPercentLabel => $"{VirtualTarget.GetTriggerOutputPercent(Target)}%";

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value)
                {
                    return;
                }

                _isHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
