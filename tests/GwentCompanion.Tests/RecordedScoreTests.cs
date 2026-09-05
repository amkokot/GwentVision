using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

/// <summary>
/// Latest real-opponent recording only. Reviewed pixel labels are independent of OCR/model output.
/// Component fixtures are explicitly partial; they must never be counted as end-to-end predictions.
/// </summary>
internal static class RecordedScoreTests
{
    private const string Session = "20260827-184848";
    private sealed record ScoreFrame(string File, int User, int Opponent);
    private sealed record Window(string Id, string Description, PlayerSide Actor, ScoreFrame Before, ScoreFrame After,
        string Scope, string[] Evidence, string? Fixture = null)
    {
        public int Swing => Actor == PlayerSide.User
            ? After.User - Before.User - (After.Opponent - Before.Opponent)
            : After.Opponent - Before.Opponent - (After.User - Before.User);
    }
    private static readonly Window[] Windows =
    [
        new("guardian", "Opening Duén Canell Guardian", PlayerSide.Opponent,
            new("frame-000317-184919897.jpg", 0, 0), new("frame-000369-184925097.jpg", 0, 5),
            "Reviewed body/entry component; round-start banner in before frame. Inactive stratagem and unknown offboard cards excluded.",
            ["Guardian enters at 5; order unavailable on entry; no unit targets before the play."], "guardian"),
        new("fruit", "Fruits of Ysgith leader", PlayerSide.User,
            new("frame-000402-184928407.jpg", 0, 5), new("frame-000487-184936895.jpg", 1, 5),
            "Reviewed leader-only component before Fiend is placed. Unknown offboard cards/stratagem excluded.",
            ["Leader charge 1 to 0; Fruit enters at 1; Fiend is still a placement ghost, not a board unit."], "fruit"),
        new("fiend", "Fiend plus existing Fruit Thrive", PlayerSide.User,
            new("frame-000487-184936895.jpg", 1, 5), new("frame-000519-184940090.jpg", 10, 5),
            "Reviewed placement/reaction component, not automatic state reconstruction. Leader already spent.",
            ["Guardian is on opponent melee; Fiend enters user ranged opposite an empty row at 8.",
             "Fruit increases 1 to 2. Net gain 8 + 1, with no opponent score change."], "fiend"),
        new("barbegazi", "Oneiromancy selects Barbegazi; Fruit thrives", PlayerSide.User,
            new("frame-004540-185622188.jpg", 1, 0), new("frame-004640-185632193.jpg", 8, 0),
            "Chosen tutor outcome only: fixture restricts the deck to Barbegazi; NOT Oneiromancy's maximum over the real deck. Inactive Mahakam Pass omitted.",
            ["frame-004561-185624284.jpg shows other tutor choices; their exclusion is a fixture restriction, not deck knowledge.",
             "frame-004587-185626888.jpg shows the selected 6-power Barbegazi preview; final Fruit is 2.",
             "Barbegazi's non-Zeal orders cannot be spent on entry."], "barbegazi"),
        new("wardancer", "Created Elven Wardancer deploy", PlayerSide.Opponent,
            new("frame-005336-185741788.jpg", 24, 0), new("frame-005389-185747087.jpg", 23, 4),
            "Generated unit subplay AFTER Backup Plan; not a prediction of the Create choice. Untouched Siren and inactive Mahakam Pass omitted.",
            ["Wardancer appears at 4 (unboosted); Barbegazi decreases 14 to 13.",
             "No other visible unit power changes in this subwindow. Siren remains 4; Fruit remains 6."], "wardancer"),
        new("ciaran", "Ciaran lock/movement in crowded round-one board", PlayerSide.Opponent,
            new("frame-003230-185411185.jpg", 37, 38), new("frame-003341-185422287.jpg", 37, 44),
            "Full chosen-action board replay is validated separately by --sequence-regression; this runner retains the original component-only fixture set.",
            ["Opponent gains 6; Siren moves from user ranged to melee and is locked. User total unchanged."]),
        new("backup-plan", "Backup Plan into Wardancer", PlayerSide.Opponent,
            new("frame-005260-185734181.jpg", 24, 0), new("frame-005389-185747087.jpg", 23, 4),
            "Full chosen Create chain is validated separately by --sequence-regression. Overlaps wardancer component; not an independent match.",
            ["Full window gains 5 net. The created identity/targeting history is not an input available before Create."]),
        new("schirru", "Call of the Forest, boosting and Schirrú order", PlayerSide.Opponent,
            new("frame-005689-185817083.jpg", 24, 0), new("frame-005785-185826692.jpg", 11, 13),
            "Chosen tutor / three separate leader charges / Order replay is validated by --sequence-regression. No combined-turn maximum is claimed. Before frame contains Schirrú preview, not a settled board unit.",
            ["Schirrú ends at 13; user Barbegazi at 13 is destroyed. Net swing 13 + 13 = 26.",
             "Opponent leader indicator changes from 3 to exhausted. Comparing this to printed Schirrú power alone would be invalid."])
    ];
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static PositionCard Card(CardDefinition card, string id, int? power = null, bool original = true) =>
        new(id, card.Id, power ?? card.Power, card.Power, card.PrintedArmor ?? 0,
            PlayRules.Compile(card).PrintedStatuses, 0, 0, original);
    private static GamePosition Put(GamePosition p, PlayerSide side, CardZone zone, BoardRow? row, params PositionCard[] cards) =>
        p with { Zones = p.Zones.Select(item => item.Side == side && item.Zone == zone && item.Row == row
            ? item with { Cards = cards.ToImmutableArray(), TotalCount = null, Complete = false } : item).ToImmutableArray() };
    private static GamePosition Partial() => GamePosition.EmptyKnown() with
    {
        Zones = GamePosition.EmptyKnown().Zones.Select(zone => zone with { Complete = false, TotalCount = null }).ToImmutableArray()
    };
    private static DateTimeOffset FrameTime(string file) => new(DateTime.ParseExact(
        "20260827" + Path.GetFileNameWithoutExtension(file).Split('-')[2], "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture), TimeSpan.FromHours(-4));

    public static async Task ProbeOcrAsync(string root)
    {
        using var reader = new ScreenStateRecognizer();
        foreach (var label in Windows.SelectMany(window => new[] { window.Before, window.After }).DistinctBy(frame => frame.File))
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, label.File));
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            foreach (var (side, top, bottom, expected) in new[] { ("U", .625, .69, label.User), ("O", .315, .385, label.Opponent) })
            {
                var values = new List<string>();
                var leftX = (int)(frame.Width * .945); var rightX = (int)(frame.Width * .985);
                var topY = (int)(frame.Height * top); var bottomY = (int)(frame.Height * bottom);
                var tileWidth = rightX - leftX; var tileHeight = bottomY - topY;
                var tiled = new byte[tileWidth * 3 * tileHeight * 4];
                for (var yy = 0; yy < tileHeight; yy++)
                for (var xx = 0; xx < tileWidth * 3; xx++)
                {
                    var pixel = frame.GetPixel(leftX + xx % tileWidth, topY + yy); var offset = (yy * tileWidth * 3 + xx) * 4;
                    tiled[offset] = pixel.Blue; tiled[offset + 1] = pixel.Green; tiled[offset + 2] = pixel.Red; tiled[offset + 3] = 255;
                }
                var repeated = await reader.ReadAsync(new PixelFrame(tileWidth * 3, tileHeight, tiled), new(0, 0, 1, 1));
                values.Add("repeated=" + repeated);
                foreach (var left in new[] { .93, .94, .945 })
                foreach (var scale in new[] { 2, 3, 4 })
                {
                    var crop = new NormalizedRegion(left, top, .995, bottom);
                    var text = string.Join(" ", (await reader.ReadLinesAsync(frame, crop, scale, enhance: false, whiteLetterMask: true, smooth: true)).Select(line => line.Text));
                    var raw = string.Join(" ", (await reader.ReadLinesAsync(frame, crop, scale, enhance: true)).Select(line => line.Text));
                    if (text.Length > 0 || raw.Length > 0) values.Add($"{left}/{scale}={text}|{raw}");
                }
                Console.WriteLine($"{label.File} {side} expected {expected}: {string.Join("; ", values)}");
            }
        }
    }
    public static async Task ScanScoresAsync(string root)
    {
        using var reader = new ScreenStateRecognizer();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "GwentCompanion/sessions", Session), "frame-*.jpg").Order().Where((_, i) => i % 40 == 0))
        {
            using var input = File.OpenRead(file);
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            var user = await reader.ReadAsync(frame, new(.93, .625, .995, .69));
            var opponent = await reader.ReadAsync(frame, new(.93, .315, .995, .385));
            if (user.Contains('6') || user.Contains('9') || opponent.Contains('6') || opponent.Contains('9')) Console.WriteLine($"{Path.GetFileName(file)}: {user}/{opponent}");
        }
    }

    public static void TrainDigits(string root)
    {
        HudDigitReader.Trace = Console.Error.WriteLine;
        var templates = new List<HudGlyph>();
        // Calibration only. Separate frames are used for held-out checks.
        foreach (var (file, user, score) in new[] {
            ("frame-000369-184925097.jpg", true, "0"), ("frame-000369-184925097.jpg", false, "5"),
            ("frame-000487-184936895.jpg", true, "1"), ("frame-004640-185632193.jpg", true, "8"),
            ("frame-003230-185411185.jpg", true, "37"), ("frame-005260-185734181.jpg", true, "24"),
            ("frame-003997-185527890.jpg", true, "39") })
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, file));
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            templates.AddRange(HudDigitReader.Train(frame, new(.94, user ? .625 : .315, .99, user ? .69 : .385), score));
        }
        // No stable scoreboard six was reviewed. A same-font card numeral is rejection-only:
        // do not mistake it for 8/9, but leave actual six-containing totals to literal OCR.
        using (var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, "frame-001340-185102185.jpg")))
        {
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            templates.AddRange(HudDigitReader.Train(frame, new(274.0 / 1280, 153.0 / 720, 296.0 / 1280, 188.0 / 720), "6").Select(item => item with { Emit = false }));
        }
        Console.WriteLine(JsonSerializer.Serialize(templates));
    }

    public static async Task FreshBoardAsync(string root)
    {
        var cache = Path.Combine(root, "GwentCompanion/cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var references = VisionReferenceLibrary.Load(catalog, cache);
        Console.WriteLine($"Fresh full-library board probe: {references.Count} references");
        using var pipeline = new CardVisionPipeline(references, catalog);
        var audit = new List<object>();
        foreach (var file in new[] { "frame-000466-184934801.jpg", "frame-000487-184936895.jpg", "frame-000495-184937690.jpg", "frame-000519-184940090.jpg", "frame-004540-185622188.jpg" })
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, file));
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            var result = await pipeline.AnalyzeAsync(frame, FrameTime(file), true);
            Console.WriteLine(file + ": " + string.Join("; ", result.Sightings.Where(s => s.Source == CardSightSource.Board).Select(s => $"{s.Side} {s.Card.Name} {s.Region}")));
            var foundFruit = result.Sightings.Any(s => s.Source == CardSightSource.Board && s.Card.Id == "202183" && s.Side == PlayerSide.User);
            Check(foundFruit == (file != "frame-000466-184934801.jpg"), "Fruit presence mismatch: " + file);
            int? fiendMaximum = null;
            if (file == "frame-000487-184936895.jpg")
            {
                var tracker = new GameStateTracker(); tracker.Reset("fresh-fiend");
                var state = GameStateVisionAdapter.Apply(tracker, result).After;
                var position = ThreatPositionBuilder.Build(CalculationPositionAdapter.FromObserved(state), catalog,
                    catalog.Single(card => card.Name == "Fiend"), null, [], []);
                fiendMaximum = new TacticalPlayEngine(catalog).Maximum(position.Position, position.HoverInstanceId, PlayerSide.User).MaximumPoints;
                Check(fiendMaximum == 9, "Fresh pixels plus conditional UI builder should include Fruit Thrive, not show +8.");
            }
            audit.Add(new { File = file, FoundFruit = foundFruit, FiendConditionalMaximum = fiendMaximum,
                IsArtworkCalibrationFrame = file == "frame-000519-184940090.jpg", result.Sightings,
                Note = "Fresh full-library recognition. Fiend hand selection supplied; board values remain conditional printed defaults, not measured stats." });
        }
        await File.WriteAllTextAsync(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-fresh-board-audit.json"), JsonSerializer.Serialize(audit, GameStateJournal.Json));
    }

    public static async Task HeldOutScoresAsync(string root)
    {
        using var reader = new ScreenStateRecognizer();
        var samples = new[] {
            new ScoreFrame("frame-000551-184943294.jpg", 10, 5), new("frame-002672-185315397.jpg", 34, 38),
            new("frame-001169-185045100.jpg", 12, 11), new("frame-001440-185112191.jpg", 20, 11),
            new("frame-001621-185130297.jpg", 17, 17), new("frame-002119-185220095.jpg", 21, 24),
            new("frame-002819-185330093.jpg", 39, 38), new("frame-004869-185655087.jpg", 21, 4),
            new("frame-003420-185430192.jpg", 37, 44), new("frame-004749-185643088.jpg", 8, 0),
            new("frame-004800-185648185.jpg", 8, 4), new("frame-004820-185650190.jpg", 8, 4),
            new("frame-004839-185652088.jpg", 12, 4), new("frame-004849-185653087.jpg", 9, 4),
            new("frame-005020-185710191.jpg", 21, 0), new("frame-003019-185350092.jpg", 37, 38),
            new("frame-003119-185400086.jpg", 37, 38), new("frame-003170-185405204.jpg", 37, 38) };
        var results = new List<object>(); var correct = 0; var unread = 0; var wrong = 0;
        foreach (var sample in samples)
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, sample.File));
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            var user = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, new(.90, .625, .995, .69), reader);
            var opponent = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, new(.90, .315, .995, .385), reader);
            foreach (var (actual, expected) in new[] { (user, sample.User), (opponent, sample.Opponent) })
            { if (actual is null) unread++; else if (actual == expected) correct++; else wrong++; }
            results.Add(new { sample.File, ExpectedUser = sample.User, ExpectedOpponent = sample.Opponent, User = user, Opponent = opponent });
            Console.WriteLine($"Held-out {sample.File}: {user}/{opponent}; expected {sample.User}/{sample.Opponent}");
        }
        // A blurred tutor overlay is not a scoreboard even though the background score is still faintly visible.
        using (var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, "frame-001300-185058195.jpg")))
        {
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            Check(HudDigitReader.ReadDefault(frame, new(.94, .625, .99, .69)) is null, "Blurred overlay generated a glyph score.");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-score-heldout.json"), JsonSerializer.Serialize(new
            { Session, Scope = "Reviewed frames excluded from glyph calibration, same match/resolution; not cross-match accuracy. Single-frame candidates, not live confirmation.", Correct = correct, Unread = unread, Wrong = wrong, Results = results }, GameStateJournal.Json));
        Console.WriteLine($"Held-out scores: {correct} correct, {unread} unread, {wrong} wrong / {samples.Length * 2}.");
        Check(wrong == 0 && correct >= 24, "Held-out score candidates failed precision/coverage guard.");
    }

    public static async Task RunAsync(string root)
    {
        var catalogPath = Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json");
        var catalog = GwentOneCardCatalog.Load(catalogPath);
        var engine = new TacticalPlayEngine(catalog);
        CardDefinition Get(string name) => catalog.Single(card => card.Name == name);
        GamePosition Fixture(string id)
        {
            var p = Partial() with { Round = id is "barbegazi" or "wardancer" ? 2 : 1 };
            if (id is "fruit" or "fiend")
                p = Put(p, PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, Card(Get("Duén Canell Guardian"), "guardian"));
            if (id is "fiend" or "barbegazi")
                p = Put(p, PlayerSide.User, CardZone.Board, id == "fiend" ? BoardRow.Ranged : BoardRow.Melee,
                    Card(Get("Gernichora's Fruit"), "fruit", 1, false));
            if (id == "fruit") return p with { User = p.User with { CurrentLeaderId = "202117", LeaderCharges = 1 } };
            if (id == "guardian") return Put(p, PlayerSide.Opponent, CardZone.Hand, null, Card(Get("Duén Canell Guardian"), "play"));
            if (id == "fiend") return Put(p, PlayerSide.User, CardZone.Hand, null, Card(Get("Fiend"), "play"));
            if (id == "barbegazi") return Put(Put(p, PlayerSide.User, CardZone.Hand, null, Card(Get("Oneiromancy"), "play")),
                PlayerSide.User, CardZone.Deck, null, Card(Get("Barbegazi"), "selected"));
            if (id == "wardancer")
            {
                p = Put(p, PlayerSide.User, CardZone.Board, BoardRow.Melee,
                    Card(Get("Barbegazi"), "barbegazi", 14), Card(Get("Gernichora's Fruit"), "fruit", 6, false));
                return Put(p, PlayerSide.Opponent, CardZone.Hand, null, Card(Get("Elven Wardancer"), "play", original: false));
            }
            throw new InvalidOperationException(id);
        }

        var results = new List<object>(); var assertions = new List<string>();
        foreach (var window in Windows)
        {
            PlaySearchResult? modeled = null; string? notation = null;
            if (window.Fixture is { } fixtureId)
            {
                var fixture = Fixture(fixtureId); notation = PositionNotation.Write(fixture);
                modeled = fixtureId == "fruit" ? engine.MaximumLeader(fixture, PlayerSide.User).Estimate
                    : engine.Maximum(fixture, "play", window.Actor);
                Check(modeled.MaximumPoints == window.Swing,
                    $"{window.Id}: recorded {window.Swing}, model {modeled.MaximumPoints}/{modeled.BestModeledPoints}; {string.Join("; ", modeled.Missing)}");
                Check(!modeled.ExactUnderSuppliedState && modeled.Assumptions.Count > 0,
                    "Partial recorded fixture was misrepresented as full exact state.");
                assertions.Add(window.Id);
            }
            results.Add(new { window.Id, window.Description, window.Actor, window.Before, window.After,
                RealizedNetSwing = window.Swing, window.Scope, window.Evidence, ReviewedComponentPosition = notation,
                ComponentModel = modeled, EndToEndValidated = false });
            Console.WriteLine($"Recorded {window.Id}: realized +{window.Swing}; " +
                (modeled is null ? "chosen chain tested in sequence regression" : $"reviewed component +{modeled.MaximumPoints}"));
        }

        // Spent/inactive effects are now modeled independently from their unsupported Deploy.
        var withPass = Put(Fixture("barbegazi"), PlayerSide.Opponent, CardZone.Board, BoardRow.Melee,
            Card(Get("Mahakam Pass"), "pass"));
        var fullBarbegazi = engine.Maximum(withPass, "play", PlayerSide.User);
        Check(fullBarbegazi.MaximumPoints == 7, "Inactive opposing Mahakam Pass incorrectly blocked an unrelated play.");
        var playPass = Put(Partial(), PlayerSide.User, CardZone.Hand, null, Card(Get("Mahakam Pass"), "play"));
        Check(engine.Maximum(playPass, "play", PlayerSide.User).MaximumPoints is null, "Unsupported Pass Deploy was treated as resolved.");
        // The actual selected card is not generally the maximum-valued tutor option.
        var alternative = Put(Fixture("barbegazi"), PlayerSide.User, CardZone.Deck, null,
            Card(Get("Barbegazi"), "selected"), Card(Get("Old Speartip"), "alternative"));
        var tutorMaximum = engine.Maximum(alternative, "play", PlayerSide.User);
        Check(tutorMaximum.MaximumPoints > Windows.Single(w => w.Id == "barbegazi").Swing,
            "Test failed to distinguish chosen tutor outcome from maximum available line.");

        // Audit the saved recognition stream without backfilling reviewed labels into detector evidence.
        var tracker = new GameStateTracker(); tracker.Reset("recorded-score-audit");
        var inventory = new ZoneInventoryTracker();
        var probeWindows = Windows.Where(window => window.Id is "fiend" or "barbegazi").ToArray();
        var probeStates = new Dictionary<string, (GameStateSnapshot State, ZoneHypothesis[] Inventory)>();
        GameStateSnapshot? lastBoard = null;
        var frames = 0; var previews = 0; var previewsWithScores = 0; var readyPrePositions = 0;
        var framesWithBoardCards = 0; var framesWithAllVisiblePowers = 0;
        var coverage = new List<object>();
        foreach (var line in File.ReadLines(Path.Combine(root, "GwentCompanion/sessions", Session, "vision-observations.jsonl")))
        {
            var frame = JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!;
            var update = GameStateVisionAdapter.Apply(tracker, frame); frames++;
            inventory.Observe(update);
            foreach (var action in frame.Events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview))
                inventory.ObserveSpecial(action.Sighting.Card, action.Sighting.Side, action.ObservedAt);
            if (update.After.Phase is GamePhase.Ended or GamePhase.RoundTransition || update.Events.Any(item => item.Kind == "PlayPreview")) lastBoard = null;
            if (update.After.Phase == GamePhase.Playing && !update.After.BoardObscured && frame.BoardWasScanned) lastBoard = update.After;
            foreach (var probe in probeWindows.Where(window => frame.SampledAt <= FrameTime(window.Before.File)))
            {
                if (lastBoard?.At is { } scanned && frame.SampledAt - scanned <= TimeSpan.FromSeconds(20))
                    probeStates[probe.Id] = (lastBoard, inventory.Entries.ToArray());
                else probeStates.Remove(probe.Id);
            }
            var pre = CalculationPositionAdapter.FromObserved(update.Before);
            var board = pre.Zones.Where(zone => zone.Zone == CardZone.Board).ToArray();
            var visible = board.SelectMany(zone => zone.Cards).ToArray();
            if (visible.Length > 0)
            {
                framesWithBoardCards++;
                if (visible.All(card => card.Power is not null)) framesWithAllVisiblePowers++;
            }
            foreach (var action in frame.Events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview))
            {
                previews++;
                var scores = update.Before.User.Score is not null && update.Before.Opponent.Score is not null;
                if (scores) previewsWithScores++;
                var ready = scores && board.All(zone => zone.Complete) && visible.All(card =>
                    card.Power is not null && card.BasePower is not null && card.Armor is not null && card.Statuses is not null);
                if (ready) readyPrePositions++;
                coverage.Add(new { action.ObservedAt, action.Sighting.Card.Name, action.Sighting.Side,
                    BeforeScoresPresent = scores, CompleteBoard = board.All(zone => zone.Complete),
                    KnownPowers = visible.Count(card => card.Power is not null), VisibleContacts = visible.Length,
                    CardHandlerSupported = PlayRules.Compile(action.Sighting.Card).Unmodeled is null,
                    ReadyForFullPrediction = ready });
            }
        }
        Check(frames > 2000 && previews > 0, "Latest recording was not replayed.");

        // Exercise the actual conditional UI builder without copying the reviewed Fruit/power labels into it.
        // The selected hand identity is an oracle here: older recordings lack the new hand-pointer evidence.
        var liveProbes = new List<object>();
        var analyzer = new ThreatAnalyzer(catalog);
        foreach (var probe in probeWindows)
        {
            if (!probeStates.TryGetValue(probe.Id, out var input))
            {
                liveProbes.Add(new { probe.Id, Status = "No eligible recent unobscured board" });
                Console.WriteLine($"Conditional UI replay {probe.Id}: no eligible board"); continue;
            }
            var hover = Get(probe.Id == "fiend" ? "Fiend" : "Oneiromancy");
            var position = ThreatPositionBuilder.Build(CalculationPositionAdapter.FromObserved(input.State), catalog,
                hover, null, [], input.Inventory);
            var estimate = analyzer.Analyze(position, ThreatAnalyzer.UserLead(input.State), []);
            liveProbes.Add(new { probe.Id, input.State.At, SelectedCardProvidedByReviewer = hover.Name,
                ActualSwing = probe.Swing, ConditionalMaximum = estimate.PlayerPlay.MaximumPoints,
                ErrorVersusRealized = estimate.PlayerPlay.MaximumPoints - probe.Swing,
                estimate.GapAfterPlay, estimate.PlayerPlay.Missing, estimate.Assumptions,
                Position = PositionNotation.Write(position.Position),
                Note = "Actual UI conditional builder with saved detections/inferred zones. No recovered own reference deck, no manually supplied board stats, no fresh artwork pass. This is not automatic hover validation." });
            Console.WriteLine($"Conditional UI replay {probe.Id}: {estimate.PlayerPlay.MaximumPoints?.ToString() ?? "unavailable"} versus realized {probe.Swing}");
        }

        // Re-run the production score crop on manually labeled original pixels, NOT generated images.
        using var reader = new ScreenStateRecognizer();
        var pixels = new List<object>(); var correct = 0; var unread = 0; var wrong = 0;
        foreach (var label in Windows.SelectMany(window => new[] { window.Before, window.After }).DistinctBy(frame => frame.File))
        {
            using var input = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, label.File));
            var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var frame = BitmapFrameAdapter.ToPixelFrame(bitmap);
            var user = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, new(.90, .625, .995, .69), reader);
            var opponent = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, new(.90, .315, .995, .385), reader);
            foreach (var (actual, expected) in new[] { (user, label.User), (opponent, label.Opponent) })
            { if (actual is null) unread++; else if (actual == expected) correct++; else wrong++; }
            pixels.Add(new { label.File, ReviewedUser = label.User, ReviewedOpponent = label.Opponent,
                RawUserScoreCandidate = user, RawOpponentScoreCandidate = opponent,
                Note = "Single-frame crop audit; not a temporally confirmed live score." });
            Console.WriteLine($"Score pixels {label.File}: {user}/{opponent}; reviewed {label.User}/{label.Opponent}");
        }
        var report = new
        {
            Session, CatalogSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))),
            ReviewedComponentMatches = assertions.Count, FullTurnAccuracyEstablished = false,
            Scope = "Purposive latest-match examples, not a random/held-out accuracy estimate. Five component fixtures manually supplied; no detector ground-truth backfill.",
            Windows = results,
            KnownInactiveArtifactGuard = new { fullBarbegazi.MaximumPoints, fullBarbegazi.Missing },
            TutorChoiceControl = new { ActualSelectedSwing = 7, AlternativeSubsetMaximum = tutorMaximum.MaximumPoints,
                Note = "Counterfactual unit test, not an additional recorded match or the complete actual tutor choice set." },
            AutomaticReplay = new { Frames = frames, PreviewEvents = previews, PreviewEventsWithBeforeScores = previewsWithScores,
                ReadyPrePositions = readyPrePositions, FramesWithBoardCards = framesWithBoardCards, FramesWithAllVisiblePowers = framesWithAllVisiblePowers,
                Note = "Saved older recognition stream replayed through current state adapter; NOT a fresh full artwork-detector replay.", Events = coverage },
            ConditionalUiReplay = liveProbes,
            ScorePixelAudit = new { Frames = pixels.Count, CorrectCandidates = correct, UnreadCandidates = unread, WrongCandidates = wrong, Results = pixels }
        };
        var output = Path.Combine(root, "GwentCompanion/diagnostics/v0.1.20-recorded-score-audit.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, GameStateJournal.Json));
        Console.WriteLine($"Recorded score audit: {assertions.Count} reviewed components agree; {readyPrePositions}/{previews} saved preview pre-positions ready for full prediction.");
        Console.WriteLine($"Pixel score candidates: {correct} correct, {unread} unread, {wrong} wrong. Report: {output}");
        Check(wrong == 0, "Recorded score OCR produced a wrong numeric total; see audit report.");
        Check(correct >= 26, "Score reading regressed below the improved recorded audit baseline; all-unread is not a passing audit.");
    }
}
