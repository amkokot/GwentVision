namespace GwentCompanion.Core.Data;

public sealed class LocalMatchStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string Save(CompactMatch match)
    {
        var folder = Path.Combine(DirectoryPath, match.GameDateUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, match.MatchId.ToString("N") + ".gvm");
        var bytes = CompactMatchCodec.Encode(match);
        if (File.Exists(path))
        {
            var existingBytes = File.ReadAllBytes(path);
            var existing = CompactMatchCodec.Decode(existingBytes); // Corruption must not be silently overwritten.
            if (existing.Revision > match.Revision || existing.Revision == match.Revision && existing.CaptureStopped && !match.CaptureStopped) return path;
            if (existingBytes.AsSpan().SequenceEqual(bytes)) return path;
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    public static CompactMatch Read(string path) => CompactMatchCodec.Decode(File.ReadAllBytes(path));
}
