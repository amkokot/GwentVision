using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class MagicCompassDeliriumProvenanceRegressionCase : IRecordingValidationCase
{
    public string Id => "magic-compass-delirium-provenance";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog=GwentOneCardCatalog.Load(Path.Combine(project,"cache","gwent-one-cards.json"));
        var byId=catalog.ToDictionary(card=>card.Id);
        var expected=new[]{"203112","203246","152403","203214"};
        var at=new DateTimeOffset(2026,9,14,1,25,38,961,TimeSpan.FromHours(-4));
        var events=new List<VisionEvidenceEvent>();
        using var screenReader=new ScreenStateRecognizer();
        var titles=new PreviewTitleRecognizer(catalog);

        for(var index=0;index<expected.Length;index++)
        {
            var pixels=definition.Load($"evidence-{index+1:00}.png");
            var screen=await screenReader.AnalyzeAsync(pixels);
            var sighting=(await titles.RecognizeAsync(pixels,screen,screenReader))
                .SingleOrDefault(item=>item.Card.Id==expected[index] && item.Side==PlayerSide.Opponent)
                ?? throw new InvalidOperationException($"Retained preview did not recover {byId[expected[index]].Name}.");
            events.Add(new(at.AddSeconds(index*13),sighting,"Retained reviewed round-three preview"));
        }

        var deckPixels=definition.Load("evidence-01.png");
        var deckCount=await OpponentHudRecognizer.ReadOpponentDeckCandidateAsync(deckPixels,screenReader);
        if(deckCount!=2)
            throw new InvalidOperationException($"Magic Compass source frame no longer reads the reviewed opposing deck count of 2 (got {deckCount?.ToString()??"none"}).");
        var hud=new OpponentHudRecognizer(); var rawScreen=await screenReader.AnalyzeAsync(deckPixels);
        await hud.ReadAsync(deckPixels,rawScreen,at,screenReader);
        var confirmedScreen=await hud.ReadAsync(deckPixels,rawScreen,at.AddSeconds(1.1),screenReader);
        if(confirmedScreen.OpponentDeckCount!=2)
            throw new InvalidOperationException("The reviewed far-deck 2 did not survive the live two-sample HUD confirmation gate.");

        var origins=new PlayProvenanceResolver();
        origins.Observe(events[0],remainingDeckCount:deckCount);
        var compassOutput=origins.Observe(events[1]);
        origins.Observe(events[2]);
        var deliriumOutput=origins.Observe(events[3]);
        if(compassOutput.Provenance!=CardProvenance.Created ||
           !compassOutput.Reason.Contains("Magic Compass",StringComparison.Ordinal))
            throw new InvalidOperationException("Magic Compass' low-deck legendary generated output was charged as an original card.");
        if(deliriumOutput.Provenance!=CardProvenance.Created ||
           !deliriumOutput.Reason.Contains("Delirium",StringComparison.Ordinal))
            throw new InvalidOperationException("Delirium's guaranteed non-starting-deck output was charged as an original card.");

        var tracker=new LiveDeckTracker(PlayerSide.Opponent); tracker.SetFactionPrior("Skellige");
        tracker.ConsiderDirectPlay(byId["203246"],.99,events[1].ObservedAt,compassOutput.Reason,compassOutput.Provenance);
        tracker.ConsiderDirectPlay(byId["203214"],.99,events[3].ObservedAt,deliriumOutput.Reason,deliriumOutput.Provenance);
        if(tracker.DeckBuildingObservations.Any(item=>item.Card.Id is "203246" or "203214"))
            throw new InvalidOperationException("A generated or generation-ambiguous output still consumed a starting-deck slot or provisions.");

        origins.Reset(); origins.Observe(events[0],remainingDeckCount:3);
        if(origins.Observe(events[1]).Provenance!=CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Magic Compass' generation branch remained active with three cards in deck.");
        origins.Reset(); origins.Observe(events[0]);
        if(origins.Observe(events[1]).Provenance!=CardProvenance.Unknown)
            throw new InvalidOperationException("Magic Compass without a readable deck count asserted either branch as certain.");
        origins.Reset(); origins.Observe(events[0],remainingDeckCount:3);
        var ordinaryCompassPlay=new VisionEvidenceEvent(events[0].ObservedAt.AddSeconds(5),
            events[1].Sighting with {Card=byId["152303"]},"Ordinary non-gold top-three Compass play");
        if(origins.Observe(ordinaryCompassPlay).Provenance!=CardProvenance.ProbableStartingDeck)
            throw new InvalidOperationException("Magic Compass' ordinary non-gold deck-play branch was mislabeled as generated.");
    }
}
