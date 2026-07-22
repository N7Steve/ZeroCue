using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace ZeroCue.DataProbe.Controls
{
    public class GenericMappingIcon : Border
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<GenericMappingIcon, string>(nameof(Text), string.Empty);

        private readonly TextBlock _textBlock;
        private readonly Viewbox _viewbox;
        private readonly MaterialIcon _materialIcon;
        private readonly Panel _container;

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public GenericMappingIcon()
        {
            Width = 42;
            Height = 42;
            CornerRadius = new CornerRadius(7);
            Background = new SolidColorBrush(Color.Parse("#2A2A2A"));
            BorderBrush = new SolidColorBrush(Color.Parse("#30343D"));
            BorderThickness = new Thickness(1);
            BoxShadow = BoxShadows.Parse("0 0 0 1 #22FFFFFF, 0 3 8 0 #33000000");
            Padding = new Thickness(7);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;

            _textBlock = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#EAF2FF")),
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Mono, monospace"),
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                LineHeight = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            _viewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = _textBlock
            };

            _materialIcon = new MaterialIcon
            {
                Width = 24,
                Height = 24,
                Foreground = new SolidColorBrush(Color.Parse("#EAF2FF")),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false
            };

            _container = new Panel();
            _container.Children.Add(_viewbox);
            _container.Children.Add(_materialIcon);

            Child = _container;
            SizeChanged += (_, _) => UpdateMetrics();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TextProperty)
            {
                var text = change.NewValue as string ?? string.Empty;
                if (text.StartsWith("Icon:"))
                {
                    _viewbox.IsVisible = false;
                    _materialIcon.IsVisible = true;
                    if (Enum.TryParse<MaterialIconKind>(text.Substring(5), true, out var kind))
                    {
                        _materialIcon.Kind = kind;
                    }
                    UpdateMaterialIconMetrics();
                }
                else
                {
                    _viewbox.IsVisible = true;
                    _materialIcon.IsVisible = false;
                    _textBlock.Text = text;
                    UpdateTextMetrics();
                }
            }
            else if (change.Property == WidthProperty ||
                     change.Property == HeightProperty ||
                     change.Property == PaddingProperty)
            {
                UpdateMetrics();
            }
        }

        private void UpdateMetrics()
        {
            UpdateTextMetrics();
            UpdateMaterialIconMetrics();
        }

        private void UpdateTextMetrics()
        {
            if (_textBlock == null || !_viewbox.IsVisible) return;

            var text = _textBlock.Text ?? string.Empty;
            var width = Bounds.Width > 0 ? Bounds.Width : Width;
            var height = Bounds.Height > 0 ? Bounds.Height : Height;
            var iconSize = Math.Min(width, height);

            if (double.IsNaN(iconSize) || iconSize <= 0)
                iconSize = 42;

            var horizontalPadding = Padding.Left + Padding.Right;
            var verticalPadding = Padding.Top + Padding.Bottom;
            var available = Math.Max(10, Math.Min(iconSize - horizontalPadding, iconSize - verticalPadding));

            var scale = text.Length switch
            {
                <= 2 => 0.62,
                3 => 0.52,
                4 => 0.44,
                5 => 0.38,
                _ => 0.32
            };

            var fontSize = Math.Max(9, Math.Round(available * scale));
            _textBlock.FontSize = fontSize;
            _textBlock.LineHeight = fontSize;
        }

        private void UpdateMaterialIconMetrics()
        {
            if (_materialIcon == null || !_materialIcon.IsVisible) return;

            var width = Bounds.Width > 0 ? Bounds.Width : Width;
            var height = Bounds.Height > 0 ? Bounds.Height : Height;
            var iconSize = Math.Min(width, height);

            if (double.IsNaN(iconSize) || iconSize <= 0)
                iconSize = 42;

            var horizontalPadding = Padding.Left + Padding.Right;
            var verticalPadding = Padding.Top + Padding.Bottom;
            var available = Math.Max(16, Math.Min(iconSize - horizontalPadding, iconSize - verticalPadding));
            var materialIconSize = Math.Round(available * 0.96);

            _materialIcon.Width = materialIconSize;
            _materialIcon.Height = materialIconSize;
        }
    }
}
