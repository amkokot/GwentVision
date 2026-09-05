using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class MatchDataAudit
{
    private const string Session = "20260828-154538";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    public static async Task ProbeAsync(string root)
    {
        var directory = Path.Combine(root, "GwentCompanion/sessions", Session);
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        using var reader = new ScreenStateRecognizer();
        var ratingReader = new PostMatchMmrRecognizer();
        foreach (var file in Directory.GetFiles(directory, "frame-*.jpg").Where(file => StringComparer.Ordinal.Compare(Path.GetFileName(file), "frame-005990") >= 0).Order())
        {
            using var stream = File.OpenRead(file);
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            var screen = await reader.AnalyzeAsync(frame);
            var stamp = Path.GetFileNameWithoutExtension(file).Split('-')[2];
            var time = DateTimeOffset.Parse("2026-08-28T00:00:00-04:00") + DateTime.ParseExact(stamp,"HHmmssfff",System.Globalization.CultureInfo.InvariantCulture).TimeOfDay;
            var result = await ratingReader.ReadAsync(frame, screen, time, reader);
            if (result.PostMatchMmr is not null) Console.WriteLine("MMRPROBE " + stamp + " " + JsonSerializer.Serialize(result.PostMatchMmr));
        }
        foreach (var key in new[] { "155229348", "155313548", "155528949", "155544040" })
        {
            var file = Directory.GetFiles(directory, "frame-*-" + key + ".jpg").Single();
            using var stream = File.OpenRead(file);
            var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
            var screen = await reader.AnalyzeAsync(frame);
            Console.WriteLine($"PROBE {key} {JsonSerializer.Serialize(screen)}");
            if (screen.TooltipRegion is { } region)
            foreach (var extra in new[] { .075, .12 })
            {
                var text = await reader.ReadAsync(frame, new(Math.Max(0, region.Left - .02), Math.Max(0, region.Top - extra), Math.Min(1, region.Right + .02), Math.Min(1, region.Bottom + .02)));
                Console.WriteLine($"crop {extra}: {HoverTitleReader.Read(text,catalog)?.Name} | {text.Replace('\n','|')}");
                if (extra == .075) foreach (var mask in new[] { false, true })
                    Console.WriteLine($"plain mask={mask}: " + string.Join('|', (await reader.ReadLinesAsync(frame, new(Math.Max(0, region.Left-.02), Math.Max(0, region.Top-.10), Math.Min(1, region.Right+.02), Math.Min(1,region.Top+.015)), scale:2, enhance:false, whiteLetterMask:mask, smooth:true)).Select(line => line.Text)));
            }
            Console.WriteLine($"score {await OpponentHudRecognizer.ReadScoreCandidateAsync(frame,new(.90,.625,.995,.69),reader)}/{await OpponentHudRecognizer.ReadScoreCandidateAsync(frame,new(.90,.315,.995,.385),reader)}; hand {await reader.ReadAsync(frame,new(.925,.94,.995,1))}");
            if (key == "155544040")
            {
                Console.WriteLine("ranked=" + await reader.ReadAsync(frame, new(.43,.44,.58,.50)));
                Console.WriteLine(JsonSerializer.Serialize(await reader.ReadLinesAsync(frame,new(.46,.25,.54,.36),scale:3)));
                foreach (var mask in new[] { false, true })
                    Console.WriteLine($"MMR mask={mask}: " + JsonSerializer.Serialize(await reader.ReadLinesAsync(frame,new(.46,.25,.54,.35),scale:2,enhance:false,whiteLetterMask:mask,smooth:true)));
            }
        }
    }
    public static async Task RunAsync(string root, bool pixels)
    {
        var directory = Path.Combine(root, "GwentCompanion/sessions", Session);
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        CardDefinition Card(string name) => catalog.First(card => card.Name == name);
        var saved = JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(directory, "game-state-final.json")), GameStateJournal.Json)!;
        var reference = saved.User.StartingDeckReference!;
        Check(reference.CardCount == 25 && reference.ProvisionTotal == 165, "Known player deck changed");
        var title = new HoverTitleIndex(catalog);
        Check(title.Read("FALSE\nCIRI\nHuman, Aristocrat, Agent")?.Name == "False Ciri", "Wrapped False Ciri became Ciri");
        Check(title.Read("CIRI\nWitcher")?.Name == "Ciri", "Standalone Ciri regressed");
        Check(title.Read("CIRI\nHuman, Aristocrat, Agent") is null, "Lost prefix misidentified False Ciri");
        Check(title.Read("no card title here") is null, "Invented hover identity");
        Check(OpponentHudRecognizer.ParseHandCrop("SELECT\nCANCEL 5/10") == 5 && OpponentHudRecognizer.ParseHandCrop("4 / 10") == 4, "HUD hints suppressed counter");
        foreach (var text in new[] { "15/10", "5/100", "15/100", "5/10 4/10", "abc5/10", "5/1O", "10" })
            Check(OpponentHudRecognizer.ParseHandCrop(text) is null, "Unsafe hand token: " + text);
        var at = DateTimeOffset.UnixEpoch;
        var known = new[] { new ObservedCard(Card("Fiend"), CardProvenance.ConfirmedStartingDeck, 1, at, "known deck", 2),
            new ObservedCard(Card("Portal"), CardProvenance.Created, 1, at), new ObservedCard(Card("False Ciri"), CardProvenance.Unknown, 1, at) };
        var usage = LiveValueLedger.Provisions(known, reference, null, new HashSet<string> { Card("Fiend").Id, Card("Portal").Id, Card("False Ciri").Id },
            new Dictionary<string, int> { [Card("Fiend").Id] = 1, [Card("Portal").Id] = 1 });
        Check(usage.SpentFloor == 4 && usage.Total == 165 && usage.RemainingCeiling == 161, "Unplayed duplicate / created / Disloyal enemy charged to player");
        Check(LiveValueLedger.Provisions(known, reference, null, new HashSet<string>()).SpentFloor == 0, "Known deck mistaken for spent deck");
        var results = File.ReadLines(Path.Combine(directory, "vision-observations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!).ToArray();
        var state = new GameStateTracker(); state.Reset("audit", reference);
        var values = new LiveValueLedger(); var origins = new PlayProvenanceResolver();
        var user = new LiveDeckTracker(PlayerSide.User); var opponent = new LiveDeckTracker(PlayerSide.Opponent);
        var events = new List<object>(); var timeline = new List<object>();
        foreach (var result in results)
        {
            foreach (var action in result.Events)
            {
                var origin = origins.Observe(action, reference);
                var tracker = action.Sighting.Side == PlayerSide.User ? user : opponent;
                tracker.ConsiderDirectPlay(action.Sighting.Card, 1 - action.Sighting.Distance, action.ObservedAt, action.Description + " " + origin.Reason, origin.Provenance);
                events.Add(new { action.ObservedAt, action.Sighting.Side, action.Sighting.Source, action.Sighting.Card.Name, action.Sighting.Card.Id, origin.Provenance, origin.Reason });
            }
            var update = GameStateVisionAdapter.Apply(state, result);
            values.Observe(update, catalog, reference.Cards.Select(item => item.Card), []);
            if (result.Events.Count > 0) timeline.Add(new { result.SampledAt,
                User = LiveValueLedger.Provisions(user.Observations, reference, null, values.Spent(PlayerSide.User), values.SpentCopies(PlayerSide.User)),
                Opponent = LiveValueLedger.Provisions(opponent.DeckBuildingObservations, null, 167, values.Spent(PlayerSide.Opponent), values.SpentCopies(PlayerSide.Opponent)) });
        }
        var userUsage = LiveValueLedger.Provisions(user.Observations, reference, null, values.Spent(PlayerSide.User), values.SpentCopies(PlayerSide.User));
        Check(userUsage.SpentFloor > 22 && userUsage.SpentFloor <= 165, "Known player board arrivals still missing from spend");
        Check(!user.DeckBuildingObservations.Any(item => item.Card.Name == "False Ciri"), "Enemy Disloyal unit became part of player deck");
        var knowledge = new OpponentKnowledge();
        knowledge.ConfigureRenfriBudget(Card("Renfri"), catalog.Where(card => card.CanBeInStartingDeck && card.Kind == CardKind.Unit && card.Provision > 0).Min(card => card.Provision),
            150 + GwentOneCardCatalog.StartingLeaders(catalog).Max(card => card.Provision));
        var renfri = knowledge.Assess(opponent.DeckBuildingObservations).Renfri;
        Check(renfri.State == ConstraintState.RuledOut, "Recorded original cards still allow a functional original Renfri within provisions");
        Check(knowledge.Assess(opponent.DeckBuildingObservations.Select(item => item.Card.Kind == CardKind.Special ? item with { Provenance = CardProvenance.Created } : item)).Renfri.State != ConstraintState.RuledOut,
            "Generated special costs excluded a Renfri original");
        var pixelReadings = new List<object>(); var newHover = 0; var newHandHover = 0; var newScores = 0; var changed = new List<object>();
        var mmrReadings = new List<PostMatchMmr>();
        if (pixels)
        {
            var files = Directory.GetFiles(directory, "frame-*.jpg").ToDictionary(file => Path.GetFileNameWithoutExtension(file).Split('-')[2]);
            using var reader = new ScreenStateRecognizer(); var hover = new HoverCardRecognizer(catalog); var hud = new OpponentHudRecognizer(); var mmr = new PostMatchMmrRecognizer();
            var reviewed = new Dictionary<string, (string Name, int User, int Opponent)>
            { ["155313548"] = ("False Ciri", 16, 4), ["155229348"] = ("Toad Prince", 0, 4), ["155528949"] = ("Hefty Helge", 13, 4) };
            foreach (var (old, i) in results.Select((result, i) => (result, i)))
            {
                var key = old.SampledAt.ToString("HHmmssfff");
                if (!files.TryGetValue(key, out var file)) throw new InvalidOperationException("Missing retained frame " + key);
                using var stream = File.OpenRead(file);
                var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
                // Same saved visual context; only changed hover/HUD readers rerun here.
                var fresh = await hover.ReadAsync(frame, old.Screen, old.SampledAt, reader);
                var screen = await hud.ReadAsync(frame, old.Screen with { UserScore = null, OpponentScore = null,
                    UserHandCount = null, OpponentHandCount = null, OpponentDeckCount = null }, old.SampledAt, reader);
                screen = await mmr.ReadAsync(frame, screen, old.SampledAt, reader);
                if (screen.PostMatchMmr is { } rating) { mmrReadings.Add(rating); Console.WriteLine($"MMR {key}: {JsonSerializer.Serialize(rating)}"); Check(rating.RatingAfter == 2406 && rating.SeasonPeak == 2431 && rating.RatingBefore is null && rating.Change is null, "Current/peak ranked rating confused with before/after or animation not settled"); }
                if (fresh.Card is not null) { newHover++; if (old.HoverInPlayerHand) newHandHover++; }
                if (screen.UserScore is not null && screen.OpponentScore is not null) newScores++;
                if (old.HoveredCard?.Id != fresh.Card?.Id) changed.Add(new { Frame = Path.GetFileName(file), Old = old.HoveredCard?.Name, New = fresh.Card?.Name, old.HoverInPlayerHand });
                if (reviewed.TryGetValue(key, out var expected))
                {
                    var region = old.Screen.TooltipRegion!.Value;
                    var text = string.Join('\n', (await reader.ReadLinesAsync(frame, new(Math.Max(0, region.Left - .02), Math.Max(0, region.Top - .10), Math.Min(1, region.Right + .02), Math.Min(1, region.Top + .015)), scale:2, enhance:false, smooth:true)).Select(line => line.Text));
                    var name = title.Read(text)?.Name;
                    var userScore = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, new(.90, .625, .995, .69), reader);
                    var opponentScore = await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, new(.90, .315, .995, .385), reader);
                    pixelReadings.Add(new { Frame = Path.GetFileName(file), Name = name, UserScore = userScore, OpponentScore = opponentScore, Expected = expected.Name, Text = text });
                    Console.WriteLine($"Reviewed {key}: {name ?? "(no title)"}, scores {userScore}/{opponentScore}; OCR: {text.Replace('\n', '|')}");
                    Check(name == expected.Name && (userScore is null || userScore == expected.User) && opponentScore == expected.Opponent, "Incorrect reviewed pixels: " + file);
                    if (key == "155528949") Check(userScore == 13 && opponentScore == 4, "Final live score with SELECT/CANCEL not read");
                }
                if (i % 300 == 299) Console.WriteLine($"Pixel audit {i+1}/{results.Length}: hover={newHover}, hand={newHandHover}, paired scores={newScores}");
            }
            Check(mmrReadings.Count > 0, "Ranked result panel not detected");
        }
        var output = Path.Combine(root, pixels ? "GwentCompanion/diagnostics/v0.1.29-match-data-audit.json" : "GwentCompanion/diagnostics/v0.1.29-match-data-audit-core.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Session, Scope = "All retained recognition records and saved state. Provision replay uses origin resolver + known player deck, not complete app mutation/watch UI. Pixel rerun changes hover/HUD readers only; not an all-card benchmark.",
            Frames = results.Length, PlayerDeck = new { reference.Name, reference.Leader, reference.CardCount, reference.ProvisionTotal },
            UserProvisionBefore = 22, UserProvisionAfter = userUsage, Renfri = renfri,
            PlayerUnaccountedIdentities = reference.Cards.Where(item => !values.Spent(PlayerSide.User).Contains(item.Card.Id)).Select(item => new { item.Card.Name, item.Count, item.Card.Provision }),
            OriginalCoverage = new { Tooltip = results.Count(row => row.Screen.HasCardTooltip), Hover = results.Count(row => row.HoveredCard is not null),
                HandHover = results.Count(row => row.HoveredCard is not null && row.HoverInPlayerHand),
                PairedScores = results.Count(row => row.Screen.UserScore is not null && row.Screen.OpponentScore is not null),
                BoardScans = results.Count(row => row.BoardWasScanned), Measurements = results.Sum(row => row.CardMeasurements?.Count ?? 0),
                MmrReadings = results.Where(row => row.Screen.PostMatchMmr is not null).Select(row => new { row.SampledAt, row.Screen.PostMatchMmr }),
                GraveyardInspections = results.Count(row => row.GraveyardInspection is not null), RuntimeValues = results.Count(row => row.RuntimeValue is not null), Descriptions = results.Count(row => row.Description is not null) },
            NewTextCoverage = new { PixelsRerun = pixels, Hover = newHover, HandHover = newHandHover, PairedScores = newScores, MmrReadings = mmrReadings },
            SavedFinal = new { saved.Phase, saved.Round, saved.User.Score, OpponentScore = saved.Opponent.Score,
                OpponentLeader = saved.Opponent.StartingLeader, OpponentStratagem = saved.Opponent.OpeningStratagemId, saved.Rows,
                CardContacts = saved.Cards.Length, ZoneEvidence = saved.ZoneEvidence.Length },
            Events = events, ProvisionTimeline = timeline, PixelReadings = pixelReadings, HoverChanges = changed,
            Caveats = new[] { "Known player deck does not reveal missed plays, targets or zone transitions.",
                "Spent is a committed-original-copy lower bound. Reveals and repeated casts do not spend another slot.",
                "Opponent allowance is not actual deck total; unread leader prevents exact capacity.",
                "No recorded carryover/statuses is not proof of zero/absence.",
                "Stale final score, partial board and unresolved graveyards prevent complete interaction validation.",
                "Hover coverage is not accuracy; only explicitly reviewed pixel cases have ground truth." }
        }, GameStateJournal.Json));
        Console.WriteLine($"PASS match-data audit: {results.Length} records; player used >= {userUsage.SpentFloor}/{reference.ProvisionTotal}p (was 22); output {output}");
    }
}
