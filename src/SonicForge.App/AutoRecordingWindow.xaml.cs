using System.Windows;
namespace SonicForge.App;
public partial class AutoRecordingWindow : Window
{
    readonly AutoRecordingService _service = new(); readonly string _folder;
    private bool _manualCloseAllowed;
    public event EventHandler<string>? Mp3Saved;
    public AutoRecordingWindow(string folder)
    {
        InitializeComponent(); _folder = folder; FolderText.Text = folder; // APP_SETTINGS_PHASE1
          var appSettings = AppSettings.Load(); StartDbBox.Text = appSettings.StartThresholdDb.ToString("0.##"); StopDbBox.Text = appSettings.StopThresholdDb.ToString("0.##"); SilenceBox.Text = appSettings.StopSilenceSeconds.ToString("0.##");
        Loaded += (_, _) => LoadDevices(); Closing += OnClosing;
        _service.StatusChanged += OnStatus;
        _service.Mp3Saved += (_, p) => Dispatcher.Invoke(() =>
        {
            AddSessionEntry(p); Mp3Saved?.Invoke(this, p); StartButton.IsEnabled = false; DeviceBox.IsEnabled = false;
            StopButton.IsEnabled = true; StopButton.Content = _service.IsRestartPending || _service.IsActive ? "Automatik beenden" : "Schließen";
        });
    }
    void LoadDevices() { DeviceBox.ItemsSource = RecordingService.GetDevices().Where(d => d.IsLoopback); DeviceBox.SelectedIndex = 0; }
    void Start_Click(object s, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not RecordingDevice d) return;
        if (!double.TryParse(StartDbBox.Text, out var a) || !double.TryParse(StopDbBox.Text, out var b) || !double.TryParse(SilenceBox.Text, out var c))
        { System.Windows.MessageBox.Show(this, "Bitte gÃ¼ltige Zahlen eingeben."); return; }
        _service.StartThresholdDb = a; _service.StopThresholdDb = b; _service.StopSilenceSeconds = Math.Max(1, c); var savedSettings = AppSettings.Load(); _service.StartConfirmSeconds = savedSettings.StartConfirmSeconds; _service.PreRollSeconds = savedSettings.PreRollSeconds; _service.AutomaticThresholdsEnabled = savedSettings.AutomaticThresholdsEnabled; savedSettings.StartThresholdDb = a; savedSettings.StopThresholdDb = b; savedSettings.StopSilenceSeconds = Math.Max(1, c); if (DeviceBox.SelectedItem is RecordingDevice selectedDevice) savedSettings.PreferredRecordingDeviceId = selectedDevice.Id; savedSettings.Save();
        _service.Start(d.Id, _folder); StartButton.IsEnabled = false; DeviceBox.IsEnabled = false;
        StopButton.IsEnabled = true; StopButton.Content = "Automatik beenden";
    }
    void Stop_Click(object s, RoutedEventArgs e)
    {
        if (_service.IsActive) { StopButton.IsEnabled = false; StopButton.Content = "Wird beendet ..."; _service.Stop(); }
        else { _manualCloseAllowed = true; Close(); }
    }
    void OnStatus(object? s, AutoRecordingStatusEventArgs e) => Dispatcher.BeginInvoke(() => { StatusText.Text = e.Text; Meter.Value = Math.Max(e.Left, e.Right); });

    // PHASE3C2_SESSION_LIST
    void AddSessionEntry(string path)
    {
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var detail = name;
            using var file = global::TagLib.File.Create(path);
            var artist = file.Tag.FirstPerformer;
            var title = file.Tag.Title;
            var album = file.Tag.Album;
            if (!string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title))
                detail = string.Join(" - ", new[] { artist, title }.Where(v => !string.IsNullOrWhiteSpace(v)));
            if (!string.IsNullOrWhiteSpace(album)) detail += "  |  " + album;
            SessionList.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + detail);
        }
        catch { SessionList.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + System.IO.Path.GetFileName(path)); }
    }    void OnClosing(object? s, System.ComponentModel.CancelEventArgs e)
    {
        if (!_manualCloseAllowed && (_service.IsActive || _service.IsRestartPending))
        {
            e.Cancel = true;
            StatusText.Text = "Automatik bleibt aktiv und wartet auf den nächsten Titel.";
            return;
        }
        _service.Dispose();
    }
}
