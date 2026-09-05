using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class ShinmiriDetectorEvaluation
{
    private sealed record FrameResult(string File, bool MatchHud, string View, bool HasTooltip, double TooltipConfidence,
        string? TooltipRegion, string[] Sightings, string[] Events, string? Hovered, int? RuntimePower,
        bool TargetSeen, bool TargetEvent, bool SideCorrect);
    private sealed record WindowResult(string Window, string Interaction, string ExpectedSide, bool VisuallyAssessable,
        int Frames, bool TargetSeen, bool TargetEvent, bool SideCorrect, int TargetFrameHits, string[] DistinctSightings,
        FrameResult[] Results, string Notes);

    public static async Task<int> RunAsync(string root, string[] args)
    {
        var scanRoot = Path.Combine(root, "GwentCompanion", "cache", "video-scan", "shinmiri", "windows");
        if (!Directory.Exists(scanRoot)) throw new DirectoryNotFoundException(scanRoot);
        var cache = Path.Combine(root, "GwentCompanion", "cache");
        var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"))).ToArray();
        var references = VisionReferenceLibrary.Load(cards, cache);
        var windows = new List<WindowResult>();
        var filterIndex = Array.IndexOf(args, "--filter");
        var filter = filterIndex >= 0 && filterIndex + 1 < args.Length ? args[filterIndex + 1] : null;
        var candidatePrior = args.Contains("--candidate-prior");
        var candidateScoped = args.Contains("--scoped");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var pipeline = new CardVisionPipeline(references, cards, Path.Combine(cache, "recognition-features"),
            candidateScoped ? VisionReferenceScope.CandidateDecks : VisionReferenceScope.FullCatalog);
        if (filter is not null)
        {
            pipeline.HoverTrace = value => Console.WriteLine("HOVER " + value);
            pipeline.Trace = value => Console.WriteLine("ART " + value);
        }
        foreach (var directory in Directory.GetDirectories(scanRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (filter is not null && !Path.GetFileName(directory).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var evidencePath = Path.Combine(directory, "evidence.json");
            if (!File.Exists(evidencePath)) continue;
            using var evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
            var rootElement = evidence.RootElement;
            var interaction = Read(rootElement, "interaction") ?? "";
            var expectedSide = Read(rootElement, "side") ?? "Unknown";
            var notes = Read(rootElement, "notes") ?? Read(rootElement, "evidence") ?? "";
            var visuallyAssessable = !interaction.Equals("Heulyn", StringComparison.OrdinalIgnoreCase);
            var names = rootElement.TryGetProperty("frames", out var frameNames) ? frameNames :
                rootElement.TryGetProperty("retainedFrames", out var retained) ? retained : default;
            if (names.ValueKind != JsonValueKind.Array) continue;
            pipeline.Reset();
            pipeline.SetKnownPlayerDeck([]); pipeline.SetLikelyOpponentCards([]);
            if (candidatePrior && cards.FirstOrDefault(card => card.Name.Equals(interaction, StringComparison.OrdinalIgnoreCase)) is { } target)
            {
                // Approximate a 25-card live hypothesis, rather than an invalid one-card
                // FLANN index. Only the target is labelled; deterministic same-faction
                // distractors make the visual ratio test behave like the real app.
                var prior = new[] { target.Id }.Concat(cards.Where(card => card.Id != target.Id && card.CanBeInStartingDeck &&
                        card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact &&
                        (card.Faction == target.Faction || card.Faction.Equals("Neutral", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(card => card.Id, StringComparer.Ordinal).Take(24).Select(card => card.Id)).ToArray();
                if (ExpectedSide(expectedSide) == PlayerSide.User) pipeline.SetKnownPlayerDeck(prior);
                if (ExpectedSide(expectedSide) == PlayerSide.Opponent) pipeline.SetLikelyOpponentCards(prior);
            }
            var results = new List<FrameResult>();
            foreach (var name in names.EnumerateArray().Select(item => item.GetString()!).Where(name => name is not null))
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                using var input = File.OpenRead(path);
                var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var frame = BitmapFrameAdapter.ToPixelFrame(bitmap);
                var at = DateTimeOffset.UnixEpoch.AddSeconds(Timestamp(name));
                var result = await pipeline.AnalyzeAsync(frame, at, includeBoard: true);
                var targetSightings = result.Sightings.Where(item => item.Card.Name.Equals(interaction, StringComparison.OrdinalIgnoreCase)).ToArray();
                var hoveredTarget = result.HoveredCard?.Name.Equals(interaction, StringComparison.OrdinalIgnoreCase) == true;
                var targetEvents = result.Events.Where(item => item.Sighting.Card.Name.Equals(interaction, StringComparison.OrdinalIgnoreCase)).ToArray();
                var targetSeen = targetSightings.Length > 0 || hoveredTarget;
                var expected = ExpectedSide(expectedSide);
                var frameSideCorrect = expected is null || targetSightings.Any(item => item.Side == expected) ||
                    hoveredTarget && result.HoverInPlayerHand && expected == PlayerSide.User;
                results.Add(new(name, result.Screen.MatchHudVisible == true, result.Screen.View.ToString(), result.Screen.HasCardTooltip,
                    result.Screen.TooltipConfidence, result.Screen.TooltipRegion?.ToString(),
                    result.Sightings.Select(item => $"{item.Side}:{item.Source}:{item.Card.Name}:{item.Distance:F3}").ToArray(),
                    result.Events.Select(item => $"{item.Sighting.Side}:{item.Description}:{item.Sighting.Card.Name}").ToArray(),
                    result.HoveredCard?.Name, result.RuntimeValue?.Amount, targetSeen, targetEvents.Length > 0, frameSideCorrect));
            }
            var distinct = results.SelectMany(item => item.Sightings).Select(value => value[..value.LastIndexOf(':')])
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            var seen = results.Any(item => item.TargetSeen); var eventSeen = results.Any(item => item.TargetEvent);
            var windowSideCorrect = seen && results.Any(item => item.TargetSeen && item.SideCorrect);
            windows.Add(new(Path.GetFileName(directory), interaction, expectedSide, visuallyAssessable, results.Count, seen,
                eventSeen, windowSideCorrect, results.Count(item => item.TargetSeen), distinct, results.ToArray(), notes));
            Console.WriteLine($"{Path.GetFileName(directory)}: target={interaction} frames={results.Count} seen={seen} event={eventSeen} side={windowSideCorrect}");
        }
        var assessed = windows.Where(item => item.VisuallyAssessable).ToArray();
        var output = Path.Combine(root, "GwentCompanion", "diagnostics", candidateScoped ?
            "v0.2-shinmiri-detector-candidate-scoped.json" : candidatePrior ?
            "v0.1.34-shinmiri-detector-candidate-prior.json" : "v0.1.34-shinmiri-detector-evaluation.json");
        var summary = new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            ConfirmedWindows = windows.Count,
            VisuallyAssessableWindows = assessed.Length,
            TargetIdentityFound = assessed.Count(item => item.TargetSeen),
            CorrectSideFound = assessed.Count(item => item.SideCorrect),
            CommittedTargetEvent = assessed.Count(item => item.TargetEvent),
            EvidenceFrames = windows.Sum(item => item.Frames),
            ElapsedSeconds = watch.Elapsed.TotalSeconds,
            pipeline.CachedReferenceImages,
            pipeline.ComputedReferenceImages,
            pipeline.OcrCalls,
            CandidatePrior = candidatePrior,
            CandidateScoped = candidateScoped,
            Limitations = new[]
            {
                "Confirmed interaction windows test target recall and side assignment, not whole-frame precision: other detected cards are not exhaustively labelled.",
                "Sparse retained frames can miss a temporal confirmation even when the identity appears once.",
                "Heulyn is bookkeeping evidence from the opening graveyard count, not a visually exposed Heulyn card, and is excluded from artwork recall.",
                candidatePrior ? "Candidate-prior mode places the confirmed target among 24 deterministic same-faction/neutral distractors. It tests recoverability, not realistic end-to-end inference." : "Blind mode uses no target-derived candidate prior.",
            },
            Windows = windows,
        };
        File.WriteAllText(output, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = new StringBuilder("# Current detector on confirmed Shinmiri windows\n\n")
            .AppendLine($"- Target identity found: **{summary.TargetIdentityFound}/{summary.VisuallyAssessableWindows}** visually assessable windows")
            .AppendLine($"- Correct side found: **{summary.CorrectSideFound}/{summary.VisuallyAssessableWindows}**")
            .AppendLine($"- Committed target play/event: **{summary.CommittedTargetEvent}/{summary.VisuallyAssessableWindows}**")
            .AppendLine($"- Frames processed: **{summary.EvidenceFrames}**; elapsed **{summary.ElapsedSeconds:F1}s**")
            .AppendLine("\n| Window | Target | Expected side | Identity | Side | Event |\n|---|---|---|---:|---:|---:|");
        foreach (var item in windows)
            markdown.AppendLine($"| `{item.Window}` | {item.Interaction} | {item.ExpectedSide} | {(item.VisuallyAssessable ? item.TargetSeen ? "yes" : "no" : "n/a")} | {(item.VisuallyAssessable ? item.SideCorrect ? "yes" : "no" : "n/a")} | {(item.VisuallyAssessable ? item.TargetEvent ? "yes" : "no" : "n/a")} |");
        markdown.AppendLine("\nThis is a target-recall evaluation, not a precision score. Unlabelled additional sightings may be real cards in the same frame.");
        File.WriteAllText(Path.ChangeExtension(output, ".md"), markdown.ToString());
        Console.WriteLine($"DONE Shinmiri detector evaluation target={summary.TargetIdentityFound}/{summary.VisuallyAssessableWindows} side={summary.CorrectSideFound}/{summary.VisuallyAssessableWindows} events={summary.CommittedTargetEvent}/{summary.VisuallyAssessableWindows} output={output}");
        return 0;
    }

    private static string? Read(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static PlayerSide? ExpectedSide(string value) => value.StartsWith("User", StringComparison.OrdinalIgnoreCase) ? PlayerSide.User :
        value.StartsWith("Opponent", StringComparison.OrdinalIgnoreCase) ? PlayerSide.Opponent : null;
    private static double Timestamp(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(?:frame|before|during|after)-(?<stamp>\d{9})");
        return match.Success ? long.Parse(match.Groups["stamp"].Value, System.Globalization.CultureInfo.InvariantCulture) / 1000d : 0;
    }
}
