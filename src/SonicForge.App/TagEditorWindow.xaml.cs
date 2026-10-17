using Microsoft.Win32;
using SonicForge.Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SonicForge.App;

public partial class TagEditorWindow : Window
{
    private readonly AudioTrack _track;
    private byte[]? _coverData;
    private string? _coverMimeType;

    public TagEditorWindow(AudioTrack track)
    {
        InitializeComponent();
        _track = track;
        FilePathText.Text = track.FilePath;
        TitleBox.Text = track.Title;
        ArtistBox.Text = track.Artist;
        AlbumBox.Text = track.Album;
        GenreBox.Text = track.Genre;
        YearBox.Text = track.Year > 0 ? track.Year.ToString() : "";
        TrackNumberBox.Text = track.TrackNumber > 0 ? track.TrackNumber.ToString() : "";
        _coverData = track.CoverData?.ToArray();
        ShowCover(_coverData);
    }

    private void SelectCover_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Bilddateien|*.jpg;*.jpeg;*.png|JPEG|*.jpg;*.jpeg|PNG|*.png",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _coverData = File.ReadAllBytes(dialog.FileName);
            _coverMimeType = Path.GetExtension(dialog.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            ShowCover(_coverData);
            StatusText.Text = "Neues Cover ausgewÃ¤hlt.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Cover konnte nicht geladen werden", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveCover_Click(object sender, RoutedEventArgs e)
    {
        _coverData = null;
        _coverMimeType = null;
        ShowCover(null);
        StatusText.Text = "Cover wird beim Speichern entfernt.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            System.Windows.MessageBox.Show(this, "Bitte einen Titel eingeben.", "SonicForge Tag-Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleBox.Focus();
            return;
        }
        if (!TryReadUInt(YearBox.Text, "Jahr", out var year) ||
            !TryReadUInt(TrackNumberBox.Text, "Titelnummer", out var trackNumber)) return;

        var backupPath = _track.FilePath + ".sonicforge-tag-backup";
        try
        {
            if (!File.Exists(backupPath)) File.Copy(_track.FilePath, backupPath, overwrite: false);

            using var media = TagLib.File.Create(_track.FilePath);
            media.Tag.Title = TitleBox.Text.Trim();
            media.Tag.Performers = Values(ArtistBox.Text);
            media.Tag.Album = AlbumBox.Text.Trim();
            media.Tag.Genres = Values(GenreBox.Text);
            media.Tag.Year = year;
            media.Tag.Track = trackNumber;
            media.Tag.Pictures = _coverData is null
                ? Array.Empty<TagLib.IPicture>()
                : new TagLib.IPicture[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(_coverData))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = _coverMimeType ?? DetectMimeType(_coverData),
                        Description = "SonicForge Front Cover"
                    }
                };
            media.Save();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this,
                $"Die Metadaten konnten nicht gespeichert werden.\n\n{ex.Message}\n\nSicherung: {backupPath}",
                "SonicForge Speicherfehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool TryReadUInt(string text, string fieldName, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (uint.TryParse(text.Trim(), out value)) return true;
        System.Windows.MessageBox.Show(this, $"{fieldName} muss eine positive ganze Zahl sein.", "UngÃ¼ltige Eingabe", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static string[] Values(string value) => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : new[] { value.Trim() };

    private void ShowCover(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            CoverImage.Source = null;
            NoCoverText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            using var stream = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            CoverImage.Source = image;
            NoCoverText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CoverImage.Source = null;
            NoCoverText.Visibility = Visibility.Visible;
        }
    }

    private static string DetectMimeType(byte[] content) =>
        content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47
            ? "image/png" : "image/jpeg";
}

