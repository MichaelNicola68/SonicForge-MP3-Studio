using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SonicForge.App;

public sealed record AlbumCoverResult(byte[] Data, string MimeType, string ReleaseId);

public sealed class AlbumCoverService
{
    private readonly HttpClient _http;

    public AlbumCoverService()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SonicForge/0.9.2 (https://github.com/SonicForge)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<AlbumCoverResult?> FindByReleaseAsync(string? releaseId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(releaseId)) return Task.FromResult<AlbumCoverResult?>(null);
        return DownloadFrontAsync(releaseId, token);
    }
    public async Task<AlbumCoverResult?> FindAsync(string artist, string album, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)) return null;
        var query = $"release:\"{Escape( album)}\" AND artist:\"{Escape(artist)}\"";
        var searchUrl = "https://musicbrainz.org/ws/2/release/?fmt=json&limit=10&query=" + Uri.EscapeDataString(query);
        using var search = await _http.GetAsync(searchUrl, token);
        if (!search.IsSuccessStatusCode) return null;
        await using var body = await search.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
        if (!json.RootElement.TryGetProperty("releases", out var releases)) return null;

        foreach (var release in releases.EnumerateArray())
        {
            if (!release.TryGetProperty("id", out var idNode)) continue;
            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var result = await DownloadFrontAsync(id, token);
            if (result is not null) return result;
        }
        return null;
    }

    private async Task<AlbumCoverResult?> DownloadFrontAsync(string releaseId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://coverartarchive.org/release/{releaseId}/front-500");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) return null;
        var data = await response.Content.ReadAsByteArrayAsync(token);
        if (data.Length < 1024) return null;
        var mime = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mime)) mime = DetectMime(data);
        return new AlbumCoverResult(data, mime!, releaseId);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string DetectMime(byte[] data) => data.Length > 3 && data[0] == 0x89 && data[1] == 0x50 ? "image/png" : "image/jpeg";
}

