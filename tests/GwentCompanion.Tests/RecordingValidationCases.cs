using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;

internal sealed class RecordingValidationCaseDefinition
{
    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Summary { get; init; } = "";
    public string AddedIn { get; init; } = "next";
    public RecordingValidationAnonymization? Anonymization { get; init; }
    public RecordingValidationCaseFile[] Files { get; init; } = [];

    [JsonIgnore]
    public string Directory { get; set; } = "";

    public string MediaPath(string file)
    {
        var entry = Files.SingleOrDefault(item => item.File.Equals(file, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Validation case {Id} does not declare {file}.");
        return Path.Combine(Directory, entry.File);
    }

    public PixelFrame Load(string file) => VisionEfficiencyTests.Load(MediaPath(file));
}

internal sealed record RecordingValidationAnonymization(string Status, string Version, string[] ProtectedRegions);
internal sealed record RecordingValidationCaseFile(string File, long Bytes, string Sha256, string[] Tags);

internal interface IRecordingValidationCase
{
    string Id { get; }
    Task RunAsync(string project, RecordingValidationCaseDefinition definition);
}

internal static class RecordingValidationCases
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task<(int Cases, int Files)> VerifyAndRunAsync(string project, IEnumerable<string> baselineFiles)
    {
        var root = Path.Combine(project, "tests", "recording-validation", "cases");
        Directory.CreateDirectory(root);
        var definitions = new List<RecordingValidationCaseDefinition>();
        var knownPaths = new HashSet<string>(baselineFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var manifestPath in Directory.EnumerateFiles(root, "case.json", SearchOption.AllDirectories).Order())
        {
            var caseDirectory = Path.GetDirectoryName(manifestPath)!;
            Check(Path.GetDirectoryName(caseDirectory)!.Equals(root, StringComparison.OrdinalIgnoreCase),
                "Validation cases must be one folder deep: " + caseDirectory);
            var definition = JsonSerializer.Deserialize<RecordingValidationCaseDefinition>(File.ReadAllText(manifestPath), GameStateJournal.Json)
                ?? throw new InvalidDataException("Validation case is empty: " + manifestPath);
            definition.Directory = caseDirectory;
            var folderId = Path.GetFileName(caseDirectory);
            Check(definition.SchemaVersion == 1, $"Unknown schema in validation case {folderId}.");
            Check(definition.Id == folderId && System.Text.RegularExpressions.Regex.IsMatch(definition.Id, "^[a-z0-9]+(?:-[a-z0-9]+)*$"),
                $"Validation case ID and folder must be the same lowercase kebab-case name: {folderId}.");
            Check(!string.IsNullOrWhiteSpace(definition.Summary), $"Validation case {folderId} needs a short summary.");
            Check(definition.Anonymization is { Status: "anonymized", Version.Length: > 0, ProtectedRegions.Length: > 0 },
                $"Validation case {folderId} needs a reviewed anonymization declaration.");
            Check(definition.Files.Length > 0, $"Validation case {folderId} contains no evidence.");

            foreach (var entry in definition.Files)
            {
                Check(entry.File == Path.GetFileName(entry.File) && Path.GetExtension(entry.File).ToLowerInvariant() is ".jpg" or ".png",
                    $"Validation case {folderId} media must be a JPG or PNG directly inside its case folder: {entry.File}.");
                var relative = Path.GetRelativePath(project, Path.Combine(caseDirectory, entry.File)).Replace('\\', '/');
                Check(knownPaths.Add(relative), "Duplicate validation path: " + relative);
                var info = new FileInfo(Path.Combine(caseDirectory, entry.File));
                Check(info.Exists && info.Length == entry.Bytes, "Missing or size-changed validation evidence: " + relative);
                using var stream = info.OpenRead();
                Check(Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase),
                    "Hash mismatch in validation evidence: " + relative);
            }
            definitions.Add(definition);
        }

        Check(definitions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == definitions.Count,
            "Validation case IDs must be unique.");
        var runners = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(IRecordingValidationCase).IsAssignableFrom(type))
            .Select(type => (IRecordingValidationCase)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Could not create validation runner " + type.Name)))
            .ToArray();
        Check(runners.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == runners.Length,
            "Each validation runner must have a unique ID.");
        foreach (var definition in definitions)
        {
            var runner = runners.SingleOrDefault(item => item.Id == definition.Id)
                ?? throw new InvalidOperationException($"Validation case {definition.Id} has no matching IRecordingValidationCase test class.");
            await runner.RunAsync(project, definition);
        }
        foreach (var runner in runners)
            Check(definitions.Any(item => item.Id == runner.Id), $"Validation runner {runner.Id} has no matching case.json.");
        return (definitions.Count, definitions.Sum(item => item.Files.Length));
    }
}
