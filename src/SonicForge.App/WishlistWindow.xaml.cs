using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace SonicForge.App;

public sealed record WishlistItem(Guid Id, string Artist, string Title, string Album, string Status, string Note, DateTime Added)
{
    public string AddedText => Added.ToString("dd.MM.yyyy");
}

public partial class WishlistWindow : Window
{
    private readonly ObservableCollection<WishlistItem> _items = new();
    private readonly string _store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicForge", "wishlist.json");

    public WishlistWindow()
    {
        InitializeComponent();
        WishlistGrid.ItemsSource = _items;
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_store))
                foreach (var item in JsonSerializer.Deserialize<List<WishlistItem>>(File.ReadAllText(_store)) ?? [])
                    _items.Add(item);
        }
        catch (Exception ex) { StatusText.Text = "Wunschliste konnte nicht geladen werden: " + ex.Message; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
        File.WriteAllText(_store, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var artist = ArtistBox.Text.Trim();
        var title = TitleBox.Text.Trim();
        if (artist.Length == 0 && title.Length == 0) { StatusText.Text = "Bitte Interpret oder Titel eingeben."; return; }
        var status = (StatusBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Gewünscht";
        _items.Add(new(Guid.NewGuid(), artist, title, AlbumBox.Text.Trim(), status, NoteBox.Text.Trim(), DateTime.Now));
        Save(); ArtistBox.Clear(); TitleBox.Clear(); AlbumBox.Clear(); NoteBox.Clear();
        StatusText.Text = "Musikwunsch gespeichert.";
    }

    private void SearchSpotify_Click(object sender, RoutedEventArgs e)
    {
        if (WishlistGrid.SelectedItem is not WishlistItem item) { StatusText.Text = "Bitte zuerst einen Eintrag auswählen."; return; }
        var query = Uri.EscapeDataString((item.Artist + " " + item.Title + " " + item.Album).Trim());
        OpenSpotify("spotify:search:" + query, "https://open.spotify.com/search/" + query);
    }

    private void OpenSpotify_Click(object sender, RoutedEventArgs e) => OpenSpotify("spotify:", "https://open.spotify.com/");
    private static void OpenSpotify(string appUri, string fallback)
    {
        try { Process.Start(new ProcessStartInfo(appUri) { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo(fallback) { UseShellExecute = true }); }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (WishlistGrid.SelectedItem is WishlistItem item) { _items.Remove(item); Save(); StatusText.Text = "Eintrag entfernt."; }
    }

    private void AdvanceStatus_Click(object sender, RoutedEventArgs e)
    {
        if (WishlistGrid.SelectedItem is not WishlistItem item) { StatusText.Text = "Bitte zuerst einen Eintrag auswählen."; return; }
        var order = new[] { "Gewünscht", "Gefunden", "Aufgenommen", "In Bibliothek" };
        var index = Array.IndexOf(order, item.Status);
        var next = order[Math.Min(order.Length - 1, Math.Max(0, index + 1))];
        var replacement = item with { Status = next };
        var position = _items.IndexOf(item);
        _items[position] = replacement;
        Save(); WishlistGrid.SelectedItem = replacement; StatusText.Text = "Status: " + next;
    }
}
