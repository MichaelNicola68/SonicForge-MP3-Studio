using System.IO;
using System.Text;

namespace SonicForge.App;

public static class RecordingMetadataService
{
    public static string CleanBaseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        clean = clean.Trim().TrimEnd('.');
        return clean.Length > 120 ? clean[..120].Trim() : clean;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{name}-{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static string RenameWithPartner(string path, string? requestedBaseName)
    {
        var clean = CleanBaseName(requestedBaseName);
        if (string.IsNullOrWhiteSpace(clean) || !File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var extension = Path.GetExtension(path);
        var target = UniquePath(Path.Combine(folder, clean + extension));
        if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) File.Move(path, target);

        var partnerExtension = extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3";
        var oldPartner = Path.ChangeExtension(path, partnerExtension);
        if (File.Exists(oldPartner))
        {
            var partnerTarget = UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(target) + partnerExtension));
            File.Move(oldPartner, partnerTarget);
        }
        return target;
    }

    public static bool WriteMp3Tags(string path, string? title, string? artist, string? album, string? year, string? genre, string? coverPath = null)
    {
        if (!path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        using (var file = TagLib.File.Create(path))
        {
            if (!string.IsNullOrWhiteSpace(title)) file.Tag.Title = title.Trim();
            if (!string.IsNullOrWhiteSpace(artist)) file.Tag.Performers = new[] { artist.Trim() };
            if (!string.IsNullOrWhiteSpace(album)) file.Tag.Album = album.Trim();
            if (uint.TryParse(year?.Trim(), out var parsedYear) && parsedYear <= 9999) file.Tag.Year = parsedYear;
            if (!string.IsNullOrWhiteSpace(genre)) file.Tag.Genres = new[] { genre.Trim() };

            if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
            {
                var picture = new TagLib.Picture(coverPath)
                {
                    Type = TagLib.PictureType.FrontCover,
                    Description = "Front Cover",
                    MimeType = Path.GetExtension(coverPath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                        ? "image/png"
                        : "image/jpeg"
                };
                file.Tag.Pictures = new TagLib.IPicture[] { picture };
            }
            file.Save();
        }

        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return false;
        using var verification = TagLib.File.Create(path);
        return verification.Tag.Pictures is { Length: > 0 } &&
               verification.Tag.Pictures[0].Data is not null &&
               verification.Tag.Pictures[0].Data.Count > 0;
    }}
