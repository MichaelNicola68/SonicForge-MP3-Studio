using Microsoft.Data.Sqlite;
using SonicForge.Core.Models;

namespace SonicForge.Library;

public sealed class LibraryRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public LibraryRepository()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicForge MP3 Studio", "Data");
        Directory.CreateDirectory(dataDirectory);
        _databasePath = Path.Combine(dataDirectory, "sonicforge.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks (
                file_path TEXT PRIMARY KEY COLLATE NOCASE,
                title TEXT NOT NULL,
                artist TEXT NOT NULL,
                album TEXT NOT NULL,
                genre TEXT NOT NULL,
                year INTEGER NOT NULL,
                track_number INTEGER NOT NULL,
                format TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                duration_ticks INTEGER NOT NULL,
                bitrate_kbps INTEGER NOT NULL,
                cover_data BLOB NULL,
                status TEXT NOT NULL,
                imported_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist);
            CREATE INDEX IF NOT EXISTS ix_tracks_album ON tracks(album);
            """;
        command.ExecuteNonQuery();
    }

    public void UpsertMany(IEnumerable<AudioTrack> tracks)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var track in tracks)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tracks (
                    file_path, title, artist, album, genre, year, track_number,
                    format, file_size, duration_ticks, bitrate_kbps, cover_data,
                    status, imported_utc, last_seen_utc)
                VALUES (
                    $path, $title, $artist, $album, $genre, $year, $track,
                    $format, $size, $duration, $bitrate, $cover,
                    $status, $now, $now)
                ON CONFLICT(file_path) DO UPDATE SET
                    title = excluded.title,
                    artist = excluded.artist,
                    album = excluded.album,
                    genre = excluded.genre,
                    year = excluded.year,
                    track_number = excluded.track_number,
                    format = excluded.format,
                    file_size = excluded.file_size,
                    duration_ticks = excluded.duration_ticks,
                    bitrate_kbps = excluded.bitrate_kbps,
                    cover_data = excluded.cover_data,
                    status = excluded.status,
                    last_seen_utc = excluded.last_seen_utc;
                """;
            Add(command, "$path", track.FilePath);
            Add(command, "$title", track.Title);
            Add(command, "$artist", track.Artist);
            Add(command, "$album", track.Album);
            Add(command, "$genre", track.Genre);
            Add(command, "$year", (long)track.Year);
            Add(command, "$track", (long)track.TrackNumber);
            Add(command, "$format", track.Format);
            Add(command, "$size", track.FileSize);
            Add(command, "$duration", track.Duration.Ticks);
            Add(command, "$bitrate", track.BitrateKbps);
            Add(command, "$cover", track.CoverData ?? (object)DBNull.Value);
            Add(command, "$status", track.Status);
            Add(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Delete(string filePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tracks WHERE file_path = $path;";
        command.Parameters.AddWithValue("$path", filePath);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AudioTrack> LoadAllExisting()
    {
        var result = new List<AudioTrack>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_path, title, artist, album, genre, year, track_number,
                   format, file_size, duration_ticks, bitrate_kbps, cover_data, status
            FROM tracks
            ORDER BY artist COLLATE NOCASE, album COLLATE NOCASE, track_number, title COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!File.Exists(path)) continue;
            result.Add(new AudioTrack
            {
                FilePath = path,
                Title = reader.GetString(1),
                Artist = reader.GetString(2),
                Album = reader.GetString(3),
                Genre = reader.GetString(4),
                Year = checked((uint)reader.GetInt64(5)),
                TrackNumber = checked((uint)reader.GetInt64(6)),
                Format = reader.GetString(7),
                FileSize = reader.GetInt64(8),
                Duration = TimeSpan.FromTicks(reader.GetInt64(9)),
                BitrateKbps = reader.GetInt32(10),
                CoverData = reader.IsDBNull(11) ? null : (byte[])reader[11],
                Status = reader.GetString(12)
            });
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);
}
