using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Simulation;

internal static class RecordedReachFootageTests
{
    private sealed record Result(string Session, string Name, int Observed, int Modeled, int Error,
        int? BestLine, int? BestLineOverstatement, string Evidence);

    private static void Check(bool yes, string message) { if (!yes) throw new InvalidOperationException(message); }

    public static void Run(string root)
    {
        // These runners reconstruct the reviewed pre-position and execute the observed choices. Reusing them keeps
        // this report tied to executable rules rather than copying claimed results into a second fixture.
        RecordedSequenceTests.Run(root);
        LatestInteractionTests.Run(root);

        var directory = Path.Combine(root, "GwentCompanion", "diagnostics");
        var results = new List<Result>();
        Read(Path.Combine(directory, "v0.1.20-sequence-replay.json"), "20260827-184848", "Resolved", "BeforeFrame", "AfterFrame");
        Read(Path.Combine(directory, "v0.1.25-interactions.json"), "20260828-112523", "Modeled", "Frames", null);

        var exact = results.Count(item => item.Error == 0);
        var mae = results.Average(item => Math.Abs(item.Error));
        var maxima = results.Where(item => item.BestLine is not null).ToArray();
        var maxExact = maxima.Count(item => item.BestLineOverstatement == 0);
        var maxOverstatement = maxima.Average(item => item.BestLineOverstatement!.Value);
        var profile = ReachValidationProfile.ReviewedFootage;
        Check(results.Count == profile.ReviewedActions && exact == profile.ExactMatches && Math.Abs(mae - profile.MeanAbsoluteError) < 1e-9,
            "Bundled chosen-action reach calibration no longer matches the executable footage fixtures.");
        Check(maxima.Length == profile.MaximumComparisons && maxExact == profile.MaximumExactMatches &&
            Math.Abs(maxOverstatement - profile.MaximumMeanOverstatement) < 1e-9,
            "Bundled best-line reach calibration no longer matches the executable footage fixtures.");
        Check(maxima.All(item => item.BestLine >= item.Observed), "A best-line reach bound fell below a legal recorded action.");

        var output = Path.Combine(directory, "v0.2.7-reach-footage-validation.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            GeneratedAt = DateTimeOffset.Now,
            Scope = "Ten purposively reviewed action segments from two local recordings. The engine follows the recorded row, target, tutor/create, order and random selections. This validates these stateful interactions, not whole-catalog or automatic-vision accuracy.",
            Sessions = results.Select(item => item.Session).Distinct().ToArray(),
            ReviewedActions = results.Count,
            ExactChosenActions = exact,
            ChosenActionMeanAbsoluteError = mae,
            MaximumComparisons = maxima.Length,
            MaximumEqualsChosen = maxExact,
            MaximumMeanOverstatement = maxOverstatement,
            StateEvidence = new[]
            {
                "Board power/status and row placement", "Thrive and Assimilate reactions", "Tutor and Create choices",
                "Leader charges and Order sequencing", "Explicit random target/overkill", "Shield, Armor, Lock and Doomed",
                "Deck removal, graveyard transfer and banished destination", "End-turn effects"
            },
            Results = results
        }, GameStateJournal.Json));
        Console.WriteLine($"Reach footage: chosen actions {exact}/{results.Count} exact (MAE {mae:0.00}); best-line bound " +
            $"equal to chosen {maxExact}/{maxima.Length}, mean upper margin {maxOverstatement:0.00}.");

        void Read(string path, string session, string modeledProperty, string evidenceProperty, string? secondEvidenceProperty)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in document.RootElement.GetProperty("Cases").EnumerateArray())
            {
                var observed = item.GetProperty("ObservedSwing").GetInt32();
                var modeled = item.GetProperty(modeledProperty).GetInt32();
                int? maximum = item.TryGetProperty("Maximum", out var maximumElement) && maximumElement.ValueKind == JsonValueKind.Number
                    ? maximumElement.GetInt32() : null;
                var evidence = item.GetProperty(evidenceProperty).GetString() ?? "";
                if (secondEvidenceProperty is not null) evidence += " -> " + item.GetProperty(secondEvidenceProperty).GetString();
                results.Add(new(session, item.GetProperty("Name").GetString()!, observed, modeled, modeled - observed,
                    maximum, maximum - observed, evidence));
            }
        }
    }
}
