using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class KnickersPersistenceNegativeRegressionCase : IRecordingValidationCase
{
    public string Id => "knickers-persistence-negative";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var knickers = catalog.Single(card => card.Id == "202397");
        if (!CompanionCardRules.IsInherentDeckArrival(knickers) ||
            CompanionCardRules.HasExplicitInherentDeckArrival(knickers))
            throw new InvalidOperationException("Knickers must remain a vague-text automatic arrival.");

        using var detector = new FeatureCardRecognizer(VisionReferenceLibrary.Load(catalog, cache),
            Path.Combine(cache, "recognition-features"), scope: VisionReferenceScope.CandidateDecks);
        detector.SetLikelyOpponentCards([knickers.Id]);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true, OpponentHandCount = 5 };
        CardSighting Candidate(string file) => detector.Recognize(definition.Load(file), screen, true)
            .Where(sighting => sighting.Side == PlayerSide.Opponent && sighting.Card.Id == knickers.Id &&
                (sighting.Evidence ?? "").Contains("bounded automatic-arrival board fallback", StringComparison.Ordinal))
            .OrderBy(sighting => sighting.Distance).FirstOrDefault() ??
            throw new InvalidOperationException($"{file} no longer exercises the retained false Knickers candidate.");
        var first = Candidate("evidence-01.png");
        var second = Candidate("evidence-02.png");
        var at = DateTimeOffset.UnixEpoch;
        var negative = new MatchVisionLedger();
        if (negative.Observe(at, screen, [first], true).Count != 0 ||
            negative.Observe(at.AddSeconds(32), screen, [second], true).Count != 0)
            throw new InvalidOperationException("Vague-text persistence again promoted the Half-Elf Hunter body to Knickers without pile/title evidence.");

        // Tightening the no-counter path must not remove real Knickers recovery:
        // the same guarded pixels remain usable when a one-card conserved departure agrees.
        var positive = new MatchVisionLedger();
        positive.Observe(at, screen with { OpponentDeckCount = 13 }, [first], true);
        positive.Observe(at.AddSeconds(1), screen with { OpponentDeckCount = 12 }, [], false);
        var recovered = positive.Observe(at.AddSeconds(2), screen with { OpponentDeckCount = 12 }, [second], true);
        if (recovered.Count != 1 || recovered[0].Sighting.Card.Id != knickers.Id)
            throw new InvalidOperationException("A counter-corroborated Knickers arrival was lost while removing the persistence false positive.");

        var impossible = new ProvisionUsage(150, 167, 17, "fixture", 20, 5, 3.4, false);
        if (!impossible.Conflicts || !impossible.RemainingReadout.Contains("conflicts", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provision UI did not reject 17 remaining provisions for five minimum-4 slots.");
        return Task.CompletedTask;
    }
}
