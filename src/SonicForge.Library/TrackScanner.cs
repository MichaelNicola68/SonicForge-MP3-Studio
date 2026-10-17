using SonicForge.Core.Models;
using SonicForge.Core.Services;

namespace SonicForge.Library;

public sealed class TrackScanner : ITrackScanner
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma"
    };

    public IReadOnlyList<AudioTrack> ScanFiles(IEnumerable<string> files)
    {
        var result = new List<AudioTrack>();
        foreach (var path in files.Where(File.Exists).Where(path => Supported.Contains(Path.GetExtension(path))))
        {
            var info = new FileInfo(path);
            var track = new AudioTrack
            {
                FilePath = path,
                Title = Path.GetFileNameWithoutExtension(path),
                Format = info.Extension.TrimStart('.').ToUpperInvariant(),
                FileSize = info.Length
            };

            try
            {
                using var media = TagLib.File.Create(path);
                track.Title = First(media.Tag.Title, Path.GetFileNameWithoutExtension(path));
                track.Artist = First(media.Tag.FirstPerformer, "Unbekannter Interpret");
                track.Album = First(media.Tag.Album, "Unbekanntes Album");
                track.Genre = First(media.Tag.FirstGenre, "");
                track.Year = media.Tag.Year;
                track.TrackNumber = media.Tag.Track;
                track.Duration = media.Properties.Duration;
                track.BitrateKbps = media.Properties.AudioBitrate;
                track.CoverData = ReadCover(media.Tag.Pictures);
                track.Status = track.HasCover ? "Metadaten + Cover" : "Metadaten";
            }
            catch (Exception)
            {
                track.Status = "Basisdaten";
            }

            result.Add(track);
        }
        return result;
    }

    private static string First(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static byte[]? ReadCover(TagLib.IPicture[] pictures)
    {
        var picture = pictures.FirstOrDefault(item => item.Type == TagLib.PictureType.FrontCover)
                      ?? pictures.FirstOrDefault();
        return picture?.Data?.Data is { Length: > 0 } data ? data.ToArray() : null;
    }
}
