using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using System.Collections.Immutable;
using GwentCompanion.Platform.Windows.Vision;

internal static class RecentMatchRefinementTests
{
    private static void Check(bool okay,string message) {if(!okay) throw new InvalidOperationException(message);}
    public static void Run(string root)
    {
        var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        CardDefinition C(string name)=>catalog.Single(c=>c.Name==name);
        var at=DateTimeOffset.UtcNow; var watch=new SummonAbsenceTracker();
        var screen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null);
        CardSighting S(string name,CardSightSource source=CardSightSource.Board)=>new(C(name),PlayerSide.Opponent,source,new(.43,.16,.49,.29),.08,1);
        VisionEvidenceEvent E(string name,DateTimeOffset time)=>new(time,S(name,CardSightSource.PlayPreview),"test");
        var weights=new List<double>();
        for(var round=1;round<=3;round++)
        {
            var t=at.AddMinutes(round); watch.ObserveEvent(E("Royal Decree",t),round);
            watch.ObserveFrame(t.AddSeconds(5),screen,[S("Mutant")],true);
            Check(watch.Evidence.Count==(round==1?0:1),"Single scan invented a miss");
            watch.ObserveFrame(t.AddSeconds(10),screen,[S("Mutant")],true);
            weights.Add(watch.Evidence.Single().Weight);
        }
        Check(weights[0]>weights[1] && weights[1]>weights[2] && weights[2]>0,"Roach absence should progressively weaken, not exclude");
        watch.ObserveEvent(E("Roach",at.AddMinutes(4)),3); Check(watch.Evidence.Count==0,"Seen Roach still downweighted");
        watch.Reset();
        Check(watch.ObserveTributeResolution(false), "First unrefunded Tribute was not recorded.");
        var kingPenalty = watch.Evidence.Single(item => item.CardId == "203100");
        Check(kingPenalty.Weight < 1 && kingPenalty.Reason.Contains("bricked in hand"), "King absence did not retain the hand-brick explanation.");
        watch.ObserveTributeResolution(false);
        Check(watch.Evidence.Single(item => item.CardId == "203100").Weight < kingPenalty.Weight,
            "Repeated unrefunded Tributes did not progressively downweight King.");
        watch.ObserveTributeResolution(true);
        Check(!watch.Evidence.Any(item => item.CardId == "203100"), "Verified King refund did not clear stale absence evidence.");
        watch.Reset(); watch.ObserveEvent(E("Royal Decree",at),1);
        watch.ObserveFrame(at.AddSeconds(5),screen with {HasCardTooltip=true},[S("Mutant")],true);
        watch.ObserveFrame(at.AddSeconds(10),screen,[],true); Check(watch.Evidence.Count==0,"Occluded/empty scan became reliable absence");
        var knowledge=new OpponentKnowledge(); knowledge.Resolve(DeckCondition.GoldenNekker,at,"test activation");
        Check(knowledge.Assess([]).Shupe.State==ConstraintState.RuledOut && knowledge.Assess([]).Radeyah.State!=ConstraintState.RuledOut,"Nekker/Shupe versus singleton/Radeyah confused");
        knowledge.Reset(); knowledge.Resolve(DeckCondition.Singleton,at,"Radeyah activation");
        Check(knowledge.Assess([]).GoldenNekker.State==ConstraintState.Possible,"Radeyah incorrectly excludes Nekker");
        var reference=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(root,"GwentCompanion/sessions/20260828-165436/game-state-final.json")),GameStateJournal.Json)!.User.StartingDeckReference!;
        var withRoach=reference with {Cards=reference.Cards.Where(c=>c.Card.Name!="Golyat").Append(new DeckCard(C("Roach"),1)).ToArray()};
        var normal=new DeckMetaAnalyzer().Analyze([withRoach],[],reference.Faction);
        var reduced=new DeckMetaAnalyzer().Analyze([withRoach],[],reference.Faction,summonEvidence:[new("112210",.1,3,3,"misses")]);
        Check(normal.Cards.Single(c=>c.Card.Id=="112210").ConditionalPresence>reduced.Cards.Single(c=>c.Card.Id=="112210").ConditionalPresence &&
            reduced.Cards.Single(c=>c.Card.Id=="112210").ConditionalPresence<=.1,"Unanimous cache ignored live Roach evidence");
        var seen=new DeckMetaAnalyzer().Analyze([withRoach],[new(C("Roach"),CardProvenance.ConfirmedStartingDeck,1,at)],reference.Faction,
            summonEvidence:[new("112210",.1,3,3,"misses")]);
        Check(seen.Cards.Single(c=>c.Card.Id=="112210").ConditionalPresence==1,"Observed Roach overridden by absence");
        var engine=new TacticalPlayEngine(new PlayRuleBook(catalog));
        PositionCard P(string name,string id)=>new(id,C(name).Id,C(name).Power,C(name).Power,0,ImmutableHashSet<CardStatus>.Empty);
        var p=GamePosition.EmptyKnown();
        p=p with {Zones=p.Zones.Select(z=>z.Side==PlayerSide.User && z.Zone==CardZone.Hand ? z with {Cards=[P("Fiend","play")]} :
            z.Side==PlayerSide.Opponent && z.Zone==CardZone.Board && z.Row==BoardRow.Melee ? z with {Cards=[P("Coerced Blacksmith","fee"),P("Gellert Bleinheim","gellert")]}:z).ToImmutableArray()};
        Check(engine.ImmediateMaximum(p,"play",PlayerSide.User).MaximumPoints is not null,"Dormant opponent Fees blocked unrelated play");
        var changed=C("Coerced Blacksmith") with {AbilityText="Profit 2. Fee 1: Boost an allied unit by 1. Whenever the opponent plays a unit, damage it by 5."};
        var patched=new TacticalPlayEngine(new PlayRuleBook(catalog.Where(c=>c.Id!=changed.Id).Append(changed)));
        Check(patched.ImmediateMaximum(p,"play",PlayerSide.User).MaximumPoints is null,"Changed passive text silently ignored");
        Console.WriteLine("PASS Roach temporal/coverage/positive-evidence guards, recommendation weighting, Nekker/Shupe/Radeyah distinctions and inactive-ability gates.");
    }
    public static void Audit(string root, string[]? args = null)
    {
        var sessionIndex = args is null ? -1 : Array.IndexOf(args, "--session");
        var session = sessionIndex >= 0 && sessionIndex + 1 < args!.Length ? args[sessionIndex + 1] : "20260828-165436";
        var folder = Path.Combine(root,"GwentCompanion/sessions",session);
        var records = File.ReadLines(Path.Combine(folder,"vision-observations.jsonl"))
            .Select(line => JsonSerializer.Deserialize<CardVisionResult>(line, GameStateJournal.Json)!).ToArray();
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json"));
        var state = new GameStateTracker(); var knowledge = new OpponentKnowledge(); var inventory = new ZoneInventoryTracker();
        var grants = new GrantedMechanicTracker();
        var reference = JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(folder,"game-state-final.json")),GameStateJournal.Json)!.User.StartingDeckReference;
        state.Reset("recent-hover-audit",reference);
        GameStateSnapshot? cached = null; var hoverRows = new List<object>();
        var reasons = new Dictionary<string,int>(); var engineMissing = new Dictionary<string,int>();
        var checkedCards = new HashSet<string>(); var engine = new TacticalPlayEngine(new PlayRuleBook(catalog));
        var synergyValues = new List<object>(); string? lastSynergy = null;
        foreach (var r in records)
        {
            var update = GameStateVisionAdapter.Apply(state,r); var s = update.After;
            inventory.Observe(update); grants.Observe(update,catalog);
            foreach(var action in r.Events.Where(item=>item.Sighting.Source==CardSightSource.PlayPreview))
                inventory.ObserveSpecial(action.Sighting.Card,action.Sighting.Side,action.ObservedAt);
            knowledge.ObserveScreen(r.Screen,r.SampledAt);
            foreach (var e in r.Events) knowledge.Observe(e);
            knowledge.SummonAbsence.ObserveFrame(r.SampledAt,r.Screen,r.Sightings,r.BoardWasScanned);
            knowledge.ObserveConditions(update);
            if(s.Phase==GamePhase.Playing)
            {
                var reading=LiveSynergyMeter.Read(s,PlayerSide.Opponent,"Symbiosis","Nature's Gift",false,
                    grants.Read(PlayerSide.Opponent,"Symbiosis"),catalog);
                if(reading.Value!=lastSynergy){lastSynergy=reading.Value;synergyValues.Add(new {r.SampledAt,reading.Value,reading.Minimum,reading.Maximum,reading.Reason});}
            }
            if (s.Phase is GamePhase.Ended or GamePhase.RoundTransition || update.Events.Any(e=>e.Kind=="PlayPreview") ||
                cached is not null && ThreatBoardFreshness.Invalidated(cached,s)) cached=null;
            if (s.Phase==GamePhase.Playing && !s.BoardObscured && r.BoardWasScanned && !ThreatBoardFreshness.MissingScoringSide(s)) cached=s;
            if (!r.HoverInPlayerHand || r.HoveredCard is not {} card) continue;
            var reason = cached is null ? "No cached board" : r.SampledAt-cached.At > TimeSpan.FromSeconds(20) ? "Board over 20s old" :
                r.Screen.UserScore is {} own && own!=cached.User.Score?.Value || r.Screen.OpponentScore is {} opp && opp!=cached.Opponent.Score?.Value ? "Score changed" : "Ready";
            reasons[reason]=reasons.GetValueOrDefault(reason)+1;
            if (reason=="Ready" && checkedCards.Add(card.Id))
            {
                var p=ThreatPositionBuilder.Build(CalculationPositionAdapter.FromObserved(cached!),catalog,card,reference,[],inventory.Entries);
                var result=engine.ImmediateMaximum(p.Position,p.HoverInstanceId,PlayerSide.User);
                foreach(var missing in result.Missing) engineMissing[missing]=engineMissing.GetValueOrDefault(missing)+1;
                Console.WriteLine(card.Name+": max="+result.MaximumPoints+" modeled="+result.BestModeledPoints+" "+string.Join("; ",result.Missing));
            }
            hoverRows.Add(new { r.SampledAt, Card=card.Name, reason, CachedAt=cached?.At, Round=s.Round?.Value });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Frames=records.Length, reasons, engineMissing,synergyValues,
            Rules=knowledge.Assess([]), knowledge.Round, SummonEvidence=knowledge.SummonAbsence.Evidence },new JsonSerializerOptions {WriteIndented=true}));
        File.WriteAllText(Path.Combine(root,$"GwentCompanion/diagnostics/v0.1.31-{session}-hover-audit.json"),JsonSerializer.Serialize(hoverRows));
    }
}
