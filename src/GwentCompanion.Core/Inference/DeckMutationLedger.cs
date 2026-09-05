using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Inference;

public sealed record DeckMutation(DateTimeOffset At, string SourceCardId, string SourceName, PlayerSide AffectedSide,
    string Description, string? AddedCardId = null, string? AddedCardName = null, int? Copies = null,
    bool Confirmed = false, string? CandidateFaction = null, string CandidateKind = "any", bool IsDeckReveal = false,
    bool CreatesOutsideDeck = false, string? GeneratedText = null, bool CopyCountOnly = false,
    bool HiddenSafeguard = false, bool HandTemplateCopy = false);

/// <summary>Acquisition risks are separate from starting-deck membership and copy counts.</summary>
public sealed class DeckMutationLedger
{
    private readonly List<DeckMutation> _changes = [];
    private readonly Dictionary<PlayerSide, DateTimeOffset> _graveReplay = [];
    private readonly Dictionary<PlayerSide, DateTimeOffset> _boardReplay = [];
    // Broad safeguards are not observed deck modifications and must not clutter
    // the user's mutation log or be saved as learned additions.
    public IReadOnlyList<DeckMutation> Changes => _changes.Where(item => !item.HiddenSafeguard).ToArray();
    public bool HasRecentGraveReplay(PlayerSide side, DateTimeOffset at, TimeSpan? window = null) =>
        _graveReplay.TryGetValue(side, out var replay) && at >= replay && at - replay <= (window ?? TimeSpan.FromSeconds(20));
    public bool HasRecentReplay(PlayerSide side, DateTimeOffset at) => HasRecentGraveReplay(side,at) ||
        _boardReplay.TryGetValue(side,out var replay) && at>=replay && at-replay<=TimeSpan.FromSeconds(20);
    public void Reset() { _changes.Clear(); _graveReplay.Clear(); _boardReplay.Clear(); }
    public void RemoveReviewedHeulynSetup() => _changes.RemoveAll(item => item.SourceCardId == "203278" && item.Description.StartsWith("User verified Heulyn setup:", StringComparison.Ordinal));
    public void Record(DeckMutation change)
    {
        if (_changes.Any(item => item.SourceCardId == change.SourceCardId && item.AffectedSide == change.AffectedSide &&
            item.AddedCardId == change.AddedCardId && item.Confirmed == change.Confirmed &&
            item.IsDeckReveal == change.IsDeckReveal && item.CreatesOutsideDeck == change.CreatesOutsideDeck &&
            item.CopyCountOnly == change.CopyCountOnly && item.HiddenSafeguard == change.HiddenSafeguard &&
            item.HandTemplateCopy == change.HandTemplateCopy)) return;
        if (_changes.Count < 128) _changes.Add(change); // bounded per-match evidence, never whole-frame history
    }

    public void Observe(VisionEvidenceEvent evidence, string? userFaction, string? opponentFaction)
    {
        var sight = evidence.Sighting;
        var side = sight.Side; var other = side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
        var faction = side == PlayerSide.User ? userFaction : opponentFaction;
        var otherFaction = side == PlayerSide.User ? opponentFaction : userFaction;
        if (sight.Source == CardSightSource.PlayPreview && Regex.IsMatch(sight.Card.AbilityText ?? "", @"\b(Play|Summon)\b[^.\n]*\bgraveyard\b", RegexOptions.IgnoreCase))
            _graveReplay[side] = evidence.ObservedAt;
        if (sight.Source == CardSightSource.PlayPreview && Regex.IsMatch(sight.Card.AbilityText ?? "", @"\breplay\b",RegexOptions.IgnoreCase))
            _boardReplay[side]=evidence.ObservedAt;
        void Add(PlayerSide target, string message, string kind = "any", string? filterFaction = null) => Record(new(
            evidence.ObservedAt, sight.Card.Id, sight.Card.Name, target, message + " Resolution/identities unverified.",
            CandidateFaction: filterFaction, CandidateKind: kind));
        if (sight.Card.AbilityText is { } ability && ability.Contains("Scenario:", StringComparison.Ordinal))
        {
            // Only printed Spawn clauses supply generated identities. A Summon/Play-from-deck
            // chapter (Damsel, Manor) must not make its real deck targets look generated.
            var spawnText = string.Join('\n', ability.Split('\n').Where(line => Regex.IsMatch(line, @"\bSpawn\b", RegexOptions.IgnoreCase)));
            if (spawnText.Length > 0) Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                "Scenario may generate its printed Spawn payoffs; chapters/removal unverified. Outputs are NOT promised. Deck-summoning chapters remain separate.",
                CandidateKind: "scenario-spawn", CreatesOutsideDeck: true, GeneratedText: spawnText,
                // A scenario chapter is normally advanced by a paid card immediately
                // before its generated payoff. A delayed hand-count read belongs to
                // that trigger, not to the spawned preview, so it cannot disprove this
                // exact printed generation route.
                HandTemplateCopy: true));
        }
        if (sight.Source == CardSightSource.Board) return; // Other presence is not a deploy/order.
        if (sight.Card.AbilityText is { } text && !text.Contains("Scenario:", StringComparison.Ordinal) && Regex.IsMatch(text, @"\bSpawn\b"))
        {
            var clauses = string.Join('\n', text.Split('\n').Where(line => Regex.IsMatch(line, @"\bSpawn\b")));
            // Named spawned bodies (e.g. Cerys' Shieldmaiden) and untargeted copy
            // effects must not turn two visible bodies into two original copies.
            var unspecifiedCopy = Regex.IsMatch(clauses, @"\b(cop(?:y|ies))\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(clauses, @"\bself\b", RegexOptions.IgnoreCase);
            var immediateDeployCopy = unspecifiedCopy && Regex.IsMatch(clauses,
                @"\bDeploy\b[^.\n]*\b(cop(?:y|ies))\b",RegexOptions.IgnoreCase);
            var handTemplateCopy = unspecifiedCopy && Regex.IsMatch(clauses,
                @"\bcopy\b[^.\n]*\bfrom your hand\b", RegexOptions.IgnoreCase);
            // A played Special whose text directly names its Spawn output resolves
            // that output as part of the card play (for example Water of Brokilon).
            // A unit/Location merely being present does not prove which Deploy row
            // or later Order was used, so it protects only additional copies.
            var resolvedNamedSpecial = sight.Card.Kind==CardKind.Special && !unspecifiedCopy;
            Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                "Printed Spawn effect may supply generated copies; resolution/target unverified.",
                CandidateKind: immediateDeployCopy ? "spawned-deploy-copies" : unspecifiedCopy ? "spawned-copies" : "spawned-names", CreatesOutsideDeck: true, GeneratedText: clauses,
                CopyCountOnly: !resolvedNamedSpecial, HiddenSafeguard: true, HandTemplateCopy: handTemplateCopy));
        }
        switch (sight.Card.Id)
        {
            case "203275":
                Add(side, "Henry may add a created legendary to hand and the other choices to deck.", "gold-unit", otherFaction);
                Add(other, "Henry may add a created legendary to hand and the other choices to deck.", "gold-unit", faction); break;
            case "203198": Add(side, "Temple may shuffle three created legendaries, absent from the original list, into deck.", "gold-unit", "Northern Realms"); break;
            case "203192": Add(side, "Torres: Founder may add three copied opponent units of at most 10p to deck; Order can banish originals.", "unit-at-most-ten", otherFaction); break;
            case "203039": Add(side, "Imperial Practitioner can copy the last opposing graveyard card into deck when its Order resolves."); break;
            case "202886": case "122310":
                // Foltest and Blue Stripes Scout copy an identity already represented
                // by an allied unit. They can make later physical copies ambiguous,
                // but cannot make the first observed identity absent from the starting
                // composition: at least one template had to exist first.
                Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                    "Can add base copies of an existing bronze non-Neutral allied unit to deck. Resolution/identity unverified.",
                    CandidateKind: "bronze-unit", CopyCountOnly: true)); break;
            case "162301": Add(side, "Daerlan generates a copy per starting copy at setup; repeated copies are not singleton violations.", "daerlan"); break;
            case "162203": Add(other, "Cynthia may put The Guardian on top of deck.", "guardian"); break;
            case "202796": case "202800": case "203212": case "203213":
                Add(other, "Can add a copy of the targeted non-Token unit to deck when its condition resolves.", "unit"); break;
            case "200118": Add(other, "Infiltrator can enter this deck at round end.", "infiltrator"); break;
            case "203211": Add(other, "Sandor can transfer a card from the other deck; Order adds a Drone."); break;
            case "162202":
                // Assire cannot introduce a previously unseen identity: its target has
                // already existed in a graveyard. Keep the visible mutation in the log,
                // but apply its uncertainty only when a later sighting would add another
                // physical copy of an identity already observed this match.
                Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                    "Assire can return a graveyard card to its owner's deck; this is not necessarily a new original. Resolution/identity unverified.",
                    CopyCountOnly: true));
                Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, other,
                    "Assire can return a graveyard card to its owner's deck; row/target not resolved. Resolution/identity unverified.",
                    CopyCountOnly: true)); break;
            case "203062": Add(side, "Alissa can return specials from graveyard to deck; replay is not another starting copy.", "special"); break;
            case "122210":
                // Returning an existing graveyard card changes its zone, not whether
                // its identity existed. Preserve uncertainty only for later copies.
                Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                    "Pavetta can return existing bronze non-Neutral units from graveyard to deck. Resolution/identity unverified.",
                    CandidateKind: "bronze-unit", CopyCountOnly: true)); break;
            case "203278": Add(side, "Heulyn's five opening graveyard Humans are generated, not starting-deck cards.", "heulyn"); break;
            case "203226": Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                "Hive Mind may transform spawned Drones into chosen bronze Kikimores. Banish stops future triggers, not prior generated origins. No outputs promised; exact selections/banish target unverified.",
                CandidateKind: "hive-mind", CreatesOutsideDeck: true)); break;
            case "200022": Record(new(evidence.ObservedAt, sight.Card.Id, sight.Card.Name, side,
                "Open the visible description to inspect the selected unit; target not read yet.", IsDeckReveal: true)); break;
        }
    }

    public PlayOriginAssessment? OriginRisk(CardSighting sight, DateTimeOffset at, IEnumerable<ObservedCard> prior, bool additionalCopy = false,
        bool independentHandPlay = false)
    {
        // These are possible origins of ALREADY SEEN cards, never an output forecast.
        // Removing a generator cannot retroactively make its existing outputs originals.
        // An established original protects that identity, not every later physical copy.
        if (!additionalCopy && prior.Any(item => item.Card.Id == sight.Card.Id && StartingDeckRules.CountsAgainstStartingDeck(item.Provenance))) return null;
        var card = sight.Card;
        var change = _changes.LastOrDefault(item => !item.IsDeckReveal && (!item.CopyCountOnly || additionalCopy) && item.AffectedSide == sight.Side && item.At <= at &&
            // A later hand decrement normally rules out a previously generated
            // body. It does not do so when the generator copied a template that
            // stayed in hand (Ramon-style): the eventual paid play can be that
            // same one original template, not a second starting copy.
            !(independentHandPlay && item.CreatesOutsideDeck && !item.HandTemplateCopy) &&
            item.SourceCardId != card.Id && (item.AddedCardId == card.Id || item.AddedCardId is null && Matches(item, card)) &&
            (item.CandidateKind != "heulyn" || sight.Source != CardSightSource.PlayPreview ||
             _graveReplay.TryGetValue(sight.Side, out var replay) && at >= replay && at - replay <= TimeSpan.FromSeconds(20)));
        if (change is null) return null;
        if(additionalCopy && sight.Source==CardSightSource.PlayPreview && change.CopyCountOnly && change.AddedCardId is null &&
            (change.CandidateKind=="spawned-deploy-copies" && at-change.At<=TimeSpan.FromSeconds(30) ||
             change.SourceCardId=="162202" && card.IsGold))
        {
            var bound=change with {AddedCardId=card.Id,AddedCardName=card.Name};
            if(change.SourceCardId=="162202")
            {
                // Assire returns exactly one card. A repeated gold cannot be a
                // second legal original, so it resolves both prior side branches
                // to this one owner/identity instead of poisoning later repeats.
                _changes.RemoveAll(item=>item.SourceCardId==change.SourceCardId && item.At==change.At);
                _changes.Add(bound);
            }
            else _changes[_changes.LastIndexOf(change)]=bound;
            change=bound;
        }
        return new(change.Confirmed && change.AddedCardId == card.Id ? CardProvenance.Created : CardProvenance.Unknown,
            $"Possibly {(change.CreatesOutsideDeck ? "generated/transformed" : "acquired")} via {change.SourceName}; excluded from original-deck provisions/constraints until origin is reviewed. " + change.Description);
    }
    private static bool Matches(DeckMutation change, CardDefinition card)
    {
        if (change.CandidateFaction is { } faction && !FactionCompatibility.IsPlayableBy(card, faction)) return false;
        return change.CandidateKind switch
        {
            "gold-unit" => card.Kind == CardKind.Unit && card.IsGold && card.Faction != "Neutral",
            "unit-at-most-ten" => card.Kind == CardKind.Unit && card.Provision <= 10,
            "unit" => card.Kind == CardKind.Unit,
            "bronze-unit" => card.Kind == CardKind.Unit && !card.IsGold && card.Faction != "Neutral",
            "special" => card.Kind == CardKind.Special,
            "daerlan" => card.Id == "162301",
            "guardian" => card.Name == "The Guardian",
            "infiltrator" => card.Id == "200118",
            "hive-mind" => card.Kind == CardKind.Unit && !card.IsGold && card.Name.StartsWith("Kikimore ", StringComparison.Ordinal),
            "scenario-spawn" or "spawned-names" => TextMentionsCard(change.GeneratedText ?? "", card.Name),
            "spawned-copies" or "spawned-deploy-copies" => card.Kind == CardKind.Unit || TextMentionsCard(change.GeneratedText ?? "", card.Name),
            // Heulyn's random Humans were never in the starting deck. Future resurrected
            // Humans need review; a previous original sighting still takes precedence.
            "heulyn" => card.Kind == CardKind.Unit && !card.IsGold && card.Faction == "Skellige" && card.HasCategory("Human"),
            _ => card.CanBeInStartingDeck,
        };
    }
    private static bool TextMentionsCard(string text, string name)
    {
        static string Words(string value) => " " + Regex.Replace(value.ToUpperInvariant(), @"[^\p{L}\p{N}]+", " ").Trim() + " ";
        var source=Words(text);
        return source.Contains(Words(name), StringComparison.Ordinal) || source.Contains(Words(name+"s"),StringComparison.Ordinal);
    }
}
