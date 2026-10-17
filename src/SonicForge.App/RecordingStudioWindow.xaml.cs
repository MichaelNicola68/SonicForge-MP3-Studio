using NAudio.Wave;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SonicForge.App;

public partial class RecordingStudioWindow : Window
{
    private readonly RecordingService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string _outputFolder;
    private bool _discardRequested;
    private string _recordingMode = "Standard";
    private string _filePrefix = "SonicForge-Aufnahme";
    public event EventHandler<string>? RecordingSaved;

    public RecordingStudioWindow()
    {
        InitializeComponent();
          // APP_SETTINGS_PHASE1
          var appSettings = AppSettings.Load();
        _outputFolder = string.IsNullOrWhiteSpace(appSettings.OutputFolder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SonicForge Aufnahmen") : appSettings.OutputFolder;
        OutputFolderBox.Text = _outputFolder;
        _service.LevelChanged += Service_LevelChanged;
        _service.RecordingStopped += Service_RecordingStopped;
        _timer.Tick += (_, _) => TimeText.Text = _service.Elapsed.ToString(@"hh\:mm\:ss");
        Loaded += (_, _) => LoadDevices();
        Closing += RecordingStudioWindow_Closing;
        UpdateTargetPreview();
    }

    private void LoadDevices()
    {
        try
        {
            var selectedId = (DeviceBox.SelectedItem as RecordingDevice)?.Id;
            DeviceBox.ItemsSource = RecordingService.GetDevices();
            DeviceBox.SelectedItem = DeviceBox.Items.Cast<RecordingDevice>().FirstOrDefault(d => d.Id == selectedId) ?? DeviceBox.Items.Cast<RecordingDevice>().FirstOrDefault();
            StatusText.Text = DeviceBox.Items.Count == 0 ? "Kein aktives AufnahmegerÃ¤t gefunden." : $"{DeviceBox.Items.Count} Aufnahmequelle(n) gefunden.";
        }
        catch (Exception ex) { StatusText.Text = "GerÃ¤tefehler: " + ex.Message; }
    }

    private void ReloadDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Ordner fÃ¼r SonicForge-Aufnahmen auswÃ¤hlen", UseDescriptionForTitle = true, SelectedPath = _outputFolder };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _outputFolder = dialog.SelectedPath; var savedSettings = AppSettings.Load(); savedSettings.OutputFolder = _outputFolder; savedSettings.Save();
        OutputFolderBox.Text = _outputFolder;
        UpdateTargetPreview();
    }

    private void AutoMode_Click(object sender, RoutedEventArgs e)
    {
        var window = new AutoRecordingWindow(_outputFolder) { Owner = this };
        window.Mp3Saved += (_, path) => RecordingSaved?.Invoke(this, path);
        window.ShowDialog();
    }
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmAvailableCustomFileName()) return;        CaptureMetadataForNewRecording();
        if (DeviceBox.SelectedItem is not RecordingDevice device)
        {
            System.Windows.MessageBox.Show(this,"Bitte zuerst eine Aufnahmequelle auswÃ¤hlen.","SonicForge Aufnahmestudio",MessageBoxButton.OK,MessageBoxImage.Information); return;
        }
        try
        {
            var path = CreateOutputPath();
            _discardRequested = false;
            _service.Start(device,path);
            DiagnosticLog.Info("RecordingStudio", "Aufnahme gestartet | Modus=" + _recordingMode + " | Quelle=" + device + " | Ziel=" + path);
            SetRecordingUi(true);
            _timer.Start();
            StatusText.Text = device.IsLoopback ? "Systemton-Aufnahme l\u00E4uft." : "Mikrofon-/Line-In-Aufnahme lÃ¤uft.";
            TargetText.Text = path;
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this,ex.Message,"Aufnahme konnte nicht gestartet werden",MessageBoxButton.OK,MessageBoxImage.Error); }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_service.IsPaused) { _service.Resume(); PauseButton.Content = "Pause"; StatusText.Text = "Aufnahme lÃ¤uft weiter."; }
        else { _service.Pause(); PauseButton.Content = "Fortsetzen"; StatusText.Text = "Aufnahme pausiert."; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { StatusText.Text = "Aufnahme wird abgeschlossen ...";
        DiagnosticLog.Info("RecordingStudio", "Aufnahme wird beendet | Modus=" + _recordingMode); _service.Stop(); }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,"Die laufende Aufnahme wirklich verwerfen?","Aufnahme verwerfen",MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _discardRequested = true;
        DiagnosticLog.Warning("RecordingStudio", "Aufnahme wird verworfen | Modus=" + _recordingMode); _service.Cancel(); StatusText.Text = "Aufnahme verworfen.";
    }

    private void Service_LevelChanged(object? sender, RecordingLevelEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LeftMeter.Value = e.Left; RightMeter.Value = e.Right;
            var clipped = e.Left >= .98f || e.Right >= .98f;
            ClipText.Text = clipped ? "ÃœBERSTEUERUNG" : $"L {ToDb(e.Left),6:0.0} dB   R {ToDb(e.Right),6:0.0} dB";
            ClipText.Foreground = clipped ? System.Windows.Media.Brushes.OrangeRed : System.Windows.Media.Brushes.LightGreen;
        });
    }

    private void Service_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _timer.Stop(); SetRecordingUi(false); LeftMeter.Value = RightMeter.Value = 0; PauseButton.Content = "Pause";
            if (e.Exception is not null) { StatusText.Text = "Aufnahmefehler: " + e.Exception.Message; return; }
            var path = _service.OutputPath;
            if (!_discardRequested && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                StatusText.Text = "Aufnahme gespeichert: " + Path.GetFileName(path);
                RecordingSaved?.Invoke(this,path);
                    DiagnosticLog.Info("RecordingStudio", "Aufnahme gespeichert | Datei=" + path);
                    // PHASE6B_MANUAL_MP3
                    _ = FinalizeSelectedFormatAsync(path);
            }
            UpdateTargetPreview();
        });
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButton.IsEnabled = !recording; DeviceBox.IsEnabled = !recording;
        QualityPanel.IsEnabled = !recording;
        PauseButton.IsEnabled = recording; StopButton.IsEnabled = recording; CancelButton.IsEnabled = recording;
    }

    private string CreateOutputPath()
    {
        Directory.CreateDirectory(_outputFolder);
        var baseName = _filePrefix + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(_outputFolder,baseName + ".wav");
        var n = 2; while(File.Exists(path)) path = Path.Combine(_outputFolder,$"{baseName}-{n++}.wav");
        return path;
    }
    private void UpdateTargetPreview() => TargetText.Text = Path.Combine(_outputFolder,_filePrefix + "-DATUM_UHRZEIT.wav");
    private static double ToDb(float value) => value <= .000001f ? -96 : 20 * Math.Log10(value);

    private void RecordingStudioWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_service.IsRecording) { _service.Dispose(); return; }
        if (System.Windows.MessageBox.Show(this,"Die laufende Aufnahme beenden und speichern?","Aufnahmestudio schlieÃŸen",MessageBoxButton.YesNo,MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        _service.Stop();
    }

    public void ConfigureMode(string mode)
    {
        _recordingMode = mode;
        DiagnosticLog.Info("RecordingStudio", "Modus: " + mode);
        Title = "SonicForge Aufnahmestudio - " + mode;
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (mode.Contains("Streaming", StringComparison.OrdinalIgnoreCase))
        {
            _outputFolder = Path.Combine(music, "SonicForge", "Streaming");
            _filePrefix = "SonicForge-Streaming";
        }
        else if (mode.Contains("Schallplatte", StringComparison.OrdinalIgnoreCase) || mode.Contains("Kassette", StringComparison.OrdinalIgnoreCase))
        {
            _outputFolder = Path.Combine(music, "SonicForge", "Schallplatten-und-Kassetten");
            _filePrefix = "SonicForge-Analog";
        }
        else if (mode.Contains("Mikrofon", StringComparison.OrdinalIgnoreCase) || mode.Contains("Instrument", StringComparison.OrdinalIgnoreCase))
        {
            _outputFolder = Path.Combine(music, "SonicForge", "Musikeraufnahmen");
            _filePrefix = "SonicForge-Musikaufnahme";
        }
        OutputFolderBox.Text = _outputFolder;
        UpdateTargetPreview();
        Loaded += (_, _) => ApplyRecordingMode();
    }

    private void ApplyRecordingMode()
    {
        var devices = DeviceBox.Items.Cast<RecordingDevice>().ToList();
        RecordingDevice? preferred;
        if (_recordingMode.Contains("Streaming", StringComparison.OrdinalIgnoreCase))
        {
            preferred = devices.FirstOrDefault(d => d.IsLoopback);
            StatusText.Text = preferred is null
                ? "Keine Systemton-Quelle erkannt. Bitte eine Loopback-Quelle waehlen."
                : "Streamingmodus: Systemton ist vorbereitet. Automatikmodus kann Titel nach Stille trennen.";
        }
        else
        {
            preferred = devices.FirstOrDefault(d => !d.IsLoopback);
            StatusText.Text = _recordingMode.Contains("Schallplatte", StringComparison.OrdinalIgnoreCase)
                ? "Analogmodus: Line-In oder Audiointerface waehlen und den Pegel vor der Aufnahme pruefen."
                : "Musikermodus: Mikrofon oder Audiointerface waehlen und Uebersteuerung vermeiden.";
        }
        if (preferred is not null) DeviceBox.SelectedItem = preferred;
        OutputFolderBox.Text = _outputFolder;
        UpdateTargetPreview();
    }
    private void ApplyQualityDefaults()
    {
        if (FormatBox is null || QualityBox is null || ChannelBox is null) return;
        if (_recordingMode.Contains("Streaming", StringComparison.OrdinalIgnoreCase))
        {
            FormatBox.SelectedIndex = 1;
            QualityBox.SelectedIndex = 1;
            ChannelBox.SelectedIndex = 0;
        }
        else if (_recordingMode.Contains("Schallplatte", StringComparison.OrdinalIgnoreCase) || _recordingMode.Contains("Kassette", StringComparison.OrdinalIgnoreCase))
        {
            FormatBox.SelectedIndex = 2;
            QualityBox.SelectedIndex = 2;
            ChannelBox.SelectedIndex = 2;
        }
        else
        {
            FormatBox.SelectedIndex = 0;
            QualityBox.SelectedIndex = 2;
            ChannelBox.SelectedIndex = 0;
        }
        UpdateQualitySummary();
    }

    private void QualitySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateQualitySummary();
    }

    private void UpdateQualitySummary()
    {
        if (FormatBox?.SelectedItem is not System.Windows.Controls.ComboBoxItem formatItem ||
            QualityBox?.SelectedItem is not System.Windows.Controls.ComboBoxItem qualityItem ||
            ChannelBox?.SelectedItem is not System.Windows.Controls.ComboBoxItem channelItem ||
            QualitySummaryText is null) return;
        var format = formatItem.Content?.ToString() ?? "WAV";
        var quality = qualityItem.Content?.ToString() ?? "Standard";
        var channels = channelItem.Content?.ToString() ?? "Quelle unveraendert";
        var detail = quality switch
        {
            "Archiv" => "WAV 48 kHz / 24 Bit, MP3 320 kbit/s",
            "Hoch" => "WAV 44,1 kHz / 16 Bit, MP3 256 kbit/s",
            _ => "WAV 44,1 kHz / 16 Bit, MP3 192 kbit/s"
        };
        QualitySummaryText.Text = $"Auswahl: {format} | {quality} | {channels} | {detail}. Phase 6a nimmt weiterhin unveraendert als WAV auf.";
    }
    private string SelectedOutputFormat() =>
        (FormatBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "WAV";

    private int SelectedMp3BitRate() =>
        ((QualityBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()) switch
        {
            "Archiv" => 320,
            "Hoch" => 256,
            _ => 192
        };

    private async Task FinalizeSelectedFormatAsync(string wavPath)
    {
        var format = SelectedOutputFormat();
        // PHASE6C1_CHANNEL_CONVERSION
        var channelMode = (ChannelBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Quelle unver\u00E4ndert";
        try
        {
            if (channelMode != "Quelle unver\u00E4ndert")
            {
                StatusText.Text = "KanÃ¤le werden umgewandelt: " + channelMode + " ...";
                QualityPanel.IsEnabled = false;
                RecordButton.IsEnabled = false;
                DiagnosticLog.Info("RecordingStudio", $"Kanalumwandlung gestartet | WAV={wavPath} | Modus={channelMode}");
                await ManualChannelConversionService.ApplyAsync(wavPath, channelMode);
                DiagnosticLog.Info("RecordingStudio", $"Kanalumwandlung abgeschlossen | WAV={wavPath} | Modus={channelMode}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("RecordingStudio.Channel", ex, "Kanalumwandlung fehlgeschlagen; Original-WAV bleibt erhalten");
            StatusText.Text = "Kanalfehler. WAV bleibt erhalten: " + ex.Message;
            System.Windows.MessageBox.Show(this,
                "Die Kanalumwandlung ist fehlgeschlagen. Die ursprÃ¼ngliche WAV-Aufnahme bleibt erhalten.\n\n" + ex.Message,
                "SonicForge Kanalumwandlung", MessageBoxButton.OK, MessageBoxImage.Warning);
            QualityPanel.IsEnabled = true;
            RecordButton.IsEnabled = true;
            return;
        }
        // PHASE6C2_QUALITY_CONVERSION
        var qualityProfile = (QualityBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Standard";
        try
        {
            var qualityTarget = ManualQualityConversionService.Profile(qualityProfile);
            StatusText.Text = $"Audioformat wird erzeugt: {qualityTarget.SampleRate} Hz / {qualityTarget.Bits} Bit ...";
            QualityPanel.IsEnabled = false;
            RecordButton.IsEnabled = false;
            DiagnosticLog.Info("RecordingStudio", $"QualitÃ¤tsumwandlung gestartet | WAV={wavPath} | Profil={qualityProfile} | Hz={qualityTarget.SampleRate} | Bit={qualityTarget.Bits}");
            await ManualQualityConversionService.ApplyAsync(wavPath, qualityProfile);
            DiagnosticLog.Info("RecordingStudio", $"QualitÃ¤tsumwandlung abgeschlossen | WAV={wavPath} | Profil={qualityProfile}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("RecordingStudio.Quality", ex, "QualitÃ¤tsumwandlung fehlgeschlagen; vorherige WAV bleibt erhalten");
            StatusText.Text = "QualitÃ¤tsfehler. WAV bleibt erhalten: " + ex.Message;
            System.Windows.MessageBox.Show(this,
                "Abtastrate oder Bittiefe konnten nicht umgesetzt werden. Die vorherige WAV-Datei bleibt erhalten.\n\n" + ex.Message,
                "SonicForge AudioqualitÃ¤t", MessageBoxButton.OK, MessageBoxImage.Warning);
            QualityPanel.IsEnabled = true;
            RecordButton.IsEnabled = true;
            return;
        }
        if (format == "WAV")
        {
            StatusText.Text = "WAV gespeichert (" + channelMode + "): " + Path.GetFileName(wavPath);
            TargetText.Text = wavPath;
            RefreshCompletionActions();
            QualityPanel.IsEnabled = true;
            RecordButton.IsEnabled = true;
            return;
        }
        var bitRate = SelectedMp3BitRate();
        try
        {
            StatusText.Text = $"MP3 mit {bitRate} kbit/s wird erzeugt ...";
            QualityPanel.IsEnabled = false;
            RecordButton.IsEnabled = false;
            DiagnosticLog.Info("RecordingStudio", $"MP3-Konvertierung gestartet | WAV={wavPath} | Bitrate={bitRate}");
            var mp3Path = await ManualMp3ConversionService.ConvertAsync(wavPath, bitRate);
            DiagnosticLog.Info("RecordingStudio", "MP3 gespeichert | Datei=" + mp3Path);
            if (format == "MP3")
            {
                try
                {
                    File.Delete(wavPath);
                    DiagnosticLog.Info("RecordingStudio", "WAV nach erfolgreicher MP3-Erzeugung geloescht | Datei=" + wavPath);
                }
                catch (Exception deleteEx)
                {
                    DiagnosticLog.Warning("RecordingStudio", "MP3 ist fertig, WAV konnte nicht geloescht werden | " + deleteEx.Message);
                }
            }
            StatusText.Text = format == "MP3"
                ? "MP3 gespeichert: " + Path.GetFileName(mp3Path)
                : "WAV + MP3 gespeichert: " + Path.GetFileName(mp3Path);
            TargetText.Text = mp3Path;
            RefreshCompletionActions();
            RecordingSaved?.Invoke(this, mp3Path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("RecordingStudio.MP3", ex, "MP3-Konvertierung fehlgeschlagen; WAV bleibt erhalten");
            StatusText.Text = "MP3-Fehler. WAV bleibt erhalten: " + ex.Message;
            System.Windows.MessageBox.Show(this,
                "Die MP3-Datei konnte nicht erzeugt werden. Die WAV-Aufnahme bleibt sicher erhalten.\n\n" + ex.Message,
                "SonicForge MP3-Erzeugung", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            QualityPanel.IsEnabled = true;
            RecordButton.IsEnabled = true;
        }
    }
    // PHASE6D1_COMPLETION_ACTIONS
    // PHASE6D1_FUNCTION_FIX
    private string? LastSavedRecordingPath()
    {
        var displayed = TargetText?.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(displayed))
        {
            if (File.Exists(displayed)) return displayed;
            var pathMatch = System.Text.RegularExpressions.Regex.Match(displayed, @"[A-Za-z]:\\[^\r\n]+?\.(?:mp3|wav)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (pathMatch.Success && File.Exists(pathMatch.Value)) return pathMatch.Value;
        }

        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var folders = new[]
        {
            Path.Combine(music, "SonicForge Aufnahmen"),
            Path.Combine(music, "SonicForge-Aufnahmen"),
            music
        };
        return folders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    private void RefreshCompletionActions()
    {
        var available = LastSavedRecordingPath() is not null;
        if (PlaySavedButton is not null) PlaySavedButton.IsEnabled = available;
        if (OpenSavedFolderButton is not null) OpenSavedFolderButton.IsEnabled = available;
    }
    // PHASE6D2_COMPLETION_SUMMARY
    private void UpdateCompletionSummary()
    {
        var path = LastSavedRecordingPath();
        if (path is null)
        {
            CompletionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var info = new FileInfo(path);
        var extension = info.Extension.TrimStart('.').ToUpperInvariant();
        var profile = (QualityBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Archiv";
        var channelMode = (ChannelBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Quelle unver\u00E4ndert";
        var fileName = info.Name;
        var sizeText = info.Length >= 1024L * 1024L
            ? (info.Length / (1024d * 1024d)).ToString("0.00") + " MB"
            : (info.Length / 1024d).ToString("0") + " KB";
        var profileText = profile;
        var audioText = channelMode;

        if (extension == "MP3")
        {
            var bitRate = profile switch { "Archiv" => 320, "Hoch" => 256, _ => 192 };
            profileText = profile + " | " + bitRate + " kbit/s";
            audioText = profile == "Standard" ? "44,1 kHz | Stereo" : "48 kHz | Stereo";
        }
        else if (extension == "WAV")
        {
            try
            {
                using var reader = new NAudio.Wave.WaveFileReader(path);
                var wf = reader.WaveFormat;
                var channels = wf.Channels == 1 ? "Mono" : wf.Channels == 2 ? "Stereo" : wf.Channels + " KanÃ¤le";
                audioText = (wf.SampleRate / 1000d).ToString("0.#") + " kHz | " + wf.BitsPerSample + " Bit | " + channels;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warning("RecordingStudio.Summary", "WAV-Details konnten nicht gelesen werden | " + ex.Message);
            }
        }

        CompletionFileText.Text = "Datei: " + fileName;
        CompletionFileText.ToolTip = path;
        CompletionFormatText.Text = "Format: " + extension;
        CompletionProfileText.Text = "Profil: " + profileText;
        CompletionAudioText.Text = "Audio: " + audioText;
        CompletionSizeText.Text = "Gr\u00F6\u00DFe: " + sizeText;
        CompletionPanel.Visibility = Visibility.Visible;
    }
    // PHASE6D2_ACTIVE_REFRESH
    private System.Windows.Threading.DispatcherTimer? _completionRefreshTimer;
    private string? _completionShownPath;

    private void CompletionPanel_Loaded(object sender, RoutedEventArgs e)
    {
        CompletionPanel.Visibility = Visibility.Visible;
        CompletionFileText.Text = "Datei: Suche nach letzter Aufnahme ...";
        CompletionFormatText.Text = "Format: wird ermittelt";
        CompletionProfileText.Text = "Profil: wird ermittelt";
        CompletionAudioText.Text = "Audio: wird ermittelt";
        CompletionSizeText.Text = "GrÃ¶ÃŸe: wird ermittelt";
        RefreshCompletionSummarySafe();

        _completionRefreshTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _completionRefreshTimer.Tick -= CompletionRefreshTimer_Tick;
        _completionRefreshTimer.Tick += CompletionRefreshTimer_Tick;
        _completionRefreshTimer.Start();
    }

    private void CompletionRefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshCompletionSummarySafe();
    }

    private void RefreshCompletionSummarySafe()
    {
        ApplyPendingRecordingMetadata();
        try
        {
            var path = LastSavedRecordingPath();
            if (path is null)
            {
                CompletionPanel.Visibility = Visibility.Visible;
                CompletionFileText.Text = "Datei: Noch keine gespeicherte Aufnahme gefunden";
                CompletionFormatText.Text = "Format: -";
                CompletionProfileText.Text = "Profil: -";
                CompletionAudioText.Text = "Audio: -";
                CompletionSizeText.Text = "GrÃ¶ÃŸe: -";
                return;
            }

            var stamp = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length;
            if (_completionShownPath == stamp && CompletionPanel.Visibility == Visibility.Visible) return;
            _completionShownPath = stamp;
            UpdateCompletionSummary();
            CompletionPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            CompletionPanel.Visibility = Visibility.Visible;
            CompletionFileText.Text = "Datei: Abschlussdaten konnten nicht gelesen werden";
            CompletionFormatText.Text = "Format: -";
            CompletionProfileText.Text = "Profil: -";
            CompletionAudioText.Text = "Audio: -";
            CompletionSizeText.Text = "GrÃ¶ÃŸe: -";
            DiagnosticLog.Warning("RecordingStudio.Summary", "Abschlussanzeige konnte nicht aktualisiert werden | " + ex.Message);
        }
    }
    private void PlaySavedButton_Click(object sender, RoutedEventArgs e)
    {
        var path = LastSavedRecordingPath();
        if (path is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            DiagnosticLog.Info("RecordingStudio", "Gespeicherte Aufnahme geÃ¶ffnet | " + path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("RecordingStudio.Play", ex, "Gespeicherte Aufnahme konnte nicht geÃ¶ffnet werden");
            System.Windows.MessageBox.Show(this, "Die Aufnahme konnte nicht geÃ¶ffnet werden.\n\n" + ex.Message,
                "SonicForge Abspielen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSavedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = LastSavedRecordingPath();
        if (path is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
            DiagnosticLog.Info("RecordingStudio", "Aufnahmeordner geÃ¶ffnet | " + path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("RecordingStudio.Folder", ex, "Aufnahmeordner konnte nicht geÃ¶ffnet werden");
            System.Windows.MessageBox.Show(this, "Der Aufnahmeordner konnte nicht geÃ¶ffnet werden.\n\n" + ex.Message,
                "SonicForge Aufnahmeordner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    // PHASE6D3A_METADATA
    private DateTime _metadataRecordingStartedUtc = DateTime.MinValue;
    private bool _metadataAppliedForCurrentRecording;

    private void CaptureMetadataForNewRecording()
    {
        _metadataRecordingStartedUtc = DateTime.UtcNow;
        _metadataAppliedForCurrentRecording = false;
        if (CompletionPanel is not null) CompletionPanel.Visibility = Visibility.Collapsed;
    }

    private void ApplyPendingRecordingMetadata()
    {
        if (_metadataAppliedForCurrentRecording || _metadataRecordingStartedUtc == DateTime.MinValue) return;

        var current = LastSavedRecordingPath();
        if (current is null || !File.Exists(current)) return;
        if (File.GetLastWriteTimeUtc(current) < _metadataRecordingStartedUtc) return;

        var folder = Path.GetDirectoryName(current);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        var candidates = Directory.GetFiles(folder)
            .Where(path => (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                            path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) &&
                           File.GetLastWriteTimeUtc(path) >= _metadataRecordingStartedUtc)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
            .ToList();
        if (candidates.Count == 0) return;

        var newest = candidates[0];
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) < TimeSpan.FromSeconds(4)) return;

        var preferred = candidates.FirstOrDefault(path => path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        ?? newest;
        try
        {
            var renamed = RecordingMetadataService.RenameWithPartner(preferred, CustomFileNameBox?.Text);
            var coverEmbedded = RecordingMetadataService.WriteMp3Tags(renamed, TitleMetadataBox?.Text, ArtistMetadataBox?.Text,
                AlbumMetadataBox?.Text, YearMetadataBox?.Text, GenreMetadataBox?.Text, _selectedCoverPath);
            if (coverEmbedded)
                CoverStatusText.Text = Path.GetFileName(_selectedCoverPath) + " | in MP3 eingebettet";
            else if (!string.IsNullOrWhiteSpace(_selectedCoverPath))
                CoverStatusText.Text = Path.GetFileName(_selectedCoverPath) + " | Cover konnte nicht bestaetigt werden";
            TargetText.Text = renamed;
            // PHASE6D3C2A_FINAL_STATUS
            var finalExtension = Path.GetExtension(renamed).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? "MP3" : "WAV";
            StatusText.Text = finalExtension + " gespeichert: " + Path.GetFileName(renamed);
            _metadataAppliedForCurrentRecording = true;
            _completionShownPath = null;
            UpdateCompletionSummary();
            DiagnosticLog.Info("RecordingStudio.Metadata", "Dateiname und Metadaten angewendet | " + renamed);
            // PHASE6D3C3_POST_RECORDING_METADATA
            if (KeepMetadataAfterRecordingCheckBox?.IsChecked != true)
            {
                ClearMetadataAfterSuccessfulRecording();
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (Exception ex)
        {
            _metadataAppliedForCurrentRecording = true;
            DiagnosticLog.Error("RecordingStudio.Metadata", ex, "Dateiname oder Metadaten konnten nicht angewendet werden");
            StatusText.Text = "Aufnahme gespeichert; Dateiname oder Metadaten konnten nicht angewendet werden: " + ex.Message;
        }
    }

    // PHASE6D3B1_COVER_SEARCH
    private string? _selectedCoverPath;

    private void ShowSelectedCover(string path)
    {
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        CoverPreviewImage.Source = bitmap;
        CoverStatusText.Text = Path.GetFileName(path);
    }

    private void SearchCoverButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CoverSearchWindow(ArtistMetadataBox.Text, AlbumMetadataBox.Text, TitleMetadataBox.Text) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.SelectedCoverPath))
        { _selectedCoverPath = dialog.SelectedCoverPath; ShowSelectedCover(_selectedCoverPath); }
    }

    private void SelectLocalCoverButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Cover auswÃ¤hlen", Filter = "Bilder|*.jpg;*.jpeg;*.png|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) == true) { _selectedCoverPath = dialog.FileName; ShowSelectedCover(_selectedCoverPath); }
    }

    private void RemoveCoverButton_Click(object sender, RoutedEventArgs e)
    { _selectedCoverPath = null; CoverPreviewImage.Source = null; CoverStatusText.Text = "Kein Cover ausgew\u00E4hlt"; }
    // PHASE6D3B2_COVER_EMBED
    private void EmbedSelectedCoverInMp3(string mp3Path)
    {
        if (string.IsNullOrWhiteSpace(_selectedCoverPath) || !File.Exists(_selectedCoverPath)) return;
        if (!mp3Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !File.Exists(mp3Path)) return;

        var tagFileType = Type.GetType("TagLib.File, TagLibSharp", throwOnError: false)
                          ?? Type.GetType("TagLib.File, taglib-sharp", throwOnError: false);
        var byteVectorType = Type.GetType("TagLib.ByteVector, TagLibSharp", throwOnError: false)
                             ?? Type.GetType("TagLib.ByteVector, taglib-sharp", throwOnError: false);
        var pictureType = Type.GetType("TagLib.Picture, TagLibSharp", throwOnError: false)
                          ?? Type.GetType("TagLib.Picture, taglib-sharp", throwOnError: false);
        var iPictureType = Type.GetType("TagLib.IPicture, TagLibSharp", throwOnError: false)
                           ?? Type.GetType("TagLib.IPicture, taglib-sharp", throwOnError: false);
        var pictureKindType = Type.GetType("TagLib.PictureType, TagLibSharp", throwOnError: false)
                              ?? Type.GetType("TagLib.PictureType, taglib-sharp", throwOnError: false);
        if (tagFileType is null || byteVectorType is null || pictureType is null || iPictureType is null || pictureKindType is null)
            throw new InvalidOperationException("TagLibSharp fÃ¼r die Covereinbettung wurde nicht gefunden.");

        var create = tagFileType.GetMethod("Create", new[] { typeof(string) })
                     ?? throw new MissingMethodException("TagLib.File.Create wurde nicht gefunden.");
        var tagFile = create.Invoke(null, new object[] { mp3Path })
                      ?? throw new InvalidOperationException("MP3-Datei konnte nicht fÃ¼r Cover geÃ¶ffnet werden.");
        try
        {
            var bytes = File.ReadAllBytes(_selectedCoverPath);
            var vector = Activator.CreateInstance(byteVectorType, new object[] { bytes })
                         ?? throw new InvalidOperationException("Coverdaten konnten nicht vorbereitet werden.");
            var picture = Activator.CreateInstance(pictureType, new[] { vector })
                          ?? throw new InvalidOperationException("Coverbild konnte nicht erzeugt werden.");
            pictureType.GetProperty("Type")?.SetValue(picture, Enum.Parse(pictureKindType, "FrontCover"));
            pictureType.GetProperty("MimeType")?.SetValue(picture,
                Path.GetExtension(_selectedCoverPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            pictureType.GetProperty("Description")?.SetValue(picture, "Front Cover");

            var tag = tagFileType.GetProperty("Tag")?.GetValue(tagFile)
                      ?? throw new InvalidOperationException("MP3-Tag konnte nicht geÃ¶ffnet werden.");
            var pictures = Array.CreateInstance(iPictureType, 1);
            pictures.SetValue(picture, 0);
            tag.GetType().GetProperty("Pictures")?.SetValue(tag, pictures);
            tagFileType.GetMethod("Save")?.Invoke(tagFile, null);
            CoverStatusText.Text = Path.GetFileName(_selectedCoverPath) + " | in MP3 eingebettet";
        }
        finally
        {
            if (tagFile is IDisposable disposable) disposable.Dispose();
        }
    }
    // PHASE6D3C1_METADATA_COMFORT
    private static string CleanFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty).Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        while (cleaned.Contains("  ", StringComparison.Ordinal)) cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        return cleaned.Trim(' ', '.');
    }

    private void BuildFileNameButton_Click(object sender, RoutedEventArgs e)
    {
        var artist = CleanFileNamePart(ArtistMetadataBox.Text);
        var title = CleanFileNamePart(TitleMetadataBox.Text);
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title))
        {
            System.Windows.MessageBox.Show(this, "Bitte zuerst Interpret oder Titel eintragen.", "Dateiname bilden", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        CustomFileNameBox.Text = string.IsNullOrWhiteSpace(artist) ? title : string.IsNullOrWhiteSpace(title) ? artist : artist + " - " + title;
        CustomFileNameBox.Focus();
        CustomFileNameBox.CaretIndex = CustomFileNameBox.Text.Length;
    }

    private void ClearMetadataButton_Click(object sender, RoutedEventArgs e)
    {
        CustomFileNameBox.Clear();
        TitleMetadataBox.Clear();
        ArtistMetadataBox.Clear();
        AlbumMetadataBox.Clear();
        YearMetadataBox.Clear();
        GenreMetadataBox.Clear();
        _selectedCoverPath = null;
        CoverPreviewImage.Source = null;
        CoverStatusText.Text = "Kein Cover ausgew\u00E4hlt";
        CustomFileNameBox.Focus();
    }
    // PHASE6D3C2_FILENAME_PROTECTION
    private bool CustomFileNameExists(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return false;
        return File.Exists(Path.Combine(_outputFolder, stem + ".mp3")) ||
               File.Exists(Path.Combine(_outputFolder, stem + ".wav"));
    }

    private string NextAvailableCustomFileName(string requested)
    {
        var stem = Path.GetFileNameWithoutExtension((requested ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(stem)) return string.Empty;
        if (!CustomFileNameExists(stem)) return stem;
        var number = 2;
        string candidate;
        do candidate = stem + " (" + number++ + ")";
        while (CustomFileNameExists(candidate));
        return candidate;
    }

    private bool ConfirmAvailableCustomFileName()
    {
        var requested = Path.GetFileNameWithoutExtension((CustomFileNameBox?.Text ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(requested) || !CustomFileNameExists(requested)) return true;

        var alternative = NextAvailableCustomFileName(requested);
        var result = System.Windows.MessageBox.Show(this,
            "Eine Datei mit diesem Namen ist bereits vorhanden:\n\n" + requested +
            "\n\nSonicForge kann stattdessen diesen freien Namen verwenden:\n\n" + alternative +
            "\n\nFreien Namen uebernehmen und Aufnahme starten?",
            "Dateiname bereits vorhanden",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            CustomFileNameBox?.Focus();
            return false;
        }
        if (CustomFileNameBox is not null)
        {
            CustomFileNameBox.Text = alternative;
            CustomFileNameBox.CaretIndex = alternative.Length;
        }
        return true;
    }
    private void ClearMetadataAfterSuccessfulRecording()
    {
        CustomFileNameBox?.Clear();
        TitleMetadataBox?.Clear();
        ArtistMetadataBox?.Clear();
        AlbumMetadataBox?.Clear();
        YearMetadataBox?.Clear();
        GenreMetadataBox?.Clear();
        _selectedCoverPath = null;
        if (CoverPreviewImage is not null) CoverPreviewImage.Source = null;
        if (CoverStatusText is not null) CoverStatusText.Text = "Kein Cover ausgew\u00E4hlt";
    }}
