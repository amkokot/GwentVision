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
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _creations = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _deckPlays = [];
    private readonly Dictionary<(PlayerSide Side, string Id), DateTimeOffset> _confirmedDeckPlays = [];
    private readonly Dictionary<PlayerSide, (DateTimeOffset At, CardDefinition Source)> _deckThefts = [];
    private readonly HashSet<PlayerSide> _mahakamPass = [];
    private readonly Dictionary<(PlayerSide, string), string> _ambiguousCreations = [];
    private readonly Dictionary<(PlayerSide Side,string Id),CardDefinition> _orderCreators = [];
    private readonly Dictionary<(PlayerSide Side,string Id),int> _orderUses = [];
    private readonly Dictionary<PlayerSide,(DateTimeOffset At,CardDefinition Source)> _namedSpawns = [];
    private readonly Dictionary<PlayerSide,(DateTimeOffset At,CardDefinition Card)> _spawnCreators = [];
    private readonly Dictionary<(PlayerSide Side,string Id),int> _spawnUses = [];

    public PlayOriginAssessment Observe(VisionEvidenceEvent evidence, DeckDefinition? userReference = null)
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
        var reason = sighting.Source == CardSightSource.Board
            ? "Board position establishes controller only, not starting-deck ownership." : string.Empty;
        if (_namedSpawns.TryGetValue(sighting.Side,out var named) && named.Source.Id!=card.Id &&
            evidence.ObservedAt>named.At && evidence.ObservedAt-named.At<=TimeSpan.FromSeconds(30) &&
            NonOrderSpawnNamesTarget(named.Source,card))
        {
            provenance=CardProvenance.Spawned;
            exactSpawnEvidence=true;
            reason=$"Explicit named Spawn output of {named.Source.Name}; not a starting-deck provision.";
            _ambiguousCreations[(sighting.Side,card.Id)]=reason;
        }
        var order=!exactDeckPlay ? _orderCreators.Where(pair=>pair.Key.Side==sighting.Side && pair.Key.Id!=card.Id &&
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
            // "Spawn ... from your starting deck" creates a new body, but its
            // selected identity still proves one template was in the starting
            // deck. Debit that template once; keep later copies ambiguous.
            provenance=CardProvenance.ProbableStartingDeck;
            spawnedTemplateEvidence=true;
            reason=$"Spawned target of {spawn.Card.Name}; its identity proves one starting-deck template, while the generated body cannot establish an additional copy.";
            _ambiguousCreations[(sighting.Side,card.Id)]=reason;
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
        }
        if (sighting.Source == CardSightSource.PlayPreview)
        {
            var pending = _creations.TryGetValue(sighting.Side, out var value) ? value : ((DateTimeOffset At, CardDefinition Source)?)null;
            // Repeated OCR episodes of the same lingering creator preview are not its target.
            if (pending is not { } same || same.Source.Id != card.Id)
                _creations.Remove(sighting.Side);
            if (pending is { } creation && creation.Source.Id != card.Id &&
                evidence.ObservedAt > creation.At && evidence.ObservedAt - creation.At <= CreationWindow(creation.Source) &&
                CanBeCreated(creation.Source, card, sighting.Side, userReference))
            {
                provenance = CardProvenance.Unknown;
                reason = $"Possible creation following {creation.Source.Name}; excluded from starting-deck constraints unless independently corroborated.";
                _ambiguousCreations[(sighting.Side, card.Id)] = reason;
            }
            else if (_deckThefts.TryGetValue(sighting.Side,out var theft) && theft.Source.Id!=card.Id &&
                evidence.ObservedAt>theft.At && userReference?.CountOf(card.Id)>0 && card.Faction!="Neutral" &&
                !FactionCompatibility.IsPlayableBy(card,theft.Source.Faction))
            {
                provenance=CardProvenance.Unknown;
                reason=$"Possible cross-faction card moved into deck by {theft.Source.Name}; excluded from original-deck constraints.";
                _ambiguousCreations[(sighting.Side,card.Id)]=reason;
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
            }
            // Tutors (Council / Call of the Forest) deliberately do not open creation
            // windows. Parse bounded Create clauses; Order clauses use the separate
            // Order tracker above. A create-to-hand target remains conservative unless
            // it is the next compatible play inside the short window.
            if (OpensCreationWindow(card) && (pending is not { } sameCreator || sameCreator.Source.Id != card.Id))
                _creations[sighting.Side] = (evidence.ObservedAt, card);
            if (HasNonOrderNamedSpawn(card) &&
                (!_namedSpawns.TryGetValue(sighting.Side,out var priorSpawn) || priorSpawn.Source.Id!=card.Id))
                _namedSpawns[sighting.Side]=(evidence.ObservedAt,card);
            if (MovesEnemyToOwnDeck(card)) _deckThefts[sighting.Side]=(evidence.ObservedAt,card);
            if (DeckPlayResolutionTracker.IsGenericDeckPlaySource(card))
                _deckPlays[sighting.Side]=(evidence.ObservedAt,card);
            if (card.Id == "203279") _mahakamPass.Add(sighting.Side);
            if (card.Id == "202473" && _mahakamPass.Contains(sighting.Side))
            {
                provenance = CardProvenance.Unknown;
                reason = "Tempering may be spawned by the observed Mahakam Pass Order; an independent starting copy remains possible.";
                _ambiguousCreations[(sighting.Side, card.Id)] = reason;
            }
        }
        else if (sighting.Source == CardSightSource.Board && _creations.TryGetValue(sighting.Side, out var boardCreation) &&
            evidence.ObservedAt > boardCreation.At &&
            evidence.ObservedAt - boardCreation.At <= CreationWindow(boardCreation.Source) &&
            CanBeCreated(boardCreation.Source, card, sighting.Side, userReference))
        {
            reason = $"Possible creation following {boardCreation.Source.Name}; first recognized on board, not an independent original.";
            _ambiguousCreations[(sighting.Side, card.Id)] = reason;
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
            provenance = CardProvenance.Unknown;
            reason += " Outside selected reference: generated/acquired card or reference mismatch; not automatically called Created.";
        }
        return new PlayOriginAssessment(provenance, reason.Trim());
    }

    private static bool CanBeCreated(CardDefinition source, CardDefinition card, PlayerSide side, DeckDefinition? userReference) =>
        source.Id == "203109" ? card.Name.EndsWith(" Runestone", StringComparison.OrdinalIgnoreCase) :
        source.Id == "203210" ? card.Kind == CardKind.Unit && card.Provision <= 10 :
        source.Id == "203045" ? card.Kind == CardKind.Special && FactionCompatibility.IsPlayableBy(card, "Scoia'tael") :
        source.Id == "201583" ? !card.IsGold && card.CanBeInStartingDeck && FactionCompatibility.IsPlayableBy(card, "Nilfgaard") :
        source.Id == "162315" ? !card.IsGold && card.CanBeInStartingDeck && card.Faction is not "Neutral" and not "Nilfgaard" :
        card.Kind == CardKind.Unit && !card.IsGold && card.CanBeInStartingDeck &&
        card.Faction != "Neutral" &&
        (source.Id == "202658" && FactionCompatibility.IsPlayableBy(card, "Nilfgaard") ||
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
            at - source.At <= CreationWindow(source.Source) && CanBeCreated(source.Source,sight.Card,sight.Side,null) ||
        _mahakamPass.Contains(sight.Side) && sight.Card.Kind==CardKind.Unit && !sight.Card.IsGold &&
            sight.Card.CanBeInStartingDeck && sight.Card.Faction!="Neutral" &&
            FactionCompatibility.IsPlayableBy(sight.Card,"Scoia'tael") && sight.Card.HasCategory("Dwarf"));

    private bool IsConfirmedDeckPlay(CardSighting sight,DateTimeOffset at) =>
        sight.Source==CardSightSource.PlayPreview && _confirmedDeckPlays.TryGetValue((sight.Side,sight.Card.Id),out var confirmedAt) &&
        at>=confirmedAt && at-confirmedAt<=TimeSpan.FromMilliseconds(500);

    public bool HasNonOrderCopyRisk(CardSighting sight, DateTimeOffset at) =>
        _ambiguousCreations.ContainsKey((sight.Side,sight.Card.Id)) ||
        _creations.TryGetValue(sight.Side,out var creation) && at>=creation.At && at-creation.At<=CreationWindow(creation.Source) &&
            CanBeCreated(creation.Source,sight.Card,sight.Side,null) ||
        _spawnCreators.TryGetValue(sight.Side,out var spawn) && at>=spawn.At && at-spawn.At<=TimeSpan.FromSeconds(30) && SpawnCanCreate(spawn.Card,sight.Card);

    public int? RecentSpawnedInitiators(CardSighting sight,DateTimeOffset at) =>
        _spawnCreators.TryGetValue(sight.Side,out var spawn) && at>=spawn.At && at-spawn.At<=TimeSpan.FromSeconds(30) &&
        SpawnCanCreate(spawn.Card,sight.Card) && _spawnUses.Where(pair=>pair.Key.Side==sight.Side).Sum(pair=>pair.Value)==1 &&
        _ambiguousCreations.ContainsKey((sight.Side,sight.Card.Id)) ? 1 : null;

    private static bool OrderCanCreate(CardDefinition source,CardDefinition target) =>
        OrderNamesTarget(source,target) || !target.IsGold && target.Kind==CardKind.Special && target.CanBeInStartingDeck &&
        FactionCompatibility.IsPlayableBy(target,source.Faction) && target.Faction!="Neutral" &&
        Regex.IsMatch(OrderText(source),@"\bCreate and play a bronze .+? special card\b",RegexOptions.IgnoreCase);
    private static bool HasGeneratedOrder(CardDefinition source) =>
        Regex.IsMatch(OrderText(source),@"\b(?:Spawn|Create)\b[^.\n]*\b(?:play|copy)\b",RegexOptions.IgnoreCase);
    private static string CreationText(CardDefinition source) => string.Join('\n',(source.AbilityText??"").Split('\n')
        .Where(line=>!line.Contains("Order:",StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(line,@"\bCreate\b",RegexOptions.IgnoreCase)));
    private static bool HasBoundedCreation(CardDefinition source) => CreationText(source).Length>0;
    // A few effects acquire/play an unknown card without using the literal word
    // "Create" in their current rules text. Keep those established bounded routes
    // while using parsed Create clauses for the broader catalogue.
    private static bool OpensCreationWindow(CardDefinition source) => HasBoundedCreation(source) ||
        source.Id is "203109" or "203210" or "203045" or "201583" or "162315";
    private static bool MovesEnemyToOwnDeck(CardDefinition source) => Regex.IsMatch(source.AbilityText??"",
        @"\bMove an enemy unit to the top of your deck\b",RegexOptions.IgnoreCase);
    private static bool GenericCreationCanProduce(CardDefinition source,CardDefinition target,PlayerSide side,DeckDefinition? userReference)
    {
        var text=CreationText(source);
        if (text.Length==0 || !target.CanBeInStartingDeck) return false;
        if (Regex.IsMatch(text,@"\bunit\b",RegexOptions.IgnoreCase) && target.Kind!=CardKind.Unit ||
            Regex.IsMatch(text,@"\bspecial card\b",RegexOptions.IgnoreCase) && target.Kind!=CardKind.Special ||
            Regex.IsMatch(text,@"\bbronze\b",RegexOptions.IgnoreCase) && target.IsGold) return false;
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
    private static string OrderText(CardDefinition source)
    {
        var text=source.AbilityText??"";
        var order=text.IndexOf("Order",StringComparison.OrdinalIgnoreCase);
        return order<0 ? "" : text[order..];
    }
    private static string? SpawnCategory(CardDefinition source)
    {
        var match=Regex.Match(source.AbilityText??"",@"\bSpawn and play an? (?:bronze )?(.+?) unit from your starting deck\b",RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
    private static bool SpawnCanCreate(CardDefinition source,CardDefinition target) => SpawnCategory(source) is {} category &&
        target.Kind==CardKind.Unit && !target.IsGold && target.CanBeInStartingDeck &&
        (target.HasCategory(category) || Regex.IsMatch(target.AbilityText??"",@"(?:^|\n)"+Regex.Escape(category)+@"\s*:",RegexOptions.IgnoreCase));
    public void Reset() { _creations.Clear(); _deckPlays.Clear(); _confirmedDeckPlays.Clear(); _deckThefts.Clear(); _mahakamPass.Clear(); _ambiguousCreations.Clear(); _orderCreators.Clear(); _orderUses.Clear(); _namedSpawns.Clear(); _spawnCreators.Clear(); _spawnUses.Clear(); }
}
