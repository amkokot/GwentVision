using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

/// <summary>
/// Read-only corpus audit. It revisits every stored match transition with the current
/// text/title/result readers and emits a deterministic retention candidate list. The
/// historical journals remain immutable and are never treated as ground-truth labels.
/// </summary>
internal static class AllRecordingPixelAudit
{
    private sealed record RetentionCandidate(string File, string[] Reasons);

    public static async Task Run(string root, string[] args)
    {
        var project = Path.Combine(root, "GwentCompanion");
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.Combine(root, args[outputIndex + 1]) :
            Path.Combine(project, "diagnostics", "all-recording-pixel-audit.json");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var references = VisionReferenceLibrary.Load(catalog, Path.Combine(project, "cache"));
        var sessionReports = new List<object>();
        var retained = new List<RetentionCandidate>();
        var totalFrames = 0;

        foreach (var folder in Directory.GetDirectories(Path.Combine(project, "sessions")).Order(StringComparer.Ordinal))
        {
            var journal = Path.Combine(folder, "vision-observations.jsonl");
            if (!File.Exists(journal) || new FileInfo(journal).Length < 10_000) continue;
            var rows = File.ReadLines(journal)
                .Select(line => JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!)
                .ToArray();
            var files = Directory.GetFiles(folder, "frame-*.jpg")
                .ToDictionary(path => Path.GetFileNameWithoutExtension(path).Split('-')[2], StringComparer.Ordinal);
            var selected = Select(rows);
            using var ocr = new ScreenStateRecognizer();
            var hover = new HoverCardRecognizer(catalog);
            var titles = new PreviewTitleRecognizer(catalog);
            var choices = new DeckPlayChoiceRecognizer(references, catalog);
            var rating = new PostMatchMmrRecognizer();
            var leader = new LeaderAbilityRecognizer(catalog, Path.Combine(project, "assets", "vision", "leaders"));
            DateTimeOffset? priorAt = null;
            var reread = new List<object>();
            var missingPixels = 0;
            var addedExactTitles = 0;
            var changedExactTitles = 0;
            var choiceTitles = 0;
            var confirmedResults = 0;
            var confirmedLeaders = 0;

            foreach (var index in selected.Keys.Order())
            {
                var old = rows[index];
                var stamp = old.SampledAt.ToString("HHmmssfff");
                if (!files.TryGetValue(stamp, out var path)) { missingPixels++; continue; }
                if (priorAt is null || old.SampledAt - priorAt > TimeSpan.FromSeconds(3)) hover.Reset();
                priorAt = old.SampledAt;
                var pixels = Load(path);
                var screen = await ocr.AnalyzeAsync(pixels);
                var freshHover = await hover.ReadAsync(pixels, screen, old.SampledAt, ocr);
                var freshTitles = await titles.RecognizeAsync(pixels, screen, ocr);
                var freshChoice = await choices.ReadAsync(pixels, screen, ocr);
                var result = await rating.ReadAsync(pixels, screen, old.SampledAt, ocr);
                var leaderReading = leader.Observe(pixels, screen, old.SampledAt);
                var oldIds = old.Sightings.Where(sighting => sighting.Source == CardSightSource.PlayPreview)
                    .Select(sighting => sighting.Card.Id).Distinct(StringComparer.Ordinal).Order().ToArray();
                var freshIds = freshTitles.Where(sighting => sighting.Source == CardSightSource.PlayPreview)
                    .Select(sighting => sighting.Card.Id).Distinct(StringComparer.Ordinal).Order().ToArray();
                if (old.HoveredCard is null && freshHover.Card is not null) addedExactTitles++;
                if (old.HoveredCard is not null && freshHover.Card is not null && old.HoveredCard.Id != freshHover.Card.Id) changedExactTitles++;
                if (freshChoice?.HighlightedCard is not null) choiceTitles++;
                if (result.PostMatchMmr is not null || result.PostMatchRank is not null) confirmedResults++;
                if (leaderReading is not null) confirmedLeaders++;
                var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
                var reasons = selected[index].Order(StringComparer.Ordinal).ToArray();
                reread.Add(new
                {
                    File = relative,
                    old.SampledAt,
                    Reasons = reasons,
                    OldHover = old.HoveredCard?.Name,
                    FreshHover = freshHover.Card?.Name,
                    FreshHoverInPlayerHand = CardVisionPipeline.IsGeometricPlayerHandHover(freshHover.Card, screen, freshHover.TitleRegion),
                    OldPreviewIds = oldIds,
                    FreshPreviewIds = freshIds,
                    Choice = freshChoice?.HighlightedCard?.Name,
                    Leader = leaderReading?.Card.Name,
                    ResultMmr = result.PostMatchMmr,
                    ResultRank = result.PostMatchRank
                });
                retained.Add(new(relative, reasons));
                totalFrames++;
                if (totalFrames % 100 == 0) Console.WriteLine($"Audited {totalFrames} selected transition frames through {Path.GetFileName(folder)}.");
            }

            sessionReports.Add(new
            {
                Session = Path.GetFileName(folder),
                JournalRows = rows.Length,
                SelectedFrames = reread.Count,
                MissingPixels = missingPixels,
                AddedExactTitles = addedExactTitles,
                ChangedExactTitles = changedExactTitles,
                ChoiceTitles = choiceTitles,
                ConfirmedResults = confirmedResults,
                ConfirmedLeaders = confirmedLeaders,
                Frames = reread
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var report = new
        {
            Version = typeof(CardVisionPipeline).Assembly.GetName().Version?.ToString(),
            Scope = "Every nontrivial stored observation journal; current OCR/title/choice/leader/result readers on bounded hand-drop, overlay, action, opening and ending windows. Historical outputs are comparison data, not labels.",
            TotalSelectedFrames = totalFrames,
            RetentionCandidates = retained.GroupBy(item => item.File).Select(group => group.First()).OrderBy(item => item.File).ToArray(),
            Sessions = sessionReports
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(GameStateJournal.Json) { WriteIndented = true }));
        Console.WriteLine($"Saved {output}; {totalFrames} selected frames across {sessionReports.Count} sessions.");
    }

    private static SortedDictionary<int, HashSet<string>> Select(IReadOnlyList<CardVisionResult> rows)
    {
        var selected = new SortedDictionary<int, HashSet<string>>();
        void Add(int index, string reason)
        {
            if (index < 0 || index >= rows.Count) return;
            if (!selected.TryGetValue(index, out var reasons)) selected[index] = reasons = new(StringComparer.Ordinal);
            reasons.Add(reason);
        }
        for (var i = 0; i < Math.Min(12, rows.Count); i++) Add(i, "opening/leader");
        for (var i = Math.Max(0, rows.Count - 18); i < rows.Count; i++) Add(i, "ending/result");
        int? userHand = null, opponentHand = null;
        DateTimeOffset? userAt = null, opponentAt = null;
        var priorOverlay = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Events.Count > 0) Add(i, "recorded action evidence");
            if (row.Screen.IsCardSelectionOverlay != priorOverlay)
            {
                for (var j = i - 2; j <= i + 3; j++) Add(j, "choice-overlay transition");
                priorOverlay = row.Screen.IsCardSelectionOverlay;
            }
            if (row.Screen.MatchHudVisible == true && !row.Screen.IsCardSelectionOverlay && string.IsNullOrWhiteSpace(row.Screen.ScreenHeader))
            {
                if (row.Screen.UserHandCount is { } nextUser)
                {
                    if (userHand is { } prior && nextUser < prior && prior - nextUser <= 3 &&
                        userAt is { } seen && row.SampledAt - seen <= TimeSpan.FromSeconds(35))
                        AddWindow(rows, selected, row.SampledAt, "player-hand decrement");
                    userHand = nextUser; userAt = row.SampledAt;
                }
                if (row.Screen.OpponentHandCount is { } nextOpponent)
                {
                    if (opponentHand is { } prior && nextOpponent < prior && prior - nextOpponent <= 3 &&
                        opponentAt is { } seen && row.SampledAt - seen <= TimeSpan.FromSeconds(35))
                        AddWindow(rows, selected, row.SampledAt, "opponent-hand decrement");
                    opponentHand = nextOpponent; opponentAt = row.SampledAt;
                }
            }
        }
        return selected;
    }

    private static void AddWindow(IReadOnlyList<CardVisionResult> rows, SortedDictionary<int, HashSet<string>> selected,
        DateTimeOffset center, string reason)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var delta = (rows[i].SampledAt - center).TotalSeconds;
            if (delta < -5 || delta > 2) continue;
            if (!selected.TryGetValue(i, out var reasons)) selected[i] = reasons = new(StringComparer.Ordinal);
            reasons.Add(reason);
        }
    }

    private static PixelFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return BitmapFrameAdapter.ToPixelFrame(bitmap);
    }
}
