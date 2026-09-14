using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class NauzicaaIndependentPairRegressionCase : IRecordingValidationCase
{
    public string Id => "nauzicaa-independent-pair";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var brigade = catalog.Single(card => card.Id == "162310");
        if (brigade.IsGold || !CompanionCardRules.HasExplicitInherentDeckArrival(brigade))
            throw new InvalidOperationException("Nauzicaa Brigade is no longer an eligible repeated self-arrival bronze.");

        var references = VisionReferenceLibrary.Load(catalog, cache)
            .Where(reference => reference.Card.Id == brigade.Id).ToArray();
        using var features = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"));
        var matcher = new CardArtMatcher(features.ArtReferences);
        var frames = new[] { definition.Load("evidence-01.png"), definition.Load("evidence-02.png") };
        NormalizedRegion[][] regions =
        [
            [new(.4769577783, .1664615049, .5219577783, .2753615049),
             new(.5413990410, .3198296638, .5938990410, .4468796638)],
            [new(.5447425273, .3166479623, .5972425273, .4436979623),
             new(.5076813263, .1578970663, .5601813263, .2849470663)]
        ];
        var distances = new List<double>();
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        foreach (var region in regions[frameIndex])
            distances.Add(matcher.IdentityDistance(frames[frameIndex], region, brigade.Id));
        // One body is mid-damage animation in the second frame. Retain that hard
        // example, but require the other crops to remain substantially clearer.
        if (distances.Count(distance => distance <= .65) < 3 || distances.Max() > .75)
            throw new InvalidOperationException("The retained Nauzicaa Brigade pair lost its reference similarity: " +
                string.Join(", ", distances.Select(distance => distance.ToString("F3"))));

        CardSighting[] Sightings(int index) => regions[index].Select(region => new CardSighting(brigade,
            PlayerSide.Opponent, CardSightSource.Board, region, .40, 1,
            "Retained bounded automatic-arrival board fallback", NeedsTemporalConfirmation: true)).ToArray();
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };
        var deck = new LiveDeckTracker(PlayerSide.Opponent); deck.SetFactionPrior("Nilfgaard");
        var copies = new ThinningCopyTracker();
        if (copies.ObserveFrame(DateTimeOffset.UnixEpoch, screen, Sightings(0), true, [deck], _ => false) ||
            !copies.ObserveFrame(DateTimeOffset.UnixEpoch.AddSeconds(17), screen, Sightings(1), true, [deck], _ => false) ||
            deck.DeckBuildingObservations.SingleOrDefault(item => item.Card.Id == brigade.Id)?.ObservedCopies != 2)
            throw new InvalidOperationException("The two independently repeated Nauzicaa Brigade bodies collapsed to one deck identity.");
        return Task.CompletedTask;
    }
}
