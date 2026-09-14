using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class BraathensCreatedEmissaryRegressionCase : IRecordingValidationCase
{
    public string Id => "braathens-created-emissary";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var cache=Path.Combine(project,"cache");
        var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var braathens=catalog.Single(card=>card.Id=="202658");
        var emissary=catalog.Single(card=>card.Id=="162314");
        var evidence=new[]{definition.Load("evidence-02.png"),definition.Load("evidence-03.png")};

        using var screenReader=new ScreenStateRecognizer();
        var titles=new PreviewTitleRecognizer(catalog); var titleLedger=new MatchVisionLedger();
        var at=new DateTimeOffset(2026,9,11,10,20,15,753,TimeSpan.FromHours(-4));
        var titleEvents=new List<VisionEvidenceEvent>();
        for(var index=0;index<evidence.Length;index++)
        {
            var screen=await screenReader.AnalyzeAsync(evidence[index]);
            var sightings=await titles.RecognizeAsync(evidence[index],screen,screenReader);
            if(!sightings.Any(item=>item.Card.Id==emissary.Id && item.Side==PlayerSide.Opponent))
                throw new InvalidOperationException("The retained exact Emissary preview was missed.");
            titleEvents.AddRange(titleLedger.Observe(at.AddMilliseconds(index*200),screen,sightings,
                boardWasScanned:false,artworkWasScanned:true));
        }
        var preview=titleEvents.SingleOrDefault(item=>item.Sighting.Card.Id==emissary.Id) ??
            throw new InvalidOperationException("The retained sequence did not emit one Emissary preview episode.");

        var references=VisionReferenceLibrary.Load(catalog,cache).Where(reference=>reference.Card.Id==braathens.Id).ToArray();
        using var features=new FeatureCardRecognizer(references,Path.Combine(cache,"recognition-features"));
        var matcher=new CardArtMatcher(features.ArtReferences);
        var braathensRegion=new NormalizedRegion(.500,.307,.569,.465);
        var distance=matcher.IdentityDistance(evidence[0],braathensRegion,braathens.Id);
        if(distance>.30)
            throw new InvalidOperationException($"The retained same-frame Braathens body was no longer recognized ({distance:F3}).");

        var board=new VisionEvidenceEvent(preview.ObservedAt,
            new(braathens,PlayerSide.Opponent,CardSightSource.Board,braathensRegion,.08,1,
                "Retained same-batch exact Braathens board body"),"Retained same-batch exact Braathens board body");
        IReadOnlyList<VisionEvidenceEvent> batch=[preview,board]; // Preserve the problematic detector order.
        var origins=new PlayProvenanceResolver(); origins.PrimeCoTemporalBoardCreations(batch);
        var origin=origins.Observe(preview);
        if(origin.Provenance!=CardProvenance.Created || !origin.Reason.Contains("Braathens",StringComparison.Ordinal))
            throw new InvalidOperationException("Braathens' created Emissary was charged as an opponent starting card.");

        var deck=new LiveDeckTracker(PlayerSide.Opponent); deck.SetFactionPrior("Nilfgaard");
        deck.ConsiderDirectPlay(emissary,1-preview.Sighting.Distance,preview.ObservedAt,
            preview.Description+" "+origin.Reason,origin.Provenance);
        if(deck.DeckBuildingObservations.Any(item=>item.Card.Id==emissary.Id))
            throw new InvalidOperationException("Created Emissary still contributed a starting-deck slot or provisions.");
    }
}
