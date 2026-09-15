using System.IO;
using GwentCompanion.Platform.Windows.Capture;

internal sealed class DiagnosticSessionRetentionValidationCase : IContributorValidationCase
{
    public string Id => "diagnostic-session-retention";
    public string Kind => "storage";
    public string Summary => "Automatic evidence retains only the newest games within its disk budget.";

    public Task RunAsync(ContributorValidationContext context)
    {
        var root = Path.Combine(Path.GetTempPath(), "GwentVision-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var paths = Enumerable.Range(1, 7).Select(index =>
            {
                var path = Path.Combine(root, $"202609{index:00}-120000");
                Directory.CreateDirectory(path);
                File.WriteAllBytes(Path.Combine(path, "evidence.bin"), new byte[32]);
                Directory.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddDays(index));
                return path;
            }).ToArray();

            var result = DiagnosticSessionRetention.Enforce(root, paths[^1], maximumSessions: 5, maximumBytes: 96);
            var remaining = Directory.GetDirectories(root).Select(Path.GetFileName).Order().ToArray();
            ContributorValidationContext.Check(result.RemovedSessions == 4,
                "Count and byte limits did not remove the expected number of oldest sessions.");
            ContributorValidationContext.Check(remaining.SequenceEqual(new[]
                { "20260905-120000", "20260906-120000", "20260907-120000" }),
                "Retention did not preserve the three newest sessions.");
            ContributorValidationContext.Check(Directory.Exists(paths[^1]),
                "The active or just-completed game was removed by retention.");
            ContributorValidationContext.Check(result.RetainedBytes <= 96,
                "Retained evidence remained over the configured byte ceiling.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Task.CompletedTask;
    }
}
