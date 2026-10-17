using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfButton = System.Windows.Controls.Button;
using WpfImage = System.Windows.Controls.Image;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;

namespace SonicForge.App;

public sealed record CoverCandidate(string ReleaseId, string Title, string Artist, string ThumbnailUrl, string ImageUrl);

public sealed class CoverSearchWindow : Window
{
    private static readonly HttpClient Http = CreateClient();
    private readonly WrapPanel _results = new() { Margin = new Thickness(8) };
    private readonly TextBlock _status = new() { Margin = new Thickness(12), Foreground = WpfBrushes.White };
    public string? SelectedCoverPath { get; private set; }

    public CoverSearchWindow(string artist, string album, string title)
    {
        Title = "Cover im Internet suchen";
        Width = 980; Height = 720; MinWidth = 760; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(WpfColor.FromRgb(9, 26, 41));

        var root = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(16) };
        header.Children.Add(new TextBlock { Text = "Cover auswählen", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = WpfBrushes.White });
        header.Children.Add(new TextBlock { Text = $"Suche: {artist} · {album} · {title}", Margin = new Thickness(0,5,0,0), Foreground = new SolidColorBrush(WpfColor.FromRgb(185,205,220)) });
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await LoadAsync(artist, album, title);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SonicForge-MP3-Studio", "0.1"));
        return client;
    }

    private async Task LoadAsync(string artist, string album, string title)
    {
        try
        {
            _status.Text = "Cover werden gesucht …";
            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(album)) queryParts.Add($"release:\"{album.Trim()}\"");
            if (!string.IsNullOrWhiteSpace(artist)) queryParts.Add($"artist:\"{artist.Trim()}\"");
            if (queryParts.Count == 0 && !string.IsNullOrWhiteSpace(title)) queryParts.Add($"release:\"{title.Trim()}\"");
            if (queryParts.Count == 0) { _status.Text = "Bitte mindestens Interpret, Album oder Titel eintragen."; return; }

            var url = "https://musicbrainz.org/ws/2/release/?fmt=json&limit=8&query=" + Uri.EscapeDataString(string.Join(" AND ", queryParts));
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var candidates = new List<CoverCandidate>();
            foreach (var release in json.RootElement.GetProperty("releases").EnumerateArray())
            {
                var id = release.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var releaseTitle = release.TryGetProperty("title", out var rt) ? rt.GetString() ?? "Unbekannt" : "Unbekannt";
                var releaseArtist = release.TryGetProperty("artist-credit", out var ac) && ac.GetArrayLength() > 0 && ac[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                candidates.Add(new CoverCandidate(id, releaseTitle, releaseArtist,
                    $"https://coverartarchive.org/release/{id}/front-250",
                    $"https://coverartarchive.org/release/{id}/front-500"));
            }
            if (candidates.Count == 0) { _status.Text = "Keine passenden Veröffentlichungen gefunden."; return; }
            foreach (var item in candidates) AddCandidate(item);
            _status.Text = "Passendes Cover anklicken. Nicht verfügbare Bilder werden übersprungen.";
        }
        catch (Exception ex) { _status.Text = "Coversuche fehlgeschlagen: " + ex.Message; }
    }

    private void AddCandidate(CoverCandidate item)
    {
        var image = new WpfImage { Width = 180, Height = 180, Stretch = Stretch.UniformToFill };
        try { image.Source = new BitmapImage(new Uri(item.ThumbnailUrl)); } catch { }
        var button = new WpfButton { Width = 210, Margin = new Thickness(8), Padding = new Thickness(8), Background = new SolidColorBrush(WpfColor.FromRgb(20,45,65)), Foreground = WpfBrushes.White };
        var content = new StackPanel(); content.Children.Add(image);
        content.Children.Add(new TextBlock { Text = item.Title, Margin = new Thickness(0,7,0,0), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = item.Artist, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(WpfColor.FromRgb(190,210,225)) });
        button.Content = content;
        button.Click += async (_, _) => await SelectAsync(item);
        _results.Children.Add(button);
    }

    private async Task SelectAsync(CoverCandidate item)
    {
        try
        {
            _status.Text = "Cover wird geladen …";
            var bytes = await Http.GetByteArrayAsync(item.ImageUrl);
            var folder = Path.Combine(Path.GetTempPath(), "SonicForge", "Cover"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, item.ReleaseId + ".jpg"); await File.WriteAllBytesAsync(path, bytes);
            SelectedCoverPath = path; DialogResult = true; Close();
        }
        catch (Exception ex) { _status.Text = "Dieses Cover konnte nicht geladen werden: " + ex.Message; }
    }
}
