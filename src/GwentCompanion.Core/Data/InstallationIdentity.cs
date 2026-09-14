using System.Text.Json;

namespace GwentCompanion.Core.Data;

public sealed record InstallationIdentity(int Schema, Guid Id, DateTimeOffset CreatedAtUtc)
{
    /// <summary>Random identity plus first-use date. No hardware, account name or claimed download date.</summary>
    public static InstallationIdentity LoadOrCreate(string path)
    {
        InstallationIdentity Read()
        {
            var identity = JsonSerializer.Deserialize<InstallationIdentity>(File.ReadAllText(path));
            if (identity is not { Schema: 1 } || identity.Id == Guid.Empty || identity.CreatedAtUtc == default)
                throw new InvalidDataException("Unknown or damaged installation identity; restore it rather than silently replacing it.");
            return identity;
        }
        if (File.Exists(path)) return Read();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var created = new InstallationIdentity(1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, created); file.Flush(flushToDisk: true); }
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { return Read(); } // Another app instance registered first.
            return created;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
