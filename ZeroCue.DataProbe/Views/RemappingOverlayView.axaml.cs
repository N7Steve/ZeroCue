using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ZeroCue.DataProbe.Views;

public partial class RemappingOverlayView : UserControl
{
    private bool _isNormalizingTriggerOutputPercent;

    public RemappingOverlayView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void TriggerOutputPercentTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isNormalizingTriggerOutputPercent || sender is not TextBox textBox)
        {
            return;
        }

        var text = textBox.Text ?? string.Empty;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        var normalized = int.TryParse(digits, out var parsed)
            ? Math.Clamp(parsed, 0, 100).ToString()
            : "0";

        if (text == normalized)
        {
            return;
        }

        _isNormalizingTriggerOutputPercent = true;
        try
        {
            textBox.Text = normalized;
            textBox.CaretIndex = normalized.Length;
        }
        finally
        {
            _isNormalizingTriggerOutputPercent = false;
        }
    }
}
