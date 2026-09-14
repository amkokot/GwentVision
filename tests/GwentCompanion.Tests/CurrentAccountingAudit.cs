using System.IO;
using System.Text.Json;
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
        int? OptionalInt(string key)
        {
            var index=Array.IndexOf(args,key);
            return index<0 ? null : int.Parse(args[index+1]);
        }
        var session=Option("--session","20260828-181243");
        var directory=Path.Combine(root,"GwentCompanion/sessions",session);
        var input=Path.Combine(root,Option("--input","GwentCompanion/diagnostics/v0.1.32-"+session+"-pixels.jsonl"));
        var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        var saved=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(directory,"game-state-final.json")),GameStateJournal.Json)!;
        var reference=saved.User.StartingDeckReference!;
        var reviewedLeaderId=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-opponent-leader" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).SingleOrDefault();
        var reviewedLeader=reviewedLeaderId is null ? null : catalog.Single(card=>card.Id==reviewedLeaderId && card.Kind==CardKind.Leader);
        var reviewedMmrCurrent=OptionalInt("--reviewed-mmr-current");
        var reviewedMmrPeak=OptionalInt("--reviewed-mmr-peak");
        if(reviewedMmrPeak is not null && reviewedMmrCurrent is null)
            throw new ArgumentException("--reviewed-mmr-peak requires --reviewed-mmr-current.");
        var reviewedPostMatchMmr=reviewedMmrCurrent is { } reviewedCurrent
            ? new PostMatchMmr(reviewedCurrent,null,true,"Reviewed stable Standard Mode summary (current / season peak)",
                reviewedMmrPeak,Confirmed:true,ReadCount:3)
            : null;
        var startingLeader=reviewedLeader ?? catalog.FirstOrDefault(card=>card.Kind==CardKind.Leader && card.Name==saved.Opponent.StartingLeader?.Value);
        int? allowance=startingLeader is null?null:150+startingLeader.Provision;
        var user=new LiveDeckTracker(PlayerSide.User); user.SetFactionPrior(reference.Faction);
        var opponent=new LiveDeckTracker(PlayerSide.Opponent); var knowledge=new OpponentKnowledge();
        var origins=new PlayProvenanceResolver(); var mutations=new DeckMutationLedger(); var watch=new TacticalWatch(catalog);
        if(catalog.FirstOrDefault(card=>card.Id=="201627") is {} shupe) knowledge.ConfigureShupe(shupe);
        knowledge.ConfigureOpeningSetupCards(catalog);
        var renfri=catalog.FirstOrDefault(card=>card.Name=="Renfri" && card.CanBeInStartingDeck);
        var units=catalog.Where(card=>card.CanBeInStartingDeck && card.Kind==CardKind.Unit && card.Provision>0).ToArray();
        var leaders=GwentOneCardCatalog.StartingLeaders(catalog).ToArray();
        if(renfri is not null && units.Length>0 && leaders.Length>0)
            knowledge.ConfigureRenfriBudget(renfri,units.Min(card=>card.Provision),150+leaders.Max(card=>card.Provision));
        var copies=new ThinningCopyTracker(); var events=new MatchVisionLedger(); var resolutions=new DeckPlayResolutionTracker(catalog); var state=new GameStateTracker();
        var hands=new HandCommitTracker();
        state.Reset(session,reference); var values=new LiveValueLedger(); var timeline=new List<object>();
        if(reviewedLeader is not null)
        {
            knowledge.ObserveVisibleLeader(reviewedLeader,.99,true);
            origins.ObserveCurrentLeader(PlayerSide.Opponent,reviewedLeader);
            opponent.SetFactionPrior(reviewedLeader.Faction);
            ApplyLeaderAssumptions(reviewedLeader,saved.At??DateTimeOffset.UtcNow);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source="ReviewedLeader",
                Name=reviewedLeader.Name,Provenance=CardProvenance.ProbableStartingDeck,
                Confirmation="Repeated retained leader-badge pixels matched the production reference."});
        }
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
                origins.ObserveCurrentLeader(PlayerSide.Opponent,visibleLeader.Card);
                if(compatible)
                {
                    opponent.SetFactionPrior(visibleLeader.Card.Faction);
                    if(knowledge.StartingLeader==visibleLeader.Card.Name) ApplyLeaderAssumptions(visibleLeader.Card,result.SampledAt);
                }
            }
            knowledge.ObserveScreen(result.Screen,result.SampledAt);
            knowledge.ObserveOpeningCounts(opponent.HasStableFaction?opponent.Faction:null);
            foreach(var setup in knowledge.OpeningStartingCards)
                opponent.ConsiderDirectPlay(setup.Card,.99,setup.At,setup.Evidence,CardProvenance.ProbableStartingDeck);
            origins.PrimeCoTemporalBoardCreations(result.Events,reference);
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
        void ApplyLeaderAssumptions(CardDefinition leader,DateTimeOffset at)
        {
            foreach(var assumption in LeaderSpawnCatalog.StartingDeckAssumptions(leader,catalog))
            {
                opponent.ConsiderDirectPlay(assumption.Card,.84,at,assumption.Reason,CardProvenance.ProbableStartingDeck);
                var existing=opponent.Observations.FirstOrDefault(item=>item.Card.Id==assumption.Card.Id);
                if(existing is not null && !StartingDeckRules.CountsAgainstStartingDeck(existing.Provenance))
                    opponent.SetProvenance(assumption.Card.Id,CardProvenance.ProbableStartingDeck,assumption.Reason);
                opponent.SetObservedCopyLowerBound(assumption.Card.Id,assumption.Copies,
                    $"copies assumed from observed {leader.Name} leader package");
            }
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
        var reviewedOpponentArrivalIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-opponent-arrival" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var id in reviewedOpponentArrivalIds)
        {
            var card=catalog.Single(card=>card.Id==id);
            if(!CompanionCardRules.IsInherentDeckArrival(card))
                throw new InvalidOperationException($"Reviewed opponent arrival {card.Name} has no inherent automatic deck-arrival rule.");
            if(opponent.HasStableFaction && !FactionCompatibility.IsPlayableBy(card,opponent.Faction!))
                throw new InvalidOperationException($"Reviewed opponent arrival {card.Name} is incompatible with the observed opponent faction.");
            if(!opponent.Observations.Any(item=>item.Card.Id==id))
                opponent.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                    "Retained repeated board/deck-conservation validation: inherent automatic arrival from the starting deck.",CardProvenance.ProbableStartingDeck);
            values.RecordStartingDeckEvidence(PlayerSide.Opponent,id,1);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source=CardSightSource.Board,
                Name=card.Name,Provenance=CardProvenance.ProbableStartingDeck,Confirmation="Reviewed repeated automatic arrival with deck conservation"});
        }
        var reviewedOpponentPairIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-opponent-pair" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var id in reviewedOpponentPairIds)
        {
            var card=catalog.Single(card=>card.Id==id);
            if(!CompanionCardRules.ThinningPairs.Contains(id) || card.IsGold)
                throw new InvalidOperationException($"Reviewed opponent pair {card.Name} is not a legal self-thinning bronze pair.");
            if(!opponent.Observations.Any(item=>item.Card.Id==id))
                opponent.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                    "Retained pixel regression: two settled self-thinning bodies with no observed create/copy route.",CardProvenance.ProbableStartingDeck);
            opponent.SetObservedCopyLowerBound(id,2,"Retained pixel regression: two settled self-thinning bodies corroborated across two scans.");
            values.RecordStartingDeckEvidence(PlayerSide.Opponent,id,2);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source=CardSightSource.Board,
                Name=card.Name,Provenance=CardProvenance.ProbableStartingDeck,Confirmation="Reviewed two-body thinning sequence"});
        }
        var reviewedOpponentGeneratedIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-opponent-generated" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).Distinct(StringComparer.Ordinal).ToArray();
        foreach(var id in reviewedOpponentGeneratedIds)
        {
            var card=catalog.Single(card=>card.Id==id);
            if(!opponent.Observations.Any(item=>item.Card.Id==id))
                opponent.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                    "Retained pixel regression: generated opponent body produced by a confirmed source; excluded from starting provisions.",CardProvenance.Spawned);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source=CardSightSource.Board,
                Name=card.Name,Provenance=CardProvenance.Spawned,Confirmation="Reviewed generated board body"});
        }
        var reviewedOpponentSummonIds=args.Select((value,index)=>(value,index)).Where(item=>item.value=="--reviewed-opponent-summon" && item.index+1<args.Length)
            .Select(item=>args[item.index+1]).ToArray();
        foreach(var group in reviewedOpponentSummonIds.GroupBy(id=>id,StringComparer.Ordinal))
        {
            var id=group.Key;
            var card=catalog.Single(card=>card.Id==id);
            var source=opponent.Observations.Select(item=>item.Card).FirstOrDefault(candidate=>
                CompanionCardRules.RecurringSummonPool(candidate,catalog).Contains(id) ||
                CompanionCardRules.NamedDeckSummonTargets(candidate,catalog).Contains(id));
            if(source is null || opponent.HasStableFaction && !StartingDeckRules.IsLegalStartingCard(card,opponent.Faction))
                throw new InvalidOperationException($"Reviewed opponent summon {card.Name} has no observed printed source or is faction-incompatible.");
            var priorCopies=opponent.Observations.FirstOrDefault(item=>item.Card.Id==card.Id)?.ObservedCopies ?? 0;
            opponent.ConsiderDirectPlay(card,.99,saved.At??DateTimeOffset.UtcNow,
                $"Retained pixel regression: strong source-scoped board geometry after {source.Name}, corroborated by a same-hand deck decrement.",CardProvenance.ProbableStartingDeck);
            var committedCopies=Math.Min(card.IsGold?1:2,priorCopies+group.Count());
            opponent.SetObservedCopyLowerBound(card.Id,committedCopies,"reviewed printed source-scoped deck summon(s)");
            values.RecordStartingDeckEvidence(PlayerSide.Opponent,id,committedCopies);
            timeline.Add(new {ObservedAt=saved.At??DateTimeOffset.UtcNow,Side=PlayerSide.Opponent,Source=CardSightSource.Board,
                Name=card.Name,Provenance=CardProvenance.ProbableStartingDeck,Confirmation="Reviewed printed source-scoped deck summon"});
        }
        object Report(LiveDeckTracker tracker,DeckDefinition? deck)
        {
            var originals=tracker.Side==PlayerSide.User?tracker.Observations:tracker.DeckBuildingObservations;
            var originalIds=originals.Select(item=>item.Card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new {
                Usage=LiveValueLedger.Provisions(originals,deck,deck is null?allowance:150+deck.LeaderProvisionBonus,
                    values.Spent(tracker.Side),values.SpentCopies(tracker.Side),knowledge.StartingSize),
                Cards=tracker.Observations.OrderBy(o=>o.Card.Name).Select(o=>new {o.Card.Name,o.Card.Provision,o.Provenance,o.ObservedCopies,
                    CommittedCopies=originalIds.Contains(o.Card.Id) && StartingDeckRules.CountsAgainstStartingDeck(o.Provenance) &&
                        values.SpentCopies(tracker.Side).GetValueOrDefault(o.Card.Id)>0
                        ? o.ObservedCopies : 0,ReferenceCopies=deck?.CountOf(o.Card.Id)}),
                MissingReference=deck?.Cards.Where(c=>!tracker.Observations.Any(o=>o.Card.Id==c.Card.Id)).Select(c=>c.Card.Name)};
        }
        var output=Path.Combine(root,Option("--output",Path.ChangeExtension(input,"accounting.json")));
        var report=new {Scope="Chronological evidence replay; production origin/copy/accounting rules. Saved HUD is not a fresh OCR evaluation. " +
            (reviewedRevealIds.Length==0 ? "No manual reveal review applied. " : "Explicit reviewed deck-reveal identities appended: "+string.Join(", ",reviewedRevealIds)+". ")+
            (reviewedUserPlayIds.Length==0 ? "No reviewed user-play pixels appended. " : "Retained selected-card pixel regressions appended: "+string.Join(", ",reviewedUserPlayIds)+". ")+
            (reviewedOpponentPlayIds.Length==0 ? "No reviewed opponent-play pixels appended. " : "Retained opponent play-preview regressions appended: "+string.Join(", ",reviewedOpponentPlayIds)+". ")+
            (reviewedOpponentArrivalIds.Length==0 ? "No reviewed opponent automatic arrivals appended. " : "Retained repeated opponent automatic-arrival regressions appended: "+string.Join(", ",reviewedOpponentArrivalIds)+". ")+
            (reviewedOpponentPairIds.Length==0 ? "No reviewed opponent-pair pixels appended. " : "Retained two-body opponent thinning regressions appended: "+string.Join(", ",reviewedOpponentPairIds)+". ")+
            (reviewedOpponentGeneratedIds.Length==0 ? "No reviewed generated opponent bodies appended. " : "Retained generated opponent bodies appended without starting provision spend: "+string.Join(", ",reviewedOpponentGeneratedIds)+". ")+
            (reviewedOpponentSummonIds.Length==0 ? "No reviewed source-scoped summons appended. " : "Retained printed source-scoped deck summons appended: "+string.Join(", ",reviewedOpponentSummonIds)+". ")+
            (reviewedLeader is null ? "No reviewed opponent leader appended. " : $"Retained opponent leader pixels establish {reviewedLeader.Name} ({reviewedLeader.Provision}p leader bonus). ")+
            (reviewedPostMatchMmr is null ? "No reviewed post-match MMR appended. " : $"Stable Standard Mode pixels establish current MMR {reviewedPostMatchMmr.RatingAfter} and season peak {reviewedPostMatchMmr.SeasonPeak}. ")+
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
            var userOriginals=user.Observations;
            var opponentOriginals=opponent.DeckBuildingObservations;
            var repairedLedger=new
            {
                Entries=values.Entries, Stored=values.Stored, Growing=values.Growing,
                SpyingGranted=values.SpyingGranted, BountyHistory=values.Bounties,
                UserProvisions=LiveValueLedger.Provisions(userOriginals,reference,150+reference.LeaderProvisionBonus,
                    values.Spent(PlayerSide.User),values.SpentCopies(PlayerSide.User),knowledge.StartingSize),
                OpponentProvisions=LiveValueLedger.Provisions(opponentOriginals,null,allowance,
                    values.Spent(PlayerSide.Opponent),values.SpentCopies(PlayerSide.Opponent),knowledge.StartingSize)
            };
            File.WriteAllText(ledgerPath,JsonSerializer.Serialize(repairedLedger,new JsonSerializerOptions(GameStateJournal.Json){WriteIndented=true}));
            Console.WriteLine($"Repaired final live ledger for {session}.");
        }
        if(args.Contains("--rewrite-game-state"))
        {
            static PlayerGameState PreserveMetadata(PlayerGameState replayed,PlayerGameState prior) => replayed with
            {
                Faction=replayed.Faction??prior.Faction,
                StartingLeader=replayed.StartingLeader??prior.StartingLeader,
                CurrentLeader=replayed.CurrentLeader??prior.CurrentLeader,
                OpeningStratagemId=replayed.OpeningStratagemId??prior.OpeningStratagemId,
                StartingDeckReference=replayed.StartingDeckReference??prior.StartingDeckReference
            };
            var repairedState=state.Current with
            {
                User=PreserveMetadata(state.Current.User,saved.User),
                Opponent=PreserveMetadata(state.Current.Opponent,saved.Opponent)
            };
            File.WriteAllText(Path.Combine(directory,"game-state-final.json"),
                JsonSerializer.Serialize(repairedState,new JsonSerializerOptions(GameStateJournal.Json){WriteIndented=true}));
            Console.WriteLine($"Repaired final game-state snapshot for {session}.");
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
            var capacity=allowance ?? (prior.LeaderBonus is { } bonus ? 150+bonus : prior.Budget.Capacity);
            var budget=OpponentProvisionCalculator.Calculate(originals,capacity,prior.StartingSize ?? prior.MinimumSize,
                conditions.Any(item=>item.Condition==DeckCondition.Musicians));
            var learned=prior with
            {
                Faction=reviewedLeader?.Faction ?? prior.Faction,
                StartingLeader=reviewedLeader?.Name ?? prior.StartingLeader,
                LeaderBonus=reviewedLeader?.Provision ?? prior.LeaderBonus,
                Cards=originals,
                Conditions=conditions,
                Budget=budget,
                DeckChanges=mutations.Changes.ToArray(),
                MatchMmr=reviewedPostMatchMmr?.RatingAfter ?? prior.MatchMmr,
                PostMatchMmr=reviewedPostMatchMmr ?? prior.PostMatchMmr,
                Patch=prior.Patch ?? DeckPatchMetadata.Current(prior.At).Label
            };
            store.Save(observedRoot,previous with
            {
                Faction=reviewedLeader?.Faction ?? previous.Faction,
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
        if(args.Contains("--rewrite-opponent-memory"))
        {
            var memoryPath=Path.Combine(root,"GwentCompanion/cache/opponent-memory.json");
            var memory=OpponentDeckMemoryStore.Load(memoryPath);
            var record=memory.Records.Single(item=>item.Encounters.Any(encounter=>encounter.SessionId==session));
            var previous=record.Encounters.Single(encounter=>encounter.SessionId==session);
            var originals=opponent.DeckBuildingObservations.ToArray();
            var conditions=previous.Conditions.Concat(knowledge.Resolved)
                .GroupBy(item=>item.Condition).Select(group=>group.OrderBy(item=>item.Suggested).ThenByDescending(item=>item.At).First()).ToArray();
            var capacity=allowance ?? (previous.LeaderBonus is { } bonus ? 150+bonus : previous.Budget.Capacity);
            var budget=OpponentProvisionCalculator.Calculate(originals,capacity,previous.StartingSize ?? previous.MinimumSize,
                conditions.Any(item=>item.Condition==DeckCondition.Musicians));
            var corrected=previous with
            {
                Faction=reviewedLeader?.Faction ?? previous.Faction,
                StartingLeader=reviewedLeader?.Name ?? previous.StartingLeader,
                LeaderBonus=reviewedLeader?.Provision ?? previous.LeaderBonus,
                Cards=originals,
                Conditions=conditions,
                Budget=budget,
                DeckChanges=mutations.Changes.ToArray(),
                MatchMmr=reviewedPostMatchMmr?.RatingAfter ?? previous.MatchMmr,
                PostMatchMmr=reviewedPostMatchMmr ?? previous.PostMatchMmr,
                Patch=previous.Patch ?? DeckPatchMetadata.Current(previous.At).Label
            };
            memory.Record(corrected,record.Name,record.Id,record.Complete,record.VariantOf);
            memory.Save(memoryPath);
            Console.WriteLine($"Repaired opponent memory for {session}: {originals.Sum(item=>item.ObservedCopies)} original copies, {budget.ObservedFloor}p.");
        }
        if(args.Contains("--rewrite-compact-match"))
        {
            var matchIndex=Array.IndexOf(args,"--rewrite-compact-match");
            if(matchIndex+1>=args.Length) throw new ArgumentException("Compact match path is required.");
            var path=Path.GetFullPath(args[matchIndex+1]);
            var compact=LocalMatchStore.Read(path);
            var gameDate=(saved.At??throw new InvalidDataException("Saved game state has no timestamp.")).UtcDateTime.Date;
            if(compact.GameDateUtc!=DateOnly.FromDateTime(gameDate))
                throw new InvalidDataException("Compact match date does not match the reviewed session.");
            var opponentCards=MatchAcquisition.Observations(opponent.Observations);
            var repaired=compact with
            {
                Revision=compact.Revision+1,
                MmrAfter=reviewedPostMatchMmr?.RatingAfter ?? compact.MmrAfter,
                MmrPeak=reviewedPostMatchMmr?.SeasonPeak ?? compact.MmrPeak,
                FactionMmr=reviewedPostMatchMmr?.IsFactionRating ?? compact.FactionMmr,
                MmrUnconfirmed=reviewedPostMatchMmr is null && compact.MmrUnconfirmed,
                Opponent=compact.Opponent with { Observations=opponentCards }
            };
            var storageRoot=Directory.GetParent(Path.GetDirectoryName(path)!)?.FullName
                ?? throw new InvalidDataException("Compact match does not have the expected monthly parent directory.");
            var savedPath=new LocalMatchStore(storageRoot).Save(repaired);
            if(!Path.GetFullPath(savedPath).Equals(path,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Compact match repair resolved to an unexpected target.");
            Console.WriteLine($"Repaired compact match for {session}: {opponentCards.Sum(item=>item.Copies)} observed opponent bodies; revision {repaired.Revision}.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new {report.User,report.Opponent},GameStateJournal.Json));
        Console.WriteLine(output);
    }
}
