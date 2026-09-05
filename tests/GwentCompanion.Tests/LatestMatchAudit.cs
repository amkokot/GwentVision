using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class LatestMatchAudit
{
    private sealed record Row(string Frame, CardVisionResult Result);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void Run(string root, string? input)
    {
        input ??= Path.Combine(root, "GwentCompanion/diagnostics/v0.1.25-latest-streaming.json");
        var rows = JsonSerializer.Deserialize<Row[]>(File.ReadAllText(input), GameStateJournal.Json)!;
        PixelFrame Load(string relative)
        {
            using var stream = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", relative));
            return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
        }
        int Number(string file) => int.Parse(Path.GetFileName(file).Substring(6, 6));
        // Reviewed round-draw cards in the preview lane, not played cards.
        var draws = new HashSet<int> { 1250, 1332, 3585, 3597 };
        var hudChecks = new List<object>();
        foreach (var row in rows.Where(r => r.Result.Events.Any(e => e.Sighting.Source == CardSightSource.PlayPreview) || draws.Contains(Number(r.Frame))))
        {
            var visible = GwentVisualStateDetector.MatchHudVisible(Load(row.Frame)); var expected = !draws.Contains(Number(row.Frame));
            hudChecks.Add(new { row.Frame, ExpectedMatchHud = expected, Actual = visible });
            Check(visible == expected, "Draw/play HUD context mismatch: " + row.Frame);
        }
        var ledger = new MatchVisionLedger(); var events = new List<VisionEvidenceEvent>();
        var tracker = new GameStateTracker(); tracker.Reset("reviewed-latest");
        var measurements = new List<object>(); var wrong = 0; var compared = 0;
        var initial = new Dictionary<(PlayerSide, string), int>
        {
            [(PlayerSide.User,"132307")]=9, [(PlayerSide.User,"202183")]=3, [(PlayerSide.User,"201701")]=17,
            [(PlayerSide.User,"202196")]=1, [(PlayerSide.User,"200112")]=4,
            [(PlayerSide.Opponent,"202650")]=13, [(PlayerSide.Opponent,"202654")]=11, [(PlayerSide.Opponent,"203092")]=7,
            [(PlayerSide.Opponent,"202792")]=5, [(PlayerSide.Opponent,"203211")]=10
        };
        foreach (var row in rows)
        {
            var result = row.Result with { Screen = row.Result.Screen with { MatchHudVisible = GwentVisualStateDetector.MatchHudVisible(Load(row.Frame)) } };
            result = result with { Events = ledger.Observe(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned, result.HoveredCard, result.ArtworkWasScanned) };
            events.AddRange(result.Events); var state = GameStateVisionAdapter.Apply(tracker, result).After;
            // Full initial board was independently read as User 34 / Opponent 46.
            // This interval ends before the next play. Never compare a transition to a settled total.
            if (Number(row.Frame) <= 90)
            foreach (var reading in result.CardMeasurements ?? [])
            {
                if (reading.Power is null || !initial.TryGetValue((reading.Side, reading.CardId), out var expected)) continue;
                compared++; if (reading.Power.Value != expected) wrong++;
                measurements.Add(new { row.Frame, reading.Side, reading.CardId, Expected = expected, Actual = reading.Power.Value });
                Check(state.Cards.Any(c => c.Card.Id == reading.CardId && c.Location.Value.Controller == reading.Side &&
                    c.Power?.Value == reading.Power.Value && c.Power.At == reading.Power.At), "Measured power failed to reach the internal state");
            }
        }
        Check(wrong == 0 && compared >= 10, $"Latest measured-state audit: {compared} compared, {wrong} wrong");
        Check(!events.Any(e => e.Sighting.Source == CardSightSource.PlayPreview &&
            (e.ObservedAt.TimeOfDay >= new TimeSpan(11,27,35) && e.ObservedAt.TimeOfDay <= new TimeSpan(11,28,0) ||
             e.ObservedAt.TimeOfDay >= new TimeSpan(11,31,55) && e.ObservedAt.TimeOfDay <= new TimeSpan(11,32,10))), "Round draw became a play event");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var families = new CardAppearanceFamilies(catalog);
        var shupe = catalog.Where(c => c.Name == "Shupe: Mage").Select(families.Normalize).ToArray();
        Check(shupe.Select(c => c.Id).Distinct().Count() == 1 && shupe.All(c => !c.CanBeInStartingDeck && c.AbilityText is null), "Shared art invented an executable ability variant");
        // The old Vypper label was Lamp Djinn, not an original NG unit. Original saved
        // detections retain that mistake; fresh-pixel coverage lives in LatestFixesTests.
        var expectedOpponent = new[] { "Artorius Vigo", "Squirrel", "Abduction", "Van Moorlehem Hunter", "Henry var Attre", "Radeyah", "Tugo the Elder", "Korathi Heatwave", "Anna Henrietta", "Shupe's Day Off", "Shupe: Mage", "Buhurt", "Runemage", "Zoria Runestone" };
        var actual = events.Where(e => e.Sighting.Side == PlayerSide.Opponent).Select(e => e.Sighting.Card.Name).ToHashSet();
        var missing = expectedOpponent.Where(name => !actual.Contains(name)).ToArray();
        Check(events.Count(e => e.Sighting.Source == CardSightSource.PlayPreview && e.Sighting.Side == PlayerSide.Opponent &&
            e.Sighting.Card.Name == "Abduction") == 2, "Two reviewed Abduction plays became duplicate/missing episodes");
        var output = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.25-match-audit.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Session = "20260828-112523", Input = input, Scope = "Reviewed event identities and initial stable-board measurements; not complete-match ground truth or original-deck composition.",
            InputFrames = rows.Length, ComparedPowerReadings = compared, WrongPowerReadings = wrong,
            InitialReviewedScores = new { User = 34, Opponent = 46 },
            ReviewedRoundScores = new[] { new { Round = 1, User = 53, Opponent = 73 }, new { Round = 2, User = 23, Opponent = 16 }, new { Round = 3, User = 10, Opponent = 4 } },
            MissingReviewedOpponentIdentities = missing, HudChecks = hudChecks, PowerChecks = measurements,
            Events = events.Select(e => new { e.ObservedAt, e.Sighting.Side, e.Sighting.Source, e.Sighting.Card.Id, e.Sighting.Card.Name, e.Description }),
            Limitations = new[] { "Recording starts mid-round; earlier actions are unavailable except later history.",
                "Other application obscures much of the board around 11:26:58–11:27:17, with further shorter interruptions.",
                "History proves an identity/action, not its exact timestamp or original owner.",
                "Created/copied/tutored/controlled cards require provenance; the event identity list is NOT a starting deck.",
                "Most armor/statuses, some powers, row completeness and several triggered effects remain unmeasured/unmodeled.",
                "Shupe family recognition does not reveal the selected random ability from artwork alone." }
        }, GameStateJournal.Json));
        Console.WriteLine($"Latest audit: {compared} measured powers, {wrong} wrong; {hudChecks.Count} draw/play context checks; {expectedOpponent.Length - missing.Length}/{expectedOpponent.Length} reviewed opponent identities; missing={string.Join(", ", missing)}");
    }
}
