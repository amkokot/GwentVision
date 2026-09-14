using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

internal sealed class LocalMatchStorageValidationCase : IContributorValidationCase
{
    public string Id => "local-match-storage";
    public string Kind => "data-integrity";
    public string Summary => "Collect full detector deltas, keep hypotheses separate, and round-trip atomic compressed match checkpoints.";

    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool condition, string message) => ContributorValidationContext.Check(condition, message);
        var tracker = new GameStateTracker(); tracker.Reset("storage-test");
        var identityPath = context.PathFromRoot("diagnostics", "local-match-storage", Guid.NewGuid().ToString("N"), "installation.json");
        var identity = InstallationIdentity.LoadOrCreate(identityPath);
        Check(InstallationIdentity.LoadOrCreate(identityPath) == identity, "Installation identity changed on restart.");
        Check(identity.Id != Guid.Empty && identity.CreatedAtUtc.Offset == TimeSpan.Zero, "Identity lacks random ID / UTC first-use date.");
        var selectedCards = new List<DeckCard> { new(new("112101", "Selected", "Monsters", CardKind.Unit, 9), 2) };
        var selected = new DeckDefinition("fixture", "Selected deck", "Monsters", "Fruits of Ysgith", 12, selectedCards);
        var collector = new MatchAcquisition("fixture-v1", identity.Id, selected);
        selectedCards.Clear(); // Library mutations after capture starts cannot alter the saved list.
        var at = new DateTimeOffset(2026, 9, 9, 23, 59, 0, TimeSpan.FromHours(-4));
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: false);
        var menu = tracker.Observe(new(at, screen, [], [], false));
        collector.Observe(menu, screen, [], []);
        Check(collector.Snapshot() is null, "Menu-only capture created a match.");
        var card = new CardDefinition("112101", "Fixture card", "Neutral", CardKind.Unit, 9);
        MatchCard[] observations = [new(card.Id, 1, MatchCardEvidence.Observed, CardProvenance.Created, 230),
            new("203100", 1, MatchCardEvidence.Observed, CardProvenance.ProbableStartingDeck, 200)];
        screen = screen with { MatchHudVisible = true, OpponentHandCount = 8, UserScore = 17, OpponentScore = 22 };
        for (var i = 0; i < 180; i++)
        {
            var time = at.AddSeconds(i + 1);
            var sight = new CardSighting(card, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.4, .2, .5, .4), .03, .5);
            var frame = new VisualGameStateFrame(time, screen, [], [new(time, sight, "fixture")], false,
                Measurements: new(Round: new(1, time, 1, EvidenceKind.Visual, "fixture")));
            var update = tracker.Observe(frame);
            collector.Observe(update, screen, [], observations);
            // Duplicate detector deliveries must not add a second action.
            collector.Observe(update, screen, [], observations);
        }
        var first = collector.Snapshot()!;
        Check(first.GameDateUtc == new DateOnly(2026, 9, 10), "Game date was not normalized to UTC.");
        Check(tracker.Current.RecentEvents.Length == 128 && first.Actions.Length == 0,
            "Acquisition should consume detector deltas without storing an unnecessary play sequence.");
        Check(first.User.Reference.Single() is { CardId: "112101", Copies: 2 } && first.User.Faction == "Monsters",
            "Selected deck snapshot was lost or changed by the live library.");
        var mismatchSelected = new DeckDefinition("mismatch-fixture", "Selected deck", "Monsters", "Fruits of Ysgith", 12,
            [new(new("112101", "Selected", "Monsters", CardKind.Unit, 9), 2)]);
        var mismatchTracker = new GameStateTracker(); mismatchTracker.Reset("reference-mismatch", mismatchSelected);
        var mismatchCollector = new MatchAcquisition("fixture-v1", identity.Id, mismatchSelected);
        var mismatchCards = new[]
        {
            new CardDefinition("off-created", "Created card", "Monsters", CardKind.Unit, 5),
            new CardDefinition("off-one", "Different card one", "Monsters", CardKind.Unit, 5),
            new CardDefinition("off-two", "Different card two", "Monsters", CardKind.Unit, 5),
            new CardDefinition("off-three", "Different card three", "Monsters", CardKind.Unit, 5),
        };
        var mismatchObserved = new List<MatchCard>();
        for (var i = 0; i < mismatchCards.Length; i++)
        {
            var time = at.AddMinutes(1).AddSeconds(i);
            var mismatchSight = new CardSighting(mismatchCards[i], PlayerSide.User, CardSightSource.PlayPreview,
                new(.82, .42, .92, .66), .02, .5);
            var mismatchFrame = new VisualGameStateFrame(time, screen,
                [], [new(time, mismatchSight, "clear player play")], false,
                Measurements: new(Round: new(1, time, 1, EvidenceKind.Visual, "fixture")));
            mismatchObserved.Add(new(mismatchCards[i].Id, 1, MatchCardEvidence.Observed,
                i == 0 ? CardProvenance.Created : CardProvenance.ProbableStartingDeck, 240));
            mismatchCollector.Observe(mismatchTracker.Observe(mismatchFrame), screen, mismatchObserved.ToArray(), []);
            if (i == 2)
                Check(!mismatchCollector.UserReferenceRejected,
                    "A created card or two substitutions incorrectly discarded the selected deck.");
        }
        Check(mismatchCollector.UserReferenceRejected && mismatchCollector.Snapshot()!.User.Reference.Length == 0 &&
            mismatchCollector.Snapshot()!.User.Observations.Length == 4,
            "Three clear off-reference starting cards did not switch storage to detected player cards.");
        mismatchCollector.SetUserHypothesis([new("user-fill", 1, MatchCardEvidence.Inferred,
            CardProvenance.Unknown, 180)]);
        Check(mismatchCollector.Snapshot()!.User.Hypothesis.Any(card => card.CardId == "user-fill") &&
            mismatchCollector.Snapshot()!.User.Observations.Length == 4,
            "Post-match player completion did not remain separate from detected player cards.");
        Check(first.StartedAtUtc == at.AddSeconds(1).ToUniversalTime(), "First game observation time was not preserved.");
        Check(first.Opponent.Observations.Length == 1 && first.Opponent.Observations[0].Origin == CardProvenance.Created,
            "Visual identity was confused with starting-deck membership.");
        Check(first.Opponent.Hypothesis.Single().CardId == "203100" && first.Opponent.Hypothesis[0].Evidence == MatchCardEvidence.Inferred,
            "Reasoned tracker card was promoted to a known card.");
        collector.SetHypothesis([new("203286", 2, MatchCardEvidence.Inferred, CardProvenance.Unknown, 120)]);
        var guessed = collector.Snapshot()!;
        Check(guessed.Opponent.Observations.SequenceEqual(first.Opponent.Observations), "Hypothesis changed original observations.");
        collector.SetHypothesis([new("203285", 1, MatchCardEvidence.ManualHypothesis, CardProvenance.Unknown, 255)]);
        Check(!collector.Snapshot()!.Opponent.Hypothesis.Any(c => c.CardId == "203286"), "Retracted prediction survived replacement.");
        var invalidHypothesisRejected = false;
        try { collector.SetHypothesis(observations); } catch (ArgumentException) { invalidHypothesisRejected = true; }
        Check(invalidHypothesisRejected, "Hypothesis accepted observed claims.");
        var endTime = at.AddMinutes(10);
        var end = screen with { ScreenHeader = "VICTORY", MatchHudVisible = false, PostMatchMmr = new(2420, 8, true, "Faction", 2450) };
        collector.Observe(tracker.Observe(new(endTime, end, [], [], false)), end, [], observations);
        var final = collector.Snapshot(stopped: true)!;
        var partial = end with { PostMatchMmr = new(null, null, true, "Faction") };
        collector.Observe(tracker.Observe(new(endTime.AddSeconds(1), partial, [], [], false)), partial, [], observations);
        Check(collector.Snapshot() is { MmrAfter: 2420, MmrChange: 8, MmrPeak: 2450 }, "Partial result reading erased confirmed rating fields.");
        Check(final.Result == "VICTORY" && final.ResultObserved && final.CaptureStopped && final.MmrChange == 8, "Final metadata was lost.");
        Check(final.Rounds.Single() is { UserScore: 17, OpponentScore: 22, FinalConfirmed: false }, "Last observed scores were called confirmed final scores.");
        var encoded = CompactMatchCodec.Encode(final);
        var decoded = CompactMatchCodec.Decode(encoded);
        // GVM1 omits time and uncertainty; GVM2 omits only uncertainty.
        foreach (var legacyVersion in new[] { 1, 2 })
        using (var compressed = new BrotliStream(new MemoryStream(encoded[36..]), CompressionMode.Decompress))
        using (var raw = new MemoryStream())
        using (var legacy = new MemoryStream())
        {
            compressed.CopyTo(raw);
            var legacyPayload = raw.ToArray()[..^(legacyVersion == 1 ? 10 : 1)];
            legacy.Write(System.Text.Encoding.ASCII.GetBytes("GVM" + legacyVersion)); legacy.Write(SHA256.HashData(legacyPayload));
            using (var writer = new BrotliStream(legacy, CompressionLevel.SmallestSize, true)) writer.Write(legacyPayload);
            var old = CompactMatchCodec.Decode(legacy.ToArray());
            Check((legacyVersion == 1 ? old.StartedAtUtc is null : old.StartedAtUtc == final.StartedAtUtc) && !old.MmrUnconfirmed && old.MmrAfter == final.MmrAfter && old.User.Reference.SequenceEqual(final.User.Reference),
                "Legacy GVM1 records lost ratings/reference cards or invented a start time.");
        }
        var json = JsonSerializer.Serialize(final, GameStateJournal.Json);
        Check(JsonSerializer.Serialize(decoded, GameStateJournal.Json) == json, "Binary round trip changed evidence.");
        Check(encoded.Length < System.Text.Encoding.UTF8.GetByteCount(json), "Binary compression did not reduce the fixture.");
        Check(typeof(MatchAction).GetProperties().All(p => p.PropertyType != typeof(DateTimeOffset)), "Actions contain timestamps.");
        foreach (var corrupt in new[] { encoded[..20], encoded.ToArray() })
        {
            if (corrupt.Length > 36) corrupt[8] ^= 1;
            var rejected = false;
            try { CompactMatchCodec.Decode(corrupt); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Corrupt match was accepted.");
        }
        var folder = context.PathFromRoot("diagnostics", "local-match-storage", Guid.NewGuid().ToString("N"));
        var store = new LocalMatchStore(folder);
        var path = store.Save(first); store.Save(final); store.Save(first);
        Check(Directory.GetFiles(folder, "*.gvm", SearchOption.AllDirectories).Length == 1, "Checkpoints duplicated a match.");
        Check(LocalMatchStore.Read(path).CaptureStopped, "Stale checkpoint replaced a final record.");
        var second = new MatchAcquisition("fixture-v2", identity.Id);
        Check(second.MatchId != collector.MatchId, "Independent captures reused a match ID.");
        Check(decoded.InstallationId == identity.Id, "Compressed record lost its contributor ID.");
        File.WriteAllText(identityPath, "{\"Schema\":999}");
        var identityProtected = false;
        try { InstallationIdentity.LoadOrCreate(identityPath); } catch (InvalidDataException) { identityProtected = true; }
        Check(identityProtected, "Unknown identity format was silently replaced.");
        File.WriteAllText(path, "corrupt");
        var protectedFile = false;
        try { store.Save(final); } catch (InvalidDataException) { protectedFile = true; }
        Check(protectedFile && File.ReadAllText(path) == "corrupt", "Save overwrote corrupted original evidence.");
        var xaml = XDocument.Load(context.PathFromRoot("src", "GwentCompanion.App", "MainWindow.xaml"));
        Check(xaml.Descendants().Any(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Click") == "OpenMatchData_OnClick"), "Settings folder button missing.");
        File.WriteAllText(Path.Combine(folder, "fixture-readable.json"), json);
        File.WriteAllBytes(Path.Combine(folder, "fixture.gvm"), encoded);
        Console.WriteLine($"Local match storage: 180 detector events consumed without a stored sequence; frozen reference deck; {encoded.Length} compressed bytes versus {System.Text.Encoding.UTF8.GetByteCount(json)} JSON bytes.");
        return Task.CompletedTask;
    }
}
