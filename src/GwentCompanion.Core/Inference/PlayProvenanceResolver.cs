using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Inference;

public sealed record PlayOriginAssessment(CardProvenance Provenance, string Reason);

/// <summary>
/// Conservative origin hints, not an effect simulator. Visible controller and printed
/// provisions do not prove starting-deck membership. Unresolved creation stays Unknown.
/// </summary>
public sealed class PlayProvenanceResolver
{
    private static TimeSpan CreationWindow(CardDefinition source) => TimeSpan.FromSeconds(
        source.Name.EndsWith(" Runestone",StringComparison.OrdinalIgnoreCase) ? 35 : 30);
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source, int? DeckCount)> _creations = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _deckPlays = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _graveyardPlays = [];
    private readonly Dictionary<(PlayerSide Side, string Id), DateTimeOffset> _confirmedDeckPlays = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _deckThefts = [];
    private readonly HashSet<PlayerSide> _mahakamPass = [];
    private readonly Dictionary<(PlayerSide, string), string> _ambiguousCreations = [];
    private readonly HashSet<(PlayerSide Side, string Id)> _boardOnlyNamedSpawns = [];
    private readonly Dictionary<(PlayerSide Side,string Id),CardDefinition> _orderCreators = [];
    private readonly Dictionary<(PlayerSide Side,string Id),int> _orderUses = [];
    private readonly Dictionary<PlayerSide,(DateTimeOffset At,CardDefinition Source)> _namedSpawns = [];
    private readonly Dictionary<PlayerSide,(DateTimeOffset At,CardDefinition Card)> _spawnCreators = [];
    private readonly Dictionary<(PlayerSide Side,string Id),int> _spawnUses = [];
    private readonly Dictionary<(PlayerSide Side,string Id),(DateTimeOffset At,string SourceId)> _generatedInitiators = [];
    private readonly Dictionary<(PlayerSide Side,string Id),int> _generationSourceUses = [];
    private readonly Dictionary<PlayerSide,CardDefinition> _currentLeaders = [];
    private readonly Dictionary<PlayerSide,CardDefinition> _openingStratagems = [];
    private readonly Dictionary<(PlayerSide Side,string TargetId),(DateTimeOffset At,CardDefinition Source)> _coTemporalBoardCreations = [];

    /// <summary>Supplies persistent public sources that do not appear as normal
    /// play-preview events. Repeated calls replace the prior identity.</summary>
    public void ObserveCurrentLeader(PlayerSide side, CardDefinition leader)
    {
        if (leader.Kind != CardKind.Leader) throw new ArgumentException("Expected a leader.", nameof(leader));
        _currentLeaders[side] = leader;
    }

    public void ObserveOpeningStratagem(PlayerSide side, CardDefinition stratagem)
    {
        if (stratagem.Kind != CardKind.Stratagem) throw new ArgumentException("Expected a stratagem.", nameof(stratagem));
        _openingStratagems[side] = stratagem;
    }

    public void ClearOpeningStratagem(PlayerSide side) => _openingStratagems.Remove(side);

    /// <summary>
    /// Recovers a missed creator preview when a newly corroborated creator body
    /// and its compatible exact play preview are emitted in the same detector
    /// batch. This is intentionally batch-local: an old creator sitting on board
    /// must not taint an unrelated later card.
    /// </summary>
    public void PrimeCoTemporalBoardCreations(IReadOnlyList<VisionEvidenceEvent> events, DeckDefinition? userReference = null)
    {
        foreach(var sourceEvent in events.Where(item=>item.Sighting.Source==CardSightSource.Board &&
                    item.Sighting.Distance<=.20 && OpensCreationWindow(item.Sighting.Card)))
        foreach(var targetEvent in events.Where(item=>item.Sighting.Side==sourceEvent.Sighting.Side &&
                    item.Sighting.Source==CardSightSource.PlayPreview && item.Sighting.Card.Id!=sourceEvent.Sighting.Card.Id &&
                    item.Sighting.Distance<=.22 && item.ObservedAt==sourceEvent.ObservedAt &&
                    CanBeCreated(sourceEvent.Sighting.Card,item.Sighting.Card,item.Sighting.Side,userReference)))
        {
            // Observe normally requires the output to follow the creator. Put the
            // recovered source one tick earlier so an out-of-order event from the
            // same detector batch can take the ordinary bounded-creation path.
            _creations[targetEvent.Sighting.Side]=(sourceEvent.ObservedAt.AddTicks(-1),sourceEvent.Sighting.Card,null);
            _coTemporalBoardCreations[(targetEvent.Sighting.Side,targetEvent.Sighting.Card.Id)]=
                (targetEvent.ObservedAt,sourceEvent.Sighting.Card);
        }
    }

    public PlayOriginAssessment Observe(VisionEvidenceEvent evidence, DeckDefinition? userReference = null,
        int? remainingDeckCount = null)
    {
        var sighting = evidence.Sighting;
        var card = sighting.Card;
        var provenance = sighting.Source == CardSightSource.Board
            ? CardProvenance.Unknown : CardProvenance.ProbableStartingDeck;
        var spawnedTemplateEvidence = false;
        var exactSpawnEvidence = false;
        (DateTimeOffset At, CardDefinition Source)? matchedDeckPlay = null;
        if (sighting.Source == CardSightSource.PlayPreview &&
            _deckPlays.TryGetValue(sighting.Side, out var deckPlay) && deckPlay.Source.Id != card.Id &&
            evidence.ObservedAt > deckPlay.At && evidence.ObservedAt - deckPlay.At <= TimeSpan.FromSeconds(25) &&
            DeckPlayResolutionTracker.CanSelectFromDeck(deckPlay.Source, card))
            matchedDeckPlay = deckPlay;
        var exactDeckPlay = matchedDeckPlay is not null;
        if (exactDeckPlay)
        {
            _deckPlays.Remove(sighting.Side);
            _confirmedDeckPlays[(sighting.Side, card.Id)] = evidence.ObservedAt;
        }
        (DateTimeOffset At, CardDefinition Source)? matchedGraveyardPlay = null;
        if (!exactDeckPlay && sighting.Source == CardSightSource.PlayPreview &&
            _graveyardPlays.TryGetValue(sighting.Side, out var graveyardPlay) && graveyardPlay.Source.Id != card.Id &&
            evidence.ObservedAt > graveyardPlay.At && evidence.ObservedAt - graveyardPlay.At <= TimeSpan.FromSeconds(25) &&
            CanSelectFromGraveyard(graveyardPlay.Source, card))
            matchedGraveyardPlay = graveyardPlay;
        var exactGraveyardPlay = matchedGraveyardPlay is not null;
        if (exactGraveyardPlay && !PlaysMultipleFromGraveyard(matchedGraveyardPlay!.Value.Source))
            _graveyardPlays.Remove(sighting.Side);
        var reason = sighting.Source == CardSightSource.Board
            ? "Board position establishes controller only, not starting-deck ownership." : string.Empty;
        if (sighting.Source == CardSightSource.PlayPreview && provenance == CardProvenance.ProbableStartingDeck &&
            CurrentLeaderHandPlaySource(sighting.Side, card) is { } handLeader)
            reason = $"Observed current leader {handLeader.Name} can play this card from hand. Capability alone does not prove activation; a real hand copy is still required and remains in starting-deck provision accounting unless separate acquisition evidence exists.";
        // A temporally matched, mandatory deck-play source is stronger than the
        // merely available persistent leader/stratagem route.
        var publicSpawnSource = !exactDeckPlay && !exactGraveyardPlay ? NamedPublicSpawnSource(sighting.Side, card) : null;
        if (publicSpawnSource is not null)
        {
            // Leader/stratagem activations are outside the hand and therefore do
            // not produce a normal source-card preview. Keep the named output out
            // of provision accounting. If this was instead a natural copy played
            // from hand, HandCommitTracker's independent one-card decrement is
            // allowed to promote it back to an original.
            provenance = CardProvenance.Spawned;
            exactSpawnEvidence = true;
            reason = $"Named Spawn output available from the observed {PublicSourceKind(publicSpawnSource)} {publicSpawnSource.Name}; not a starting-deck provision unless an independent hand decrement proves a natural copy.";
            RememberAmbiguity(sighting.Side, card.Id, reason, boardOnlyNamedSpawn: true);
        }
        if (_namedSpawns.TryGetValue(sighting.Side,out var named) && named.Source.Id!=card.Id &&
            evidence.ObservedAt>named.At && evidence.ObservedAt-named.At<=TimeSpan.FromSeconds(30) &&
            NonOrderSpawnNamesTarget(named.Source,card))
        {
            provenance=CardProvenance.Spawned;
            exactSpawnEvidence=true;
            reason=$"Explicit named Spawn output of {named.Source.Name}; not a starting-deck provision.";
            RememberAmbiguity(sighting.Side, card.Id, reason, NonOrderNamedSpawnIsBoardOnly(named.Source, card));
        }
        var order=!exactDeckPlay && !exactGraveyardPlay ? _orderCreators.Where(pair=>pair.Key.Side==sighting.Side && pair.Key.Id!=card.Id &&
                _orderUses.GetValueOrDefault(pair.Key)>0)
            .Select(pair=>pair.Value).FirstOrDefault(source=>OrderCanCreate(source,card)) : null;
        if (order is not null)
        {
            var exact=OrderNamesTarget(order,card);
            provenance=exact ? CardProvenance.Spawned : CardProvenance.Unknown;
            reason=exact
                ? $"Explicit named Spawn output of {order.Name}'s Order; not a starting-deck provision."
                : $"Possible generation by {order.Name}'s Order; independent hand commitment needed before claiming an original.";
            var resolvedChoice=evidence.Description.Contains("resolved PICK CARD TO PLAY selection",StringComparison.OrdinalIgnoreCase) ||
                (sighting.Evidence?.Contains("resolved PICK CARD TO PLAY selection",StringComparison.OrdinalIgnoreCase) ?? false);
            if (sighting.Source==CardSightSource.PlayPreview && (exact || resolvedChoice))
            {
                var key=(sighting.Side,order.Id);
                _orderUses[key]=Math.Max(0,_orderUses.GetValueOrDefault(key)-1);
            }
        }
        if (_spawnCreators.TryGetValue(sighting.Side,out var spawn) && spawn.Card.Id!=card.Id &&
            evidence.ObservedAt>spawn.At && evidence.ObservedAt-spawn.At<=TimeSpan.FromSeconds(30) && SpawnCanCreate(spawn.Card,card))
        {
            // Some creators select a template from the starting deck (Auberon:
            // Conqueror/Garrison); others generate from the whole category
            // (Auberon: King). In both cases the played body is generated. Only
            // the former proves that at least one original template exists.
            var provesTemplate=SpawnFromStartingDeck(spawn.Card);
            provenance=provesTemplate ? CardProvenance.ProbableStartingDeck : CardProvenance.Spawned;
            spawnedTemplateEvidence=provesTemplate;
            exactSpawnEvidence=!provesTemplate;
            reason=provesTemplate
                ? $"Spawned target of {spawn.Card.Name}; its identity proves one starting-deck template, while the generated body cannot establish an additional copy."
                : $"Generated played target of {spawn.Card.Name}; the initiating body is not a starting-deck provision.";
            RememberAmbiguity(sighting.Side, card.Id, reason);
            RememberGeneratedInitiator(sighting.Side,card.Id,evidence.ObservedAt,spawn.Card.Id);
        }
        if (sighting.Source==CardSightSource.PlayPreview && HasGeneratedOrder(card))
        {
            var key=(sighting.Side,card.Id);
            _orderCreators[key]=card;
            _orderUses[key]=Math.Min(9,_orderUses.GetValueOrDefault(key)+1);
        }
        if (sighting.Source==CardSightSource.PlayPreview && SpawnCategory(card) is not null)
        {
            _spawnCreators[sighting.Side]=(evidence.ObservedAt,card);
            var key=(sighting.Side,card.Id); _spawnUses[key]=_spawnUses.GetValueOrDefault(key)+1;
            _generationSourceUses[key]=Math.Min(9,_generationSourceUses.GetValueOrDefault(key)+1);
        }
        if (sighting.Source == CardSightSource.PlayPreview)
        {
            var pending = _creations.TryGetValue(sighting.Side, out var value) ? value :
                ((DateTimeOffset At, CardDefinition Source, int? DeckCount)?)null;
            // Repeated OCR episodes of the same lingering creator preview are not its target.
            if (pending is not { } same || same.Source.Id != card.Id)
                _creations.Remove(sighting.Side);
            if (pending is { } creation && creation.Source.Id != card.Id &&
                evidence.ObservedAt > creation.At && evidence.ObservedAt - creation.At <= CreationWindow(creation.Source) &&
                !ConditionalGenerationRuledOut(creation.Source,creation.DeckCount) &&
                CanBeCreated(creation.Source, card, sighting.Side, userReference))
            {
                var exactBoardRoute=_coTemporalBoardCreations.TryGetValue((sighting.Side,card.Id),out var coTemporal) &&
                    coTemporal.At==evidence.ObservedAt && coTemporal.Source.Id==creation.Source.Id;
                var conditionalBranchProven = ConditionalGenerationProven(creation.Source,creation.DeckCount);
                var guaranteedGeneratedPlay = AlwaysPlaysOutsideStartingDeck(creation.Source) || conditionalBranchProven;
                provenance = exactBoardRoute || guaranteedGeneratedPlay ? CardProvenance.Created : CardProvenance.Unknown;
                reason = exactBoardRoute
                    ? $"Exact compatible play preview emitted with newly corroborated {creation.Source.Name}; classified as its Create-and-play output, not a starting-deck provision."
                    : conditionalBranchProven
                    ? $"Compatible played output of {creation.Source.Name}; the observed remaining deck count of {creation.DeckCount} proves its outside-starting-deck generation branch."
                    : guaranteedGeneratedPlay
                    ? $"Compatible played output of {creation.Source.Name}; its printed choice is always outside the starting deck."
                    : $"Possible creation following {creation.Source.Name}; excluded from starting-deck constraints unless independently corroborated.";
                RememberAmbiguity(sighting.Side, card.Id, reason, DirectlyPlaysCreatedCard(creation.Source));
                if(DirectlyPlaysCreatedCard(creation.Source))
                    RememberGeneratedInitiator(sighting.Side,card.Id,evidence.ObservedAt,creation.Source.Id);
                if(exactBoardRoute) _coTemporalBoardCreations.Remove((sighting.Side,card.Id));
            }
            else if (_deckThefts.TryGetValue(sighting.Side,out var theft) && theft.Source.Id!=card.Id &&
                evidence.ObservedAt>theft.At && userReference?.CountOf(card.Id)>0 && card.Faction!="Neutral" &&
                !FactionCompatibility.IsPlayableBy(card,theft.Source.Faction))
            {
                provenance=CardProvenance.Unknown;
                reason=$"Possible cross-faction card moved into deck by {theft.Source.Name}; excluded from original-deck constraints.";
                RememberAmbiguity(sighting.Side, card.Id, reason);
                _deckThefts.Remove(sighting.Side);
            }
            else if (!spawnedTemplateEvidence && !exactSpawnEvidence && _ambiguousCreations.TryGetValue((sighting.Side, card.Id), out var prior))
            {
                provenance = CardProvenance.Unknown;
                reason = prior + " Another preview does not identify an independent starting copy; review is required.";
            }
            if (exactDeckPlay && !spawnedTemplateEvidence && !exactSpawnEvidence)
            {
                provenance = CardProvenance.ProbableStartingDeck;
                reason = $"{matchedDeckPlay!.Value.Source.Name} was observed immediately before this compatible mandatory deck play; deck origin established while mutation checks remain authoritative.";
                _ambiguousCreations.Remove((sighting.Side, card.Id));
                _boardOnlyNamedSpawns.Remove((sighting.Side, card.Id));
            }
            if (exactGraveyardPlay)
            {
                provenance = CardProvenance.Replayed;
                reason = $"{matchedGraveyardPlay!.Value.Source.Name} was observed immediately before this compatible graveyard play; this is a replay of an existing card, not another starting-deck copy.";
                _ambiguousCreations.Remove((sighting.Side, card.Id));
                _boardOnlyNamedSpawns.Remove((sighting.Side, card.Id));
            }
            // Tutors (Council / Call of the Forest) deliberately do not open creation
            // windows. Parse bounded Create clauses; Order clauses use the separate
            // Order tracker above. A create-to-hand target remains conservative unless
            // it is the next compatible play inside the short window.
            if (OpensCreationWindow(card) && (pending is not { } sameCreator || sameCreator.Source.Id != card.Id))
            {
                _creations[sighting.Side] = (evidence.ObservedAt, card, remainingDeckCount);
                var key=(sighting.Side,card.Id);
                _generationSourceUses[key]=Math.Min(9,_generationSourceUses.GetValueOrDefault(key)+1);
            }
            if (HasNonOrderNamedSpawn(card) &&
                (!_namedSpawns.TryGetValue(sighting.Side,out var priorSpawn) || priorSpawn.Source.Id!=card.Id))
                _namedSpawns[sighting.Side]=(evidence.ObservedAt,card);
            if (MovesEnemyToOwnDeck(card)) _deckThefts[sighting.Side]=(evidence.ObservedAt,card);
            if (DeckPlayResolutionTracker.IsGenericDeckPlaySource(card))
                _deckPlays[sighting.Side]=(evidence.ObservedAt,card);
            if (IsGenericGraveyardPlaySource(card))
                _graveyardPlays[sighting.Side]=(evidence.ObservedAt,card);
            if (card.Id == "203279") _mahakamPass.Add(sighting.Side);
            if (card.Id == "202473" && _mahakamPass.Contains(sighting.Side))
            {
                provenance = CardProvenance.Unknown;
                reason = "Tempering may be spawned by the observed Mahakam Pass Order; an independent starting copy remains possible.";
                RememberAmbiguity(sighting.Side, card.Id, reason);
            }
        }
        else if (sighting.Source == CardSightSource.Board && _creations.TryGetValue(sighting.Side, out var boardCreation) &&
            evidence.ObservedAt > boardCreation.At &&
            evidence.ObservedAt - boardCreation.At <= CreationWindow(boardCreation.Source) &&
            CanBeCreated(boardCreation.Source, card, sighting.Side, userReference))
        {
            reason = $"Possible creation following {boardCreation.Source.Name}; first recognized on board, not an independent original.";
            RememberAmbiguity(sighting.Side, card.Id, reason);
            // Background board evidence must not consume the pending preview target.
        }
        else if (!spawnedTemplateEvidence && !exactSpawnEvidence && _ambiguousCreations.TryGetValue((sighting.Side, card.Id), out var priorCreation))
        {
            provenance = CardProvenance.Unknown;
            reason = priorCreation + " Later board/history recognition does not establish an independent original.";
        }
        if (!card.CanBeInStartingDeck || card.Kind is CardKind.Stratagem or CardKind.Leader)
        {
            provenance = CardProvenance.Spawned;
            reason = "Catalog marks this identity as unavailable in a starting deck; printed provisions do not count against it.";
        }
        // The player's selected deck is known, not an opponent hypothesis. An ordinary
        // matching board arrival can account for a known slot even when its play preview
        // was missed. Specific creation/mutation/zone risks still override this in the caller.
        if (sighting.Side == PlayerSide.User && userReference?.CountOf(card.Id) > 0 &&
            provenance == CardProvenance.Unknown && !HasCopyRisk(sighting, evidence.ObservedAt) &&
            reason == "Board position establishes controller only, not starting-deck ownership.")
        {
            provenance = CardProvenance.ConfirmedStartingDeck;
            reason = "Recognized board arrival matches the known player deck; no observed creation route. Reacquisition does not spend another copy.";
        }
        if (sighting.Side == PlayerSide.User && userReference is not null &&
            userReference.CountOf(card.Id) == 0 && provenance != CardProvenance.Spawned)
        {
            // A selected library deck can be stale. An exact hand tooltip followed
            // by its own one-card cost is stronger evidence than reference
            // membership, but only after the creation/copy logic above has left the
            // play probable. This never rescues a merely visual preview, board card,
            // or a card still compatible with a known generation route.
            var independentlyPaidExactHand = provenance == CardProvenance.ProbableStartingDeck &&
                sighting.Source == CardSightSource.PlayPreview &&
                evidence.Description.StartsWith("Exact player-hand tooltip", StringComparison.OrdinalIgnoreCase) &&
                evidence.Description.Contains("one-card hand decrement", StringComparison.OrdinalIgnoreCase) &&
                !HasCopyRisk(sighting, evidence.ObservedAt);
            if (independentlyPaidExactHand)
                reason += " Exact hand identity and its one-card cost show that the selected library reference is stale for this slot; no observed generation route applies.";
            else
            {
                provenance = CardProvenance.Unknown;
                reason += " Outside selected reference: generated/acquired card or reference mismatch; not automatically called Created.";
            }
        }
        return new PlayOriginAssessment(provenance, reason.Trim());
    }

    private static bool CanBeCreated(CardDefinition source, CardDefinition card, PlayerSide side, DeckDefinition? userReference) =>
        source.Id == "203109" ? card.Name.EndsWith(" Runestone", StringComparison.OrdinalIgnoreCase) :
        source.Id == "203210" ? card.Kind == CardKind.Unit && card.Provision <= 10 :
        source.Id == "203045" ? card.Kind == CardKind.Special && FactionCompatibility.IsPlayableBy(card, "Scoia'tael") :
        source.Id == "201583" ? !card.IsGold && card.CanBeInStartingDeck && FactionCompatibility.IsPlayableBy(card, "Nilfgaard") :
        source.Id == "162315" ? !card.IsGold && card.CanBeInStartingDeck && card.Faction is not "Neutral" and not "Nilfgaard" :
        source.Id == "152403" ? card.Kind == CardKind.Unit && card.CanBeInStartingDeck &&
            FactionCompatibility.IsPlayableBy(card, "Skellige") && (card.HasCategory("Beast") || card.HasCategory("Human")) :
        source.Id == "203112" ? card.IsGold && card.CanBeInStartingDeck &&
            FactionCompatibility.IsPlayableBy(card, "Skellige") :
        card.Kind == CardKind.Unit && !card.IsGold && card.CanBeInStartingDeck &&
        card.Faction != "Neutral" &&
        (source.Id == "202658" && FactionCompatibility.IsPlayableBy(card, "Nilfgaard") &&
            Regex.IsMatch(card.AbilityText??"",@"(?:^|\n)Disloyal(?:\.|:|\b)",RegexOptions.IgnoreCase) ||
         FactionCompatibility.IsPlayableBy(card, "Scoia'tael") &&
         (source.Id == "203169" || source.Id == "203279" && card.HasCategory("Dwarf") ||
          source.Id is "203220" or "203047" && card.HasCategory("Elf"))) ||
        GenericCreationCanProduce(source,card,side,userReference);

    public bool HasCopyRisk(CardSighting sight, DateTimeOffset at) =>
        !IsConfirmedDeckPlay(sight,at) &&
        (_orderCreators.Any(pair=>pair.Key.Side==sight.Side && pair.Key.Id!=sight.Card.Id &&
            _orderUses.GetValueOrDefault(pair.Key)>0 && OrderCanCreate(pair.Value,sight.Card)) ||
        _spawnCreators.TryGetValue(sight.Side,out var spawn) && at>=spawn.At && at-spawn.At<=TimeSpan.FromSeconds(30) &&
            SpawnCanCreate(spawn.Card,sight.Card) ||
        _ambiguousCreations.ContainsKey((sight.Side, sight.Card.Id)) ||
        _creations.TryGetValue(sight.Side, out var source) && source.Source.Id != sight.Card.Id && at >= source.At &&
            !ConditionalGenerationRuledOut(source.Source,source.DeckCount) &&
            at - source.At <= CreationWindow(source.Source) && CanBeCreated(source.Source,sight.Card,sight.Side,null) ||
        _mahakamPass.Contains(sight.Side) && sight.Card.Kind==CardKind.Unit && !sight.Card.IsGold &&
            sight.Card.CanBeInStartingDeck && sight.Card.Faction!="Neutral" &&
            FactionCompatibility.IsPlayableBy(sight.Card,"Scoia'tael") && sight.Card.HasCategory("Dwarf"));

    private bool IsConfirmedDeckPlay(CardSighting sight,DateTimeOffset at) =>
        sight.Source==CardSightSource.PlayPreview && _confirmedDeckPlays.TryGetValue((sight.Side,sight.Card.Id),out var confirmedAt) &&
        at>=confirmedAt && at-confirmedAt<=TimeSpan.FromMilliseconds(500);

    public bool HasNonOrderCopyRisk(CardSighting sight, DateTimeOffset at) =>
        _ambiguousCreations.ContainsKey((sight.Side,sight.Card.Id)) ||
        _creations.TryGetValue(sight.Side,out var creation) && at>=creation.At &&
            !ConditionalGenerationRuledOut(creation.Source,creation.DeckCount) && at-creation.At<=CreationWindow(creation.Source) &&
            CanBeCreated(creation.Source,sight.Card,sight.Side,null) ||
        _spawnCreators.TryGetValue(sight.Side,out var spawn) && at>=spawn.At && at-spawn.At<=TimeSpan.FromSeconds(30) && SpawnCanCreate(spawn.Card,sight.Card);

    /// <summary>A confirmed hand cost can overcome a named spawn that was explicitly
    /// placed on the board, but never a route that could have put the card in hand.</summary>
    public bool HasHandAcquisitionRisk(CardSighting sight, DateTimeOffset at) =>
        _ambiguousCreations.ContainsKey((sight.Side,sight.Card.Id)) &&
            !_boardOnlyNamedSpawns.Contains((sight.Side,sight.Card.Id)) ||
        _creations.TryGetValue(sight.Side,out var creation) && at>=creation.At &&
            !ConditionalGenerationRuledOut(creation.Source,creation.DeckCount) && at-creation.At<=CreationWindow(creation.Source) &&
            CanBeCreated(creation.Source,sight.Card,sight.Side,null) ||
        _spawnCreators.TryGetValue(sight.Side,out var spawn) && at>=spawn.At && at-spawn.At<=TimeSpan.FromSeconds(30) && SpawnCanCreate(spawn.Card,sight.Card);

    public int? RecentSpawnedInitiators(CardSighting sight,DateTimeOffset at) =>
        _generatedInitiators.TryGetValue((sight.Side,sight.Card.Id),out var generated) && at>=generated.At &&
        at-generated.At<=TimeSpan.FromSeconds(30) &&
        _generationSourceUses.GetValueOrDefault((sight.Side,generated.SourceId))==1 &&
        _ambiguousCreations.ContainsKey((sight.Side,sight.Card.Id)) ? 1 : null;

    private static bool OrderCanCreate(CardDefinition source,CardDefinition target) =>
        OrderNamesTarget(source,target) || !target.IsGold && target.Kind==CardKind.Special && target.CanBeInStartingDeck &&
        FactionCompatibility.IsPlayableBy(target,source.Faction) && target.Faction!="Neutral" &&
        Regex.IsMatch(OrderText(source),@"\bCreate and play a bronze .+? special card\b",RegexOptions.IgnoreCase);
    private static bool HasGeneratedOrder(CardDefinition source) =>
        Regex.IsMatch(OrderText(source),@"\b(?:Spawn|Create)\b[^.\n]*\b(?:play|copy)\b",RegexOptions.IgnoreCase);
    private static string CreationText(CardDefinition source) => string.Join('\n',(source.AbilityText??"").Split('\n')
        .Where(line=>!line.Contains("Order:",StringComparison.OrdinalIgnoreCase) &&
            (Regex.IsMatch(line,@"\bCreate\b",RegexOptions.IgnoreCase) ||
             line.Contains("not in your starting deck",StringComparison.OrdinalIgnoreCase) &&
             Regex.IsMatch(line,@"\bSpawn and play\b",RegexOptions.IgnoreCase))));
    private static bool HasBoundedCreation(CardDefinition source) => CreationText(source).Length>0;
    // A few effects acquire/play an unknown card without using the literal word
    // "Create" in their current rules text. Keep those established bounded routes
    // while using parsed Create clauses for the broader catalogue.
    private static bool OpensCreationWindow(CardDefinition source) => HasBoundedCreation(source) ||
        source.Id is "203109" or "203210" or "203045" or "201583" or "162315";
    private static bool DirectlyPlaysCreatedCard(CardDefinition source) =>
        Regex.IsMatch(CreationText(source),@"\bCreate and play\b",RegexOptions.IgnoreCase) ||
        CreationText(source).Contains("not in your starting deck",StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(CreationText(source),@"\bSpawn and play\b",RegexOptions.IgnoreCase);
    private static bool AlwaysPlaysOutsideStartingDeck(CardDefinition source) =>
        DirectlyPlaysCreatedCard(source) &&
        CreationText(source).Contains("not in your starting deck",StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(CreationText(source),@"\bIf there are fewer than\b",RegexOptions.IgnoreCase);
    private static int? ConditionalGenerationThreshold(CardDefinition source)
    {
        var match=Regex.Match(CreationText(source),@"\bIf there are fewer than\s+(?<count>\d+)\s+cards? in your deck\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups["count"].Value,System.Globalization.CultureInfo.InvariantCulture) : null;
    }
    private static bool ConditionalGenerationProven(CardDefinition source,int? deckCount) =>
        ConditionalGenerationThreshold(source) is { } threshold && deckCount is { } count && count<threshold;
    private static bool ConditionalGenerationRuledOut(CardDefinition source,int? deckCount) =>
        ConditionalGenerationThreshold(source) is { } threshold && deckCount is { } count && count>=threshold;
    private static bool MovesEnemyToOwnDeck(CardDefinition source) => Regex.IsMatch(source.AbilityText??"",
        @"\bMove an enemy unit to the top of your deck\b",RegexOptions.IgnoreCase);
    private static Match GraveyardPlayClause(CardDefinition source) => Regex.Match(NonOrderText(source),
        @"\bPlay\s+(?<selector>[^.\n,]+?)\s+from\s+(?:your|your opponent['’]s)\s+graveyard\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // If the same source also has a Create-and-play branch (notably Lydia), row
    // choice is required to distinguish creation from replay. Keep that route
    // Unknown unless row evidence is added rather than asserting a graveyard play.
    private static bool IsGenericGraveyardPlaySource(CardDefinition source) =>
        GraveyardPlayClause(source).Success && !HasBoundedCreation(source);
    private static bool PlaysMultipleFromGraveyard(CardDefinition source) =>
        Regex.IsMatch(GraveyardPlayClause(source).Groups["selector"].Value, @"\b(?:all copies|unique)\b", RegexOptions.IgnoreCase);
    private static bool CanSelectFromGraveyard(CardDefinition source, CardDefinition target)
    {
        var match=GraveyardPlayClause(source);
        if(!match.Success || !target.CanBeInStartingDeck) return false;
        var selector=match.Groups["selector"].Value;
        if(Regex.IsMatch(selector,@"\bunit\b",RegexOptions.IgnoreCase) && target.Kind!=CardKind.Unit ||
           Regex.IsMatch(selector,@"\bspecial(?: card)?\b",RegexOptions.IgnoreCase) && target.Kind!=CardKind.Special ||
           Regex.IsMatch(selector,@"\bbronze\b",RegexOptions.IgnoreCase) && target.IsGold ||
           Regex.IsMatch(selector,@"\bnon-Neutral\b",RegexOptions.IgnoreCase) && target.Faction=="Neutral") return false;
        var maximum=Regex.Match(selector,@"provision cost(?: of)?\s+(?<p>\d+)\s+or less",RegexOptions.IgnoreCase);
        if(maximum.Success && target.Provision>int.Parse(maximum.Groups["p"].Value)) return false;
        var exactProvision=Regex.Match(selector,@"(?<p>\d+)-provision(?: cost)?",RegexOptions.IgnoreCase);
        if(exactProvision.Success && target.Provision!=int.Parse(exactProvision.Groups["p"].Value)) return false;
        if(Regex.IsMatch(selector,@"\b"+Regex.Escape(target.Name)+@"(?:s)?\b",RegexOptions.IgnoreCase)) return true;
        // Remove selection grammar; any remaining descriptor must be a printed
        // category on the target (Tactic, Nature, Warrior, Alchemy, Spell, etc.).
        var descriptor=Regex.Replace(selector,
            @"\b(?:a|an|the|all|copies|of|unique|bronze|non-Neutral|unit|special|card|provision|cost|or|less)\b|\[[^\]]*\]|\d+|-|\(s\)",
            " ",RegexOptions.IgnoreCase);
        descriptor=Regex.Replace(descriptor,@"\s+"," ").Trim();
        return descriptor.Length==0 || target.Categories.Any(category=>
            descriptor.Equals(category,StringComparison.OrdinalIgnoreCase));
    }
    private static bool GenericCreationCanProduce(CardDefinition source,CardDefinition target,PlayerSide side,DeckDefinition? userReference)
    {
        var text=CreationText(source);
        if (text.Length==0 || !target.CanBeInStartingDeck) return false;
        if (Regex.IsMatch(text,@"\bunit\b",RegexOptions.IgnoreCase) && target.Kind!=CardKind.Unit ||
            Regex.IsMatch(text,@"\bspecial card\b",RegexOptions.IgnoreCase) && target.Kind!=CardKind.Special ||
            Regex.IsMatch(text,@"\bbronze\b",RegexOptions.IgnoreCase) && target.IsGold ||
            Regex.IsMatch(text,@"\bDisloyal\b",RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(target.AbilityText??"",@"(?:^|\n)Disloyal(?:\.|:|\b)",RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text,@"\bRunestone\b",RegexOptions.IgnoreCase) &&
            !target.Name.EndsWith(" Runestone",StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(text,@"\bScoia'tael\b",RegexOptions.IgnoreCase) &&
            (target.Faction=="Neutral" || !FactionCompatibility.IsPlayableBy(target,"Scoia'tael"))) return false;
        if (Regex.IsMatch(text,@"opponent's faction",RegexOptions.IgnoreCase) && side==PlayerSide.Opponent &&
            userReference is { } opponent && !FactionCompatibility.IsPlayableBy(target,opponent.Faction)) return false;
        if (Regex.IsMatch(text,@"opponent's starting deck",RegexOptions.IgnoreCase) && side==PlayerSide.Opponent &&
            userReference?.CountOf(target.Id)<=0) return false;
        foreach(var category in target.Categories)
            if (Regex.IsMatch(text,@"\b"+Regex.Escape(category)+@"\b",RegexOptions.IgnoreCase)) return true;
        return !Regex.IsMatch(text,@"\b(?:Elf|Dwarf|Human|Witcher|Mage|Dryad|Druid|Soldier|Aristocrat|Cultist|Pirate|Beast|Dragon)\b",RegexOptions.IgnoreCase);
    }
    private static bool OrderNamesTarget(CardDefinition source,CardDefinition target) =>
        Regex.IsMatch(OrderText(source),@"\bSpawn and play\s+"+Regex.Escape(target.Name)+@"\b",RegexOptions.IgnoreCase);
    private static string NonOrderText(CardDefinition source) => string.Join('\n',(source.AbilityText??"").Split('\n')
        .Where(line=>!line.Contains("Order:",StringComparison.OrdinalIgnoreCase)));
    private static bool HasNonOrderNamedSpawn(CardDefinition source) =>
        Regex.IsMatch(NonOrderText(source),@"\bSpawn(?: and play)?\b",RegexOptions.IgnoreCase);
    private static bool NonOrderSpawnNamesTarget(CardDefinition source,CardDefinition target) =>
        Regex.IsMatch(NonOrderText(source),@"\bSpawn(?: and play)?\b[^.\n]*\b"+Regex.Escape(target.Name)+@"\b",RegexOptions.IgnoreCase);
    private CardDefinition? NamedPublicSpawnSource(PlayerSide side,CardDefinition target)
    {
        if (_currentLeaders.TryGetValue(side,out var leader) && LeaderSpawnCatalog.NamesSpawnedCard(leader,target))
            return leader;
        if (_openingStratagems.TryGetValue(side,out var stratagem) && LeaderSpawnCatalog.NamesSpawnedCard(stratagem,target))
            return stratagem;
        return null;
    }
    public CardDefinition? CurrentLeaderHandPlaySource(PlayerSide side, CardDefinition target) =>
        _currentLeaders.TryGetValue(side, out var leader) && LeaderSpawnCatalog.HandPlayRule(leader) is { } rule && rule.Matches(target)
            ? leader : null;
    private static string PublicSourceKind(CardDefinition source) => source.Kind == CardKind.Leader ? "leader" : "opening stratagem";
    private static bool NonOrderNamedSpawnIsBoardOnly(CardDefinition source,CardDefinition target)
    {
        var text=NonOrderText(source);
        var clause=Regex.Match(text,@"\bSpawn(?: and play)?\b[^.\n]*\b"+Regex.Escape(target.Name)+@"\b[^.\n]*",RegexOptions.IgnoreCase);
        return clause.Success && !Regex.IsMatch(clause.Value,@"\b(?:hand|deck)\b",RegexOptions.IgnoreCase) &&
            Regex.IsMatch(clause.Value,@"\bon (?:this|that|your|an? enemy) row\b",RegexOptions.IgnoreCase);
    }
    private void RememberAmbiguity(PlayerSide side,string id,string reason,bool boardOnlyNamedSpawn=false)
    {
        var key=(side,id);
        _ambiguousCreations[key]=reason;
        if(boardOnlyNamedSpawn) _boardOnlyNamedSpawns.Add(key); else _boardOnlyNamedSpawns.Remove(key);
    }
    private void RememberGeneratedInitiator(PlayerSide side,string id,DateTimeOffset at,string sourceId) =>
        _generatedInitiators[(side,id)]=(at,sourceId);
    private static string OrderText(CardDefinition source)
    {
        var text=source.AbilityText??"";
        var order=text.IndexOf("Order",StringComparison.OrdinalIgnoreCase);
        return order<0 ? "" : text[order..];
    }
    private static string? SpawnCategory(CardDefinition source)
    {
        var text=source.AbilityText??"";
        // Garrison's printed grammar calls the chosen category itself a
        // "bronze Soldier", without a trailing "unit" noun.
        var baseCopy=Regex.Match(text,
            @"\bSpawn and play a base copy of a bronze (?<category>[^.\r\n]+?) from your starting deck(?:\.|$)",
            RegexOptions.IgnoreCase);
        if(baseCopy.Success) return baseCopy.Groups["category"].Value.Trim();
        var match=Regex.Match(text,
            @"\bSpawn and play an? (?:random )?(?:bronze )?(?<category>.+?) unit(?: from your starting deck)?(?:\.|\r|\n|$)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["category"].Value.Trim() : null;
    }
    private static bool SpawnFromStartingDeck(CardDefinition source) => Regex.IsMatch(source.AbilityText??"",
        @"\bSpawn and play an? [^.\n]*? from your starting deck\b",RegexOptions.IgnoreCase);
    private static bool SpawnCanCreate(CardDefinition source,CardDefinition target) => SpawnCategory(source) is {} category &&
        target.Kind==CardKind.Unit && !target.IsGold && target.CanBeInStartingDeck &&
        (target.HasCategory(category) || Regex.IsMatch(target.AbilityText??"",@"(?:^|\n)"+Regex.Escape(category)+@"\s*:",RegexOptions.IgnoreCase));
    public void Reset() { _creations.Clear(); _deckPlays.Clear(); _graveyardPlays.Clear(); _confirmedDeckPlays.Clear(); _deckThefts.Clear(); _mahakamPass.Clear(); _ambiguousCreations.Clear(); _boardOnlyNamedSpawns.Clear(); _orderCreators.Clear(); _orderUses.Clear(); _namedSpawns.Clear(); _spawnCreators.Clear(); _spawnUses.Clear(); _generatedInitiators.Clear(); _generationSourceUses.Clear(); _currentLeaders.Clear(); _openingStratagems.Clear(); _coTemporalBoardCreations.Clear(); }
}
