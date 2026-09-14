using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class DetectorUpdateTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Confirmation()
    {
        using (var candidates = new FeatureCardRecognizer([], scope: VisionReferenceScope.CandidateDecks))
        {
            Check(candidates.CanSeedPlayerCandidateFromAmbiguousHover("unscoped"),
                "An unpinned player candidate scope rejected an exact hover.");
            candidates.SetKnownPlayerDeck(["player-deck-card"]);
            Check(candidates.CanSeedPlayerCandidateFromAmbiguousHover("player-deck-card") &&
                  !candidates.CanSeedPlayerCandidateFromAmbiguousHover("opponent-hover"),
                "An ambiguous opponent hover leaked into a complete player-deck candidate scope.");
            candidates.ObservePlayerCards(["generated-player-card"]);
            Check(candidates.CanSeedPlayerCandidateFromAmbiguousHover("generated-player-card"),
                "An independently established generated player card was excluded from hover candidates.");
            candidates.ObserveOpponentCards(["opponent-hover"]);
            Check(candidates.CanUseFallback("player-deck-card", PlayerSide.User) &&
                  candidates.CanUseFallback("generated-player-card", PlayerSide.User) &&
                  !candidates.CanUseFallback("opponent-hover", PlayerSide.User),
                "An opponent-only appearance leaked through the player fallback scope.");
            Check(candidates.CanUseFallback("opponent-hover", PlayerSide.Opponent) &&
                  !candidates.CanUseFallback("player-deck-card", PlayerSide.Opponent),
                "A pinned player-only appearance leaked through the opponent fallback scope.");
            candidates.ObserveOpponentSummonCandidates(["generated-opponent-card"]);
            Check(candidates.CanUseFallback("generated-opponent-card", PlayerSide.Opponent),
                "An explicitly armed opponent summon output was excluded from fallback recognition.");
        }
        var ledger = new MatchVisionLedger(); var at = DateTimeOffset.UnixEpoch;
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
        var sight = new CardSighting(new("test", "Test", "Scoia'tael", CardKind.Unit, 4),
            PlayerSide.Opponent, CardSightSource.PlayPreview, new(.8, .1, .9, .4), .16, 1, NeedsTemporalConfirmation: true);
        Check(ledger.Observe(at, board, [sight]).Count == 0, "Weak single frame must not commit.");
        Check(ledger.Observe(at, board, [sight]).Count == 0, "Same timestamp cannot confirm itself.");
        Check(ledger.Observe(at.AddMilliseconds(200), board, [sight]).Count == 1, "Second independent frame confirms.");
        Check(ledger.Observe(at.AddSeconds(1), board, [sight]).Count == 0, "Persistent preview cannot duplicate.");
        ledger.Reset(); ledger.Observe(at, board, [sight]);
        Check(ledger.Observe(at.AddSeconds(3), board, [sight]).Count == 0, "Old evidence expires.");
        ledger.Reset(); ledger.Observe(at, board, [sight]);
        Check(ledger.Observe(at.AddMilliseconds(200), board, [sight with { Side = PlayerSide.User }]).Count == 0, "Sides cannot confirm each other.");
        ledger.Reset(); ledger.Observe(at, board, [sight]); ledger.Observe(at.AddMilliseconds(100), board, []);
        Check(ledger.Observe(at.AddMilliseconds(200), board, [sight]).Count == 1, "A brief empty tick must not erase a corroborating weak candidate.");
        ledger.Reset(); ledger.Observe(at, board, [sight]);
        var arrival = sight with { Source = CardSightSource.Board, Region = new(.45, .2, .52, .36), Distance = .2 };
        Check(ledger.Observe(at.AddSeconds(1), board with { MatchHudVisible = true }, [arrival]).Single().Sighting.Card.Id == sight.Card.Id,
            "Short preview plus immediate same-side board arrival did not corroborate the play.");
        ledger.Reset(); ledger.Observe(at, board, [sight]); ledger.Observe(at.AddMilliseconds(100), board, []);
        Check(ledger.Observe(at.AddSeconds(1), board with { MatchHudVisible = true }, [arrival with { Side = PlayerSide.User }]).Count == 0,
            "Opponent preview was corroborated by a player-side board arrival.");
        Check(ledger.Observe(at.AddSeconds(5), board with { MatchHudVisible = true }, [arrival]).Count == 0,
            "Stale short preview was corroborated by a later board identity.");
        var opponentHud = board with { MatchHudVisible = true, OpponentHandCount = 9,
            TooltipRegion = new(.58, .28, .75, .50), HasCardTooltip = true };
        var popupSight = sight with { Region = new(.811, .146, .924, .399), Distance = .24, Margin = 1 };
        ledger.Reset();
        ledger.Observe(at, opponentHud, [], confirmedHover: popupSight.Card);
        ledger.Observe(at.AddMilliseconds(200), opponentHud, [], confirmedHover: popupSight.Card);
        Check(ledger.Observe(at.AddMilliseconds(400), opponentHud with { OpponentHandCount = null }, [popupSight],
                  confirmedHover: popupSight.Card).Count == 0,
            "A one-frame difficult popup committed before its opponent hand cost was visible.");
        var paidPopup = ledger.Observe(at.AddSeconds(3), opponentHud with
            { OpponentHandCount = 8, TooltipRegion = null, HasCardTooltip = false }, []);
        Check(paidPopup.Count == 1 && paidPopup[0].Sighting.Card.Id == popupSight.Card.Id &&
              !paidPopup[0].Sighting.NeedsTemporalConfirmation,
            "Repeated exact opponent popup plus its later one-card hand decrement did not recover the difficult play.");
        ledger.Reset();
        var ambiguousPopup = popupSight with { Margin = .69 };
        ledger.Observe(at, opponentHud, [], confirmedHover: ambiguousPopup.Card);
        ledger.Observe(at.AddMilliseconds(200), opponentHud, [], confirmedHover: ambiguousPopup.Card);
        ledger.Observe(at.AddMilliseconds(400), opponentHud with { OpponentHandCount = null }, [ambiguousPopup],
            confirmedHover: ambiguousPopup.Card);
        Check(ledger.Observe(at.AddSeconds(3), opponentHud with
            { OpponentHandCount = 8, TooltipRegion = null, HasCardTooltip = false }, []).Count == 0,
            "An ambiguous artwork candidate was promoted by popup text and a later hand decrement.");
        ledger.Reset();
        ledger.Observe(at, opponentHud, [], confirmedHover: popupSight.Card);
        ledger.Observe(at.AddMilliseconds(200), opponentHud, [], confirmedHover: popupSight.Card);
        ledger.Observe(at.AddMilliseconds(400), opponentHud with { OpponentHandCount = null }, [popupSight],
            confirmedHover: popupSight.Card);
        Check(ledger.Observe(at.AddSeconds(3), opponentHud with { OpponentHandCount = 7 }, []).Count == 0,
            "A multi-card/round hand jump confirmed a weak opponent popup.");
        ledger.Reset();
        ledger.Observe(at, opponentHud, [], confirmedHover: popupSight.Card);
        ledger.Observe(at.AddMilliseconds(200), opponentHud, [], confirmedHover: popupSight.Card);
        ledger.Observe(at.AddMilliseconds(400), opponentHud with { OpponentHandCount = null }, [popupSight],
            confirmedHover: popupSight.Card);
        var otherPopup = popupSight.Card with { Id = "other-popup", Name = "Other Popup" };
        Check(ledger.Observe(at.AddSeconds(1), opponentHud with { OpponentHandCount = null }, [], confirmedHover: otherPopup).Count == 0 &&
              ledger.Observe(at.AddSeconds(3), opponentHud with { OpponentHandCount = 8 }, []).Count == 0,
            "A changed inspected title left an old weak popup armed for the next opponent play.");
        ledger.Reset(); ledger.Observe(at, board, [sight]);
        ledger.Observe(at.AddMilliseconds(100), board with { IsCardSelectionOverlay = true }, [sight]);
        Check(ledger.Observe(at.AddMilliseconds(200), board, [sight]).Count == 0, "Choice overlay clears weak candidate.");
        ledger.Reset(); ledger.Observe(at, board, [sight]);
        Check(ledger.Observe(at.AddMilliseconds(200), board, [sight with { Card = sight.Card with { Id = "other" } }]).Count == 0, "Different identities cannot confirm each other.");
        ledger.Reset();
        Check(ledger.Observe(at, board, [sight with { NeedsTemporalConfirmation = false }]).Count == 1, "Strong preview retains immediate path.");
        ledger.Observe(at.AddSeconds(1), board, [], false, artworkWasScanned: false);
        ledger.Observe(at.AddSeconds(4), board, [], false, artworkWasScanned: false);
        Check(ledger.Observe(at.AddSeconds(5), board, [sight]).Count == 0, "Unscanned artwork split a persistent preview episode.");
        ledger.Observe(at.AddSeconds(6), board, []);
        ledger.Observe(at.AddSeconds(9), board, []);
        Check(ledger.Observe(at.AddSeconds(10), board, [sight with { NeedsTemporalConfirmation = false }]).Count == 1, "Verified absence failed to end a preview episode.");
        var shupe = sight with { Card = sight.Card with { Id = "shupe-root", Name = "Shupe's Day Off" }, NeedsTemporalConfirmation = false };
        var mage = sight with { Card = sight.Card with { Id = "visual-family:mage", Name = "Shupe: Mage", CanBeInStartingDeck = false, AbilityText = null },
            Source = CardSightSource.Board, Distance = .08, NeedsTemporalConfirmation = false };
        ledger.Reset();
        Check(ledger.Observe(at, board with { MatchHudVisible = true }, [mage]).Count == 0, "A single uncorroborated family image was trusted.");
        ledger.Reset(); ledger.Observe(at, board, [shupe]);
        Check(ledger.Observe(at.AddSeconds(9), board with { MatchHudVisible = true }, [mage with { Side = PlayerSide.User }]).Count == 0, "Opponent creation confirmed a player-side family.");
        Check(ledger.Observe(at.AddSeconds(10), board with { MatchHudVisible = true }, [mage]).Count == 1, "Recent Shupe play did not corroborate its visible generated form.");
        ledger.Reset(); ledger.Observe(at, board, [shupe]);
        Check(ledger.Observe(at.AddSeconds(25), board with { MatchHudVisible = true }, [mage]).Count == 0, "Expired creation context confirmed a family.");
        var hiveMind = sight with { Card = new CardDefinition("203226", "Hive Mind", "Monsters", CardKind.Artifact, 12,
            AbilityText: "Choose 4 different bronze Kikimore units."), Side = PlayerSide.User, NeedsTemporalConfirmation = false };
        var pick = board with { IsCardSelectionOverlay = true, ScreenHeader = "PICK A CARD" };
        var choiceHand = board with { MatchHudVisible = true, UserHandCount = 5 };
        ledger.Reset();
        Check(ledger.Observe(at, choiceHand, [], confirmedHover: hiveMind.Card, hoverInPlayerHand: true).Count == 0,
            "A hand tooltip alone became a play.");
        var inferred = ledger.Observe(at.AddSeconds(8), pick, []);
        Check(inferred.Count == 1 && inferred[0].Sighting.Card.Id == "203226" && inferred[0].Sighting.Side == PlayerSide.User &&
              inferred[0].Sighting.Source == CardSightSource.PlayPreview,
            "Exact hand tooltip followed by its choice overlay did not recover the missed play animation.");
        var mutations = new DeckMutationLedger(); mutations.Observe(inferred[0], "Monsters", "Skellige");
        var generatedKikimore = new CardSighting(new("kikimore", "Kikimore Warrior", "Monsters", CardKind.Unit, 4,
            IsGold: false), PlayerSide.User, CardSightSource.Board, new(.5, .65, .57, .82), .08, 1);
        Check(mutations.OriginRisk(generatedKikimore, at.AddSeconds(12), [])?.Provenance == CardProvenance.Unknown,
            "Recovered Hive Mind play did not keep a later transformed Kikimore out of original-deck provisions.");
        Check(ledger.Observe(at.AddSeconds(9), pick, []).Count == 0, "A persistent choice overlay duplicated the inferred play.");
        ledger.Reset(); ledger.Observe(at, board, [], confirmedHover: hiveMind.Card, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(2), pick with { ScreenHeader = "DECK" }, []).Count == 0,
            "Opening a deck inspection converted a hand hover into a play.");
        Check(ledger.Observe(at.AddSeconds(16), pick, []).Count == 0, "A stale hand hover inferred a later unrelated play.");
        ledger.Reset(); ledger.Observe(at, board, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(2), pick, []).Count == 0, "A card with no choice text inferred a selection play.");
        var handHud = board with { MatchHudVisible = true, UserHandCount = 5 };
        ledger.Reset();
        Check(ledger.Observe(at, handHud, [], confirmedHover: sight.Card, hoverInPlayerHand: true).Count == 0,
            "A hand hover alone became a play.");
        var handPlay = ledger.Observe(at.AddSeconds(1), handHud with { UserHandCount = 4 }, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        Check(handPlay.Count == 1 && handPlay[0].Sighting.Card.Id == sight.Card.Id && handPlay[0].Sighting.Side == PlayerSide.User,
            "Exact hand hover plus a one-card decrement did not recover the player play.");
        var frog = sight.Card with { Id = "203153", Name = "Frog", CanBeInStartingDeck = false };
        ledger.Reset();
        ledger.Observe(at, handHud, [], confirmedHover: frog, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(1), handHud with { UserHandCount = 4 }, []).Count == 0,
            "A non-ownable token tooltip plus a hand decrement manufactured a player play.");
        ledger.Reset();
        var directFrog = sight with { Card = frog, Side = PlayerSide.User, NeedsTemporalConfirmation = false };
        Check(ledger.Observe(at, handHud, [directFrog]).Single().Sighting.Card.Id == frog.Id,
            "The hand-hover token guard suppressed a directly observed generated play.");
        var evolved = sight.Card with { Id = "203021", Name = "Saov Ainmhi'dh: Unity", CanBeInStartingDeck = false };
        ledger.Reset();
        ledger.Observe(at, handHud, [], confirmedHover: evolved, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(1), handHud with { UserHandCount = 4 }, []).Single().Sighting.Card.Id == evolved.Id,
            "The token guard suppressed a collectible card's evolved hand identity.");
        ledger.Observe(at.AddSeconds(3), handHud with { UserHandCount = 4 }, []);
        ledger.Observe(at.AddSeconds(7), handHud with { UserHandCount = 4 }, []);
        ledger.Observe(at.AddSeconds(20), handHud with { UserHandCount = 4 }, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        var secondCopy = ledger.Observe(at.AddSeconds(21), handHud with { UserHandCount = 3 }, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        Check(secondCopy.Count == 1 && secondCopy[0].Sighting.Card.Id == sight.Card.Id,
            "A later genuine second copy was suppressed as repeated hover frames from the first play.");
        ledger.Reset(); ledger.Observe(at, handHud, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(14), handHud with { UserHandCount = 4 }, []).Single().Sighting.Card.Id == sight.Card.Id,
            "A deliberate player selection was lost before the hand counter settled.");
        ledger.Reset(); ledger.Observe(at, handHud, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(16), handHud with { UserHandCount = 4 }, []).Count == 0,
            "A stale hand hover was attributed to a later hand decrement.");
        var katakan = sight.Card with { Id = "round-start-placement", Name = "Katakan" };
        ledger.Reset();
        var emptyRoundStart = handHud with { UserHandCount = 8, UserScore = 0, OpponentScore = 0 };
        ledger.Observe(at, emptyRoundStart, [], boardWasScanned: true, confirmedHover: katakan, hoverInPlayerHand: false);
        var openingPlacement = ledger.Observe(at.AddSeconds(2), emptyRoundStart with { UserHandCount = 7 }, [], boardWasScanned: true);
        Check(openingPlacement.Count == 1 && openingPlacement[0].Sighting.Card.Id == katakan.Id,
            "Exact empty-board round-start placement plus a hand decrement did not recover the first play.");
        ledger.Reset();
        ledger.Observe(at, emptyRoundStart with { OpponentScore = 3 }, [], boardWasScanned: true,
            confirmedHover: katakan, hoverInPlayerHand: false);
        Check(ledger.Observe(at.AddSeconds(2), emptyRoundStart with { OpponentScore = 3, UserHandCount = 7 }, [], boardWasScanned: true).Count == 0,
            "Ordinary mid-round board hover was treated as a first-card placement.");
        ledger.Reset();
        ledger.Observe(at, handHud, []);
        ledger.Observe(at.AddMilliseconds(300), handHud with { UserHandCount = null }, [], confirmedHover: sight.Card, hoverInPlayerHand: true);
        Check(ledger.Observe(at.AddSeconds(5), handHud with { UserHandCount = 4 }, []).Single().Sighting.Card.Id == sight.Card.Id,
            "Intermittent hand-count OCR discarded a selected card whose preceding count was stable.");
        var targeted = sight.Card with { Id="targeted", AbilityText="Banish a unit or an artifact." };
        ledger.Reset(); ledger.Observe(at, handHud, [], confirmedHover: targeted, hoverInPlayerHand: true);
        ledger.Observe(at.AddSeconds(1), handHud, [], confirmedHover: sight.Card, hoverInPlayerHand: false);
        Check(ledger.Observe(at.AddSeconds(2), handHud with { UserHandCount=4, HasCardTooltip=true }, [],
                confirmedHover:sight.Card, hoverInPlayerHand:false).Count == 0,
            "A temporary hand decrement while board targeting was still open became a committed play.");
        ledger.Observe(at.AddSeconds(3), handHud with { UserHandCount=5 }, []);
        var targetedPlay = ledger.Observe(at.AddSeconds(8), handHud with { UserHandCount=4 }, []);
        Check(targetedPlay.Count == 1 && targetedPlay[0].Sighting.Card.Id == targeted.Id,
            "A board-targeted player play was not recovered after the extended targeting interaction.");
        var tutor = sight.Card with { Id="tutor", AbilityText="Play any card from your deck." };
        ledger.Reset(); ledger.Observe(at, handHud, [], confirmedHover:tutor, hoverInPlayerHand:true);
        Check(ledger.Observe(at.AddSeconds(2), pick, []).Single().Sighting.Card.Id == tutor.Id,
            "A deck tutor hover followed by PICK CARD TO PLAY did not recover the tutor itself.");

        var circle = sight.Card with { Id="202677", Name="Circle of Life", Kind=CardKind.Special,
            AbilityText="Damage an enemy unit by 3, then boost a random non-Neutral unit in your hand by 2." };
        var playChoice = pick with { ScreenHeader="PICK CARD TO PLAY", HasCardTooltip=true,
            TooltipRegion=new(.58,.35,.75,.58), MatchHudVisible=false };
        ledger.Reset();
        ledger.Observe(at, handHud, []);
        ledger.Observe(at.AddSeconds(1), playChoice with {UserHandCount=4}, [],
            confirmedHover:circle, hoverInPlayerHand:true);
        Check(ledger.Observe(at.AddSeconds(2), handHud with {UserHandCount=3}, []).Count==0,
            "A choice-overlay tooltip overlapping the hand band armed a false hand play.");
        ledger.Reset();
        ledger.Observe(at, playChoice, [], confirmedHover:circle);
        ledger.Observe(at.AddMilliseconds(250), playChoice, [], confirmedHover:circle);
        var nested = ledger.Observe(at.AddMilliseconds(500), playChoice with { IsCardSelectionOverlay=false, ScreenHeader=null }, [],
            confirmedHover:circle);
        Check(nested.Count==1 && nested[0].Sighting.Card.Id==circle.Id,
            "Repeated exact generated-choice title was not recovered when the PLAY menu closed on that identity.");
        Check(ledger.Observe(at.AddSeconds(1), board with { HasCardTooltip=false },
            [sight with { Card=circle, Side=PlayerSide.User, NeedsTemporalConfirmation=false }]).Count==0,
            "Recovered nested choice duplicated its following ordinary play preview.");
        ledger.Reset();
        ledger.Observe(at, playChoice, [], confirmedHover:circle);
        ledger.Observe(at.AddMilliseconds(250), playChoice, [], confirmedHover:circle);
        Check(ledger.Observe(at.AddMilliseconds(500), playChoice with { IsCardSelectionOverlay=false, ScreenHeader=null }, [],
            confirmedHover:sight.Card).Count==0, "A changed/cancelled PLAY choice was committed as the earlier highlighted card.");
        ledger.Reset();
        ledger.Observe(at, playChoice, [], confirmedHover:circle);
        Check(ledger.Observe(at.AddMilliseconds(500), playChoice with { IsCardSelectionOverlay=false, ScreenHeader=null }, [],
            confirmedHover:circle).Count==0, "A single choice-title frame became a generated play.");
        ledger.Reset();
        ledger.Observe(at, handHud, [], confirmedHover:tutor, hoverInPlayerHand:true);
        ledger.Observe(at.AddSeconds(1), pick, []); // tutor source recovered
        ledger.Observe(at.AddSeconds(2), playChoice, [], confirmedHover:circle);
        ledger.Observe(at.AddSeconds(2.25), playChoice, [], confirmedHover:circle);
        Check(ledger.Observe(at.AddSeconds(2.5), playChoice with {IsCardSelectionOverlay=false,ScreenHeader=null},[],
                  confirmedHover:circle).Count==0 &&
              ledger.Observe(at.AddSeconds(2.75),board,[sight with {Card=circle,Side=PlayerSide.User,NeedsTemporalConfirmation=false}]).Count==0,
            "A lingering highlighted deck-tutor alternative became a target play without deck conservation.");

        var creator=sight.Card with {Id="creator",Name="Creator",IsGold=false,AbilityText="Create and play a bronze special card."};
        ledger.Reset();
        ledger.Observe(at,handHud,[],confirmedHover:creator,hoverInPlayerHand:true,pointerInPlayerHand:false);
        Check(ledger.Observe(at.AddSeconds(1),pick,[]).Count==0,
            "A lower-board tooltip classified geometrically as hand overrode the authoritative live pointer.");
        ledger.Reset();
        var boardCreator=new CardSighting(creator,PlayerSide.User,CardSightSource.Board,new(.5,.62,.57,.82),.08,1);
        ledger.Observe(at,handHud,[boardCreator],boardWasScanned:true);
        ledger.Observe(at.AddSeconds(1),handHud,[],confirmedHover:creator,hoverInPlayerHand:true);
        Check(ledger.Observe(at.AddSeconds(2),pick,[]).Count==0,
            "A legacy geometric hover matching a recent player-board identity became a hand play.");
        ledger.Reset();
        var gold=sight.Card with {Id="gold",Name="Gold",IsGold=true};
        var goldPlay=sight with {Card=gold,Side=PlayerSide.User,NeedsTemporalConfirmation=false};
        Check(ledger.Observe(at,handHud,[goldPlay]).Count==1,"Gold setup play was not committed.");
        ledger.Observe(at.AddSeconds(3),handHud,[],artworkWasScanned:true);
        ledger.Observe(at.AddSeconds(6),handHud,[],artworkWasScanned:true);
        ledger.Observe(at.AddSeconds(7),handHud,[],confirmedHover:gold,hoverInPlayerHand:true);
        Check(ledger.Observe(at.AddSeconds(8),handHud with {UserHandCount=4},[]).Count==0,
            "A legacy geometric board hover manufactured a second singleton-gold play.");
        ledger.Reset();
        ledger.Observe(at,handHud,[],confirmedHover:creator,hoverInPlayerHand:true);
        Check(ledger.Observe(at.AddSeconds(1),pick,[]).Single().Sighting.Card.Id==creator.Id,
            "Choice-producing hand source was not recovered for delayed-decrement regression.");
        ledger.Observe(at.AddSeconds(2),handHud,[],confirmedHover:sight.Card,hoverInPlayerHand:true);
        Check(ledger.Observe(at.AddSeconds(3),handHud with {UserHandCount=4},[]).Count==0,
            "A generated choice target consumed its source card's delayed hand decrement as a second hand play.");
    }

    public static void Origins(string root)
    {
        var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json")).ToDictionary(c => c.Id);
        var at = DateTimeOffset.UnixEpoch; var resolver = new PlayProvenanceResolver();
        VisionEvidenceEvent E(string id, int second, CardSightSource source = CardSightSource.PlayPreview, PlayerSide side = PlayerSide.Opponent) =>
            new(at.AddSeconds(second), new(cards[id], side, source, new(.4, .2, .5, .4), .1, 1), "Fixture");
        var saov=EvolvingCardCatalog.StartingIdentity(E("203021",0,side:PlayerSide.User).Sighting,
            new(CardProvenance.Spawned,"Non-ownable transformed identity."),cards.Values,"Scoia'tael");
        Check(saov.Card.Id=="202986" && saov.Card.Provision==5,
            "Saov Ainmhi'dh: Unity was not grouped with its 5-provision starting identity.");
        resolver.Observe(E("203047", 0));
        Check(resolver.Observe(E("200535", 3, CardSightSource.Board)).Provenance == CardProvenance.Unknown, "Board-first Swordmaster is ambiguous.");
        Check(resolver.Observe(E("200535", 50, CardSightSource.History)).Provenance == CardProvenance.Unknown, "History cannot launder board-first creation.");
        Check(resolver.Observe(E("200535", 60)).Provenance == CardProvenance.Unknown, "Another preview is not independent original-copy evidence.");
        resolver.Reset(); resolver.Observe(E("203047", 0)); resolver.Observe(E("200535", 3, CardSightSource.Board));
        Check(resolver.Observe(E("142313", 4)).Provenance == CardProvenance.Unknown, "Background board recognition does not consume creation window.");
        resolver.Reset(); resolver.Observe(E("203047", 0)); resolver.Observe(E("200535", 31, CardSightSource.Board));
        Check(resolver.Observe(E("200535", 50, CardSightSource.History)).Provenance == CardProvenance.ProbableStartingDeck, "Expired window cannot implicate later board cards.");
        resolver.Reset(); resolver.Observe(E("203047", 0)); resolver.Observe(E("200535", 3, CardSightSource.Board, PlayerSide.User));
        Check(resolver.Observe(E("200535", 50, CardSightSource.History, PlayerSide.User)).Provenance == CardProvenance.ProbableStartingDeck, "Creator on opposite side cannot contaminate origin.");
        var tracker = new LiveDeckTracker(PlayerSide.Opponent);
        tracker.ConsiderDirectPlay(cards["200535"], .9, at, "Independent earlier play", CardProvenance.ProbableStartingDeck);
        tracker.ConsiderDirectPlay(cards["200535"], .99, at.AddSeconds(10), "Possible later creation", CardProvenance.Unknown);
        Check(tracker.DeckBuildingObservations.Count == 1, "Later ambiguity does not erase a known original.");
        resolver.Reset();
        Check(resolver.Observe(E("200535", 51, CardSightSource.History)).Provenance == CardProvenance.ProbableStartingDeck, "New match resets creation memory.");
        resolver.Observe(E("202658", 60));
        Check(resolver.Observe(E("202248", 64)).Provenance == CardProvenance.Unknown,
            "Braathens' created Duchess' Informant was incorrectly counted as an original Nilfgaard card.");
        resolver.Reset();
        var emissaryPreview=E("162314",70); var braathensBoard=E("202658",70,CardSightSource.Board);
        resolver.PrimeCoTemporalBoardCreations([emissaryPreview,braathensBoard]);
        var createdEmissary=resolver.Observe(emissaryPreview);
        Check(createdEmissary.Provenance==CardProvenance.Created && createdEmissary.Reason.Contains("Braathens",StringComparison.Ordinal),
            "A same-batch Braathens body plus exact Emissary preview was charged as a starting-deck card.");
        var falseTarget=E("162314",80) with { Sighting=E("162314",80).Sighting with
            { Card=cards.Values.First(card=>card.Kind==CardKind.Unit && !card.IsGold && card.Faction=="Nilfgaard" &&
                !(card.AbilityText??"").StartsWith("Disloyal",StringComparison.OrdinalIgnoreCase)) } };
        resolver.Reset(); resolver.PrimeCoTemporalBoardCreations([falseTarget,E("202658",80,CardSightSource.Board)]);
        Check(resolver.Observe(falseTarget).Provenance==CardProvenance.ProbableStartingDeck,
            "Braathens' route spread beyond bronze Disloyal outputs.");

        resolver.Reset();
        resolver.Observe(E("142308",0,side:PlayerSide.User));
        Check(resolver.Observe(E("200535",8,side:PlayerSide.User)).Provenance==CardProvenance.ProbableStartingDeck,
            "Waylay's named Deadeye Spawn contaminated a distinct following Swordmaster play.");
        resolver.Reset();
        resolver.Observe(E("142308",0,side:PlayerSide.User));
        Check(resolver.Observe(E("202184",8,side:PlayerSide.User)).Provenance==CardProvenance.Spawned,
            "Waylay's exact Elven Deadeye output was not classified as spawned.");
        resolver.Reset();
        resolver.Observe(E("202915",0,side:PlayerSide.User));
        var chosenWaylayBase=E("142308",4,side:PlayerSide.User);
        var chosenWaylay=chosenWaylayBase with { Sighting=chosenWaylayBase.Sighting with
            { Evidence="Repeated exact title in a resolved PICK CARD TO PLAY selection." },
            Description="The exact highlighted title repeated in PICK CARD TO PLAY and remained selected as the menu closed." };
        Check(resolver.Observe(chosenWaylay).Provenance==CardProvenance.Unknown,
            "Sorceress' selected Waylay was treated as an original.");
        Check(resolver.Observe(E("203047",90,side:PlayerSide.User)).Provenance==CardProvenance.ProbableStartingDeck,
            "A resolved generic Order choice left its creation risk active over a later real Harvest.");

        var userReference=new DeckDefinition("reviewed","reviewed","Scoia'tael","",0,
            [new(cards["201579"]),new(cards["203150"]),new(cards["203047"])]);
        resolver.Reset();
        resolver.Observe(E("203109",0),userReference);
        Check(resolver.Observe(E("201585",4),userReference).Provenance==CardProvenance.Unknown &&
              resolver.Observe(E("202472",10),userReference).Provenance==CardProvenance.Unknown,
            "Nested generic Runemage → Runestone → created card chain spent original provisions.");
        resolver.Reset(); resolver.Observe(E("203038",0),userReference);
        Check(resolver.Observe(E("203047",4),userReference).Provenance==CardProvenance.Unknown,
            "Lydia's compatible created/replayed bronze special was counted as an original.");
        var staleReferencePlay=E("200535",50,side:PlayerSide.User) with
        {
            Description="Exact player-hand tooltip was followed by board targeting and a one-card hand decrement; inferred the missed player play animation."
        };
        resolver.Reset();
        Check(resolver.Observe(staleReferencePlay,userReference).Provenance==CardProvenance.ProbableStartingDeck,
            "An exact independently paid player-hand card was discarded solely because the selected library reference was stale.");
        resolver.Reset();
        Check(resolver.Observe(E("200535",50,side:PlayerSide.User),userReference).Provenance==CardProvenance.Unknown,
            "An ordinary off-reference preview bypassed the selected-deck mismatch guard.");
        resolver.Reset(); resolver.Observe(E("202915",0,side:PlayerSide.User),userReference);
        var generatedHandPlay=staleReferencePlay with { ObservedAt=at.AddSeconds(4),
            Sighting=staleReferencePlay.Sighting with { Card=cards["142308"] } };
        Check(resolver.Observe(generatedHandPlay,userReference).Provenance!=CardProvenance.ProbableStartingDeck,
            "Exact hand-cost evidence laundered a card still compatible with a known generated Order route.");
        resolver.Reset(); resolver.Observe(E("163201",0),userReference);
        Check(resolver.Observe(E("201579",20),userReference).Provenance==CardProvenance.Unknown,
            "Bribery's unit from the reviewed opposing starting deck was counted as an original.");
        resolver.Reset(); resolver.Observe(E("201601",0),userReference);
        Check(resolver.Observe(E("203150",180),userReference).Provenance==CardProvenance.Unknown,
            "An incompatible reviewed opposing card played after Invocation was counted as an original.");

        var mutation=new DeckMutationLedger(); var water=E("202266",0); mutation.Observe(water,"Scoia'tael","Scoia'tael");
        var dryad=E("202276",60,CardSightSource.Board);
        Check(mutation.OriginRisk(dryad.Sighting,dryad.ObservedAt,[])?.Provenance==CardProvenance.Unknown,
            "A first named Spawn output was allowed to become an original before duplicate handling.");
        var knowledge=new OpponentKnowledge(); knowledge.Observe(E("203090",1));
        var saskiaPool=CompanionCardRules.RecurringSummonPool(cards["203090"],cards.Values);
        Check(saskiaPool.Contains(cards.Values.Single(card=>card.Name=="Dwarven Skirmisher").Id) && saskiaPool.Count<=80 &&
              saskiaPool.All(id=>StartingDeckRules.IsLegalStartingCard(cards[id],"Scoia'tael") && cards[id].Kind==CardKind.Unit && !cards[id].IsGold && cards[id].Faction!="Neutral"),
            "Saskia's recurring visual search pool omitted Dwarven Skirmisher or admitted an illegal target.");
        var portal=cards.Values.Single(card=>card.Name=="Portal");
        var peasant=cards.Values.Single(card=>card.Name=="Peasant Militia");
        var brigade=cards.Values.Single(card=>card.Name=="Vrihedd Brigade");
        var swordmaster=cards.Values.Single(card=>card.Name=="Elven Swordmaster");
        var minerCard=cards.Values.Single(card=>card.Name=="Miner");
        var portalPool=CompanionCardRules.RecurringSummonPool(portal,cards.Values,"Scoia'tael");
        Check(portalPool.Contains(peasant.Id)&&portalPool.Contains(brigade.Id)&&
              portalPool.All(id=>cards[id].Kind==CardKind.Unit&&cards[id].Provision==4&&
                  StartingDeckRules.IsLegalStartingCard(cards[id],"Scoia'tael"))&&
              !portalPool.Contains(cards.Values.Single(card=>card.Name=="Roach").Id),
            "Portal's recurring pool omitted legal faction/Neutral 4p units or admitted an illegal target.");

        var lambert=cards.Values.Single(card=>card.Name=="Lambert");
        var eskel=cards.Values.Single(card=>card.Name=="Eskel");
        var vesemir=cards.Values.Single(card=>card.Name=="Vesemir");
        var trioTargets=CompanionCardRules.NamedDeckSummonTargets(lambert,cards.Values);
        Check(trioTargets.SequenceEqual([eskel.Id,vesemir.Id]) &&
              CompanionCardRules.NamedDeckSummonTargets(eskel,cards.Values).SequenceEqual([vesemir.Id,lambert.Id]) &&
              CompanionCardRules.NamedDeckSummonTargets(vesemir,cards.Values).SequenceEqual([eskel.Id,lambert.Id]),
            "A Silver Witcher initiator's exact printed summon list lost its resolution order.");
        var madame=cards["203286"];
        var seductress=cards["202308"];
        var peaches=cards["202561"];
        var madameTargets=CompanionCardRules.NamedDeckSummonTargets(madame,cards.Values);
        Check(madameTargets.SequenceEqual([seductress.Id,peaches.Id]),
            "Madam Marquise Serenity's separate row clauses were not encapsulated by her explicit Summon both instruction.");
        var exclusiveRows=madame with { Id="conditional-without-both", AbilityText=
            "Deploy (Melee): Summon a Sly Seductress from your deck to this row.\n"+
            "Deploy (Ranged): Summon Passiflora Peaches from your deck to this row." };
        Check(CompanionCardRules.NamedDeckSummonTargets(exclusiveRows,cards.Values).Count==0,
            "Separate mutually exclusive row clauses were incorrectly combined without an explicit aggregate summon.");
        CardSighting TrioSight(CardDefinition card,double left,double right)=>new(card,PlayerSide.Opponent,
            CardSightSource.Board,new(left,.26,right,.42),.08,1,
            card.Id==lambert.Id ? null :
                "legal target of a recently committed printed deck summon; independent settled-board confirmation still required");
        var trioScreen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,
            OpponentHandCount:2,OpponentDeckCount:8,MatchHudVisible:true);
        var rightTrioLedger=new MatchVisionLedger();
        rightTrioLedger.Observe(at,trioScreen,[Sight(lambert,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Lambert; card-frame boundary also present")],false);
        var rightTrio=rightTrioLedger.Observe(at.AddSeconds(6),trioScreen with {OpponentDeckCount=6},
            [TrioSight(lambert,.40,.46),TrioSight(vesemir,.47,.53),TrioSight(eskel,.54,.60)],true);
        Check(rightTrio.Count(item=>trioTargets.Contains(item.Sighting.Card.Id)&&item.ResolvedDeckCopies==1&&
                  item.EstablishesDistinctDeckCopy)==2,
            "Right-side Lambert sequence did not resolve both named deck arrivals.");
        var darkSourceLedger=new MatchVisionLedger();
        darkSourceLedger.Observe(at,trioScreen,[Sight(lambert,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Lambert; card-frame boundary also present")],false);
        var darkSourcePair=darkSourceLedger.Observe(at.AddSeconds(6),trioScreen with {OpponentDeckCount=6},
            [TrioSight(vesemir,.47,.53),TrioSight(eskel,.54,.60)],true);
        Check(darkSourcePair.Count(item=>trioTargets.Contains(item.Sighting.Card.Id)&&item.ResolvedDeckCopies==1)==2,
            "The complete adjacent target pair was lost when the dark played source artwork was not localized again.");
        var leftTrioLedger=new MatchVisionLedger();
        leftTrioLedger.Observe(at,trioScreen,[Sight(lambert,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Lambert; card-frame boundary also present")],false);
        var leftTrio=leftTrioLedger.Observe(at.AddSeconds(6),trioScreen with {OpponentDeckCount=6},
            [TrioSight(eskel,.26,.32),TrioSight(vesemir,.33,.39),TrioSight(lambert,.40,.46)],true);
        Check(leftTrio.Count(item=>trioTargets.Contains(item.Sighting.Card.Id)&&item.ResolvedDeckCopies==1)==2,
            "Mirrored left-side Lambert sequence was not accepted in its deterministic order.");
        var wrongOrderLedger=new MatchVisionLedger();
        wrongOrderLedger.Observe(at,trioScreen,[Sight(lambert,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Lambert; card-frame boundary also present")],false);
        var wrongOrder=wrongOrderLedger.Observe(at.AddSeconds(6),trioScreen with {OpponentDeckCount=6},
            [TrioSight(lambert,.40,.46),TrioSight(eskel,.47,.53),TrioSight(vesemir,.54,.60)],true);
        Check(!wrongOrder.Any(item=>trioTargets.Contains(item.Sighting.Card.Id)&&item.ResolvedDeckCopies==1),
            "An impossible reversed Lambert insertion order was accepted as a named summon sequence.");

        var madameScreen=trioScreen with {OpponentHandCount=5,OpponentDeckCount=10};
        var madameLedger=new MatchVisionLedger();
        madameLedger.Observe(at,madameScreen,[Sight(madame,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Madam Marquise Serenity; card-frame boundary also present")],false);
        var madamePair=madameLedger.Observe(at.AddSeconds(5),madameScreen with {OpponentDeckCount=8},
            [Sight(madame,CardSightSource.Board,.40,.46),
             Sight(peaches,CardSightSource.Board,.47,.53,.08,1,
                 "legal target of a recently committed printed deck summon; independent settled-board confirmation still required"),
             Sight(seductress,CardSightSource.Board,.54,.60,.08,1,
                 "legal target of a recently committed printed deck summon; independent settled-board confirmation still required")],true);
        Check(madamePair.Count(item=>madameTargets.Contains(item.Sighting.Card.Id)&&item.ResolvedDeckCopies==1&&
                  item.EstablishesDistinctDeckCopy)==2,
            "Madam Marquise Serenity's visually complete Tribute pair did not resolve both deck summons.");
        var singleMadameLedger=new MatchVisionLedger();
        singleMadameLedger.Observe(at,madameScreen,[Sight(madame,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Madam Marquise Serenity; card-frame boundary also present")],false);
        var singleMadame=singleMadameLedger.Observe(at.AddSeconds(5),madameScreen with {OpponentDeckCount=9},
            [Sight(madame,CardSightSource.Board,.40,.46),
             Sight(peaches,CardSightSource.Board,.47,.53,.08,1,
                 "legal target of a recently committed printed deck summon; independent settled-board confirmation still required")],true);
        Check(singleMadame.Count(item=>item.Sighting.Card.Id==peaches.Id&&item.ResolvedDeckCopies==1&&
                  item.EstablishesDistinctDeckCopy)==1 &&
              !singleMadame.Any(item=>item.Sighting.Card.Id==seductress.Id),
            "Madam Marquise Serenity's ordinary row branch did not report only the observed Peaches summon.");
        var noTargetMadameLedger=new MatchVisionLedger();
        noTargetMadameLedger.Observe(at,madameScreen,[Sight(madame,CardSightSource.PlayPreview,.815,.915,.08,1,
            "Exact visible preview title: Madam Marquise Serenity; card-frame boundary also present")],false);
        var noTargetMadame=noTargetMadameLedger.Observe(at.AddSeconds(5),madameScreen with {OpponentDeckCount=8},
            [Sight(madame,CardSightSource.Board,.40,.46)],true);
        Check(!noTargetMadame.Any(item=>madameTargets.Contains(item.Sighting.Card.Id)),
            "Madam Marquise Serenity inferred unseen targets from the Tribute-sized pile decrement alone.");

        var portalLedger=new MatchVisionLedger();
        var portalScreen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,
            OpponentHandCount:9,MatchHudVisible:true);
        CardSighting Sight(CardDefinition card,CardSightSource source,double left,double right,double distance=.08,double margin=1,
            string? evidence=null,bool temporal=false)=>new(card,PlayerSide.Opponent,source,
                new(left,.26,right,.42),distance,margin,evidence,temporal);
        portalLedger.Observe(at,portalScreen,[Sight(portal,CardSightSource.PlayPreview,.815,.915)],false);
        var deployEvents=portalLedger.Observe(at.AddSeconds(6),portalScreen,
            [Sight(peasant,CardSightSource.Board,.401,.463,.36,.875,
                "7 spatially consistent SIFT features · legal target of a recently committed printed deck summon; independent settled-board confirmation still required"),
             Sight(portal,CardSightSource.Board,.467,.533)],true);
        Check(deployEvents.Any(item=>item.Sighting.Card.Id==peasant.Id&&item.Sighting.Source==CardSightSource.Board&&item.ResolvedDeckCopies==1),
            "Portal's legal left-side Deploy summon was not resolved from source-relative board geometry.");
        portalLedger.Observe(at.AddSeconds(20),portalScreen with {OpponentHandCount=8},[],false);
        var timerEvents=portalLedger.Observe(at.AddSeconds(25),portalScreen with {OpponentHandCount=8},
            [Sight(brigade,CardSightSource.PlayPreview,.815,.915,.10,1,"Exact visible preview title: Vrihedd Brigade; card-frame boundary also present")],false);
        Check(timerEvents.Any(item=>item.Sighting.Card.Id==brigade.Id&&item.Sighting.Source==CardSightSource.Board&&item.ResolvedDeckCopies==1)&&
              timerEvents.All(item=>item.Sighting.Card.Id!=brigade.Id||item.Sighting.Source!=CardSightSource.PlayPreview),
            "Portal's Timer summon was charged as an opponent hand play instead of a resolved deck arrival.");

        var duplicatePortalLedger=new MatchVisionLedger();
        duplicatePortalLedger.Observe(at,portalScreen,
            [Sight(swordmaster,CardSightSource.PlayPreview,.815,.915,.10,1,"Exact visible preview title: Elven Swordmaster; card-frame boundary also present")],false);
        duplicatePortalLedger.Observe(at.AddSeconds(4),portalScreen,
            [Sight(portal,CardSightSource.PlayPreview,.815,.915,.10,1,"Exact visible preview title: Portal; card-frame boundary also present")],false);
        var duplicateDeploy=duplicatePortalLedger.Observe(at.AddSeconds(10),portalScreen,
            [Sight(swordmaster,CardSightSource.Board,.30,.36,.08,1),
             Sight(swordmaster,CardSightSource.Board,.401,.463,.34,.50,
                 "8 spatially consistent SIFT features · legal target of a recently committed printed deck summon; independent settled-board confirmation still required"),
             Sight(portal,CardSightSource.Board,.467,.533,.08,1)],true);
        var duplicateSwordmaster=duplicateDeploy.SingleOrDefault(item=>item.Sighting.Card.Id==swordmaster.Id&&item.ResolvedDeckCopies==1);
        Check(duplicateSwordmaster is not null && duplicateSwordmaster.EstablishesDistinctDeckCopy,
            "Portal's fixed left slot was suppressed because the summoned bronze identity had already been played.");
        var portalCopies=new ThinningCopyTracker(); var portalDeck=new LiveDeckTracker(PlayerSide.Opponent);
        portalDeck.SetFactionPrior("Scoia'tael");
        var priorSwordmaster=new VisionEvidenceEvent(at.AddSeconds(-8),
            Sight(swordmaster,CardSightSource.PlayPreview,.815,.915),"prior independently paid hand copy");
        portalDeck.ConsiderDirectPlay(swordmaster,.99,priorSwordmaster.ObservedAt,
            priorSwordmaster.Description,CardProvenance.ProbableStartingDeck);
        portalCopies.ObserveEvent(priorSwordmaster,CardProvenance.ProbableStartingDeck,portalDeck);
        Check(portalCopies.ObserveEvent(duplicateSwordmaster!,CardProvenance.ProbableStartingDeck,portalDeck) &&
              portalDeck.Observations.Single(item=>item.Card.Id==swordmaster.Id).ObservedCopies==2,
            "Portal's distinct fixed-placement summon did not advance an existing original bronze to two copies.");

        var expiredPortalLedger=new MatchVisionLedger();
        expiredPortalLedger.Observe(at,portalScreen,
            [Sight(portal,CardSightSource.PlayPreview,.815,.915,.10,1,"Exact visible preview title: Portal; card-frame boundary also present")],false);
        expiredPortalLedger.Observe(at.AddSeconds(15),portalScreen with {ScreenHeader="ROUND 2",MatchHudVisible=false},[],false);
        expiredPortalLedger.Observe(at.AddSeconds(20),portalScreen with {OpponentHandCount=8},[],false);
        var postRoundMiner=expiredPortalLedger.Observe(at.AddSeconds(22),portalScreen with {OpponentHandCount=8},
            [Sight(minerCard,CardSightSource.PlayPreview,.815,.915,.10,1,"Exact visible preview title: Miner; card-frame boundary also present")],false);
        Check(postRoundMiner.Any(item=>item.Sighting.Card.Id==minerCard.Id&&item.Sighting.Source==CardSightSource.PlayPreview)&&
              postRoundMiner.All(item=>item.Sighting.Card.Id!=minerCard.Id||item.Sighting.Source!=CardSightSource.Board),
            "A prior-round Portal source incorrectly claimed a later generated/play-preview card.");

        var tempest=cards.Values.Single(card=>card.Name=="Tempest");
        var fog=cards.Values.Single(card=>card.Name=="Impenetrable Fog");
        var weatherLedger=new MatchVisionLedger();
        weatherLedger.Observe(at,portalScreen,[Sight(tempest,CardSightSource.PlayPreview,.815,.915,.10,1,
            "Exact visible preview title: Tempest; card-frame boundary also present")],false);
        var weakFog=Sight(fog,CardSightSource.PlayPreview,.833,.929,.16,1,
            "Unique long visible preview-title edge: Impenetrable Fog; repeated frames required",true);
        weatherLedger.Observe(at.AddSeconds(3),portalScreen,[weakFog],false);
        weatherLedger.Observe(at.AddSeconds(3.4),portalScreen,[weakFog],false);
        var pairEvents=weatherLedger.Observe(at.AddSeconds(4.8),portalScreen,
            [Sight(fog,CardSightSource.PlayPreview,.833,.929,.10,1,
                "Exact visible preview title: Impenetrable Fog; card-frame boundary also present")],false);
        Check(pairEvents.Any(item=>item.Sighting.Card.Id==fog.Id&&item.ResolvedDeckCopies==2),
            "A named play-all-copies sequence collapsed its second framed identical target into the first popup episode.");

        var gapWeatherLedger=new MatchVisionLedger();
        gapWeatherLedger.Observe(at,portalScreen,[Sight(tempest,CardSightSource.PlayPreview,.815,.915,.10,1,
            "Exact visible preview title: Tempest; card-frame boundary also present")],false);
        var exactFog=Sight(fog,CardSightSource.PlayPreview,.833,.929,.10,1,
            "Exact visible preview title: Impenetrable Fog; card-frame boundary also present");
        gapWeatherLedger.Observe(at.AddSeconds(3),portalScreen,[exactFog],false);
        foreach(var offset in new[]{3.9,4.1,4.3,4.5})
            gapWeatherLedger.Observe(at.AddSeconds(offset),portalScreen,[],false,artworkWasScanned:false);
        gapWeatherLedger.Observe(at.AddSeconds(4.7),portalScreen,[weakFog],false,artworkWasScanned:false);
        var gapPair=gapWeatherLedger.Observe(at.AddSeconds(4.9),portalScreen,[weakFog],false,artworkWasScanned:false);
        Check(gapPair.Any(item=>item.Sighting.Card.Id==fog.Id&&item.ResolvedDeckCopies==2),
            "A sustained blank interval followed by repeated GWENT-font title reads did not separate Tempest's second identical Fog animation.");
        foreach(var (id,second) in new[]{("203073",10),("203074",20),("200535",30)}) knowledge.Observe(E(id,second));
        // The earlier Water source owns this Dryad identity and must not consume a
        // Saskia deck-summon credit. ApplyVisionResult resolves mutation risk first.
        Check(mutation.OriginRisk(dryad.Sighting,dryad.ObservedAt,[]) is not null,
            "Named Spawn risk disappeared before Saskia attribution.");
        knowledge.Observe(dryad);
        var miner=E("202474",70,CardSightSource.Board) with
        {
            ResolvedDeckCopies=1,
            Description="Typed source-scoped deck-summon route established by a legal rightmost target and conserved deck departure."
        };
        Check(knowledge.BoardOrigin(miner)?.Provenance==CardProvenance.ProbableStartingDeck,
            "Typed source-scoped deck-copy evidence did not recover a fresh board-only bronze deck target.");

        resolver.Reset();
        resolver.Observe(E("203061",0));
        var spawnedTemplate=resolver.Observe(E("203040",3));
        var laterTemplate=resolver.Observe(E("203040",90));
        Check(spawnedTemplate.Provenance==CardProvenance.ProbableStartingDeck && laterTemplate.Provenance==CardProvenance.Unknown,
            "Spawn-from-starting-deck did not debit exactly one template while suppressing a later duplicate claim.");

        Check(!RecordingPersistenceGate.CanWriteLiveLedger(null,false,"prior-session") &&
              RecordingPersistenceGate.CanWriteLiveLedger(null,true,"current-session") &&
              !RecordingPersistenceGate.CanWriteLiveLedger("offline-review",true,"current-session"),
            "Stopped-session live-ledger persistence gate is unsafe.");

        var factionTracker=new LiveDeckTracker(PlayerSide.Opponent); factionTracker.SetFactionPrior("Nilfgaard");
        foreach(var id in new[]{"202472","203047","142401","202915","202916"})
            factionTracker.ConsiderDirectPlay(cards[id],.95,at,"generated cross-faction fixture",CardProvenance.Unknown);
        Check(factionTracker.Faction=="Nilfgaard" && factionTracker.HasStableFaction &&
              factionTracker.DeckBuildingObservations.Count==0,
            "Generated cross-faction observations destabilized a leader-established faction or entered its provision floor.");

        var mixedLedger=new MatchVisionLedger(); var mixedEvents=new List<VisionEvidenceEvent>();
        foreach(var line in File.ReadLines(Path.Combine(root,"GwentCompanion/sessions/20260901-191628/vision-observations.jsonl")))
        {
            var result=JsonSerializer.Deserialize<CardVisionResult>(line,GameStateJournal.Json)!;
            if(!result.Sightings.Any(item=>item.Card.Id=="203196")) continue;
            mixedEvents.AddRange(mixedLedger.Observe(result.SampledAt,result.Screen,result.Sightings,result.BoardWasScanned,
                result.HoveredCard,result.ArtworkWasScanned,result.HoverInPlayerHand));
        }
        Check(mixedEvents.Count(item=>item.Sighting.Card.Id=="203196" && item.Sighting.Source==CardSightSource.PlayPreview)==1,
            "A weak preview followed by the same projected-lane identity did not recover exactly one Quarixis play.");
    }

    public static void RefillingHandPlays(string root)
    {
        var cards=GwentOneCardCatalog.Load(Path.Combine(root,"GwentCompanion/cache/gwent-one-cards.json")).ToDictionary(card=>card.Id);
        var battle=cards["203242"]; var hunter=cards["202547"]; var arbalest=cards["162305"]; var megascope=cards["202931"];
        var at=DateTimeOffset.UnixEpoch; var hand=new HandCommitTracker();
        var hud=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,OpponentHandCount:5,MatchHudVisible:true);
        VisionEvidenceEvent E(CardDefinition card,int seconds,PlayerSide side=PlayerSide.Opponent) => new(at.AddSeconds(seconds),
            new(card,side,CardSightSource.PlayPreview,new(.8,.1,.9,.4),.1,1),"Fixture");

        Check(hand.Observe(at,hud,[E(battle,0)]).Count==0,"The refill source was counted as one of its own child plays.");
        var first=hand.Observe(at.AddSeconds(10),hud,[E(hunter,10)]);
        var second=hand.Observe(at.AddSeconds(12),hud,[E(arbalest,12)]);
        Check(first.Single().Sighting.Card.Id==hunter.Id && second.Single().Sighting.Card.Id==arbalest.Id,
            "Battle Stations' two flat-counter hand plays were not independently confirmed.");
        Check(hand.Observe(at.AddSeconds(13),hud,[E(hunter,13)]).Count==0,"A third preview escaped the printed two-play bound.");

        var origins=new PlayProvenanceResolver(); var mutations=new DeckMutationLedger(); var copies=new ThinningCopyTracker();
        var user=new LiveDeckTracker(PlayerSide.User); var opponent=new LiveDeckTracker(PlayerSide.Opponent); opponent.SetFactionPrior("Nilfgaard");
        var earlierHunter=E(hunter,-60); var earlierArbalest=E(arbalest,-50); var scope=E(megascope,-40);
        opponent.ConsiderDirectPlay(hunter,.95,earlierHunter.ObservedAt,"Earlier original",CardProvenance.ProbableStartingDeck);
        opponent.ConsiderDirectPlay(arbalest,.95,earlierArbalest.ObservedAt,"Earlier original",CardProvenance.ProbableStartingDeck);
        copies.ObserveEvent(earlierHunter,CardProvenance.ProbableStartingDeck,opponent);
        copies.ObserveEvent(earlierArbalest,CardProvenance.ProbableStartingDeck,opponent);
        mutations.Observe(scope,"Monsters","Nilfgaard");
        Check(HandCommitTracker.Apply(first.Concat(second).ToArray(),origins,mutations,copies,user,opponent,null),
            "Independent hand-play evidence did not overcome an unrelated board-spawn copy risk.");
        Check(opponent.Observations.Single(item=>item.Card.Id==hunter.Id).ObservedCopies==2 &&
              opponent.Observations.Single(item=>item.Card.Id==arbalest.Id).ObservedCopies==2,
            "Distinct round-one and round-three Hunter/Arbalest episodes did not establish two original copies.");
        var acquiredMutations=new DeckMutationLedger();
        acquiredMutations.Record(new(at.AddSeconds(1),"creator","Fixture creator",PlayerSide.Opponent,
            "May add a bronze unit to hand.",CandidateKind:"bronze-unit"));
        var acquiredTracker=new LiveDeckTracker(PlayerSide.Opponent); acquiredTracker.SetFactionPrior("Nilfgaard");
        Check(!HandCommitTracker.Apply(first,origins,acquiredMutations,new(),new(PlayerSide.User),acquiredTracker,null) &&
              acquiredTracker.Observations.Count==0,
            "A card that may have been acquired into hand was laundered into an original by refill evidence.");

        hand.Reset(); hand.Observe(at,hud,[E(battle,0)]);
        Check(hand.Observe(at.AddSeconds(1),hud,[E(hunter with { IsGold=true },1)]).Count==0,"Gold card satisfied a bronze-only refill slot.");
        Check(hand.Observe(at.AddSeconds(2),hud,[E(hunter,2,PlayerSide.User)]).Count==0,"Opponent refill evidence crossed player sides.");
        Check(hand.Observe(at.AddSeconds(21),hud,[E(hunter,21)]).Count==0,"Expired refill evidence confirmed an unrelated later play.");
        hand.Reset();
        Check(hand.Observe(at,hud,[E(hunter,0)]).Count==0 && hand.Observe(at.AddSeconds(1),hud,[E(arbalest,1)]).Count==0,
            "Unchanged hand counts without a printed source confirmed ordinary previews.");
        hand.Reset();
        var three=hud with {OpponentHandCount=3};
        hand.Observe(at,three,[]);
        hand.Observe(at.AddSeconds(1),three with {OpponentHandCount=6},[]);
        Check(hand.Observe(at.AddSeconds(2),three,[E(hunter,2)]).Count==0 &&
              hand.Observe(at.AddSeconds(3),three with {OpponentHandCount=2},[]).Single().Sighting.Card.Id==hunter.Id,
            "One isolated 3-to-6 HUD misread poisoned the next independently paid play.");
        hand.Reset();
        hand.Observe(at,three,[E(hunter,0)]);
        hand.Observe(at.AddSeconds(1),three,[]); // stable after the earlier generated/Order payoff
        Check(hand.Observe(at.AddSeconds(2),three with {OpponentHandCount=2},[E(arbalest,2)]).Single().Sighting.Card.Id==arbalest.Id,
            "An earlier no-cost preview poisoned the next play despite an intervening stable hand read.");

        // Under load, the HUD worker can publish the one-card payment before the
        // corresponding title worker finishes confirmation. Preserve that exact
        // edge briefly and require a later stable count before assigning it.
        hand.Reset();
        var seven=hud with {OpponentHandCount=7};
        hand.Observe(at,seven,[]);
        hand.Observe(at.AddSeconds(1),seven with {OpponentHandCount=6},[]);
        Check(hand.Observe(at.AddSeconds(3),seven with {OpponentHandCount=6},[E(hunter,3)]).Count==0,
            "A delayed preview committed retrospectively without a later stable HUD sample.");
        var delayed=hand.Observe(at.AddSeconds(4),seven with {OpponentHandCount=6},[]);
        Check(delayed.Count==1 && delayed[0].Sighting.Card.Id==hunter.Id &&
              delayed[0].Description.Contains("preceded delayed visual confirmation",StringComparison.Ordinal),
            "A stable exact one-card payment was not joined to its delayed preview.");

        // A named token spawned explicitly onto the board must not poison a
        // later, independently paid copy of that identity from hand. Creation
        // routes that can put the target in hand remain blocking.
        var manor=cards["203080"]; var damsel=cards["203082"];
        var boardSpawnOrigins=new PlayProvenanceResolver();
        boardSpawnOrigins.Observe(E(manor,30));
        var spawnedDamsel=E(damsel,31) with { Sighting=E(damsel,31).Sighting with { Source=CardSightSource.Board } };
        Check(boardSpawnOrigins.Observe(spawnedDamsel).Provenance==CardProvenance.Spawned &&
              boardSpawnOrigins.HasNonOrderCopyRisk(spawnedDamsel.Sighting,spawnedDamsel.ObservedAt) &&
              !boardSpawnOrigins.HasHandAcquisitionRisk(spawnedDamsel.Sighting,spawnedDamsel.ObservedAt),
            "A named board-only spawn was not separated from hand-acquisition risk.");
        var paidDamsel=E(damsel,40); var monsterDeck=new LiveDeckTracker(PlayerSide.Opponent); monsterDeck.SetFactionPrior("Monsters");
        Check(HandCommitTracker.Apply([paidDamsel],boardSpawnOrigins,new(),new(),new(PlayerSide.User),monsterDeck,null) &&
              monsterDeck.DeckBuildingObservations.Single(item=>item.Card.Id==damsel.Id).ObservedCopies==1,
            "The later independently paid Cursed Damsel remained suppressed by The Manor's board token.");
        var scenarioMutations=new DeckMutationLedger(); scenarioMutations.Observe(E(manor,60),"Scoia'tael","Monsters");
        Check(scenarioMutations.OriginRisk(E(damsel,70).Sighting,at.AddSeconds(70),[],independentHandPlay:true) is not null &&
              scenarioMutations.OriginRisk(E(damsel,100).Sighting,at.AddSeconds(100),[],independentHandPlay:true) is null,
            "Scenario output protection did not distinguish an immediate generated payoff from a later independent hand play.");

        var handSource=new CardDefinition("fixture-hand-spawn","Fixture Hand Spawn","Monsters",CardKind.Unit,4,
            AbilityText:"Deploy: Spawn Cursed Damsel in your hand.");
        var handSpawnOrigins=new PlayProvenanceResolver(); handSpawnOrigins.Observe(E(handSource,50)); handSpawnOrigins.Observe(E(damsel,51));
        var blockedDeck=new LiveDeckTracker(PlayerSide.Opponent); blockedDeck.SetFactionPrior("Monsters");
        Check(!HandCommitTracker.Apply([E(damsel,52)],handSpawnOrigins,new(),new(),new(PlayerSide.User),blockedDeck,null) &&
              blockedDeck.DeckBuildingObservations.Count==0,
            "A possible spawn-to-hand route was laundered into an original by a hand decrement.");
    }

    public static async Task RecordedTitlesAsync(string root)
    {
        const string session = "20260827-184848/";
        string[] files = ["frame-000592-184947389.jpg", "frame-000594-184947593.jpg", "frame-004758-185643987.jpg",
            "frame-004778-185645987.jpg", "frame-004780-185646180.jpg"];
        var project = Path.Combine(root, "GwentCompanion");
        using var training = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "tests/vision-training-v0.1.14.json")));
        var trainingFiles = training.RootElement.EnumerateArray().Select(row => row.GetProperty("Source").GetString()).ToHashSet();
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "tests/vision-fixtures.json")));
        Check(fixtures.RootElement.EnumerateArray().All(row => !trainingFiles.Contains(row.GetProperty("File").GetString())) &&
            files.All(file => !trainingFiles.Contains(session + file)), "Training frames must not enter held-out assertions.");
        var cards = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json"));
        using var reader = new ScreenStateRecognizer(); var titles = new PreviewTitleRecognizer(cards);
        var ledger = new MatchVisionLedger(); var resolver = new PlayProvenanceResolver();
        var recovered = new List<(string Id, CardProvenance Origin)>();
        foreach (var file in files)
        {
            var pixels = OakEffectProbe.Load(Path.Combine(project, "sessions", session, file));
            var screen = await reader.AnalyzeAsync(pixels);
            var sightings = await titles.RecognizeAsync(pixels, screen, reader);
            var time = DateTime.ParseExact(Path.GetFileNameWithoutExtension(file).Split('-')[2], "HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var e in ledger.Observe(new DateTimeOffset(time), screen, sightings))
            {
                Check(e.Sighting.Side == PlayerSide.Opponent, "Held-out preview attributed to wrong side.");
                recovered.Add((e.Sighting.Card.Id, resolver.Observe(e).Provenance));
            }
        }
        Check(recovered.SequenceEqual(new[] { ("202474", CardProvenance.ProbableStartingDeck),
            ("203047", CardProvenance.ProbableStartingDeck), ("200535", CardProvenance.Unknown) }),
            "Expected Miner original, Harvest original, Swordmaster creation-ambiguous; got " + string.Join(",", recovered));
        Console.WriteLine("Held-out sequence: Miner recovered as likely original; Harvest → Swordmaster recovered without claiming an original Swordmaster.");
    }

    public static async Task NamedSpawnCandidatePixelsAsync(string root)
    {
        var project=Path.Combine(root,"GwentCompanion"); var cache=Path.Combine(project,"cache");
        var catalog=GwentOneCardCatalog.Load(Path.Combine(cache,"gwent-one-cards.json"));
        var refs=VisionReferenceLibrary.Load(catalog,cache);
        var reference=JsonSerializer.Deserialize<GameStateSnapshot>(File.ReadAllText(Path.Combine(project,
            "sessions/20260902-100300/game-state-final.json")),GameStateJournal.Json)!.User.StartingDeckReference!;
        using var pipeline=new CardVisionPipeline(refs,catalog,Path.Combine(cache,"recognition-features"),VisionReferenceScope.CandidateDecks);
        pipeline.SetKnownPlayerDeck(reference.Cards.Select(card=>card.Card.Id)); pipeline.SetLikelyOpponentCards([]);
        var at=DateTimeOffset.Parse("2026-09-02T10:05:23.503-04:00");
        var screen=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null) {MatchHudVisible=true,UserHandCount=7};
        var waylay=catalog.Single(card=>card.Id=="142308");
        var waylayFrame=OakEffectProbe.Load(Path.Combine(project,
            "sessions/20260902-100300/play-events/20260902-100523503-User/during.png"));
        var prepared=await pipeline.PrepareAsync(waylayFrame,at);
        Check(prepared.HoveredCard?.Id==waylay.Id || prepared.Titles.Any(s=>s.Card.Id==waylay.Id),
            "The styled tooltip reader missed Sorceress' generated Waylay popup.");
        var source=new CardSighting(waylay,PlayerSide.User,CardSightSource.PlayPreview,new(.82,.41,.92,.66),.05,1,
            "Repeated exact title in a resolved PICK CARD TO PLAY selection.");
        Check(pipeline.Commit(new(at,screen,[source],[],false,false)).Events.Any(e=>e.Sighting.Card.Id==waylay.Id),
            "Waylay setup did not enter the production ledger.");
        var frame=OakEffectProbe.Load(Path.Combine(project,
            "sessions/20260902-100300/play-events/20260902-100531825-User/during.png"));
        var result=pipeline.RecognizePrepared(new(frame,at.AddSeconds(8),screen,[]),false);
        Check(result.Sightings.Any(s=>s.Side==PlayerSide.User&&s.Source==CardSightSource.PlayPreview&&s.Card.Id=="200535") &&
              !result.Sightings.Any(s=>s.Side==PlayerSide.User&&s.Source==CardSightSource.PlayPreview&&s.Card.Id=="202184"),
            "Named-output candidate expansion displaced the distinct Swordmaster played after Waylay: " +
            string.Join(" | ",result.Sightings.Select(s=>$"{s.Card.Name}:{s.Side}/{s.Source} d={s.Distance:F2} {s.Evidence}")));
    }

    public static void KnownPlayerThinningPairPixels(string root)
    {
        var project = Path.Combine(root, "GwentCompanion"); var cache = Path.Combine(project, "cache");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
        var rider = catalog.Single(card => card.Id == "132310");
        var references = VisionReferenceLibrary.Load(catalog, cache);
        using var features = new FeatureCardRecognizer(references, Path.Combine(cache, "recognition-features"),
            scope: VisionReferenceScope.CandidateDecks);
        features.SetKnownPlayerDeck([rider.Id]);
        var recognizer = new CardFrameRecognizer(new CardArtMatcher(features.ArtReferences));
        recognizer.SetKnownPlayerDeck([rider.Id]);
        var at = DateTimeOffset.UnixEpoch.AddSeconds(112);
        recognizer.ObserveKnownPlayerBoardCandidates(at,
            [new(rider, PlayerSide.User, CardSightSource.Board, new(.36, .47, .43, .62), .20, 1)]);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 21, 5, null)
            { MatchHudVisible = true, UserHandCount = 7, UserDeckCount = 14 };
        var caseRoot = Path.Combine(project, "tests", "stream-validation", "cases",
            "source-l-known-player-thinning-pair");
        var first = recognizer.Recognize(OakEffectProbe.Load(Path.Combine(caseRoot, "evidence-02.jpg")),
            screen, includeBoard: true);
        var second = recognizer.Recognize(OakEffectProbe.Load(Path.Combine(caseRoot, "evidence-03.jpg")),
            screen, includeBoard: true);
        Check(first.Count(item => item.Card.Id == rider.Id && item.Side == PlayerSide.User) == 2 &&
              second.Count(item => item.Card.Id == rider.Id && item.Side == PlayerSide.User) == 2,
            "The known-deck Rider seed did not recover both gap-separated board bodies.");
        var tracker = new LiveDeckTracker(PlayerSide.User); tracker.SetFactionPrior("Monsters", 10);
        var copies = new ThinningCopyTracker();
        var deck = new DeckDefinition("rider-pair", "Rider pair", "Monsters", "White Frost", 0,
            [new(rider, 2)]);
        copies.ObserveFrame(at.AddSeconds(2), screen, first, true, [tracker], _ => false, deck);
        copies.ObserveFrame(at.AddSeconds(5), screen, second, true, [tracker], _ => false, deck);
        Check(tracker.Observations.Single(item => item.Card.Id == rider.Id).ObservedCopies == 2,
            "Two independently corroborated Rider bodies were flattened to one observed copy.");
    }
}
