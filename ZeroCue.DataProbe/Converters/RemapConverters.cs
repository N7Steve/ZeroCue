using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using ZeroCue.DataProbe.Controls;
using ZeroCue.DataProbe.Models;
using ZeroCue.DataProbe.Services;

namespace ZeroCue.DataProbe.Converters
{
    public static class RemapContext
    {
        public static bool IsShiftModeUi { get; set; }
    }

    public class RemapBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (parameter is string name)
            {
                var target = RemapConverterTargetResolver.GetTarget(name);
                var isRemapped = target != "Original"
                    && target != "Sin Mapeo"
                    && !string.IsNullOrWhiteSpace(target);
                isRemapped = isRemapped
                    || ScufDeviceService.Instance.GetAdvancedRemapCount(name, RemapContext.IsShiftModeUi) > 0;
                return new SolidColorBrush(Color.Parse(isRemapped ? "#FFB300" : "#5A6478"));
            }

            return new SolidColorBrush(Color.Parse("#5A6478"));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class RemapTooltipConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (parameter is not string name)
                return null;

            var target = RemapConverterTargetResolver.GetTarget(name);
            var targetDisplay = target switch
            {
                "Original" => LocalizationService.Get("Original"),
                "Sin Mapeo" => LocalizationService.Get("Unmapped"),
                _ => target
            };

            return target == "Original" || target == "Sin Mapeo"
                ? $"{name} ({targetDisplay})"
                : $"{name} -> {targetDisplay}";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class RemapContentConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (parameter is not string name)
                return null;

            var target = RemapConverterTargetResolver.GetTarget(name);
            var displayValue = target == "Original" ? name : target;

            if (displayValue == "Sin Mapeo" || string.IsNullOrWhiteSpace(displayValue))
            {
                displayValue = name.StartsWith("Paddle_") || name.StartsWith("SAX_") || name.StartsWith("G")
                    ? name
                    : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(displayValue))
                return new TextBlock { Text = string.Empty, HorizontalAlignment = HorizontalAlignment.Center };

            var bitmap = MappingIconCatalog.GetBitmap(displayValue);
            if (bitmap != null)
            {
                return new Image
                {
                    Source = bitmap,
                    Width = 90,
                    Height = 90,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            if (ZeroCue.DataProbe.Models.MacroTarget.IsMacroTarget(displayValue))
            {
                return new Material.Icons.Avalonia.MaterialIcon
                {
                    Kind = Material.Icons.MaterialIconKind.RobotOutline,
                    Width = 46,
                    Height = 46,
                    Foreground = new SolidColorBrush(Color.Parse("#EAF2FF")),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
            }

            var fallbackText = MappingIconCatalog.GetFallbackText(displayValue);
            if (string.IsNullOrWhiteSpace(fallbackText))
                fallbackText = displayValue.Replace("Paddle_", "P").Replace("SAX_", "S");

            return new GenericMappingIcon
            {
                Text = fallbackText,
                Width = 80,
                Height = 80,
                CornerRadius = new Avalonia.CornerRadius(16),
                Padding = new Avalonia.Thickness(14)
            };
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    internal static class RemapConverterTargetResolver
    {
        public static string GetTarget(string name)
        {
            var svc = ScufDeviceService.Instance;
            var target = svc.GetRemapTarget(name, RemapGestureTypes.Simple, RemapContext.IsShiftModeUi);
            if (string.IsNullOrWhiteSpace(target) || target == "Sin Mapeo")
            {
                return "Sin Mapeo";
            }

            if (IsAuxiliarySource(name))
            {
                return target;
            }

            var canonicalSource = CanonicalizeSourceName(name);
            var isDefaultTarget = target == canonicalSource
                || (VirtualTarget.GetBaseTarget(target) == canonicalSource
                    && VirtualTarget.GetTriggerOutputPercent(target) == 100);
            return isDefaultTarget ? "Original" : target;
        }

        private static bool IsAuxiliarySource(string name)
        {
            return name.StartsWith("Paddle_", StringComparison.Ordinal)
                || name.StartsWith("SAX_", StringComparison.Ordinal)
                || name is "G1" or "G2" or "G3" or "G4" or "G5";
        }

        private static string CanonicalizeSourceName(string name)
        {
            return name switch
            {
                "LB" => "LeftShoulder",
                "RB" => "RightShoulder",
                "LT" => "LeftTrigger",
                "RT" => "RightTrigger",
                "L3" => "LeftThumb",
                "R3" => "RightThumb",
                "View" => "Back",
                "Menu" => "Start",
                _ => name
            };
        }
    }

}
