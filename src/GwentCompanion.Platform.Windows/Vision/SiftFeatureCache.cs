using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

internal sealed record CachedSiftFeatures(int Width, int Height, KeyPoint[] Points, Mat Descriptors);

/// <summary>Lossless, disposable acceleration data. Never stores card metadata or changes feature limits.</summary>
internal sealed class SiftFeatureCache(string directory)
{
    private const string Recipe = "sift-v1:500/300,3,.018,10,1.6;smallx2;variant=.4,.65,1;static=.5,1;area-cubic";
    public static string Key(string image, bool variant)
    {
        using var source = File.OpenRead(image);
        var content = Convert.ToHexString(SHA256.HashData(source));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Recipe + Cv2.GetVersionString() + variant + content)));
    }

    public IReadOnlyList<CachedSiftFeatures>? Load(string key)
    {
        var result = new List<CachedSiftFeatures>();
        try
        {
            var path = Path.Combine(directory, key + ".gz");
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip);
            if (reader.ReadInt32() != 0x47564631 || reader.ReadString() != key) throw new InvalidDataException("Feature cache header");
            var count = reader.ReadInt32();
            if (count is < 0 or > 3) throw new InvalidDataException("Feature cache scales");
            for (var i = 0; i < count; i++)
            {
                var width = reader.ReadInt32(); var height = reader.ReadInt32(); var rows = reader.ReadInt32();
                if (width is < 1 or > 4096 || height is < 1 or > 4096 || rows is < 8 or > 2048) throw new InvalidDataException("Feature cache dimensions");
                var points = new KeyPoint[rows];
                for (var p = 0; p < rows; p++) points[p] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32(), reader.ReadInt32());
                var bytes = reader.ReadBytes(rows * 128 * sizeof(float));
                if (bytes.Length != rows * 128 * sizeof(float)) throw new EndOfStreamException();
                var descriptors = new Mat(rows, 128, MatType.CV_32FC1);
                Marshal.Copy(bytes, 0, descriptors.Data, bytes.Length);
                result.Add(new(width, height, points, descriptors));
            }
            if (reader.Read() != -1) throw new InvalidDataException("Trailing feature data");
            return result;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            foreach (var item in result) item.Descriptors.Dispose();
            return null; // Corrupt/stale/unavailable cache is a miss, never a missing reference.
        }
    }

    public void Save(string key, IReadOnlyList<CachedSiftFeatures> features)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, key + ".gz");
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var file = File.Create(temporary))
            using (var zip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(zip)) Write(writer, key, features);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Recognition remains fully usable without the cache. */ }
        finally
        {
            if (temporary is not null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Write(BinaryWriter writer, string key, IReadOnlyList<CachedSiftFeatures> features)
    {
        writer.Write(0x47564631); writer.Write(key); writer.Write(features.Count);
        foreach (var feature in features)
        {
            writer.Write(feature.Width); writer.Write(feature.Height); writer.Write(feature.Points.Length);
            foreach (var p in feature.Points)
            { writer.Write(p.Pt.X); writer.Write(p.Pt.Y); writer.Write(p.Size); writer.Write(p.Angle); writer.Write(p.Response); writer.Write(p.Octave); writer.Write(p.ClassId); }
            var bytes = new byte[feature.Descriptors.Rows * feature.Descriptors.Cols * sizeof(float)];
            Marshal.Copy(feature.Descriptors.Data, bytes, 0, bytes.Length); writer.Write(bytes);
        }
    }
}
