using SonicForge.Core.Models;
namespace SonicForge.Core.Services;
public interface ITrackScanner { IReadOnlyList<AudioTrack> ScanFiles(IEnumerable<string> files); }
