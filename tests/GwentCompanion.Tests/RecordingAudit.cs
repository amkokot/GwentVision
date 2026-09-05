using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

// Explicit offline diagnostic; no production thresholds or historical records change.
internal static class RecordingAudit
{
    public static async Task Mmr(string root)
    {
        var folder = Path.Combine(root, "GwentCompanion/sessions/20260831-141529");
        using var reader = new ScreenStateRecognizer();
        var mmr = new PostMatchMmrRecognizer(); var report = new List<object>();
        foreach (var path in Directory.GetFiles(folder, "frame-*.jpg").Order())
        {
            var stamp = Path.GetFileNameWithoutExtension(path).Split('-')[2];
            if (string.CompareOrdinal(stamp, "142829000") < 0 || string.CompareOrdinal(stamp, "142834000") > 0) continue;
            var time = DateTime.ParseExact(stamp, "HHmmssfff", null);
            var at = new DateTimeOffset(2026,8,31,time.Hour,time.Minute,time.Second,time.Millisecond,TimeSpan.FromHours(-4));
            var frame = VisionEfficiencyTests.Load(path); var screen = await reader.AnalyzeAsync(frame);
            var qualified = await mmr.ReadAsync(frame, screen, at, reader);
            var label = await reader.ReadAsync(frame, new(.43,.44,.58,.50));
            var numbers = await reader.ReadLinesAsync(frame, new(.46,.25,.54,.35), scale:2,enhance:false,smooth:true);
            var candidate = PostMatchMmrRecognizer.ParseRankedPanel(screen.ScreenHeader,label,numbers);
            report.Add(new { Frame=Path.GetFileName(path), At=at, screen.ScreenHeader, Label=label, Numbers=numbers, Candidate=candidate, Confirmed=qualified.PostMatchMmr });
        }
        File.WriteAllText(Path.Combine(root,"GwentCompanion/diagnostics/20260831-141529-audit/mmr-probe.json"),JsonSerializer.Serialize(report,GameStateJournal.Json));
        Console.WriteLine($"MMR probe: {report.Count} distinct retained frames; diagnostic only, no manual votes or substituted text.");
    }

    public static async Task Run(string root)
    {
        const string session = "20260831-141529";
        var project = Path.Combine(root, "GwentCompanion");
        var folder = Path.Combine(project, "sessions", session);
        var cache = Path.Combine(project, "cache");
        var output = Path.Combine(project, "diagnostics", session + "-audit");
        Directory.CreateDirectory(output);
        var protectedFiles = new[] { "session.json", "vision-observations.jsonl", "game-state-final.json", "live-value-ledger.json" }
            .Select(f => Path.Combine(folder, f)).Concat(new[] { "deck-library.json", "opponent-memory.json", "settings.json" }.Select(f => Path.Combine(cache, f))).ToArray();
        string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
        var before = protectedFiles.ToDictionary(f => f, Hash);
        var rows = File.ReadLines(Path.Combine(folder, "vision-observations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!).ToArray();
        var saved = JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(folder, "game-state-final.json")), GameStateJournal.Json)!;
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var families = new CardAppearanceFamilies(catalog);
        // Enumerate existing references without the library loader's optional premium extraction.
        var references = new List<(CardDefinition Card, string Path)>();
        foreach (var card in catalog.GroupBy(c => c.Id).Select(g => g.First()))
        {
            var portrait = Path.Combine(cache, "portraits", card.Id + ".jpg");
            if (File.Exists(portrait)) references.Add((families.Normalize(card), portrait));
            foreach (var kind in new[] { "premium-frames", "observed-art" })
            {
                var dir = Path.Combine(cache, kind, card.Id);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.jpg").Order())
                {
                    var metadata = Path.ChangeExtension(file, ".json");
                    if (kind == "observed-art" && File.Exists(metadata) && File.ReadAllText(metadata).Contains(session, StringComparison.Ordinal)) continue;
                    references.Add((families.Normalize(card), file));
                }
            }
        }
        var events = rows.SelectMany(r => r.Events).ToArray();
        var sightings = rows.SelectMany(r => r.Sightings.Select(s => (r.SampledAt, Sight: s))).ToArray();
        var identities = sightings.GroupBy(s => (s.Sight.Side, s.Sight.Card.Id)).Select(g => new
        {
            g.Key.Side, g.Key.Id, g.First().Sight.Card.Name, FirstSighting = g.Min(s => s.SampledAt),
            FirstEvent = events.Where(e => e.Sighting.Side == g.Key.Side && e.Sighting.Card.Id == g.Key.Id).Select(e => (DateTimeOffset?)e.ObservedAt).FirstOrDefault(),
            PreviewSightings = g.Count(s => s.Sight.Source == CardSightSource.PlayPreview), BoardSightings = g.Count(s => s.Sight.Source == CardSightSource.Board)
        }).ToArray();
        var trace = new List<string>(); var resolver = new DeckPlayResolutionTracker(catalog) { Trace = trace.Add };
        var resolved = rows.SelectMany(r => resolver.Observe(r.SampledAt, r.Screen, r.Events, r.DeckPlayChoices)).ToArray();
        var timeFiles = Directory.GetFiles(folder, "frame-*.jpg").Order().Select(path =>
        {
            var time = DateTime.ParseExact(Path.GetFileNameWithoutExtension(path).Split('-')[2], "HHmmssfff", null);
            return (Path: path, At: new DateTimeOffset(2026, 8, 31, time.Hour, time.Minute, time.Second, time.Millisecond, rows[0].SampledAt.Offset));
        }).ToArray();
        bool Between(DateTimeOffset at, string from, string to) => string.CompareOrdinal(at.ToString("HHmmss"), from) >= 0 && string.CompareOrdinal(at.ToString("HHmmss"), to) <= 0;
        // Reviewed-case windows, not automatically labelled ground truth.
        var artPaths = timeFiles.Where(f => Between(f.At, "141650", "141805") || Between(f.At, "142014", "142026"))
            .GroupBy(f => (int)(f.At - rows[0].SampledAt).TotalSeconds / 3).Select(g => g.First().Path).ToHashSet();
        var selected = timeFiles.Where(f => artPaths.Contains(f.Path) || Between(f.At, "142810", "142824") || Between(f.At, "142829", "142834")).ToArray();
        using var pipeline = new CardVisionPipeline(references, catalog, Path.Combine(output, "feature-cache"), VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck(saved.User.StartingDeckReference?.Cards.Select(c => c.Card.Id) ?? []);
        var opponentIds = sightings.Where(s => s.Sight.Side == PlayerSide.Opponent).Select(s => s.Sight.Card.Id).Distinct().ToArray();
        pipeline.SetLikelyOpponentCards(opponentIds);
        var fresh = new List<CardVisionResult>();
        foreach (var file in selected)
        {
            var prepared = await pipeline.PrepareAsync(VisionEfficiencyTests.Load(file.Path), file.At);
            fresh.Add(artPaths.Contains(file.Path) ? pipeline.RecognizePrepared(prepared, true) : prepared.TextResult);
            if (fresh.Count % 10 == 0) Console.WriteLine($"Audit pixels {fresh.Count}/{selected.Length}: {file.At:HH:mm:ss.fff}");
        }
        var freshTrace = new List<string>(); var freshResolver = new DeckPlayResolutionTracker(catalog) { Trace = freshTrace.Add };
        var freshResolved = new List<VisionEvidenceEvent>(); var previousAt = DateTimeOffset.MinValue;
        foreach (var r in fresh.Where(r => Between(r.SampledAt, "142810", "142824")))
        {
            var sourceEvents = events.Where(e => e.ObservedAt > previousAt && e.ObservedAt <= r.SampledAt && Between(e.ObservedAt, "142810", "142824")).ToArray();
            freshResolved.AddRange(freshResolver.Observe(r.SampledAt, r.Screen, sourceEvents, r.DeckPlayChoices)); previousAt = r.SampledAt;
        }
        var unchanged = protectedFiles.All(f => Hash(f) == before[f]);
        var report = new
        {
            Session = session, Version = typeof(CardVisionPipeline).Assembly.GetName().Version?.ToString(),
            Scope = "Saved-evidence audit plus fresh selected 1280px frames. Current cached references and hindsight opponent candidates; no trained-on-this-session art. Not live candidate reconstruction or paced performance test. Fresh tutor trace retains recognized source-event identities only.",
            SavedRows = rows.Length, SavedEvents = events.Length, Identities = identities,
            HoverTitles = rows.Where(r => r.HoveredCard is not null).GroupBy(r => r.HoveredCard!.Id).Select(g => new { Id = g.Key, g.First().HoveredCard!.Name, First = g.First().SampledAt, Count = g.Count() }),
            ReferenceCoverage = references.GroupBy(r => r.Card.Id).Where(g => opponentIds.Contains(g.Key)).Select(g => new { Id = g.Key, g.First().Card.Name, Paths = g.Select(r => Path.GetRelativePath(cache, r.Path)).ToArray() }),
            SavedTutorTrace = trace, SavedTutorResolutions = resolved, FreshTutorTrace = freshTrace, FreshTutorResolutions = freshResolved,
            PixelFrames = selected.Select(f => Path.GetFileName(f.Path)), Fresh = fresh, ProtectedFilesUnchanged = unchanged, ProtectedFileHashes = before
        };
        File.WriteAllText(Path.Combine(output, "pixel-audit.json"), JsonSerializer.Serialize(report, GameStateJournal.Json));
        Console.WriteLine($"Fresh audit: {fresh.Count} frames, saved tutor resolutions={resolved.Length}, fresh tutor resolutions={freshResolved.Count}, source files unchanged={unchanged}");
        if (!unchanged) throw new InvalidOperationException("Protected source files changed during diagnostic.");
    }
}
