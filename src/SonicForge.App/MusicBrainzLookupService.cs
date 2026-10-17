using System.Net.Http;
using System.Text.Json;

namespace SonicForge.App;

public sealed record MusicBrainzAlbum(string ReleaseId, string Album, string Artist, string Year, IReadOnlyList<string> Titles);
public sealed record MusicBrainzDiscMatch(string DiscId, IReadOnlyList<MusicBrainzAlbum> Releases);

public sealed class MusicBrainzLookupService
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SonicForge-MP3-Studio/0.9.0.6 (NicolaMichael@gmx.de)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<MusicBrainzDiscMatch> FindExactAsync(string discId, int expectedTracks, CancellationToken token = default)
    {
        var url = $"https://musicbrainz.org/ws/2/discid/{Uri.EscapeDataString(discId)}?inc=recordings+artist-credits&fmt=json";
        using var response = await Http.GetAsync(url, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(discId, Array.Empty<MusicBrainzAlbum>());
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var releases = new List<MusicBrainzAlbum>();
        if (json.RootElement.TryGetProperty("releases", out var items))
            foreach (var release in items.EnumerateArray()) { var parsed = ParseRelease(release, expectedTracks); if (parsed is not null) releases.Add(parsed); }
        return new(discId, releases);
    }

    private static MusicBrainzAlbum? ParseRelease(JsonElement release, int expectedTracks)
    {
        var id = release.TryGetProperty("id", out var rid) ? rid.GetString() ?? "" : "";
        var album = release.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
        var date = release.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "";
        var year = date.Length >= 4 ? date[..4] : date;
        var artist = ReadArtistCredit(release);
        if (!release.TryGetProperty("media", out var media)) return null;
        foreach (var medium in media.EnumerateArray())
        {
            if (!medium.TryGetProperty("tracks", out var tracks) || tracks.GetArrayLength() != expectedTracks) continue;
            var names = tracks.EnumerateArray().Select(t => t.TryGetProperty("title", out var n) ? n.GetString() ?? "" : "").ToArray();
            return new(id, album, artist, year, names);
        }
        return null;
    }

    private static string ReadArtistCredit(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out var credits)) return "";
        return string.Concat(credits.EnumerateArray().Select(c => c.TryGetProperty("name", out var n) ? n.GetString() : "")).Trim();
    }
}
