using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class CurrentAccountingAudit
{
    public static void Run(string root, string[] args)
    {
        string Option(string key,string fallback) {var i=Array.IndexOf(args,key);return i<0?fallback:args[i+1];}
        var session=Option("--session","20260828-181243");
        var directory=Path.Combine(root,"GwentCompanion/sessions",session);
        var input=Path.Combine(root,Option("--input","GwentCompanion/diagnostics/v0.1.32-"+session+"-pixels.jsonl"));
        var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        var saved=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(directory,"game-state-final.json")),GameStateJournal.Json)!;
        var reference=saved.User.StartingDeckReference!;
        var startingLeader=catalog.FirstOrDefault(card=>card.Kind==CardKind.Leader && card.Name==saved.Opponent.StartingLeader?.Value);
        int? allowance=startingLeader is null?null:150+startingLeader.Provision;
        var user=new LiveDeckTracker(PlayerSide.User); user.SetFactionPrior(reference.Faction);
        var opponent=new LiveDeckTracker(PlayerSide.Opponent); var knowledge=new OpponentKnowledge();
        var origins=new PlayProvenanceResolver(); var mutations=new DeckMutationLedger(); var watch=new TacticalWatch(catalog);
        if(catalog.FirstOrDefault(card=>card.Id=="201627") is {} shupe) knowledge.ConfigureShupe(shupe);
        var renfri=catalog.FirstOrDefault(card=>card.Name=="Renfri" && card.CanBeInStartingDeck);
        var units=catalog.Where(card=>card.CanBeInStartingDeck && card.Kind==CardKind.Unit && card.Provision>0).ToArray();
        var leaders=GwentOneCardCatalog.StartingLeaders(catalog).ToArray();
        if(renfri is not null && units.Length>0 && leaders.Length>0)
            knowledge.ConfigureRenfriBudget(renfri,units.Min(card=>card.Provision),150+leaders.Max(card=>card.Provision));
        var copies=new ThinningCopyTracker(); var events=new MatchVisionLedger(); var resolutions=new DeckPlayResolutionTracker(catalog); var state=new GameStateTracker();
        var hands=new HandCommitTracker();
        state.Reset(session,reference); var values=new LiveValueLedger(); var timeline=new List<object>();
        foreach(var line in File.ReadLines(input))
        {
            var result=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
            result=result with {HoverInPlayerHand=result.PointerInPlayerHand ?? (result.HoverInPlayerHand ||
                CardVisionPipeline.IsGeometricPlayerHandHover(result.HoveredCard,result.Screen))};
            var sightings=result.Sightings.Select(CardVisionPipeline.RequirePreviewCorroboration).ToArray();
            result=result with {Sightings=sightings,Events=events.Observe(result.SampledAt,result.Screen,sightings,result.BoardWasScanned,result.HoveredCard,result.ArtworkWasScanned,result.HoverInPlayerHand,result.PointerInPlayerHand)};
            result=result with {Events=result.Events.Concat(resolutions.Observe(result.SampledAt,result.Screen,result.Events,result.DeckPlayChoices)).ToArray()};
            if(result.OpponentLeader is { } visibleLeader)
            {
                var compatible=!opponent.HasStableFaction || FactionCompatibility.IsPlayableBy(visibleLeader.Card,opponent.Faction!);
                knowledge.ObserveVisibleLeader(visibleLeader.Card,visibleLeader.Confidence,!knowledge.HasOpponentPlay || compatible);
                if(compatible) opponent.SetFactionPrior(visibleLeader.Card.Faction);
            }
            knowledge.ObserveScreen(result.Screen,result.SampledAt);
            foreach(var e in result.Events)
            {
                var sight=e.Sighting; var tracker=sight.Side==PlayerSide.User?user:opponent;
                mutations.Observe(e,reference.Faction,opponent.Faction);
                var baseOrigin=origins.Observe(e,reference);
                var origin=mutations.OriginRisk(sight,e.ObservedAt,tracker.Observations) ?? knowledge.BoardOrigin(e, opponent.HasStableFaction ? opponent.Faction : null) ??
                    watch.ArrivalOrigin(e,knowledge.Opportunities) ?? baseOrigin;
                if(tracker.Observations.Any(item=>item.Card.Id==sight.Card.Id))
                {
                    var repeatRisk=mutations.OriginRisk(sight,e.ObservedAt,tracker.Observations,additionalCopy:true);
                    if(repeatRisk is not null) origin=repeatRisk;
                    else if(origins.HasCopyRisk(sight,e.ObservedAt) || mutations.HasRecentReplay(sight.Side,e.ObservedAt))
                        origin=new(CardProvenance.Unknown,"Repeated identity follows an observed create/copy/replay route; it cannot upgrade an earlier uncertain card into an original.");
                }
                if(!sight.Card.CanBeInStartingDeck && !EvolvingCardCatalog.IsEvolved(sight.Card.Id)) origin=baseOrigin;
                var identity=EvolvingCardCatalog.StartingIdentity(sight,origin,catalog,tracker.Faction);
                knowledge.Observe(e,opponent.HasStableFaction?opponent.Faction:null,identity.Origin.Provenance);
                watch.ObserveEvent(e);
                tracker.ConsiderDirectPlay(identity.Card,1-sight.Distance,e.ObservedAt,e.Description+" "+identity.Origin.Reason,identity.Origin.Provenance);
                var copyOrigin=mutations.OriginRisk(sight,e.ObservedAt,tracker.Observations,additionalCopy:true) is not null ||
                    origins.HasCopyRisk(sight,e.ObservedAt) ? CardProvenance.Unknown : identity.Origin.Provenance;
                copies.ObserveEvent(e,copyOrigin,tracker,mutations.HasRecentReplay(sight.Side,e.ObservedAt));
                timeline.Add(new {e.ObservedAt,sight.Side,sight.Source,identity.Card.Name,identity.Origin.Provenance,
                    OriginReason=identity.Origin.Reason,e.Description});
            }
            var handConfirmed=hands.Observe(result.SampledAt,result.Screen,result.Events);
            foreach(var confirmed in handConfirmed)
                timeline.Add(new {confirmed.ObservedAt,confirmed.Sighting.Side,confirmed.Sighting.Source,confirmed.Sighting.Card.Name,
                    Provenance=CardProvenance.ProbableStartingDeck,Confirmation="Independent hand commitment"});
            HandCommitTracker.Apply(handConfirmed,origins,mutations,copies,user,opponent,reference);
            watch.ObserveFrame(result.SampledAt,result.Screen,sightings,result.BoardWasScanned,knowledge.Round);
            knowledge.ObserveStartingConservation(result.SampledAt,opponent.HasStableFaction?opponent.Faction:null,
                opponent.DeckBuildingObservations.Sum(item=>item.ObservedCopies));
            copies.ObserveFrame(result.SampledAt,result.Screen,sightings,result.BoardWasScanned,[user,opponent],
                s=>origins.HasCopyRisk(s,result.SampledAt) || mutations.OriginRisk(s,result.SampledAt,[],additionalCopy:true) is not null,reference,
                s=>mutations.OriginRisk(s,result.SampledAt,[],additionalCopy:true) is null ? origins.RecentSpawnedInitiators(s,result.SampledAt) : null);
            var update=GameStateVisionAdapter.Apply(state,result,new(DeckChanges:mutations.Changes));
            knowledge.ObserveConditions(update);
            values.Observe(update,catalog,reference.Cards.Select(c=>c.Card),[]);
        }
        var reviewedRevealIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-deck-reveal" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var id in reviewedRevealIds)
        {
            var card=catalog.Single(card=>card.Id==id);
            opponent.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                "Manually reviewed retained frame: explicit opponent-deck reveal; identity was not played.",CardProvenance.ProbableStartingDeck);
            values.RecordStartingDeckEvidence(PlayerSide.Opponent,card.Id);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source=CardSightSource.DeckReveal,
                Name=card.Name,Provenance=CardProvenance.ProbableStartingDeck});
        }
        var reviewedUserPlayIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-user-play" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).ToArray();
        foreach(var group in reviewedUserPlayIds.GroupBy(id=>id,StringComparer.Ordinal))
        {
            var card=catalog.Single(card=>card.Id==group.Key);
            var priorCopies=user.Observations.FirstOrDefault(item=>item.Card.Id==card.Id)?.ObservedCopies ?? 0;
            user.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                "Retained pixel regression: exact selected hand/deck-choice title with conservative resolution guards.",CardProvenance.ProbableStartingDeck);
            var committedCopies=Math.Min(reference.CountOf(card.Id),priorCopies+group.Count());
            user.SetObservedCopyLowerBound(card.Id,committedCopies,"reviewed selected-card play(s)");
            values.RecordStartingDeckEvidence(PlayerSide.User,card.Id,committedCopies);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.User,Source=CardSightSource.PlayPreview,
                Name=card.Name,Provenance=CardProvenance.ProbableStartingDeck});
        }
        var reviewedOpponentPlayIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-opponent-play" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).ToArray();
        foreach(var group in reviewedOpponentPlayIds.GroupBy(id=>id,StringComparer.Ordinal))
        {
            var card=catalog.Single(card=>card.Id==group.Key);
            var priorCopies=opponent.Observations.FirstOrDefault(item=>item.Card.Id==card.Id)?.ObservedCopies ?? 0;
            opponent.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                "Retained pixel regression: exact opponent play-preview title with conservative episode guards.",CardProvenance.ProbableStartingDeck);
            var committedCopies=Math.Min(card.IsGold?1:2,priorCopies+group.Count());
            opponent.SetObservedCopyLowerBound(card.Id,committedCopies,"reviewed opponent play episode(s)");
            values.RecordStartingDeckEvidence(PlayerSide.Opponent,card.Id,committedCopies);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source=CardSightSource.PlayPreview,
                Name=card.Name,Provenance=CardProvenance.ProbableStartingDeck});
        }
        object Report(LiveDeckTracker tracker,DeckDefinition? deck)
        {
            var originals=tracker.Side==PlayerSide.User?tracker.Observations:tracker.DeckBuildingObservations;
            var originalIds=originals.Select(item=>item.Card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new {
                Usage=LiveValueLedger.Provisions(originals,deck,deck is null?allowance:null,
                    values.Spent(tracker.Side),values.SpentCopies(tracker.Side),knowledge.StartingSize),
                Cards=tracker.Observations.OrderBy(o=>o.Card.Name).Select(o=>new {o.Card.Name,o.Card.Provision,o.Provenance,o.ObservedCopies,
                    CommittedCopies=originalIds.Contains(o.Card.Id) && StartingDeckRules.CountsAgainstStartingDeck(o.Provenance) &&
                        (deck is null || deck.CountOf(o.Card.Id)>0) && values.SpentCopies(tracker.Side).GetValueOrDefault(o.Card.Id)>0
                        ? o.ObservedCopies : 0,ReferenceCopies=deck?.CountOf(o.Card.Id)}),
                MissingReference=deck?.Cards.Where(c=>!tracker.Observations.Any(o=>o.Card.Id==c.Card.Id)).Select(c=>c.Card.Name)};
        }
        var output=Path.Combine(root,Option("--output",Path.ChangeExtension(input,"accounting.json")));
        var report=new {Scope="Chronological evidence replay; production origin/copy/accounting rules. Saved HUD is not a fresh OCR evaluation. " +
            (reviewedRevealIds.Length==0 ? "No manual reveal review applied. " : "Explicit reviewed deck-reveal identities appended: "+string.Join(", ",reviewedRevealIds)+". ")+
            (reviewedUserPlayIds.Length==0 ? "No reviewed user-play pixels appended. " : "Retained selected-card pixel regressions appended: "+string.Join(", ",reviewedUserPlayIds)+". ")+
            (reviewedOpponentPlayIds.Length==0 ? "No reviewed opponent-play pixels appended. " : "Retained opponent play-preview regressions appended: "+string.Join(", ",reviewedOpponentPlayIds)+". ")+
            "No manual zone reviews reconstructed. Opponent allowance follows the saved starting leader and current catalog; unknown if no leader was recorded.",
            Session=session,User=Report(user,reference),Opponent=Report(opponent,null),knowledge.StartingSize,knowledge.StartingSizeEvidence,
            Rules=knowledge.Assess(opponent.DeckBuildingObservations),Timeline=timeline};
        if (args.Contains("--expect-opponent-floor"))
        {
            var expectedFloor=int.Parse(Option("--expect-opponent-floor","-1"));
            var actualFloor=StartingDeckRules.ProbableProvisionLowerBound(opponent.DeckBuildingObservations);
            if(actualFloor!=expectedFloor) throw new InvalidOperationException($"Opponent floor {actualFloor}p; expected {expectedFloor}p.");
        }
        if (args.Contains("--expect-opponent-copies"))
        {
            var expectedCopies=int.Parse(Option("--expect-opponent-copies","-1"));
            var actualCopies=opponent.DeckBuildingObservations.Sum(item=>item.ObservedCopies);
            if(actualCopies!=expectedCopies) throw new InvalidOperationException($"Opponent copies {actualCopies}; expected {expectedCopies}.");
        }
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions(GameStateJournal.Json){WriteIndented=true}));
        if(args.Contains("--rewrite-live-ledger"))
        {
            var ledgerPath=Path.Combine(directory,"live-value-ledger.json");
            var ledger=JsonNode.Parse(File.ReadAllText(ledgerPath))?.AsObject() ?? throw new InvalidDataException("Invalid live value ledger.");
            var userOriginals=user.Observations;
            var opponentOriginals=opponent.DeckBuildingObservations;
            ledger["UserProvisions"]=JsonSerializer.SerializeToNode(LiveValueLedger.Provisions(userOriginals,reference,null,
                values.Spent(PlayerSide.User),values.SpentCopies(PlayerSide.User),knowledge.StartingSize),GameStateJournal.Json);
            ledger["OpponentProvisions"]=JsonSerializer.SerializeToNode(LiveValueLedger.Provisions(opponentOriginals,null,allowance,
                values.Spent(PlayerSide.Opponent),values.SpentCopies(PlayerSide.Opponent),knowledge.StartingSize),GameStateJournal.Json);
            File.WriteAllText(ledgerPath,ledger.ToJsonString(new JsonSerializerOptions(GameStateJournal.Json){WriteIndented=true}));
            Console.WriteLine($"Repaired final live ledger for {session}.");
        }
        if (args.Contains("--rewrite-observed-cache"))
        {
            var observedRoot=Path.Combine(root,"GwentCompanion/cache/observed-decks");
            var store=new ObservedDeckStore();
            var previous=store.Load(observedRoot).Single(item=>item.SessionId==session && item.Side==PlayerSide.Opponent);
            var prior=previous.LearnedEvidence ?? throw new InvalidDataException("The cached opponent record has no learned evidence to repair.");
            var observations=opponent.Observations.OrderBy(item=>item.Card.Name).ToArray();
            var originals=opponent.DeckBuildingObservations.ToArray();
            var assessment=knowledge.Assess(originals);
            var conditions=prior.Conditions.Concat(knowledge.Resolved)
                .GroupBy(item=>item.Condition).Select(group=>group.OrderBy(item=>item.Suggested).ThenByDescending(item=>item.At).First()).ToArray();
            var capacity=prior.LeaderBonus is { } bonus ? 150+bonus : prior.Budget.Capacity;
            var budget=OpponentProvisionCalculator.Calculate(originals,capacity,prior.StartingSize ?? prior.MinimumSize,
                conditions.Any(item=>item.Condition==DeckCondition.Musicians));
            var learned=prior with
            {
                Cards=originals,
                Conditions=conditions,
                Budget=budget,
                DeckChanges=mutations.Changes.ToArray(),
                Patch=prior.Patch ?? DeckPatchMetadata.Current(prior.At).Label
            };
            store.Save(observedRoot,previous with
            {
                Cards=observations.Select(item=>new StoredObservedCard(item.Card.Id,item.Card.Name,item.Card.Faction,item.Card.Provision,
                    item.Confidence,item.Provenance,opponent.IsFactionException(item.Card),item.ObservedCopies)).ToArray(),
                ProvisionLowerBound=StartingDeckRules.ProbableProvisionLowerBound(originals),
                RecognitionVersion=Math.Max(3,previous.RecognitionVersion),
                Devotion=assessment.Devotion.State,
                DevotionEvidence=assessment.Devotion.Reason,
                LearnedEvidence=learned
            });
            Console.WriteLine($"Repaired {session}: {originals.Sum(item=>item.ObservedCopies)} original copies, {budget.ObservedFloor}p.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {report.User,report.Opponent},GameStateJournal.Json));
        Console.WriteLine(output);
    }
}
