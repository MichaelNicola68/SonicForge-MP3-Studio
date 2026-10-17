using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicForge.App;

public sealed record CdTextResult(string Album, string AlbumArtist, IReadOnlyDictionary<int, string> Titles)
{
    public bool HasData => !string.IsNullOrWhiteSpace(Album) || !string.IsNullOrWhiteSpace(AlbumArtist) || Titles.Count > 0;
}

public static class CdTextReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint IoctlCdromReadTocEx = 0x00024054;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    public static CdTextResult TryRead(string driveRoot)
    {
        try
        {
            var letter = char.ToUpperInvariant(driveRoot.Trim()[0]);
            using var device = CreateFile($@"\\.\{letter}:", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (device.IsInvalid) return Empty();

            // CDROM_READ_TOC_EX: Format 5 = CD-TEXT, remaining fields zero.
            var query = new byte[] { 5, 0, 0, 0 };
            var buffer = new byte[64 * 1024];
            if (!DeviceIoControl(device, IoctlCdromReadTocEx, query, 4, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero) || returned < 22)
                return Empty();

            var album = new StringBuilder();
            var albumArtist = new StringBuilder();
            var titles = new Dictionary<int, StringBuilder>();
            for (var offset = 4; offset + 17 < returned; offset += 18)
            {
                var packType = buffer[offset];
                var track = buffer[offset + 1];
                if (packType is not (0x80 or 0x81)) continue;
                var text = Encoding.Latin1.GetString(buffer, offset + 4, 12).Replace('\0', ' ').Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (packType == 0x80 && track == 0) Append(album, text);
                else if (packType == 0x81 && track == 0) Append(albumArtist, text);
                else if (packType == 0x80 && track > 0)
                {
                    if (!titles.TryGetValue(track, out var builder)) titles[track] = builder = new StringBuilder();
                    Append(builder, text);
                }
            }
            return new CdTextResult(album.ToString().Trim(), albumArtist.ToString().Trim(), titles.ToDictionary(x => x.Key, x => x.Value.ToString().Trim()));
        }
        catch { return Empty(); }
    }

    private static void Append(StringBuilder builder, string text)
    {
        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1])) builder.Append(' ');
        builder.Append(text);
    }

    private static CdTextResult Empty() => new("", "", new Dictionary<int, string>());
}
