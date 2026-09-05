using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class LiveInsightsTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        CardDefinition Get(string name) => catalog.First(card => card.Name == name);
        var offer = PointDistribution.BestOffer([1, 2, 3, 4], 3, "test");
        Check(offer.Bins.Single(bin => bin.Points == 3).Probability == .25 && offer.Mean == 3.75 && offer.Ahead(3).Low == .75, "Best-of-three distribution");
        var incomplete = PointDistribution.BestOffer([1, 2, 3, null], 3, "test");
        Check(incomplete.Resolved == .25 && incomplete.Unresolved == .75 && incomplete.Ahead(2) == (.25, 1d), "Unknown pool members were silently dropped");
        Check(PointDistribution.BestOffer([1, 2], 5, "test").Mean == 2, "Small pools / Runemage offer count");
        var knownFloor = PointDistribution.BestKnownOffer([1, 2, 3, null], 3, "test");
        Check(knownFloor.IsOptionFloor && knownFloor.Resolved == 1 && knownFloor.Ahead(3).High == 1 && knownFloor.Mean == 2.75,
            "Known-option floor incorrectly bounded unmodeled alternatives");
        var tied = PointDistribution.BestOffer([2, 2, 5], 1, "test");
        Check(Math.Abs(tied.Bins.First().Probability - 2d / 3) < 1e-12, "Equal-valued options lost multiplicity");
        var damage = RandomDamageStatistics.Packets([(2, 0, false), (10, 0, false)], [5])!;
        Check(damage.Bins.SequenceEqual(new[] { new PointMass(2, .5), new PointMass(5, .5) }) && damage.Mean == 3.5, "Overkill histogram");
        Check(RandomDamageStatistics.Packets([(1, 0, true), (1, 0, false)], [1, 1])!.Bins.Single() == new PointMass(1, 1), "Target removal / shield timing");
        Check(RandomDamageStatistics.Packets([(10, 5, false)], [4], true)!.Mean == 4 && RandomDamageStatistics.Packets([(10, 5, false)], [4])!.Mean == 0, "Armor handling");
        Check(RandomDamageStatistics.Packets([(2, 0, false), (10, 0, false)], [1, 1], stateLimit: 1) is null, "Truncation invented a histogram");
        var plain = new CardDefinition("stat-target", "Target", "Neutral", CardKind.Unit, 4, 6, AbilityText: "", PrintedArmor: 0);
        var bombardment = Get("Bombardment"); var p = GamePosition.EmptyKnown();
        p = p with { Zones = p.Zones.Select(zone => zone.Side == PlayerSide.User && zone.Zone == CardZone.Hand ? zone with
            { Cards = [new("play", bombardment.Id, 0, 0, 0, ImmutableHashSet<CardStatus>.Empty)], TotalCount = 1 } :
            zone.Side == PlayerSide.Opponent && zone.Zone == CardZone.Board && zone.Row == BoardRow.Melee ? zone with
            { Cards = [new("target", plain.Id, 6, 6, 0, ImmutableHashSet<CardStatus>.Empty)], TotalCount = 1 } : zone).ToImmutableArray() };
        var statCatalog = catalog.Append(plain).ToArray();
        var randomMaximum = new TacticalPlayEngine(statCatalog).ImmediateMaximum(p, "play", PlayerSide.User);
        Check(randomMaximum.MaximumPoints == 4 && randomMaximum.FavorableRandomness, "Bombardment maximum integration");
        var statistics = RandomDamageStatistics.For(bombardment, p, PlayerSide.User, new(statCatalog));
        Check(statistics?.Distribution.Mean == 4, "Random effect lookup");
        var conditional = p with { Zones = p.Zones.Select(zone => zone with { Complete = false }).ToImmutableArray() };
        Check(RandomDamageStatistics.For(bombardment, conditional, PlayerSide.User, new(statCatalog))?.Distribution.Approximate == true,
            "Partial visible target set presented as unconditional probability");
        var at = DateTimeOffset.UnixEpoch; var ledger = new LiveValueLedger();
        var soldier = new CardDefinition("soldier", "Soldier", "Northern Realms", CardKind.Unit, 4, 4,
            CardCategories: new HashSet<string>(["Soldier"], StringComparer.OrdinalIgnoreCase));
        var symbiosis = new CardDefinition("symbiosis", "Engine", "Scoia'tael", CardKind.Unit, 4, 4, AbilityText: "Symbiosis.");
        var symbiosisReference = new CardDefinition("symbiosis-reference", "Reference only", "Scoia'tael", CardKind.Unit, 4, 4,
            AbilityText: "Whenever your Symbiosis is triggered, give another unit Vitality. Infuse a unit with \"Symbiosis\".");
        var damaged = new CardDefinition("damaged", "Damaged", "Monsters", CardKind.Unit, 4, 5);
        GameCardInstance MeterCard(CardDefinition card, string id, PlayerSide side, BoardRow row, double left, int power, int basePower) => new(id, card,
            new(new(side, CardZone.Board, row, new(left, .2, left + .05, .4)), at, 1, EvidenceKind.Visual, "fixture"), at, at,
            CardPresence.Visible, Power: new(power, at, 1, EvidenceKind.Visual, "fixture"), BasePower: new(basePower, at, 1, EvidenceKind.Visual, "fixture"));
        var meterCards = new[] {
            MeterCard(soldier,"s1",PlayerSide.User,BoardRow.Melee,.1,4,4), MeterCard(symbiosis,"engine",PlayerSide.User,BoardRow.Melee,.2,4,4),
            MeterCard(soldier,"s2",PlayerSide.User,BoardRow.Melee,.3,4,4), MeterCard(soldier,"s3",PlayerSide.User,BoardRow.Ranged,.1,4,4),
            MeterCard(soldier,"s4",PlayerSide.User,BoardRow.Ranged,.2,4,4), MeterCard(symbiosisReference,"reference",PlayerSide.User,BoardRow.Ranged,.3,4,4),
            MeterCard(damaged,"hurt",PlayerSide.Opponent,BoardRow.Melee,.1,3,5) };
        var meterRows = Enum.GetValues<PlayerSide>().SelectMany(side => Enum.GetValues<BoardRow>().Select(row => new GameRowState(side, row,
            RowCoverage.Complete, at, meterCards.Where(card => card.Location.Value.Controller == side && card.Location.Value.Row == row)
                .OrderBy(card => card.Location.Value.Region!.Value.Left).Select(card => card.InstanceId).ToImmutableArray(), []))).ToImmutableArray();
        var meterState = new GameStateTracker().Current with { SessionId="meters", At=at, Phase=GamePhase.Playing,
            Cards=meterCards.ToImmutableArray(), Rows=meterRows };
        Check(LiveSynergyMeter.Read(meterState, PlayerSide.User, "Symbiosis", "Nature's Gift") is { Value: "+2", Active: true },
            "Symbiosis did not count one actual visible engine plus Nature's Gift, or counted a prose/Infusion reference");
        var phooca = MeterCard(Get("Phooca"), "phooca", PlayerSide.User, BoardRow.Melee, .1, 4, 4);
        var phoocaRows = meterRows.Select(row => row with { VisibleInstanceIds =
            row.Side == PlayerSide.User && row.Row == BoardRow.Melee ? [phooca.InstanceId] : [] }).ToImmutableArray();
        var phoocaState = meterState with { Cards = [phooca], Rows = phoocaRows };
        Check(LiveSynergyMeter.Read(phoocaState, PlayerSide.User, "Thrive") is { Value: "+2", Minimum: 2, Active: true },
            "Printed Thrive 2 was collapsed to one Boolean engine instead of +2.");
        var partialRows = meterRows.Select(row => row with { Coverage = RowCoverage.Partial, ScannedAt = at.AddSeconds(1), VisibleInstanceIds =
            row.VisibleInstanceIds.Where(id => id != "engine").ToImmutableArray() }).ToImmutableArray();
        var rememberedCards = meterCards.Select(card => card.InstanceId == "engine" ? card with { Presence = CardPresence.LastKnown } : card).ToImmutableArray();
        var partialState = meterState with { At = at.AddSeconds(1), Rows = partialRows, Cards = rememberedCards };
        Check(LiveSynergyMeter.Read(partialState, PlayerSide.User, "Symbiosis", "Nature's Gift", false) is { Value: "+2", Active: true },
            "Recent Symbiosis contact or likely Nature's Gift passive was lost on a partial artwork pass");
        var fledgling = Get("Naiad Fledgling"); var aucwenn = Get("Aucwenn"); var grovekeeper = Get("Dryad Grovekeeper");
        var functionalCards = new[] { MeterCard(grovekeeper,"dryad",PlayerSide.User,BoardRow.Ranged,.1,4,4),
            MeterCard(fledgling,"fledgling",PlayerSide.User,BoardRow.Ranged,.2,4,4),
            MeterCard(aucwenn,"aucwenn",PlayerSide.User,BoardRow.Ranged,.3,4,4) };
        var functionalRows = Enum.GetValues<PlayerSide>().SelectMany(side => Enum.GetValues<BoardRow>().Select(row => new GameRowState(side, row,
            RowCoverage.Complete, at, functionalCards.Where(card => card.Location.Value.Controller == side && card.Location.Value.Row == row)
                .Select(card => card.InstanceId).ToImmutableArray(), []))).ToImmutableArray();
        var functionalState = meterState with { Cards=functionalCards.ToImmutableArray(), Rows=functionalRows };
        var functionalSymbiosis = FunctionalSynergyMeter.Read(functionalState, PlayerSide.User, "Symbiosis");
        Check(functionalSymbiosis is { MinimumPoints: 5, MaximumPoints: 5, Display: "+5*" },
            $"Naiad Fledgling +2 and Aucwenn's current three-Dryad scaling were not represented as functional Symbiosis value: {functionalSymbiosis}.");
        Check(LiveSynergyMeter.Read(functionalState, PlayerSide.User, "Symbiosis", catalog: catalog, functional: functionalSymbiosis)
                .Value == "+6*",
            "Printed and functional Symbiosis value did not collapse into one compact button total.");
        var warlordState = functionalState with { RecentEvents = [new("warlord-play", at, "PlayPreview", PlayerSide.User,
            Get("Highland Warlord").Id, null, "fixture")] };
        Check(FunctionalSynergyMeter.Read(warlordState, PlayerSide.User, "Raid") is { Display: "+1", MinimumPoints: 1 },
            "First Highland Warlord Deploy did not persist as Raid +1 damage.");
        var packageCards = new[] { MeterCard(Get("Crow Clan Preacher"),"preacher1",PlayerSide.User,BoardRow.Melee,.1,4,4),
            MeterCard(Get("Crow Clan Preacher"),"preacher2",PlayerSide.User,BoardRow.Melee,.2,4,4),
            MeterCard(Get("Fire Scorpion"),"scorpion",PlayerSide.User,BoardRow.Ranged,.1,4,4),
            MeterCard(Get("Hefty Helge"),"helge",PlayerSide.User,BoardRow.Ranged,.2,4,4),
            MeterCard(Get("Cutup Lackey"),"lackey1",PlayerSide.User,BoardRow.Melee,.3,4,4),
            MeterCard(Get("Cutup Lackey"),"lackey2",PlayerSide.User,BoardRow.Melee,.4,4,4),
            MeterCard(Get("Harald the Cripple"),"harald",PlayerSide.User,BoardRow.Ranged,.3,4,4),
            MeterCard(Get("An Craite Blacksmith"),"blacksmith",PlayerSide.User,BoardRow.Ranged,.4,4,4) };
        var packageState = functionalState with { Cards=packageCards.ToImmutableArray() };
        var singlePreacherState = packageState with { Cards = [packageCards[0]] };
        var singleAlchemy = FunctionalSynergyMeter.Read(singlePreacherState, PlayerSide.User, "Alchemy");
        Check(singleAlchemy is { MinimumPoints: 1 } && LiveSynergyMeter.Read(singlePreacherState, PlayerSide.User, "Alchemy", functional: singleAlchemy).Value == "+1",
            "A single Crow Clan Preacher did not display as the simple Alchemy +1 button.");
        Check(FunctionalSynergyMeter.Read(packageState, PlayerSide.User, "Alchemy") is { MinimumPoints: 4 },
            "Bonded Crow Clan Preachers were not treated as four functional Alchemy points per trigger.");
        var bondedAlchemy = FunctionalSynergyMeter.Read(packageState, PlayerSide.User, "Alchemy");
        Check(LiveSynergyMeter.Read(packageState, PlayerSide.User, "Alchemy", functional: bondedAlchemy).Value == "+4" &&
            bondedAlchemy!.Reason.Contains("+2 each (Bonded)"), "Bonded Preachers did not collapse to Alchemy +4 with +2 attributed to each engine.");
        var damagedPreacher = packageCards[0] with
        {
            Power = new(3, at, 1, EvidenceKind.Visual, "fixture"),
            BasePower = new(4, at, 1, EvidenceKind.Visual, "fixture")
        };
        var battleTranceState = packageState with { Cards = packageState.Cards.Replace(packageCards[0], damagedPreacher) };
        var battleTranceAlchemy = FunctionalSynergyMeter.Read(battleTranceState, PlayerSide.User, "Alchemy", "Battle Trance");
        Check(battleTranceAlchemy is { MinimumPoints: 5, MaximumPoints: 5 } &&
            LiveSynergyMeter.Read(battleTranceState, PlayerSide.User, "Alchemy", functional: battleTranceAlchemy).Value == "+5",
            "Battle Trance did not add exactly +1 while an allied unit was visibly damaged.");
        Check(FunctionalSynergyMeter.Read(packageState, PlayerSide.User, "Alchemy", "Battle Trance") is { MinimumPoints: 4, MaximumPoints: 4 },
            "Battle Trance added +1 without a visibly damaged allied unit.");
        var inferredBattleTrance = FunctionalSynergyMeter.Read(battleTranceState, PlayerSide.User, "Alchemy", "Battle Trance", false);
        Check(inferredBattleTrance is { MinimumPoints: 4, MaximumPoints: 5 } &&
            LiveSynergyMeter.Read(battleTranceState, PlayerSide.User, "Alchemy", functional: inferredBattleTrance).Value == "+5",
            "Unconfirmed Battle Trance was presented as exact rather than an approximate +1 ceiling.");
        var swarmOrganic = FunctionalSynergyMeter.Read(functionalState, PlayerSide.User, "Organic", "Arachas Swarm");
        Check(swarmOrganic is { MinimumPoints: 1, MaximumPoints: 1 } &&
            LiveSynergyMeter.Read(functionalState, PlayerSide.User, "Organic", functional: swarmOrganic).Value == "+1",
            "Arachas Swarm did not provide the compact Organic +1 passive.");
        var queen = MeterCard(Get("Kikimore Queen"), "queen", PlayerSide.User, BoardRow.Melee, .1, 6, 6);
        var hatchlingEngine = MeterCard(Get("Kikimore Hatchling"), "hatchling", PlayerSide.User, BoardRow.Melee, .2, 4, 4);
        var organicState = functionalState with { Cards = [queen, hatchlingEngine] };
        var organicEngines = FunctionalSynergyMeter.Read(organicState, PlayerSide.User, "Organic");
        var thriveEngines = FunctionalSynergyMeter.Read(organicState, PlayerSide.User, "Thrive");
        Check(organicEngines is { MinimumPoints: 3 } && thriveEngines is { MinimumPoints: 2 },
            $"Kikimore Queen row population and Hatchling were not carried into both Organic and Thrive engine totals (Organic {organicEngines?.MinimumPoints}, Thrive {thriveEngines?.MinimumPoints}).");
        Check(FunctionalSynergyMeter.Read(packageState, PlayerSide.User, "Tactic") is { MinimumPoints: 3 },
            "Portable charge parsing missed Fire Scorpion + Hefty Helge Tactic reach.");
        Check(FunctionalSynergyMeter.Read(packageState, PlayerSide.User, "Crime") is { MinimumPoints: 4 },
            "Portable payoff parsing missed two Bonded Cutup Lackeys.");
        Check(FunctionalSynergyMeter.Read(packageState, PlayerSide.User, "Warrior") is { MinimumPoints: 2 },
            "Portable payoff/charge parsing missed Harald plus An Craite Blacksmith.");
        var pondkeeper = new CardDefinition("203151", "Naiad Pondkeeper", "Scoia'tael", CardKind.Unit, 4, 4,
            AbilityText: "Deploy (Melee): Damage an enemy unit by 1. Deploy (Ranged): Infuse a non-Symbiosis allied unit with \"Symbiosis\".");
        var mantrap = new CardDefinition("203083", "Orchard Mantrap", "Monsters", CardKind.Unit, 4, 5,
            AbilityText: "Deploy (Melee): Consume an allied unit. Deploy (Ranged): Infuse 2 allied units with \"Thrive\".");
        var grantAt = at.AddSeconds(10);
        GameCardInstance GrantCard(CardDefinition card, string id, double left) => new(id, card,
            new(new(PlayerSide.User, CardZone.Board, BoardRow.Ranged, new(left,.4,left+.05,.6)), grantAt, 1, EvidenceKind.Visual, "fixture"),
            grantAt, grantAt, CardPresence.Visible, Statuses: []);
        var grantBefore = new GameStateTracker().Current with { SessionId="grants", At=grantAt.AddMilliseconds(-1), Phase=GamePhase.Playing,
            Round=new(1,at,1,EvidenceKind.Reviewed,"fixture") };
        var pondState = grantBefore with { At=grantAt, Cards=[GrantCard(pondkeeper,"pond",.2), GrantCard(soldier,"recipient",.4)] };
        var grants = new GrantedMechanicTracker();
        grants.Observe(new(grantBefore, pondState, [new("pond-play",grantAt,"PlayPreview",PlayerSide.User,pondkeeper.Id,null,"fixture")], true),
            [pondkeeper,mantrap,soldier,symbiosis]);
        Check(grants.Read(PlayerSide.User,"Symbiosis") is { Minimum: 1, Maximum: 1, Estimated: 1 },
            "Required-row Pondkeeper infusion was not carried into the generic granted-mechanic ledger");
        var trapAt=grantAt.AddSeconds(2);
        var trapState=pondState with { At=trapAt, Cards=pondState.Cards.Add(GrantCard(mantrap,"trap",.6) with { FirstSeen=trapAt,LastSeen=trapAt,
            Location=new(new(PlayerSide.User,CardZone.Board,BoardRow.Ranged,new(.6,.4,.65,.6)),trapAt,1,EvidenceKind.Visual,"fixture") }) };
        grants.Observe(new(pondState,trapState,[new("trap-play",trapAt,"PlayPreview",PlayerSide.User,mantrap.Id,null,"fixture")],true),
            [pondkeeper,mantrap,soldier,symbiosis]);
        Check(grants.Read(PlayerSide.User,"Thrive") is { Minimum: 2, Maximum: 2, Estimated: 2 },
            "The portable infusion parser did not preserve Orchard Mantrap's two-target Thrive range");
        Check(LiveSynergyMeter.Read(meterState, PlayerSide.User, "Crew") is { Value: "+2", Active: true },
            "Crew did not count one occupied and one empty Soldier pocket");
        Check(LiveSynergyMeter.Read(meterState, PlayerSide.User, "Bloodthirst") is { Value: "+1", Active: true },
            "Bloodthirst did not count damaged enemy units");
        PirateArmorTests.Run();
        ledger.Add(new("buff", PlayerSide.User, "source", "Source", PendingValueKind.HandBoost, 4, 4, "reviewed", at, TargetCardId: "target"));
        ledger.RealizeBoost("play1", PlayerSide.User, "target", 2, PendingValueKind.HandBoost);
        ledger.RealizeBoost("play1", PlayerSide.User, "target", 2, PendingValueKind.HandBoost);
        Check(ledger.Pending.Single().Maximum == 2, "Duplicate frame consumed boost twice");
        ledger.RealizeBoost("play2", PlayerSide.Opponent, "target", 2, PendingValueKind.HandBoost);
        Check(ledger.Pending.Single().Maximum == 2, "Cross-side allocation");
        ledger.RealizeBoost("play3", PlayerSide.User, "other", 2, PendingValueKind.HandBoost);
        Check(ledger.Pending.Single().Maximum == 2, "Wrong target allocation");
        ledger.Settle("buff", "reviewed"); Check(ledger.Pending.Count == 0, "Settlement"); ledger.Undo(); Check(ledger.Pending.Count == 1, "Undo");
        ledger.Store(PlayerSide.Opponent, Get("Fiend"), "reviewed");
        Check(ledger.Pending.Count == 1 && ledger.Stored.Single().Target.Name == "Fiend", "Sword stored soul became carryover");
        ledger.AddSpying(PlayerSide.Opponent, Get("Fiend"));
        ledger.AddSpying(PlayerSide.Opponent, Get("Fiend"));
        Check(ledger.SpyingGranted.Count == 1 && ledger.SpyingGranted.Single().Side == PlayerSide.Opponent,
            "Reviewed Artaud Spying identity was not persisted or was double-counted");
        Check(ledger.SetBountyHistory(PlayerSide.Opponent, 17, 3, 8) &&
            ledger.Bounties.Single(value => value.Side == PlayerSide.Opponent) is
                { TotalBasePower: 17, TotalPlacements: 3, MaximumBasePower: 8 },
            "Reviewed Bounty totals/placements/maximum were not retained as three distinct inputs");
        Check(!ledger.SetBountyHistory(PlayerSide.User, 4, 2, 5),
            "Invalid Bounty maximum larger than total base power was accepted");
        var scenarioLedger = new LiveValueLedger();
        var scenarioBase = new GameStateTracker().Current with { SessionId = "scenario-stage", At = at, Phase = GamePhase.Playing };
        var passiflora = Get("One Night at the Passiflora"); var courier = Get("Courier");
        var scenarioSeen = scenarioBase with { At = at.AddSeconds(1) };
        scenarioLedger.Observe(new(scenarioBase, scenarioSeen,
            [new("scenario-play", at.AddSeconds(1), "PlayPreview", PlayerSide.Opponent, passiflora.Id, null, "fixture")], true), catalog, [], [passiflora, courier]);
        var blindeyeSeen = scenarioSeen with { At = at.AddSeconds(2) };
        scenarioLedger.Observe(new(scenarioSeen, blindeyeSeen,
            [new("blindeye-play", at.AddSeconds(2), "PlayPreview", PlayerSide.Opponent, courier.Id, null, "fixture")], true), catalog, [], [passiflora, courier]);
        Check(scenarioLedger.ScenarioStage(PlayerSide.Opponent, passiflora.Id) == 1,
            "Play history did not preserve the opponent Scenario chapter stage when the counter was not OCR-readable");
        var aerondightLedger = new LiveValueLedger(); var aerondight = Get("Aerondight");
        var beforePass = scenarioBase with { SessionId = "aerondight-pass",
            User = new(PlayerSide.User, Score: new(12, at, 1, EvidenceKind.Reviewed, "fixture"), Passed: new(false, at, 1, EvidenceKind.Reviewed, "fixture")),
            Opponent = new(PlayerSide.Opponent, Score: new(8, at, 1, EvidenceKind.Reviewed, "fixture")) };
        var passed = beforePass with { At = at.AddSeconds(1),
            User = beforePass.User with { Passed = new(true, at.AddSeconds(1), 1, EvidenceKind.Reviewed, "fixture") } };
        aerondightLedger.Observe(new(beforePass, passed, [], true), catalog, [aerondight], []);
        Check(aerondightLedger.Growth(PlayerSide.User, aerondight.Id) is { Minimum: 1, Maximum: 1 },
            "Live Aerondight hypothesis did not grow when a visible pass ended the turn while ahead");
        var king = Get("King of Beggars");
        Check(!ledger.TributeRefund("refund", PlayerSide.Opponent, 4, 4, false, at, king), "Generic coin gain confirmed King");
        Check(ledger.TributeRefund("refund", PlayerSide.Opponent, 4, 4, true, at, king) && ledger.Growth(PlayerSide.Opponent, king.Id)?.Maximum == 8, "Refund counter");
        Check(!ledger.TributeRefund("refund", PlayerSide.Opponent, 4, 4, true, at, king), "Duplicate refund");
        var tribute = Get("Moreelse");
        GameStateSnapshot CoinState(string session, int second, int coins) => new GameStateTracker().Current with
        {
            SessionId = session, Revision = second, At = at.AddSeconds(second), Phase = GamePhase.Playing,
            Opponent = new(PlayerSide.Opponent, Coins: new(coins, at.AddSeconds(second), .9, EvidenceKind.Visual, "fixture"))
        };
        GameStateEvent TributePlay(int second) => new($"tribute:{second}", at.AddSeconds(second), "PlayPreview",
            PlayerSide.Opponent, tribute.Id, null, "fixture Tribute play");
        TributeRefundObservation[] RefundSequence(string session, int afterCoins)
        {
            var refunds = new TributeRefundTracker(); var initialCoins = CoinState(session, 0, 8);
            refunds.Observe(new(initialCoins, initialCoins, [], true), [tribute]);
            var played = CoinState(session, 1, 8);
            refunds.Observe(new(initialCoins, played, [TributePlay(1)], true), [tribute]);
            var after = CoinState(session, 2, afterCoins);
            return refunds.Observe(new(played, after, [], true), [tribute]).ToArray();
        }
        var partialRefund = RefundSequence("king-partial", 7).Single();
        Check(partialRefund is { Verified: true, NoRefund: false, PaidCoins: 6, RefundedCoins: 5 }, "Moreelse-like 8→7 did not verify a five-coin King refund.");
        var fullCompatible = RefundSequence("king-compatible", 8).Single();
        Check(!fullCompatible.Verified && fullCompatible.RefundedCoins == 6 && fullCompatible.CompatibleEventCount == 1,
            "Unchanged coin HUD was either discarded or incorrectly called verified.");
        Check(RefundSequence("king-normal", 2).Single() is { NoRefund: true, RefundedCoins: 0 },
            "Normal Tribute payment did not produce King absence/hand-brick evidence.");
        var hintLedger = new LiveValueLedger();
        Check(hintLedger.TributeRefundHint("hint", PlayerSide.Opponent, 6, 6, at, king) &&
            hintLedger.Growth(PlayerSide.Opponent, king.Id) is { Minimum: 6, Maximum: 12 }, "Ambiguous Tribute hint did not preserve a counter range.");
        Check(hintLedger.TributeRefund("verified", PlayerSide.Opponent, 6, 5, true, at, king) &&
            hintLedger.Growth(PlayerSide.Opponent, king.Id) is { Minimum: 1, Maximum: 7 }, "Verified refund did not reconcile prior compatible Tribute range.");
        ledger.Reset();
        var initial = new GameStateTracker().Current with { SessionId = "insights", At = at, Phase = GamePhase.Playing,
            Round = new(1, at, 1, EvidenceKind.Reviewed, "fixture") };
        GameCardInstance Instance(string name, string id, int second, int? charges = null) => new(id, Get(name),
            new(new(PlayerSide.Opponent, CardZone.Board, BoardRow.Melee, new(.4,.2,.5,.4)), at.AddSeconds(second), 1, EvidenceKind.Reviewed, "fixture"),
            at, at.AddSeconds(second), CardPresence.Visible, Charges: charges is null ? null : new(charges.Value, at.AddSeconds(second), 1, EvidenceKind.Reviewed, "fixture"));
        var location = initial with { Cards = [Instance("The Mushy Truffle", "contact1", 0), Instance("Mahakam Pass", "pass", 0)] };
        ledger.Observe(new(initial, location, [], true), catalog, [], []);
        Check(ledger.Pending.Sum(item => item.Maximum) == 11, "Truffle 6 + Pass 5 not aggregated");
        var reacquired = location with { At = at.AddSeconds(1), Cards = [Instance("The Mushy Truffle", "contact2", 1), Instance("Mahakam Pass", "pass", 1)] };
        ledger.Observe(new(location, reacquired, [], true), catalog, [], []);
        Check(ledger.Pending.Sum(item => item.Maximum) == 11, "Reacquired artwork duplicated banked value");
        var spent = reacquired with { At = at.AddSeconds(2), Cards = [Instance("The Mushy Truffle", "contact2", 2, 0)] };
        ledger.Observe(new(reacquired, spent, [], true), catalog, [], []);
        Check(ledger.Pending.Single().Maximum == 5, "Exhausted location Order stayed pending");
        var phoenix = spent with { At = at.AddSeconds(3), ZoneEvidence = [new("201579", "Phoenix", PlayerSide.Opponent, CardZone.Graveyard,
            at.AddSeconds(3), at.AddSeconds(3), 1, false, CardProvenance.Unknown, "reviewed graveyard")] };
        ledger.Observe(new(spent, phoenix, [], true), catalog, [], []);
        Check(ledger.Pending.Where(item => item.Kind == PendingValueKind.Phoenix).Sum(item => item.Maximum) == 10, "Phoenix round-one remaining-match potential");
        var hatchling = phoenix with { At = at.AddSeconds(3.5), Round = new(2, at.AddSeconds(3.5), 1, EvidenceKind.Reviewed, "fixture"),
            Cards = [Instance("Phoenix Hatchling", "egg", 3) with { LastSeen = at.AddSeconds(3.5) }] };
        ledger.Observe(new(phoenix, hatchling, [], true), catalog, [], []);
        Check(ledger.Pending.Where(item => item.Kind == PendingValueKind.Phoenix).Sum(item => item.Maximum) == 10, "Hatchling arrival alone realized body points");
        Check(!ledger.Pending.Any(item => item.CardId == "202114"), "Hatchling artifact Order was counted a second time");
        var next = phoenix with { At = at.AddSeconds(4), Round = new(2, at.AddSeconds(4), 1, EvidenceKind.Reviewed, "fixture"), Cards = [Instance("Phoenix", "phoenix", 4)] };
        ledger.Observe(new(phoenix, next, [], true), catalog, [], []);
        Check(ledger.Pending.Where(item => item.Kind == PendingValueKind.Phoenix).Sum(item => item.Maximum) == 5, "Phoenix round-two realization must retain round-three potential");
        var freshSecond = new LiveValueLedger(); freshSecond.Observe(new(phoenix, next, [], true), catalog, [], []);
        Check(freshSecond.Pending.Where(item => item.Kind == PendingValueKind.Phoenix).Sum(item => item.Maximum) == 5,
            "Phoenix first played in round two must bank only one future return");
        var third = next with { At = at.AddSeconds(5), Round = new(3, at.AddSeconds(5), 1, EvidenceKind.Reviewed, "fixture"), Cards = [Instance("Phoenix", "phoenix3", 5)] };
        ledger.Observe(new(next, third, [], true), catalog, [], []);
        Check(!ledger.Pending.Any(item => item.Kind == PendingValueKind.Phoenix), "Phoenix round-three body stayed pending / stale graveyard re-added it");
        var freshThird = new LiveValueLedger(); freshThird.Observe(new(next, third, [], true), catalog, [], []);
        Check(freshThird.Pending.Count == 0, "First Phoenix in round three manufactured a future round");
        var vypperLedger = new LiveValueLedger();
        var vypperSeen = initial with { At = at.AddSeconds(6), Cards = [Instance("Vypper", "vypper-carry", 6)] };
        vypperLedger.Observe(new(initial, vypperSeen, [], true), catalog, [], []);
        Check(vypperLedger.Pending.Single(item => item.CardId == Get("Vypper").Id) is
            { Kind: PendingValueKind.GraveyardReturn, Minimum: 0 } vypperCarry && vypperCarry.Maximum == Get("Vypper").Power,
            "Visible Vypper did not bank its conditional cross-graveyard body as carryover");
        var vypperReturned = vypperSeen with { At = at.AddSeconds(7), Round = new(2, at.AddSeconds(7), 1, EvidenceKind.Reviewed, "fixture"),
            Cards = [Instance("Vypper", "vypper-carry", 7) with
            {
                Statuses = [new(CardStatus.Doomed, new(true, at.AddSeconds(7), 1, EvidenceKind.Reviewed, "fixture"))]
            }] };
        vypperLedger.Observe(new(vypperSeen, vypperReturned, [], true), catalog, [], []);
        Check(!vypperLedger.Pending.Any(item => item.CardId == Get("Vypper").Id),
            "Returned Doomed Vypper remained pending as a second carryover body");
        var toadLedger = new LiveValueLedger();
        var toadZone = new ZoneHypothesis(Get("Giant Toad").Id, PlayerSide.Opponent, CardZone.Graveyard, 1, true, at, "fixture");
        toadLedger.ObserveInventory([toadZone], initial);
        Check(toadLedger.Pending.Single(item => item.CardId == Get("Giant Toad").Id) is { Kind: PendingValueKind.GraveyardReturn, Minimum: 0, Maximum: 4 },
            "Giant Toad in graveyard did not bank its conditional four-point return.");
        var toadBoard = initial with { At = at.AddSeconds(1), Cards = [Instance("Giant Toad", "toad", 1)] };
        toadLedger.ObserveInventory([toadZone with { Copies = 0, At = at.AddSeconds(1) }], toadBoard);
        Check(!toadLedger.Pending.Any(item => item.CardId == Get("Giant Toad").Id), "Realized Giant Toad return remained in carryover.");
        var reference = new DeckDefinition("test", "Fixture", "Monsters", "", 0, [new(Get("Fiend"), 2)]);
        var observed = new[] { new ObservedCard(Get("Fiend"), CardProvenance.ConfirmedStartingDeck, 1, at, "fixture", 2),
            new ObservedCard(Get("Old Speartip"), CardProvenance.Created, 1, at, "generated") };
        var provisions = LiveValueLedger.Provisions(observed, reference, 165);
        Check(provisions.SpentFloor == reference.ProvisionTotal && provisions.Total == reference.ProvisionTotal, "Generated/replayed costs or allowance confused with deck sum");
        Check(LiveValueLedger.Provisions(observed, null, 165, new HashSet<string>()).SpentFloor == 0, "Unplayed deck reveal counted as spent");
        var soul = CreatedCardDescriptionReader.Parse("Hen Gaidth Sword\nStored soul: Nekker Warrior", catalog);
        Check(soul is { Candidates.Count: 1 } && soul.Candidates[0].Name == "Nekker Warrior", "Sword captured bronze unit description");
        Check(RuntimeCardValueReader.Read(Get("Aerondight"), "Aerondight Damage an enemy unit by 12, then boost...")?.Amount == 12, "Runtime damage parser");
        Check(RuntimeCardValueReader.Read(Get("Fiend"), "Aerondight Damage an enemy unit by 12") is null, "Wrong hovered identity authorized dynamic value");
        Check(RuntimeCardValueReader.Read(Get("Spring Equinox"), "Spring Equinox Nature Doomed. Boost an allied unit by [10]. Increase the boost") is { Amount: 10, Unit: "other Nature cards" },
            "Spring Equinox starting-Nature count parser");
        Check(RuntimeCardValueReader.Read(Get("Caranthir: Golden Child"), "Caranthir: Golden Child last 5 enemy turns (2, 0, 4, 1, 3)") is { Amount: 11, Unit: "current power" },
            "Caranthir Frost-history power parser");
        UiShellTests.Navigation(root);
        Console.WriteLine("PASS Live insights: probability mass, best-of-3/5, unknown offers, overkill/shield/armor, ledger settlement/undo, locations/Phoenix, Sword, refunds and provision provenance.");
    }

    public static void Profiles(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var profiles = CreatePointProfiles.LoadOrBuild(Path.Combine(root, "GwentCompanion/cache/create-point-profiles.json"), catalog);
        CardDefinition Get(string name) => catalog.Single(card => card.Name == name);
        var empty = GamePosition.EmptyKnown();
        Check(profiles.Estimate(Get("Bountiful Harvest"), empty, PlayerSide.User, catalog, new("Scoia'tael", "Monsters")) is { PoolCards: > 0 },
            "Bountiful Harvest's bronze Scoia'tael Elf pool was not modeled.");
        Check(profiles.Estimate(Get("Filavandrel aén Fidháil"), empty, PlayerSide.User, catalog, new("Scoia'tael", "Monsters")) is { PoolCards: > 0 },
            "Filavandrel's row-dependent provision pool was not modeled.");
        Check(profiles.Estimate(Get("Runemage"), empty, PlayerSide.User, catalog, new("Neutral", "Monsters")) is { PoolCards: > 0 },
            "Runemage's nested Runestone Create was not modeled.");
        Check(profiles.Estimate(Get("Sorceress of Dol Blathanna"), empty, PlayerSide.User, catalog, new("Scoia'tael", "Monsters")) is null,
            "A non-Zeal Sorceress Order was counted as immediate reach.");
        var cases = catalog.Where(card => card.CanBeInStartingDeck && card.AbilityText?.Contains("Create") == true)
            .Select(card => profiles.Estimate(card, GamePosition.EmptyKnown(), PlayerSide.User, catalog, new("Nilfgaard", "Monsters")))
            .Where(estimate => estimate is not null).Select(estimate => new { estimate!.Card.Name, estimate.ModeledCards, estimate.PoolCards,
                ChanceOfModeledChoice = estimate.Distribution.Resolved, MeanKnownChoiceFloor = estimate.Distribution.Mean,
                Low = estimate.Distribution.Quantile(.1), High = estimate.Distribution.Quantile(.9) }).ToArray();
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.26-create-analysis.json"), JsonSerializer.Serialize(new
        { Patch = DeckPatchMetadata.Current(DateTimeOffset.Now).Label, profiles.ModelVersion, profiles.Fingerprint, ElapsedSeconds = started.Elapsed.TotalSeconds,
            Context = "Empty representative board; not a current-board guarantee. Known-choice floor only; unknown options may be better even when modeled options are offered.", Cases = cases }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Create profiles: {profiles.Values.Count} stored card/context entries; {cases.Length} applicable static recipes; {started.Elapsed.TotalSeconds:0.00}s.");
        foreach (var item in cases) Console.WriteLine($"{item.Name}: {item.ModeledCards}/{item.PoolCards} options; {item.ChanceOfModeledChoice:P1} chance of modeled choice; known-choice floor {item.Low}–{item.High}");
    }
    private sealed record ReplayRow(string Frame, CardVisionResult Result);
    public static void Replay(string root)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var rows = JsonSerializer.Deserialize<ReplayRow[]>(File.ReadAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.25-final-streaming.json")), GameStateJournal.Json)!;
        var ledger = new LiveValueLedger(); var tracker = new GameStateTracker(); tracker.Reset("insights-replay"); var events = new MatchVisionLedger();
        var peak = 0;
        foreach (var row in rows)
        {
            var result = row.Result;
            result = result with { Events = events.Observe(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned, result.HoveredCard, result.ArtworkWasScanned) };
            var update = GameStateVisionAdapter.Apply(tracker, result); ledger.Observe(update, catalog, [], []);
            peak = Math.Max(peak, ledger.Pending.Count);
            Check(ledger.Pending.All(item => item.Minimum >= 0 && !(item.Maximum < item.Minimum)), "Invalid live range");
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.26-live-ledger-replay.json"), JsonSerializer.Serialize(new
        { Frames = rows.Length, PeakPending = peak, Pending = ledger.Pending, Note = "Integration replay only; this recording is not ground truth for every carryover mechanic. No early AI matches used." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS recent real-opponent replay: {rows.Length} frames, peak {peak} pending sources; uncertain sources remain ranges.");
    }
}
