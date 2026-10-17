using NAudio.Wave;
using NAudio.Effects;
using SonicForge.Core.Models;
using SonicForge.Library;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace SonicForge.App;

public partial class MainWindow : Window
{
    private readonly TrackScanner _scanner = new();
    private readonly SoundLabSettings _soundLab = SoundLabSettings.Load();
    private Equalizer? _activeEqualizer;
    private SoundLabWindow? _soundLabWindow;
    private CdImportWindow? _cdImportWindow;
    private RecordingStudioWindow? _recordingWindow;
    private readonly LibraryRepository _libraryRepository = new();
    private readonly DispatcherTimer _playerTimer;
    private WaveOut? _outputDevice;
    private AudioFileReader? _audioFile;
    private AudioTrack? _currentTrack;
    private bool _isUpdatingProgress;

    public ObservableCollection<AudioTrack> Tracks { get; } = new();
    public ICollectionView TracksView { get; }

    public MainWindow()
    {
        DiagnosticLog.Initialize();
        DiagnosticLog.Info("MainWindow", "Hauptfenster wird erstellt");
        Loaded += ShowSonicForgeStartCenter;
        TracksView = CollectionViewSource.GetDefaultView(Tracks);
        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playerTimer.Tick += PlayerTimer_Tick;
        InitializeComponent();
          HookRadioPodcastAccess();
        DataContext = this;
        Closed += MainWindow_Closed;
        LoadPersistentLibrary();
        ConfigureLibraryContextMenu();
        _soundLab.Changed += (_, _) => ApplySoundLabToActivePlayback();
        Loaded += (_, _) => HookGlobalSoundLabAccess();
        Loaded += (_, _) => HookCdImportAccess();
        Loaded += (_, _) => HookRecordingAccess();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Audio-Dateien|*.mp3;*.flac;*.wav;*.aac;*.m4a;*.ogg;*.wma|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog() == true) AddTracks(dialog.FileNames);
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Audio-Ordner auswählen",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            AddTracks(Directory.EnumerateFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories));
    }

    private void AddTracks(IEnumerable<string> files)
    {
        var incomingTracks = _scanner.ScanFiles(files);
        var knownPaths = Tracks.Select(track => track.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        foreach (var track in incomingTracks)
        {
            if (!knownPaths.Add(track.FilePath)) continue;
            Tracks.Add(track);
            addedCount++;
        }
        _libraryRepository.UpsertMany(Tracks);
        StatusText.Text = $"{incomingTracks.Count} Datei(en) geprüft, {addedCount} neu, {Tracks.Count} Titel dauerhaft gespeichert.";
        if (TracksGrid.SelectedItem is null && Tracks.Count > 0) TracksGrid.SelectedIndex = 0;
    }

    private void LoadPersistentLibrary()
    {
        try
        {
            _libraryRepository.Initialize();
            var loaded = _libraryRepository.LoadAllExisting();

            // Cover-Fix: Persistierte Einträge können veraltete oder leere CoverData enthalten,
            // obwohl das Cover inzwischen korrekt in der Audiodatei eingebettet wurde.
            // Deshalb beim Start jede vorhandene Datei erneut scannen und den aktuellen
            // Tag-/Cover-Stand übernehmen. Falls eine Datei nicht gelesen werden kann,
            // bleibt der gespeicherte Bibliothekseintrag erhalten.
            var refreshedTracks = new List<AudioTrack>(loaded.Count);
            foreach (var storedTrack in loaded)
            {
                var refreshed = _scanner.ScanFiles(new[] { storedTrack.FilePath }).FirstOrDefault();
                refreshedTracks.Add(refreshed ?? storedTrack);
            }

            _libraryRepository.UpsertMany(refreshedTracks);
            foreach (var track in refreshedTracks) Tracks.Add(track);

            if (Tracks.Count > 0) TracksGrid.SelectedIndex = 0;
            StatusText.Text = Tracks.Count == 0
                ? "Bereit. Audio-Dateien können importiert werden."
                : $"Dauerhafte Bibliothek aktualisiert: {Tracks.Count} Titel, Metadaten und Cover neu eingelesen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Bibliothek konnte nicht geladen werden: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Die lokale SonicForge-Bibliothek konnte nicht geöffnet werden.\n\n{ex.Message}",
                "SonicForge Bibliotheksfehler", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ConfigureLibraryContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(CreateMenuItem("Abspielen", (_, _) => PlaySelectedTrack()));
        menu.Items.Add(CreateMenuItem("SoundLab Equalizer", (_, _) => OpenSoundLab()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(CreateMenuItem("Im Explorer anzeigen", (_, _) => ShowSelectedInExplorer()));
        menu.Items.Add(CreateMenuItem("Dateipfad kopieren", (_, _) => CopySelectedPath()));
        menu.Items.Add(CreateMenuItem("Metadaten neu einlesen", (_, _) => RefreshSelectedMetadata()));
        menu.Items.Add(CreateMenuItem("Metadaten und Cover bearbeiten", (_, _) => EditSelectedMetadata()));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(CreateMenuItem("Alle sichtbaren Titel auswählen", (_, _) => TracksGrid.SelectAll()));
        menu.Items.Add(CreateMenuItem("Ausgewählte Titel aus Bibliothek entfernen", (_, _) => RemoveSelectedFromLibrary()));
        TracksGrid.ContextMenu = menu;
        TracksGrid.ContextMenuOpening += (_, e) =>
        {
            if (TracksGrid.Items.Count == 0) e.Handled = true;
        };
    }

    private static System.Windows.Controls.MenuItem CreateMenuItem(
        string header, RoutedEventHandler handler)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private AudioTrack? SelectedTrack => TracksGrid.SelectedItem as AudioTrack;

    private void OpenSoundLab()
    {
        if (_soundLabWindow is { IsLoaded: true })
        {
            _soundLabWindow.Activate();
            if (_soundLabWindow.WindowState == WindowState.Minimized)
                _soundLabWindow.WindowState = WindowState.Normal;
            return;
        }

        _soundLabWindow = new SoundLabWindow(_soundLab) { Owner = this };
        _soundLabWindow.Closed += (_, _) => _soundLabWindow = null;
        _soundLabWindow.Show();
    }

    private void HookGlobalSoundLabAccess()
    {
        var button = FindButtonByText(this, "Audio-Werkstatt");
        if (button is not null)
        {
            button.Click -= GlobalSoundLab_Click;
            button.Click += GlobalSoundLab_Click;
            button.ToolTip = "Globalen Equalizer fÃ¼r alle Titel Ã¶ffnen";
        }
    }

    private void GlobalSoundLab_Click(object sender, RoutedEventArgs e) => OpenSoundLab();
    private void HookRecordingAccess()
    {
        var button = FindButtonByText(this, "Aufnehmen");
        if (button is null) return;
        button.Click -= Recording_Click;
        button.Click += Recording_Click;
        button.ToolTip = "SonicForge Aufnahmestudio Ã¶ffnen";
    }
    private void Recording_Click(object sender, RoutedEventArgs e) => OpenRecordingStudio();
    private void OpenRecordingStudio()
    {
        if (_recordingWindow is { IsLoaded: true }) { _recordingWindow.Activate(); return; }
        _recordingWindow = new RecordingStudioWindow { Owner = this };
        _recordingWindow.RecordingSaved += (_, path) => AddTracks(new[] { path });
        _recordingWindow.Closed += (_, _) => _recordingWindow = null;
        _recordingWindow.Show();
    }
    private void HookCdImportAccess()
    {
        var button = FindButtonByText(this, "CD-Import");
        if (button is not null)
        {
            button.Click -= CdImport_Click;
            button.Click += CdImport_Click;
            button.ToolTip = "Audio-CD erkennen und Import vorbereiten";
        }
    }

    private void CdImport_Click(object sender, RoutedEventArgs e) => OpenCdImportstudio();

    private void OpenCdImportstudio()
    {
        if (_cdImportWindow is { IsLoaded: true })
        {
            _cdImportWindow.Activate();
            return;
        }
        _cdImportWindow = new CdImportWindow { Owner = this };
        _cdImportWindow.Closed += (_, _) => _cdImportWindow = null;
        _cdImportWindow.Show();
    }

    private static System.Windows.Controls.Button? FindButtonByText(DependencyObject parent, string text)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.Button button &&
                button.Content?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                return button;
            var match = FindButtonByText(child, text);
            if (match is not null) return match;
        }
        return null;
    }

    private void ApplySoundLabToActivePlayback()
    {
        if (_activeEqualizer is not null)
        {
            for (var i = 0; i < _activeEqualizer.Bands.Count && i < _soundLab.Gains.Length; i++)
                _activeEqualizer.Bands[i].GainDb = _soundLab.Gains[i];
            _activeEqualizer.Bypass = !_soundLab.Enabled;
            _activeEqualizer.Update();
        }
        if (_audioFile is not null)
        {
            var headroom = MathF.Pow(10f, _soundLab.EffectivePreampDb / 20f);
            _audioFile.Volume = (float)VolumeSlider.Value * headroom;
        }
    }
    private void PlaySelectedTrack()
    {
        if (SelectedTrack is AudioTrack track) StartTrack(track);
    }

    private void ShowSelectedInExplorer()
    {
        var track = SelectedTrack;
        if (track is null) return;
        if (!File.Exists(track.FilePath))
        {
            StatusText.Text = "Datei wurde nicht gefunden.";
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{track.FilePath}\"",
            UseShellExecute = true
        });
        StatusText.Text = "Datei im Explorer markiert.";
    }

    private void CopySelectedPath()
    {
        var track = SelectedTrack;
        if (track is null) return;
        System.Windows.Clipboard.SetText(track.FilePath);
        StatusText.Text = "Dateipfad wurde kopiert.";
    }

    private void RefreshSelectedMetadata()
    {
        var oldTrack = SelectedTrack;
        if (oldTrack is null) return;
        if (!File.Exists(oldTrack.FilePath))
        {
            StatusText.Text = "Metadaten konnten nicht gelesen werden: Datei fehlt.";
            return;
        }
        var refreshed = _scanner.ScanFiles(new[] { oldTrack.FilePath }).FirstOrDefault();
        if (refreshed is null)
        {
            StatusText.Text = "Metadaten konnten nicht neu eingelesen werden.";
            return;
        }
        var index = Tracks.IndexOf(oldTrack);
        Tracks[index] = refreshed;
        _libraryRepository.UpsertMany(new[] { refreshed });
        TracksGrid.SelectedIndex = index;
        StatusText.Text = $"Metadaten aktualisiert: {refreshed.Title}";
    }

    private void EditSelectedMetadata()
    {
        var oldTrack = SelectedTrack;
        if (oldTrack is null) return;
        if (!File.Exists(oldTrack.FilePath))
        {
            StatusText.Text = "Tag-Editor konnte nicht geöffnet werden: Datei fehlt.";
            return;
        }

        if (ReferenceEquals(_currentTrack, oldTrack))
            StopPlayback(resetPosition: false);

        var editor = new TagEditorWindow(oldTrack) { Owner = this };
        if (editor.ShowDialog() != true) return;

        var refreshed = _scanner.ScanFiles(new[] { oldTrack.FilePath }).FirstOrDefault();
        if (refreshed is null)
        {
            StatusText.Text = "Gespeicherte Metadaten konnten nicht neu eingelesen werden.";
            return;
        }

        var index = Tracks.IndexOf(oldTrack);
        Tracks[index] = refreshed;
        _libraryRepository.UpsertMany(new[] { refreshed });
        TracksGrid.SelectedIndex = index;
        TracksGrid.ScrollIntoView(refreshed);
        StatusText.Text = $"Metadaten und Cover gespeichert: {refreshed.Title}";
    }

    private void RemoveSelectedFromLibrary()
    {
        var selected = TracksGrid.SelectedItems
            .OfType<AudioTrack>()
            .Distinct()
            .ToList();
        if (selected.Count == 0) return;

        var question = selected.Count == 1
            ? $"Soll der ausgewählte Titel nur aus der SonicForge-Bibliothek entfernt werden?\n\n{selected[0].Title}\n{selected[0].Artist}\n\nDie Musikdatei bleibt unverändert erhalten."
            : $"Möchten Sie alle {selected.Count} ausgewählten Titel aus der SonicForge-Bibliothek entfernen?\n\nDie Musikdateien bleiben unverändert erhalten.";

        var answer = System.Windows.MessageBox.Show(
            question,
            selected.Count == 1 ? "Aus Bibliothek entfernen" : "Ausgewählte Titel entfernen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        if (_currentTrack is not null && selected.Any(track => ReferenceEquals(_currentTrack, track) ||
            string.Equals(track.FilePath, _currentTrack.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            DisposePlayback();
            _currentTrack = null;
            CurrentTitleText.Text = "Noch kein Titel gewählt";
            CurrentAlbumText.Text = "";
            PlayerStatusText.Text = "SonicForge Player bereit";
            SetCover(null);
            ProgressSlider.Value = 0;
            PositionText.Text = "00:00";
            DurationText.Text = "00:00";
            PlayPauseButton.Content = "▶";
        }

        foreach (var track in selected)
        {
            _libraryRepository.Delete(track.FilePath);
            Tracks.Remove(track);
        }

        TracksGrid.UnselectAll();
        StatusText.Text = selected.Count == 1
            ? $"Aus Bibliothek entfernt: {selected[0].Title}. Datei blieb erhalten."
            : $"{selected.Count} Titel aus der Bibliothek entfernt. Dateien blieben erhalten.";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        TracksView.Filter = item => item is AudioTrack track &&
            (query.Length == 0 || track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             track.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             track.Format.Contains(query, StringComparison.OrdinalIgnoreCase));
        TracksView.Refresh();
    }

    private void TracksGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TracksGrid.SelectedItem is not AudioTrack track) return;
        CurrentTitleText.Text = track.Title;
        CurrentAlbumText.Text = track.Album;
        PlayerStatusText.Text = $"{track.Artist} · {track.Format} · {track.BitrateText}";
        DurationText.Text = track.DurationText;
        SetCover(track.CoverData);
    }

    private void TracksGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TracksGrid.SelectedItem is AudioTrack track) StartTrack(track);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_outputDevice is null || _audioFile is null)
        {
            if (TracksGrid.SelectedItem is AudioTrack selected) StartTrack(selected);
            else StatusText.Text = "Bitte zuerst einen Titel importieren und auswählen.";
            return;
        }

        if (_outputDevice.PlaybackState == PlaybackState.Playing)
        {
            _outputDevice.Pause();
            PlayPauseButton.Content = "▶";
            PlayerStatusText.Text = "Pausiert";
        }
        else
        {
            _outputDevice.Play();
            PlayPauseButton.Content = "Ⅱ";
            PlayerStatusText.Text = "Wiedergabe läuft";
            _playerTimer.Start();
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback(resetPosition: true);

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (Tracks.Count == 0) return;
        var index = TracksGrid.SelectedIndex;
        if (index <= 0) index = Tracks.Count;
        TracksGrid.SelectedIndex = index - 1;
        TracksGrid.ScrollIntoView(TracksGrid.SelectedItem);
        if (TracksGrid.SelectedItem is AudioTrack track) StartTrack(track);
    }

    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        if (Tracks.Count == 0) return;
        var index = TracksGrid.SelectedIndex;
        index = index < 0 || index >= Tracks.Count - 1 ? 0 : index + 1;
        TracksGrid.SelectedIndex = index;
        TracksGrid.ScrollIntoView(TracksGrid.SelectedItem);
        if (TracksGrid.SelectedItem is AudioTrack track) StartTrack(track);
    }

    private void StartTrack(AudioTrack track)
    {
        try
        {
            DisposePlayback();
            _audioFile = new AudioFileReader(track.FilePath) { Volume = (float)VolumeSlider.Value };
            _outputDevice = new WaveOut();
            _outputDevice.PlaybackStopped += OutputDevice_PlaybackStopped;
            var bands = SoundLabSettings.Frequencies
                .Select((frequency, index) => EqualizerBand.Peaking(frequency, 1.0f, _soundLab.Gains[index]))
                .ToArray();
            _activeEqualizer = new Equalizer(bands) { Bypass = !_soundLab.Enabled };
            var processedAudio = new EffectSampleProvider(_audioFile, _activeEqualizer);
            _outputDevice.Init(processedAudio);
            ApplySoundLabToActivePlayback();
            _currentTrack = track;
            CurrentTitleText.Text = track.Title;
            CurrentAlbumText.Text = track.Album;
            PlayerStatusText.Text = $"{track.Artist} · {track.Format} · {track.BitrateText}";
            SetCover(track.CoverData);
            DurationText.Text = FormatTime(_audioFile.TotalTime);
            ProgressSlider.Maximum = Math.Max(1, _audioFile.TotalTime.TotalSeconds);
            ProgressSlider.Value = 0;
            _outputDevice.Play();
            PlayPauseButton.Content = "Ⅱ";
            _playerTimer.Start();
            StatusText.Text = $"Wiedergabe: {track.Title}";
        }
        catch (Exception ex)
        {
            DisposePlayback();
            PlayerStatusText.Text = "Datei konnte nicht abgespielt werden";
            StatusText.Text = $"Wiedergabefehler: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"Die Audiodatei konnte nicht wiedergegeben werden.\n\n{ex.Message}",
                "SonicForge Wiedergabefehler", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        if (_audioFile is null) return;
        _isUpdatingProgress = true;
        ProgressSlider.Value = Math.Min(ProgressSlider.Maximum, _audioFile.CurrentTime.TotalSeconds);
        PositionText.Text = FormatTime(_audioFile.CurrentTime);
        _isUpdatingProgress = false;
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingProgress || _audioFile is null || !IsLoaded) return;
        if (Math.Abs(_audioFile.CurrentTime.TotalSeconds - e.NewValue) > 1)
            _audioFile.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioFile is not null) ApplySoundLabToActivePlayback();
    }

    private void OutputDevice_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Exception is not null)
            {
                StatusText.Text = $"Wiedergabe abgebrochen: {e.Exception.Message}";
                StopPlayback(resetPosition: false);
                return;
            }

            if (_audioFile is not null && _audioFile.Position >= _audioFile.Length)
            {
                DisposePlayback();
                PlayNext();
            }
        });
    }

    private void StopPlayback(bool resetPosition)
    {
        _playerTimer.Stop();
        if (_outputDevice is not null) _outputDevice.Stop();
        if (resetPosition && _audioFile is not null) _audioFile.Position = 0;
        PlayPauseButton.Content = "▶";
        PlayerStatusText.Text = _currentTrack is null ? "SonicForge Player bereit" : "Gestoppt";
        if (resetPosition)
        {
            ProgressSlider.Value = 0;
            PositionText.Text = "00:00";
        }
    }

    private void DisposePlayback()
    {
        _playerTimer.Stop();
        if (_outputDevice is not null) _outputDevice.PlaybackStopped -= OutputDevice_PlaybackStopped;
        _outputDevice?.Stop();
        _outputDevice?.Dispose();
        _audioFile?.Dispose();
        _outputDevice = null;
        _audioFile = null;
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => DisposePlayback();

    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"mm\:ss");

    private void SetCover(byte[]? coverData)
    {
        if (coverData is null || coverData.Length == 0)
        {
            CurrentCoverImage.Source = null;
            CurrentCoverImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            using var stream = new MemoryStream(coverData);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            CurrentCoverImage.Source = image;
            CurrentCoverImage.Visibility = Visibility.Visible;
        }
        catch
        {
            CurrentCoverImage.Source = null;
            CurrentCoverImage.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        ShowOpenSourceProjectInfo();
    }

      private void Settings_Click(object sender, RoutedEventArgs e)
      {
          try
          {
              var existing = System.Windows.Application.Current.Windows
                  .OfType<SettingsWindow>()
                  .FirstOrDefault();
              if (existing is not null)
              {
                  if (existing.WindowState == WindowState.Minimized)
                      existing.WindowState = WindowState.Normal;
                  existing.Activate();
                  return;
              }
              new SettingsWindow { Owner = this }.ShowDialog();
          }
          catch (Exception ex)
          {
              System.Windows.MessageBox.Show(this,
                  "Die Einstellungen konnten nicht geÃ¶ffnet werden.\n\n" + ex,
                  "SonicForge Einstellungen",
                  MessageBoxButton.OK,
                  MessageBoxImage.Error);
          }
      }

      private void HomeNav_Click(object sender, RoutedEventArgs e)
      {
          SearchBox.Text = string.Empty;
          TracksView?.Refresh();
          if (Tracks.Count > 0)
          {
              if (TracksGrid.SelectedItem is null)
                  TracksGrid.SelectedIndex = 0;
              if (TracksGrid.SelectedItem is not null)
                  TracksGrid.ScrollIntoView(TracksGrid.SelectedItem);
          }
          SetMainNavigationState(HomeNavButton);
          StatusText.Text = "Startseite ge\u00F6ffnet. Bibliothek und Player sind bereit.";
          System.Windows.Input.Keyboard.Focus(this);
      }

      private void LibraryNav_Click(object sender, RoutedEventArgs e)
      {
          SetMainNavigationState(LibraryNavButton);
          StatusText.Text = $"Bibliothek ge\u00F6ffnet: {Tracks.Count} Titel.";
          SearchBox.Focus();
          SearchBox.SelectAll();
          if (TracksGrid.SelectedItem is not null)
              TracksGrid.ScrollIntoView(TracksGrid.SelectedItem);
      }

      private void SetMainNavigationState(System.Windows.Controls.Button activeButton)
      {
          var normalBackground = new System.Windows.Media.SolidColorBrush(
              System.Windows.Media.Color.FromRgb(0x08, 0x1A, 0x25));
          var normalBorder = new System.Windows.Media.SolidColorBrush(
              System.Windows.Media.Color.FromRgb(0x1D, 0x45, 0x58));
          var activeBackground = new System.Windows.Media.SolidColorBrush(
              System.Windows.Media.Color.FromRgb(0x10, 0x36, 0x48));
          var activeBorder = new System.Windows.Media.SolidColorBrush(
              System.Windows.Media.Color.FromRgb(0x18, 0xE5, 0xFF));

          foreach (var button in new[] { HomeNavButton, LibraryNavButton })
          {
              button.Background = normalBackground;
              button.BorderBrush = normalBorder;
          }
          activeButton.Background = activeBackground;
          activeButton.BorderBrush = activeBorder;
      }

      private RadioPodcastWindow? _radioPodcastWindow;
      private void HookRadioPodcastAccess()
      {
          var button = FindButtonByText(this, "Radio und Podcasts");
          if (button is null) return;
          button.Click -= RadioPodcast_Click;
          button.Click += RadioPodcast_Click;
          button.ToolTip = "Internetradio und Podcasts Ã¶ffnen";
      }
      private void RadioPodcast_Click(object sender, RoutedEventArgs e)
      {
          if (_radioPodcastWindow is { IsLoaded: true }) { _radioPodcastWindow.Activate(); return; }
          _radioPodcastWindow = new RadioPodcastWindow { Owner = this };
          _radioPodcastWindow.Closed += (_, _) => _radioPodcastWindow = null;
          _radioPodcastWindow.Show();
      }

      private RadioPodcastWindow? _radioPodcastDirectWindow;
      private void RadioPodcastDirect_Click(object sender, RoutedEventArgs e)
      {
          if (_radioPodcastDirectWindow is { IsLoaded: true })
          {
              _radioPodcastDirectWindow.Activate();
              if (_radioPodcastDirectWindow.WindowState == WindowState.Minimized)
                  _radioPodcastDirectWindow.WindowState = WindowState.Normal;
              return;
          }
          _radioPodcastDirectWindow = new RadioPodcastWindow { Owner = this };
          _radioPodcastDirectWindow.Closed += (_, _) => _radioPodcastDirectWindow = null;
          _radioPodcastDirectWindow.Show();
      }

    private void ShowSonicForgeStartCenter(object sender, RoutedEventArgs e)
    {
        Loaded -= ShowSonicForgeStartCenter;
        Dispatcher.BeginInvoke(() =>
        {
            var center = new StartCenterWindow { Owner = this };
            center.ShowDialog();
        });
    }
    private void OpenStudioOverview_Click(object sender, RoutedEventArgs e)
    {
        var existing = System.Windows.Application.Current.Windows.OfType<StartCenterWindow>().FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var center = new StartCenterWindow { Owner = this };
        center.Show();
        center.Activate();
    }
    // SPOTIFY_NAV_ACTIVE_FIX_V3
    private void SpotifyNav_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SetSpotifyNavigationActive();
        LaunchSpotifyDesktop();
    }

    private void SetSpotifyNavigationActive()
    {
        var activeBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 65, 82));
        var activeBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 225, 245));
        var normalBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 28, 40));
        var normalBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 62, 78));
        SpotifyNavButton.Background = activeBackground;
        SpotifyNavButton.BorderBrush = activeBorder;
        SpotifyNavButton.BorderThickness = new Thickness(1);
        SpotifyNavButton.Foreground = System.Windows.Media.Brushes.White;
        HomeNavButton.Background = normalBackground;
        HomeNavButton.BorderBrush = normalBorder;
        HomeNavButton.BorderThickness = new Thickness(1);
        DiagnosticLog.Info("Navigation", "Spotify ist aktiv");
    }
    // SPOTIFY_NAV_ACTIVE_FIX_V6
    private void LaunchSpotifyDesktop()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "spotify:",
                UseShellExecute = true
            });
            DiagnosticLog.Info("Spotify", "Spotify-URI gestartet");
            return;
        }
        catch (Exception uriEx)
        {
            DiagnosticLog.Warning("Spotify", "Spotify-URI fehlgeschlagen | " + uriEx.Message);
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "Spotify.exe")
        };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(candidate) { UseShellExecute = true });
                DiagnosticLog.Info("Spotify", "Spotify-Programm gestartet | " + candidate);
                return;
            }
            catch (Exception fileEx)
            {
                DiagnosticLog.Warning("Spotify", "Spotify-Programmstart fehlgeschlagen | " + fileEx.Message);
            }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = @"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
                UseShellExecute = true
            });
            DiagnosticLog.Info("Spotify", "Spotify ueber Windows AppsFolder gestartet");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Spotify", ex, "Spotify konnte nicht gestartet werden");
            System.Windows.MessageBox.Show(this,
                "Spotify konnte nicht gestartet werden. Bitte pruefen, ob die Spotify-Desktop-App installiert ist.",
                "SonicForge Spotify", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void UpdateInfo_Click(object sender, RoutedEventArgs e)
    {
        var window = new UpdateInfoWindow { Owner = this };
        window.ShowDialog();
    }
    private void Wishlist_Click(object sender, RoutedEventArgs e)
    {
        var window = new WishlistWindow { Owner = this };
        window.ShowDialog();
    }
    private void ShowOpenSourceProjectInfo()
    {
        var window = new System.Windows.Window
        {
            Title = "Open Source und Projektinformationen",
            Width = 560,
            Height = 520,
            MinWidth = 520,
            MinHeight = 460,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.CanResize,
            ShowInTaskbar = false,
            Owner = this,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 24, 34))
        };

        var root = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(24) };
        var buttons = new System.Windows.Controls.WrapPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 20, 0, 0)
        };
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);

        System.Windows.Controls.Button MakeButton(string text, int width)
        {
            return new System.Windows.Controls.Button
            {
                Content = text,
                Width = width,
                Height = 34,
                Margin = new System.Windows.Thickness(6, 0, 0, 0),
                Padding = new System.Windows.Thickness(10, 4, 10, 4)
            };
        }

        var licenseButton = MakeButton("GPLv3 anzeigen", 120);
        var webButton = MakeButton("Offizielle GPL-Webseite", 165);
        var mailButton = MakeButton("E-Mail schreiben", 125);
        var closeButton = MakeButton("Schliessen", 90);

        licenseButton.Click += (_, _) =>
        {
            var licensePath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "LICENSE.txt");
            if (System.IO.File.Exists(licensePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(licensePath) { UseShellExecute = true });
            else
                System.Windows.MessageBox.Show("LICENSE.txt wurde nicht gefunden.", "SonicForge MP3 Studio", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        };
        webButton.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.gnu.org/licenses/gpl-3.0.html") { UseShellExecute = true });
        mailButton.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mailto:NicolaMichael@gmx.de?subject=SonicForge%20MP3%20Studio") { UseShellExecute = true });
        closeButton.Click += (_, _) => window.Close();
        buttons.Children.Add(licenseButton); buttons.Children.Add(webButton); buttons.Children.Add(mailButton); buttons.Children.Add(closeButton);

        var text = new System.Windows.Controls.TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 14,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            LineHeight = 22,
            Text = "SonicForge MP3 Studio 1.0.0\n\n" +
                   "Projektidee, Konzeption und Projektleitung:\nMichael Nicola\n" +
                   "E-Mail: NicolaMichael@gmx.de\n\n" +
                   "Technische Umsetzung und Entwicklungsunterstuetzung:\nMicrosoft 365 Copilot\n\n" +
                   "Lizenz:\nGNU General Public License Version 3 oder hoeher\n" +
                   "SPDX: GPL-3.0-or-later\n\n" +
                   "Dieses Programm ist freie Software. Weitergabe und Aenderungen sind nach den Bedingungen der GNU GPLv3 oder einer spaeteren Version erlaubt.\n\n" +
                   "Keine Gewaehleistung. Keine Werbung. Keine Telemetrie. Kein Cloud-Zwang.\n\n" +
                   "Der vollstaendige Lizenztext ist in LICENSE.txt enthalten. Der korrespondierende Quellcode wird mit dem Release bereitgestellt."
        };
        var scroll = new System.Windows.Controls.ScrollViewer
        {
            Content = text,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        root.Children.Add(buttons); root.Children.Add(scroll); window.Content = root; window.ShowDialog();
    }}



