using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class BoardSpawnThenHandOriginalRegressionCase : IRecordingValidationCase
{
    public string Id => "board-spawn-then-hand-original";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog=GwentOneCardCatalog.Load(Path.Combine(project,"cache","gwent-one-cards.json"));
        var manor=catalog.Single(card=>card.Id=="203080"); var damsel=catalog.Single(card=>card.Id=="203082");
        using var reader=new ScreenStateRecognizer(); var titles=new PreviewTitleRecognizer(catalog); var ledger=new MatchVisionLedger();
        var at=DateTimeOffset.UnixEpoch.AddMinutes(1); var emitted=new List<VisionEvidenceEvent>();
        foreach(var (file,offset) in new[]{("evidence-02.png",0d),("evidence-03.png",.2)})
        {
            var frame=definition.Load(file); var screen=await reader.AnalyzeAsync(frame);
            var sightings=await titles.RecognizeAsync(frame,screen,reader);
            if(!sightings.Any(item=>item.Card.Id==damsel.Id && item.Side==PlayerSide.Opponent))
                throw new InvalidOperationException("The retained exact Cursed Damsel preview was missed.");
            emitted.AddRange(ledger.Observe(at.AddSeconds(offset),screen,sightings,
                boardWasScanned:false,artworkWasScanned:true));
        }
        var play=emitted.SingleOrDefault(item=>item.Sighting.Card.Id==damsel.Id) ??
            throw new InvalidOperationException("The two title frames did not produce one Cursed Damsel play episode.");

        VisionEvidenceEvent E(CardDefinition card,DateTimeOffset when,CardSightSource source=CardSightSource.PlayPreview) =>
            new(when,new(card,PlayerSide.Opponent,source,new(.8,.1,.9,.4),.1,1,"Retained fixture"),"Retained fixture");
        var origins=new PlayProvenanceResolver(); var mutations=new DeckMutationLedger();
        origins.Observe(E(manor,DateTimeOffset.UnixEpoch)); mutations.Observe(E(manor,DateTimeOffset.UnixEpoch),"Scoia'tael","Monsters");
        origins.Observe(E(damsel,DateTimeOffset.UnixEpoch.AddSeconds(1),CardSightSource.Board));
        var unresolved=origins.Observe(play); mutations.Observe(play,"Scoia'tael","Monsters");
        if(unresolved.Provenance!=CardProvenance.Unknown)
            throw new InvalidOperationException("The raw later Damsel preview unexpectedly bypassed conservative origin review.");

        var hand=new HandCommitTracker(); var hud=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null)
            {MatchHudVisible=true,OpponentHandCount=5};
        hand.Observe(at.AddSeconds(-1),hud,[]); hand.Observe(at,hud,[play]);
        var confirmed=hand.Observe(at.AddSeconds(1),hud with {OpponentHandCount=4},[]);
        var deck=new LiveDeckTracker(PlayerSide.Opponent); deck.SetFactionPrior("Monsters");
        deck.ConsiderDirectPlay(damsel,.9,play.ObservedAt,"Unresolved after earlier Manor token.",CardProvenance.Unknown);
        if(!HandCommitTracker.Apply(confirmed,origins,mutations,new(),new(PlayerSide.User),deck,null) ||
           deck.DeckBuildingObservations.Single(item=>item.Card.Id==damsel.Id).ObservedCopies!=1)
            throw new InvalidOperationException("The later one-card hand cost did not establish one Damsel original.");

        // Preserve the other Manor interaction from this recording: two Thrive
        // triggers advanced Chapter 2, then a stable-hand 13→12 deck decrement
        // and the exact following preview identified Nekker Warrior as its play.
        var larva=catalog.Single(card=>card.Id=="202440"); var warrior=catalog.Single(card=>card.Id=="132211");
        var chapters=new MatchVisionLedger();
        var counts=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null)
            {MatchHudVisible=true,OpponentHandCount=6,OpponentDeckCount=13};
        CardSighting P(CardDefinition card) => new(card,PlayerSide.Opponent,CardSightSource.PlayPreview,
            new(.815,.136,.915,.399),.08,1,"Exact retained preview");
        chapters.Observe(at,counts,[P(manor)]); chapters.Observe(at.AddSeconds(1),counts with {OpponentHandCount=5},[]);
        chapters.Observe(at.AddSeconds(10),counts with {OpponentHandCount=5},[P(larva)]);
        chapters.Observe(at.AddSeconds(11),counts with {OpponentHandCount=4},[]);
        chapters.Observe(at.AddSeconds(13),counts with {OpponentHandCount=4},[]);
        chapters.Observe(at.AddSeconds(20),counts with {OpponentHandCount=4},[P(larva)]);
        chapters.Observe(at.AddSeconds(21),counts with {OpponentHandCount=3},[]);
        chapters.Observe(at.AddSeconds(24),counts with {OpponentHandCount=3},[]);
        chapters.Observe(at.AddSeconds(25),counts with {OpponentHandCount=3,OpponentDeckCount=12},[]);
        var chapterPlay=chapters.Observe(at.AddSeconds(25.5),counts with {OpponentHandCount=3,OpponentDeckCount=12},[P(warrior)])
            .Single(item=>item.Sighting.Card.Id==warrior.Id);
        if(chapterPlay.ResolvedDeckCopies!=1 || !chapterPlay.Description.Contains("Manor",StringComparison.Ordinal) ||
           !chapterPlay.Description.Contains("Chapter 2",StringComparison.Ordinal))
            throw new InvalidOperationException($"The Manor Chapter 2 deck play was counted without preserving its causal attribution: copies={chapterPlay.ResolvedDeckCopies}; {chapterPlay.Description}");
    }
}
