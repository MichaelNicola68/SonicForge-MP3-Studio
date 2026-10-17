using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace SonicForge.App;

public sealed class CdTrackRow : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _displayTitle = "";
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnChanged(); } }
    public int Number { get; init; }
    public string DisplayTitle { get => _displayTitle; set { _displayTitle = value; MetadataSource = "Manuell"; OnChanged(); OnChanged(nameof(MetadataSource)); } }
    public string DurationText { get; init; } = "";
    public string MetadataSource { get; set; } = "Platzhalter";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public partial class CdImportWindow : Window
{
    private string? _selectedReleaseId;

    private readonly AudioCdService _cdService = new();
    private readonly MusicBrainzLookupService _musicBrainz = new();
    private readonly DriveDiscIdService _driveDiscId = new();
    private readonly CdAudioImportService _audioImporter = new();
    private CancellationTokenSource? _importCancellation;
    private AudioCdInfo? _currentDisc;
    private readonly ObservableCollection<CdTrackRow> _tracks = new();
    public CdImportWindow() { InitializeComponent(); TracksGrid.ItemsSource = _tracks; RefreshDrives(); }
    private void RefreshDrives() { var drives = _cdService.GetOpticalDrives(); DriveBox.ItemsSource = drives; if (drives.Count > 0) DriveBox.SelectedIndex = 0; StatusText.Text = drives.Count == 0 ? "Kein optisches Laufwerk gefunden." : $"{drives.Count} optisches Laufwerk erkannt."; }

    private void ReadDisc_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is not DriveInfo drive) { StatusText.Text = "Bitte ein CD-Laufwerk auswählen."; return; }
        try
        {
            StatusText.Text = "Audio-CD und CD-Text werden eingelesen...";
            var disc = _cdService.ReadDisc(drive.Name);
            _currentDisc = disc;
            var cdText = CdTextReader.TryRead(drive.Name);
            _tracks.Clear();
            foreach (var track in disc.Tracks)
            {
                var hasTitle = cdText.Titles.TryGetValue(track.Number, out var title) && !string.IsNullOrWhiteSpace(title);
                _tracks.Add(new CdTrackRow { Number = track.Number, DisplayTitle = hasTitle ? title! : track.DisplayTitle, DurationText = track.DurationText, MetadataSource = hasTitle ? "CD-Text" : "Platzhalter" });
            }
            AlbumBox.Text = cdText.Album;
            ArtistBox.Text = cdText.AlbumArtist;
            DiscSummaryText.Text = $"{disc.DriveName} · {disc.Tracks.Count} Audiotitel";
            DiscDurationText.Text = $"Gesamtdauer {disc.TotalDurationText}";
            StatusText.Text = cdText.HasData ? "CD-Text erkannt. Alle Angaben können bearbeitet werden." : "Kein CD-Text gefunden. Album, Interpret und Titel können manuell eingegeben werden.";
        }
        catch (Exception ex)
        {
            _tracks.Clear(); DiscSummaryText.Text = "Keine lesbare Audio-CD"; DiscDurationText.Text = ""; StatusText.Text = ex.Message;
            System.Windows.MessageBox.Show(this, "Die Disc konnte nicht als Audio-CD eingelesen werden.\n\n" + ex.Message, "SonicForge CD-Importstudio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MusicBrainz_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDisc is null || DriveBox.SelectedItem is not DriveInfo drive) { StatusText.Text = "Bitte zuerst Disc einlesen."; return; }
        try
        {
            StatusText.Text = "Disc-ID wird nur aus dem CD-Inhaltsverzeichnis gelesen...";
            var discId = await _driveDiscId.ReadAsync(drive.Name);
            StatusText.Text = $"Disc-ID {discId} wird bei MusicBrainz gesucht...";
            var match = await _musicBrainz.FindExactAsync(discId, _currentDisc.Tracks.Count);
            if (match.Releases.Count == 0) { StatusText.Text = $"Kein exakter MusicBrainz-Treffer. Laufwerk-Disc-ID: {discId}"; return; }
            var selected = match.Releases[0];
            _selectedReleaseId = selected.ReleaseId;
            AlbumBox.Text = selected.Album; ArtistBox.Text = selected.Artist; YearBox.Text = selected.Year;
            for (var i = 0; i < _tracks.Count && i < selected.Titles.Count; i++) { _tracks[i].DisplayTitle = selected.Titles[i]; _tracks[i].MetadataSource = "MusicBrainz Disc-ID"; }
            TracksGrid.Items.Refresh();
            StatusText.Text = match.Releases.Count == 1
                ? $"Exakter Laufwerk-Disc-ID-Treffer übernommen: {discId}"
                : $"Exakte Laufwerk-Disc-ID mit {match.Releases.Count} Pressungen gefunden. Erste Pressung übernommen, bitte prüfen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Laufwerk-Disc-ID-Suche fehlgeschlagen: " + ex.Message;
            System.Windows.MessageBox.Show(this, StatusText.Text, "MusicBrainz Disc-ID", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Eject_Click(object sender, RoutedEventArgs e) { if (DriveBox.SelectedItem is not DriveInfo drive) return; try { _cdService.Eject(drive.Name); _tracks.Clear(); DiscSummaryText.Text = "Disc ausgeworfen"; DiscDurationText.Text = ""; } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDisc is null || DriveBox.SelectedItem is not DriveInfo drive) { StatusText.Text = "Bitte zuerst Disc einlesen."; return; }
        TracksGrid.CommitEdit(); TracksGrid.CommitEdit();
        var selected = _tracks.Where(t => t.IsSelected).Select(t => new CdImportTrack(t.Number, t.DisplayTitle)).ToArray();
        if (selected.Length == 0) { StatusText.Text = "Bitte mindestens einen Titel auswÃ¤hlen."; return; }
        _importCancellation = new CancellationTokenSource(); ImportButton.IsEnabled = false; CancelImportButton.IsEnabled = true; CancelImportButton.Visibility = Visibility.Visible; ImportProgressPanel.Visibility = Visibility.Visible; TrackProgressBar.Value = 0; TotalProgressBar.Value = 0;
        var progress = new Progress<CdImportProgress>(p =>
        {
            var total = Math.Max(1, p.Total);
            var current = Math.Clamp(p.Current, 0, total);
            var percent = Math.Clamp(p.Percent, 0, 100);
            StatusText.Text = $"{current}/{p.Total} | {p.Message} | {percent:0}%";
            TrackProgressBar.Value = percent;
            TrackProgressText.Text = $"{percent:0} %";
            TotalProgressBar.Maximum = total;
            var completedBeforeCurrent = Math.Max(0, current - 1);
            var totalValue = Math.Min(total, completedBeforeCurrent + (percent / 100.0));
            TotalProgressBar.Value = totalValue;
            TotalProgressText.Text = $"{totalValue:0.00} / {p.Total}";
        });
        try
        {
            await _audioImporter.ImportAsync(drive.Name, TargetFolderBox.Text.Trim(), ArtistBox.Text.Trim(), AlbumBox.Text.Trim(), YearBox.Text.Trim(), _selectedReleaseId, selected,
                CreateWavBox.IsChecked == true, CreateMp3Box.IsChecked == true, progress, _importCancellation.Token);
            StatusText.Text = $"Import abgeschlossen: {selected.Length} Titel.";
            System.Windows.MessageBox.Show(this, "WAV- und MP3-Import erfolgreich abgeschlossen.", "SonicForge CD-Importstudio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { StatusText.Text = "Import abgebrochen."; }
        catch (Exception ex) { StatusText.Text = "Import fehlgeschlagen: " + ex.Message; System.Windows.MessageBox.Show(this, StatusText.Text, "CD-Import", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { ImportButton.IsEnabled = true; CancelImportButton.IsEnabled = false; CancelImportButton.Visibility = Visibility.Collapsed; _importCancellation.Dispose(); _importCancellation = null; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        if (_importCancellation is null) return;
        CancelImportButton.IsEnabled = false;
        StatusText.Text = "Import wird abgebrochen ...";
        _importCancellation.Cancel();
    }}
