using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class MissedSelfSpawnOriginalRegressionCase : IRecordingValidationCase
{
    public string Id => "missed-self-spawn-original";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var nekker = catalog.Single(card => card.Id == "132305");
        if (!CompanionCardRules.SpawnsBaseCopyOfSelfOnDeploy(nekker))
            throw new InvalidOperationException("Nekker's printed self-spawn rule was not parsed.");
        var references = VisionReferenceLibrary.Load(catalog, cache).Where(reference => reference.Card.Id == nekker.Id).ToArray();
        using var features = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"));
        var matcher = new CardArtMatcher(features.ArtReferences);
        var regions = new[] { new NormalizedRegion(.435,.153,.500,.296), new NormalizedRegion(.500,.153,.565,.296) };
        var frames = new[] { definition.Load("evidence-01.png"), definition.Load("evidence-02.png") };
        var distances = frames.SelectMany(frame => regions.Select(region => matcher.IdentityDistance(frame, region, nekker.Id))).ToArray();
        if (distances.Any(distance => distance > .58))
            throw new InvalidOperationException("The retained frames no longer contain two Nekker bodies: " +
                string.Join('/', distances.Select(distance => distance.ToString("F3"))));

        var screen = new GwentVisualObservation(GwentViewKind.Board,false,0,0,null) { MatchHudVisible=true };
        var deck = new LiveDeckTracker(PlayerSide.Opponent); deck.SetFactionPrior("Monsters");
        deck.ConsiderDirectPlay(nekker,.92,DateTimeOffset.UnixEpoch,"Board-only identity before origin resolution.",CardProvenance.Unknown);
        var copies = new ThinningCopyTracker();
        for(var index=0;index<frames.Length;index++)
        {
            var sightings=regions.Select((region,body) => new CardSighting(nekker,PlayerSide.Opponent,CardSightSource.Board,
                region,distances[index*2+body],1,"Retained exact-identity board body",NeedsTemporalConfirmation:true)).ToArray();
            copies.ObserveFrame(DateTimeOffset.UnixEpoch.AddSeconds(index*2),screen,sightings,true,[deck],_=>false);
        }
        var observed=deck.DeckBuildingObservations.SingleOrDefault(item=>item.Card.Id==nekker.Id);
        if(observed?.ObservedCopies!=1 || observed.Provenance!=CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Two self-spawn bodies did not establish exactly one Nekker original.");
        return Task.CompletedTask;
    }
}
