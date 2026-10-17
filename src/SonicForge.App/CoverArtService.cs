using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
namespace SonicForge.App;
internal static class CoverArtService
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SonicForge-MP3-Studio/0.1.0-alpha (open-source desktop application)");
        return c;
    }
    public static async Task<string> TryEmbedAsync(string mp3Path, string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mp3Path) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return Log(mp3Path, title, artist, "Metadaten fehlen");
        try
        {
            
            var album = await FindAlbumWithDeezerAsync(title, artist, ct).ConfigureAwait(false);
            if (album is null) return Log(mp3Path, title, artist, "kein eindeutiger Deezer-Treffer");

            // GENRE_YEAR_FLOW_FIX2
            await ApplyDeezerGenreAndYearAsync(mp3Path, album.Value.AlbumId, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(album.Value.CoverUrl))
            {
                var direct = await DownloadImageAsync(album.Value.CoverUrl, ct).ConfigureAwait(false);
                if (direct is not null)
                {
                    Embed(mp3Path, direct.Value.Bytes, direct.Value.Mime, album.Value.Album, "Deezer");
                    return Log(mp3Path, title, artist, $"Deezer-Cover eingebettet: {album.Value.Album}");
                }
                Log(mp3Path, title, artist, "Deezer-Coverabruf fehlgeschlagen, versuche MusicBrainz");
            }

            

            var releaseId = await FindMusicBrainzReleaseAsync(album.Value.Album, artist, ct).ConfigureAwait(false);
            if (releaseId is null) return Log(mp3Path, title, artist, $"Album erkannt, aber kein MusicBrainz-Release: {album.Value.Album}");
            var fallback = await DownloadImageAsync($"https://coverartarchive.org/release/{releaseId}/front-500", ct).ConfigureAwait(false);
            if (fallback is null) return Log(mp3Path, title, artist, "MusicBrainz-Release erkannt, aber Coverabruf fehlgeschlagen");
            Embed(mp3Path, fallback.Value.Bytes, fallback.Value.Mime, album.Value.Album, "Cover Art Archive");
            return Log(mp3Path, title, artist, $"Albumcover eingebettet: {album.Value.Album}");
        }
        catch(Exception ex) { return Log(mp3Path, title, artist, "Coverfehler: " + ex.Message); }
    }
    private static void Embed(string mp3Path, byte[] bytes, string mime, string album, string source)
    {
        using var tf = global::TagLib.File.Create(mp3Path);
        if (tf.Tag.Pictures is { Length: > 0 }) return;
        tf.Tag.Pictures = new global::TagLib.IPicture[] { new global::TagLib.Picture(new global::TagLib.ByteVector(bytes)) { Type=global::TagLib.PictureType.FrontCover, MimeType=mime, Description=$"{source}: {album}" } };
        if (string.IsNullOrWhiteSpace(tf.Tag.Album)) tf.Tag.Album = album;
        tf.Save();
    }
    private static async Task<(byte[] Bytes,string Mime)?> DownloadImageAsync(string url,CancellationToken ct)
    {
        using var req=new HttpRequestMessage(HttpMethod.Get,url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var r=await Http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
        if(!r.IsSuccessStatusCode)return null;
        var bytes=await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var mime=r.Content.Headers.ContentType?.MediaType??"image/jpeg";
        if(bytes.Length<1024||bytes.Length>8*1024*1024||!mime.StartsWith("image/",StringComparison.OrdinalIgnoreCase))return null;
        return(bytes,mime);
    }
    private static async Task<(string Album,long AlbumId,string CoverUrl)?> FindAlbumWithDeezerAsync(string title,string artist,CancellationToken ct)
    {
        var queries = new[]
        {
            $"track:\"{title}\" artist:\"{artist}\"",
            $"{artist} {title}"
        };
        var nt=Norm(title); var na=Norm(artist);
        var candidates=new List<(string Album,long AlbumId,string CoverUrl,int Score,string FoundTitle,string FoundArtist)>();
        foreach (var rawQuery in queries)
        {
            var q=Uri.EscapeDataString(rawQuery);
            using var r=await Http.GetAsync($"https://api.deezer.com/search/track?q={q}&limit=25",ct).ConfigureAwait(false);
            if(!r.IsSuccessStatusCode) { Log("",title,artist,$"Deezer HTTP {(int)r.StatusCode} fuer: {rawQuery}"); continue; }
            using var j=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if(!j.RootElement.TryGetProperty("data",out var data)) continue;
            foreach(var x in data.EnumerateArray())
            {
                var xt=x.TryGetProperty("title",out var tn)?tn.GetString()??"":"";
                var xa=x.TryGetProperty("artist",out var an)&&an.TryGetProperty("name",out var nn)?nn.GetString()??"":"";
                if(!x.TryGetProperty("album",out var al)) continue;
                var at=al.TryGetProperty("title",out var atn)?atn.GetString()??"":"";
                var id=al.TryGetProperty("id",out var i)&&i.TryGetInt64(out var v)?v:0;
                var cover=al.TryGetProperty("cover_xl",out var cx)?cx.GetString()??"":al.TryGetProperty("cover_big",out var cb)?cb.GetString()??"":"";
                var titleScore=Similarity(nt,Norm(xt));
                var artistScore=Similarity(na,Norm(xa));
                var score=(titleScore*65)+(artistScore*35);
                Log("",title,artist,$"Kandidat: {xa} | {xt} | Album: {at} | Treffer: {score}%");
                if(id>0 && !string.IsNullOrWhiteSpace(cover) && !IsCompilation(at) && titleScore>=80 && artistScore>=80)
                    candidates.Add((at,id,cover,score,xt,xa));
            }
            if(candidates.Count>0) break;
        }
        return candidates
            .GroupBy(x=>x.AlbumId).Select(g=>g.OrderByDescending(x=>x.Score).First())
            .OrderByDescending(x=>x.Score)
            .Select(x=>((string Album,long AlbumId,string CoverUrl)?)(x.Album,x.AlbumId,x.CoverUrl))
            .FirstOrDefault();
    }
    private static int Similarity(string wanted,string found)
    {
        if(string.IsNullOrEmpty(wanted)||string.IsNullOrEmpty(found)) return 0;
        if(wanted==found) return 100;
        if(found.StartsWith(wanted,StringComparison.Ordinal)||wanted.StartsWith(found,StringComparison.Ordinal)) return 94;
        if(found.Contains(wanted,StringComparison.Ordinal)||wanted.Contains(found,StringComparison.Ordinal)) return 90;
        var max=Math.Max(wanted.Length,found.Length);
        var distance=Levenshtein(wanted,found);
        return Math.Max(0,(int)Math.Round(100.0*(max-distance)/max));
    }
    private static int Levenshtein(string a,string b)
    {
        var previous=Enumerable.Range(0,b.Length+1).ToArray();
        for(var i=1;i<=a.Length;i++)
        {
            var current=new int[b.Length+1]; current[0]=i;
            for(var j=1;j<=b.Length;j++)
                current[j]=Math.Min(Math.Min(current[j-1]+1,previous[j]+1),previous[j-1]+(a[i-1]==b[j-1]?0:1));
            previous=current;
        }
        return previous[b.Length];
    }
    private static async Task<string?> FindMusicBrainzReleaseAsync(string album,string artist,CancellationToken ct)
    {
        var q=Uri.EscapeDataString($"release:\"{Escape(album)}\" AND artist:\"{Escape(artist)}\" AND status:official");
        using var r=await Http.GetAsync($"https://musicbrainz.org/ws/2/release/?fmt=json&limit=15&query={q}",ct).ConfigureAwait(false);
        if(!r.IsSuccessStatusCode)return null;
        using var j=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if(!j.RootElement.TryGetProperty("releases",out var releases))return null;
        var nal=Norm(album);var nar=Norm(artist);
        foreach(var x in releases.EnumerateArray())
        {
            var score=x.TryGetProperty("score",out var sn)&&sn.TryGetInt32(out var sv)?sv:0;
            var t=x.TryGetProperty("title",out var tn)?tn.GetString()??"":"";
            if(score<85||Norm(t)!=nal||IsCompilation(t)||!ArtistMatches(x,nar))continue;
            if(x.TryGetProperty("id",out var id)&&Guid.TryParse(id.GetString(),out _))return id.GetString();
        }
        return null;
    }
    private static string Log(string mp3Path,string title,string artist,string message)
    {
        try { var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..","..")); File.AppendAllText(Path.Combine(root,"cover-diagnose.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {artist} | {title} | {message}{Environment.NewLine}"); } catch { }
        return message;
    }
    private static bool ArtistMatches(JsonElement x,string wanted){if(!x.TryGetProperty("artist-credit",out var a))return false;var s=new StringBuilder();foreach(var c in a.EnumerateArray())if(c.TryGetProperty("name",out var n))s.Append(n.GetString());return Norm(s.ToString())==wanted;}
    private static bool IsCompilation(string s){var n=Norm(s);return n.Contains("variousartists")||n.Contains("musicawards")||n.Contains("hits202")||n.Contains("sampler")||n.Contains("compilation");}
    private static string Norm(string s){var b=new StringBuilder();foreach(var c in s.Normalize(NormalizationForm.FormD))if(char.IsLetterOrDigit(c))b.Append(char.ToLowerInvariant(c));return b.ToString();}
    private static string Escape(string s)=>s.Replace("\\","\\\\").Replace("\"","\\\"");

    private static readonly string GenreYearLogPath=Path.Combine(AppContext.BaseDirectory,"genre-jahr-diagnose.log");
    private static void GenreYearLog(string message){try{global::System.IO.File.AppendAllText(GenreYearLogPath,$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");}catch{}}

    private static async Task ApplyDeezerGenreAndYearAsync(string mp3Path,long albumId,CancellationToken ct)
    {
        GenreYearLog($"START mp3={mp3Path}; albumId={albumId}");
        try
        {
            using var response=await Http.GetAsync($"https://api.deezer.com/album/{albumId}",ct).ConfigureAwait(false);
            GenreYearLog($"HTTP status={(int)response.StatusCode} {response.StatusCode}"); if(!response.IsSuccessStatusCode)return;
            var jsonText=await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); GenreYearLog($"JSON length={jsonText.Length}"); using var json=JsonDocument.Parse(jsonText);
            var genres=new List<string>();
            if(json.RootElement.TryGetProperty("genres",out var genresNode)&&genresNode.TryGetProperty("data",out var data))
                foreach(var item in data.EnumerateArray())
                    if(item.TryGetProperty("name",out var name)&&!string.IsNullOrWhiteSpace(name.GetString()))genres.Add(name.GetString()!);
            uint year=0;
            if(json.RootElement.TryGetProperty("release_date",out var dateNode))
            {
                var date=dateNode.GetString();
                if(!string.IsNullOrWhiteSpace(date)&&date.Length>=4)uint.TryParse(date[..4],out year);
            }
            GenreYearLog($"GEFUNDEN genres={string.Join(", ",genres)}; year={year}"); using var file=global::TagLib.File.Create(mp3Path);
            if(genres.Count>0)file.Tag.Genres=genres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(year>0)file.Tag.Year=year;
            file.Save(); GenreYearLog($"GESPEICHERT genres={string.Join(", ",file.Tag.Genres)}; year={file.Tag.Year}");
        }
        catch(Exception ex) { GenreYearLog("FEHLER "+ex); }
    }
}
