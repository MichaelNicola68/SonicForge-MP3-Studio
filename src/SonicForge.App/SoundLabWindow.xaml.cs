using System.Windows;
using System.Windows.Controls;

namespace SonicForge.App;

public partial class SoundLabWindow : Window
{
    private readonly SoundLabSettings _settings;
    private readonly Slider[] _sliders = new Slider[10];
    private readonly TextBlock[] _values = new TextBlock[10];
    private bool _loading = true;

    public SoundLabWindow(SoundLabSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        BuildBands();
        EnabledCheck.IsChecked = settings.Enabled;
        AutoHeadroomCheck.IsChecked = settings.AutoHeadroom;
        SelectPreset(settings.PresetName);
        LoadGains();
        _loading = false;
        UpdateHeadroom();
    }

    private void BuildBands()
    {
        BandsGrid.ColumnDefinitions.Clear();
        for (var i = 0; i < 10; i++)
        {
            BandsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var value = new TextBlock { Text = "0,0 dB", Foreground = System.Windows.Media.Brushes.Cyan, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var slider = new Slider { Minimum = -12, Maximum = 12, Value = 0, Orientation = System.Windows.Controls.Orientation.Vertical, TickFrequency = 1, IsSnapToTickEnabled = false };
            slider.ValueChanged += BandChanged;
            var label = new TextBlock { Text = FormatFrequency(SoundLabSettings.Frequencies[i]), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var meaning = new TextBlock { Text = Meaning(i), Foreground = System.Windows.Media.Brushes.SlateGray, FontSize = 9, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            panel.Children.Add(value); panel.Children.Add(slider); panel.Children.Add(label); panel.Children.Add(meaning);
            Grid.SetColumn(panel, i); BandsGrid.Children.Add(panel);
            _sliders[i] = slider; _values[i] = value;
        }
    }

    private void LoadGains()
    {
        for (var i = 0; i < 10; i++)
        {
            _sliders[i].Value = _settings.Gains[i];
            _values[i].Text = $"{_settings.Gains[i]:+0.0;-0.0;0.0} dB";
        }
    }

    private void BandChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        for (var i = 0; i < 10; i++)
        {
            _settings.Gains[i] = (float)_sliders[i].Value;
            _values[i].Text = $"{_sliders[i].Value:+0.0;-0.0;0.0} dB";
        }
        _settings.PresetName = "Benutzerdefiniert";
        _settings.NotifyChanged();
        UpdateHeadroom();
    }

    private void ControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Enabled = EnabledCheck.IsChecked == true;
        _settings.AutoHeadroom = AutoHeadroomCheck.IsChecked == true;
        _settings.NotifyChanged();
        UpdateHeadroom();
    }

    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetBox.SelectedItem is not ComboBoxItem item) return;
        _settings.ApplyPreset(item.Content?.ToString() ?? "Neutral");
        _loading = true; LoadGains(); _loading = false; UpdateHeadroom();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApplyPreset("Neutral");
        _loading = true; SelectPreset("Neutral"); LoadGains(); _loading = false; UpdateHeadroom();
    }

    private void Bypass_Click(object sender, RoutedEventArgs e)
    {
        EnabledCheck.IsChecked = !(EnabledCheck.IsChecked == true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateHeadroom() => HeadroomText.Text = _settings.AutoHeadroom
        ? $"Automatischer Preamp: {_settings.EffectivePreampDb:0.0} dB"
        : $"Manueller Preamp: {_settings.EffectivePreampDb:0.0} dB";

    private void SelectPreset(string name)
    {
        foreach (ComboBoxItem item in PresetBox.Items)
            if (string.Equals(item.Content?.ToString(), name, StringComparison.OrdinalIgnoreCase)) { PresetBox.SelectedItem = item; return; }
        PresetBox.SelectedIndex = 0;
    }

    private static string FormatFrequency(float value) => value >= 1000 ? $"{value / 1000:0.#} kHz" : $"{value:0} Hz";
    private static string Meaning(int index) => new[] { "Tiefbass", "Druck", "Wärme", "Fülle", "Mitten", "Stimme", "Klarheit", "Präsenz", "Details", "Luft" }[index];
}
