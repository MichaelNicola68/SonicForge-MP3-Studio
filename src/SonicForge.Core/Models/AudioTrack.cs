namespace SonicForge.Core.Models;

public sealed class AudioTrack
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FilePath { get; init; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "Unbekannter Interpret";
    public string Album { get; set; } = "Unbekanntes Album";
    public string Genre { get; set; } = "";
    public uint Year { get; set; }
    public uint TrackNumber { get; set; }
    public string Format { get; init; } = "";
    public long FileSize { get; init; }
    public TimeSpan Duration { get; set; }
    public int BitrateKbps { get; set; }
    public byte[]? CoverData { get; set; }
    public string Status { get; set; } = "Bereit";

    public string FileSizeText => FileSize < 1_048_576
        ? $"{FileSize / 1024d:0.0} KB"
        : $"{FileSize / 1_048_576d:0.0} MB";

    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"mm\:ss");

    public string BitrateText => BitrateKbps > 0 ? $"{BitrateKbps} kbit/s" : "–";
    public string YearText => Year > 0 ? Year.ToString() : "–";
    public bool HasCover => CoverData is { Length: > 0 };
}
