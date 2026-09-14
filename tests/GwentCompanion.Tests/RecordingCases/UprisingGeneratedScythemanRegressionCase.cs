using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class UprisingGeneratedScythemanRegressionCase : IRecordingValidationCase
{
    public string Id => "uprising-generated-scytheman";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache=Path.Combine(project,"cache");
        var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var uprising=catalog.Single(card=>card.Id=="202574");
        var scytheman=catalog.Single(card=>card.Id=="202161");
        using var screenReader=new ScreenStateRecognizer();
        var titles=new PreviewTitleRecognizer(catalog); var ledger=new MatchVisionLedger();
        var at=new DateTimeOffset(2026,9,11,9,38,13,286,TimeSpan.FromHours(-4));
        var emitted=new List<VisionEvidenceEvent>();
        for(var index=0;index<3;index++)
        {
            var frame=definition.Load($"evidence-{index+1:D2}.png");
            var screen=await screenReader.AnalyzeAsync(frame);
            var sightings=await titles.RecognizeAsync(frame,screen,screenReader);
            if(index>0 && !sightings.Any(item=>item.Card.Id==scytheman.Id && item.Side==PlayerSide.Opponent))
                throw new InvalidOperationException("The retained Lyrian Scytheman title was missed.");
            emitted.AddRange(ledger.Observe(at.AddMilliseconds(index*200),screen,sightings,
                boardWasScanned:false,artworkWasScanned:true));
        }
        var play=emitted.SingleOrDefault(item=>item.Sighting.Card.Id==scytheman.Id) ??
            throw new InvalidOperationException("The retained sequence did not produce one Lyrian Scytheman preview episode.");
        var origins=new PlayProvenanceResolver(); origins.ObserveCurrentLeader(PlayerSide.Opponent,uprising);
        var origin=origins.Observe(play);
        if(origin.Provenance!=CardProvenance.Spawned || !origin.Reason.Contains("Uprising",StringComparison.Ordinal))
            throw new InvalidOperationException("Uprising's collectible Scytheman output was charged as an opponent starting card.");
        var deck=new LiveDeckTracker(PlayerSide.Opponent); deck.SetFactionPrior("Northern Realms");
        deck.ConsiderDirectPlay(scytheman,1-play.Sighting.Distance,play.ObservedAt,
            play.Description+" "+origin.Reason,origin.Provenance);
        if(deck.DeckBuildingObservations.Any(item=>item.Card.Id==scytheman.Id))
            throw new InvalidOperationException("Leader-generated Scytheman still contributed a card or provisions to the starting deck.");
    }
}
