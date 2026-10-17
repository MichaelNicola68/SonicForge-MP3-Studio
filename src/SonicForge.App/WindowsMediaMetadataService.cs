using System.IO;
using Windows.Media.Control;

namespace SonicForge.App;

public sealed record CurrentMediaInfo(string Title, string Artist, string Album)
{
    public bool HasUsefulData => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);
}

public static class WindowsMediaMetadataService
{
    public static async Task<CurrentMediaInfo> GetCurrentAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            var session = manager.GetCurrentSession();
            if (session is null) return new("", "", "");
            var properties = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            return new(properties.Title?.Trim() ?? "", properties.Artist?.Trim() ?? "", properties.AlbumTitle?.Trim() ?? "");
        }
        catch
        {
            return new("", "", "");
        }
    }

    public static string SafeFilePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Trim().TrimEnd('.');
    }
}
