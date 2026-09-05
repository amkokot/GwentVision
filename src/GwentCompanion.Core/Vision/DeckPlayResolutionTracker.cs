using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

/// <summary>A fully recognized choice page, never itself a play or original-copy claim.</summary>
public sealed record DeckPlayChoiceReading(IReadOnlyList<CardDefinition> Cards, bool Complete = true,
    CardDefinition? HighlightedCard = null);

/// <summary>
/// Recovers skipped multi-card tutor animations from independent source, choice and
/// HUD evidence. Named "play all copies ... from your deck" rules share this path.
/// Mixed/incomplete choices, browsing, draws and interrupted resolutions abstain.
/// Origin/mutation rules remain authoritative downstream: deck does not mean original.
/// </summary>
public sealed class DeckPlayResolutionTracker(IReadOnlyList<CardDefinition> catalog)
{
    public Action<string>? Trace { get; set; }
    private Pending? _pending;
    private GenericPending? _generic;
    private (int Count, int Hand, DateTimeOffset At)? _lastDeck;
    private DateTimeOffset? _last;
    public static bool IsPlayChoiceHeader(string? header) => string.Concat((header ?? "").Where(char.IsLetter)).ToUpperInvariant()
        is "PICKACARDTOPLAY" or "PICKCARDTOPLAY";
    private static bool PartialPlayChoiceHeader(string header)
    {
        var text=string.Concat(header.Where(char.IsLetter)).ToUpperInvariant();
        return text.Length>=4 && ("PICKACARDTOPLAY".StartsWith(text,StringComparison.Ordinal) ||
            "PICKCARDTOPLAY".StartsWith(text,StringComparison.Ordinal));
    }
    public static IReadOnlyList<CardDefinition> NamedTargets(CardDefinition source, IReadOnlyList<CardDefinition> catalog)
    {
        var match = Regex.Match(source.AbilityText ?? "", @"\bPlay all copies of (.+?) from your deck\b", RegexOptions.IgnoreCase);
        if (!match.Success) return [];
        var names = Regex.Split(match.Groups[1].Value, @",\s*(?:or\s+)?|\s+or\s+", RegexOptions.IgnoreCase)
            .Select(name => name.Trim()).Where(name => name.Length > 0).ToArray();
        var cards = names.Select(name => catalog.FirstOrDefault(card => card.CanBeInStartingDeck &&
            card.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        return cards.Length > 0 && cards.All(card => card is not null) ? cards.Select(card => card!).ToArray() : [];
    }

    public IReadOnlyList<VisionEvidenceEvent> Observe(DateTimeOffset at, GwentVisualObservation screen,
        IReadOnlyList<VisionEvidenceEvent> events, DeckPlayChoiceReading? choices)
    {
        if (_last is { } last && at <= last) return [];
        _last = at;
        if (_pending is { } expired && at - expired.At > TimeSpan.FromSeconds(40)) _pending = null;
        if (_generic is { } genericExpired && at - genericExpired.At > TimeSpan.FromSeconds(25)) _generic = null;
        if (screen.IsCardSelectionOverlay && screen.ScreenHeader is { Length: > 0 } header && !PartialPlayChoiceHeader(header))
        {
            if (_pending is not null) Trace?.Invoke($"{at:HH:mm:ss.fff} cancelled by header {header}");
            _pending = null; _generic = null;
        }

        foreach (var action in events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview))
        {
            var sight = action.Sighting;
            if(_pending is { } resolving && sight.Side==PlayerSide.User && resolving.TargetIds.Contains(sight.Card.Id) &&
                action.ObservedAt>resolving.At)
                continue;
            // Any other recognized action may account for a deck change. Do not
            // assign a later draw/tutor to the earlier choice by coincidence.
            _pending = null; _generic = null;
            var targets = NamedTargets(sight.Card, catalog);
            if (sight.Side == PlayerSide.User && targets.Count > 0 && _lastDeck is { } prior &&
                at - prior.At <= TimeSpan.FromSeconds(5))
            {
                _pending = new(sight.Card, targets.Select(card => card.Id).ToHashSet(), at, prior.Count, prior.Hand, true);
                Trace?.Invoke($"{at:HH:mm:ss.fff} source {sight.Card.Name}: {prior.Count} deck / {prior.Hand} hand");
            }
            else if (sight.Side == PlayerSide.User && targets.Count == 0 && IsGenericDeckPlaySource(sight.Card))
            {
                _generic = new(sight.Card, at, _lastDeck?.Count, _lastDeck?.Hand);
                Trace?.Invoke($"{at:HH:mm:ss.fff} generic deck-play source {sight.Card.Name}");
            }
        }
        // The source animation can be shorter than the retained cadence. A repeated,
        // complete multi-copy choice page plus an exact one-card hand decrement and
        // matching deck decrement can recover it when exactly one catalog card names
        // that target in a "play all copies" rule (for example Tempest). Merely opening
        // or browsing the page still emits nothing.
        if (_pending is null && screen.IsCardSelectionOverlay && choices is { Complete:true, Cards.Count:>=2 and <=25 } &&
            choices.Cards.Select(card=>card.Id).Distinct().Count()==1 && _lastDeck is { } before && at-before.At<=TimeSpan.FromSeconds(5))
        {
            var target=choices.Cards[0];
            var candidates=catalog.Where(source=>NamedTargets(source,catalog).Any(card=>card.Id==target.Id)).ToArray();
            if(candidates.Length==1)
            {
                var source=candidates[0];
                _pending=new(source,NamedTargets(source,catalog).Select(card=>card.Id).ToHashSet(),at,before.Count,before.Hand,false);
                Trace?.Invoke($"{at:HH:mm:ss.fff} source animation absent; unique named choice candidate {source.Name}: {before.Count} deck / {before.Hand} hand");
            }
        }
        if (_generic is { } generic)
        {
            if (screen.IsCardSelectionOverlay)
            {
                if (choices?.HighlightedCard is { } highlighted && CanSelectFromDeck(generic.Source, highlighted))
                {
                    generic.Confirmed = generic.Card?.Id == highlighted.Id && at > generic.ChoiceAt &&
                        at - generic.ChoiceAt <= TimeSpan.FromSeconds(2);
                    generic.Card = highlighted; generic.ChoiceAt = at;
                    Trace?.Invoke($"{at:HH:mm:ss.fff} highlighted deck choice {highlighted.Name}; confirmed={generic.Confirmed}");
                }
                else if (choices?.HighlightedCard is not null)
                {
                    generic.Card = null; generic.Confirmed = false; generic.ChoiceAt = default;
                }
            }
            else if (generic.Confirmed && generic.Card is { } selected && at > generic.ChoiceAt &&
                at - generic.ChoiceAt <= TimeSpan.FromSeconds(6) &&
                generic.Deck is { } beforeDeck && screen.UserDeckCount == beforeDeck - 1 &&
                generic.Hand is { } beforeHand && screen.UserHandCount is { } hand &&
                hand <= beforeHand && hand >= beforeHand - 1)
            {
                _generic = null;
                return [new(at, new(selected, PlayerSide.User, CardSightSource.PlayPreview,
                    new(.78, .20, .98, .50), .04, 1,
                    "Repeated exact title plus resolved deck-play conservation"),
                    $"{generic.Source.Name}: the highlighted {selected.Name} title was read on repeated selection frames and the mandatory play was independently confirmed by a {beforeDeck}→{screen.UserDeckCount} deck decrement without a draw.", 1)];
            }
        }
        if (_pending is { } pending)
        {
            // A tutored source and an automatic summon can leave the deck before
            // its choices appear. Prefer a newer independently confirmed HUD count
            // from that interval; never subtract guessed costs from a larger drop.
            if (!screen.IsCardSelectionOverlay && pending.Key is null && screen.MatchHudVisible == true &&
                screen.UserDeckCount is { } beforeChoiceDeck && screen.UserHandCount is { } beforeChoiceHand)
            {
                if (beforeChoiceDeck > pending.Deck || beforeChoiceHand > pending.Hand || beforeChoiceHand < pending.Hand - 1)
                    _pending = null;
                else pending.Deck = beforeChoiceDeck;
            }
            if (_pending is not null && screen.IsCardSelectionOverlay)
            {
                if (choices is { Cards.Count: > 0 } && choices.Cards.Count < pending.Cards.Count &&
                    choices.Cards.All(card=>card.Id==pending.Cards[0].Id) && at-pending.ChoiceAt<=TimeSpan.FromSeconds(3))
                {
                    // A disappearing card rectangle is not a new complete page.
                    // Retain the larger page vote briefly without refreshing it;
                    // matching HUD decrement remains mandatory for any resolution.
                }
                else if (choices is { Complete: true, Cards.Count: > 0 and <= 25 } && choices.Cards.All(card => pending.TargetIds.Contains(card.Id)) &&
                    choices.Cards.Select(card => card.Id).Distinct().Count() == 1)
                {
                    var key = choices.Cards[0].Id + ":" + choices.Cards.Count;
                    pending.Confirmed = pending.Key == key && at > pending.ChoiceAt && at - pending.ChoiceAt <= TimeSpan.FromSeconds(3);
                    pending.Key = key; pending.ChoiceAt = at; pending.Cards = choices.Cards;
                    Trace?.Invoke($"{at:HH:mm:ss.fff} choice {key}; confirmed={pending.Confirmed}");
                }
                else if (choices is { Complete: true } || choices?.Cards.Any(card =>
                    !pending.TargetIds.Contains(card.Id) || pending.Cards.Count > 0 && card.Id != pending.Cards[0].Id) == true ||
                    at - pending.ChoiceAt > TimeSpan.FromSeconds(3))
                { Trace?.Invoke($"{at:HH:mm:ss.fff} contrary or expired choice"); pending.Confirmed = false; pending.Key = null; }
                // A short fade/unreadable scan does not contradict a complete,
                // repeated page. It also never refreshes its timestamp or copy count.
            }
            else if (_pending is not null && screen.MatchHudVisible == true && pending.Confirmed && at > pending.ChoiceAt &&
                at - pending.ChoiceAt <= TimeSpan.FromSeconds(15) && screen.UserDeckCount is { } deck &&
                screen.UserHandCount is { } hand)
            {
                // A source can be tutored rather than played from hand; at most its
                // own hand decrement is allowed. Any draw invalidates this inference.
                if (hand > pending.Hand || hand < pending.Hand - 1 || deck > pending.Deck) _pending = null;
                else if (pending.Deck - deck == pending.Cards.Count)
                {
                    _pending = null;
                    _lastDeck = (deck, hand, at);
                    var card = pending.Cards[0];
                    var targetEvent=new VisionEvidenceEvent(at, new(card, PlayerSide.User, CardSightSource.PlayPreview,
                        new(.815, .413, .918, .665), .08, 1,
                        "Resolved named deck tutor; independent choice and repeated HUD evidence"),
                        $"{pending.Source.Name}: {pending.Cards.Count} identical named targets recognized on repeated choice pages, followed by a confirmed {pending.Deck}→{deck} deck decrement. Deck origin established; original membership still subject to mutation checks.",
                        pending.Cards.Count);
                    if(pending.SourceObserved) return [targetEvent];
                    var sourceEvent=new VisionEvidenceEvent(at,new(pending.Source,PlayerSide.User,CardSightSource.PlayPreview,
                        new(.30,.58,.48,.96),.08,1,"Recovered unique named deck-play source from complete choice/counter conservation"),
                        $"{pending.Source.Name}: source animation was missed, but repeated complete named choices plus exact one-card hand and {pending.Cards.Count}-card deck decrements uniquely identify the initiating play.");
                    return [sourceEvent,targetEvent];
                }
                else if (deck != pending.Deck) _pending = null;
            }
        }
        if (!screen.IsCardSelectionOverlay && screen.MatchHudVisible == true && screen.UserDeckCount is { } current && screen.UserHandCount is { } currentHand)
            _lastDeck = (current, currentHand, at);
        return [];
    }
    public static bool IsGenericDeckPlaySource(CardDefinition source) => Regex.IsMatch(source.AbilityText ?? "",
        @"\bPlay\b[^.\n]*\bfrom your deck\b", RegexOptions.IgnoreCase);
    public static bool CanSelectFromDeck(CardDefinition source, CardDefinition target)
    {
        if (!IsGenericDeckPlaySource(source)) return false;
        var text = source.AbilityText ?? "";
        if (Regex.IsMatch(text, @"\b(?:a|an) unit\b[^.\n]*\bfrom your deck\b", RegexOptions.IgnoreCase) && target.Kind != CardKind.Unit) return false;
        if (Regex.IsMatch(text, @"\b(?:a|an) special\b[^.\n]*\bfrom your deck\b", RegexOptions.IgnoreCase) && target.Kind != CardKind.Special) return false;
        if (Regex.IsMatch(text, @"\b(?:a|an) artifact\b[^.\n]*\bfrom your deck\b", RegexOptions.IgnoreCase) && target.Kind != CardKind.Artifact) return false;
        if (Regex.IsMatch(text, @"\bbronze\b[^.\n]*\bfrom your deck\b", RegexOptions.IgnoreCase) && target.IsGold) return false;
        var qualifiedCard = Regex.Match(text,
            @"\bPlay\s+(?:a|an)\s+(?<qualifier>[^.\n]+?)\s+card\s+from your deck\b", RegexOptions.IgnoreCase);
        if (qualifiedCard.Success)
        {
            var qualifier = qualifiedCard.Groups["qualifier"].Value.Trim();
            if (Regex.IsMatch(qualifier, @"\bunit\b", RegexOptions.IgnoreCase) && target.Kind != CardKind.Unit) return false;
            if (Regex.IsMatch(qualifier, @"\bspecial\b", RegexOptions.IgnoreCase) && target.Kind != CardKind.Special) return false;
            if (Regex.IsMatch(qualifier, @"\bartifact\b", RegexOptions.IgnoreCase) && target.Kind != CardKind.Artifact) return false;
            if (Regex.IsMatch(qualifier, @"\bbronze\b", RegexOptions.IgnoreCase) && target.IsGold) return false;
            if (Regex.IsMatch(qualifier, @"\bgold\b", RegexOptions.IgnoreCase) && !target.IsGold) return false;
            foreach (var faction in new[] { "Scoia'tael", "Nilfgaard", "Monsters", "Northern Realms", "Skellige", "Syndicate", "Neutral" })
            {
                if (!Regex.IsMatch(qualifier, @"\b" + Regex.Escape(faction) + @"\b", RegexOptions.IgnoreCase)) continue;
                if (faction == "Neutral" ? target.Faction != "Neutral" : !FactionCompatibility.IsPlayableBy(target, faction)) return false;
                qualifier = Regex.Replace(qualifier, @"\b" + Regex.Escape(faction) + @"\b", "", RegexOptions.IgnoreCase);
            }
            qualifier = Regex.Replace(qualifier, @"\b(?:any|random|bronze|gold|unit|special|artifact|non-Neutral)\b", "", RegexOptions.IgnoreCase).Trim(' ', '-');
            if (qualifier.Length > 0 && !target.HasCategory(qualifier)) return false;
        }
        return target.CanBeInStartingDeck;
    }
    public void Reset() { _pending = null; _generic = null; _lastDeck = null; _last = null; }
    private sealed class Pending(CardDefinition source, HashSet<string> targets, DateTimeOffset at, int deck, int hand, bool sourceObserved)
    {
        public CardDefinition Source { get; } = source;
        public HashSet<string> TargetIds { get; } = targets;
        public DateTimeOffset At { get; } = at;
        public int Deck { get; set; } = deck;
        public int Hand { get; } = hand;
        public bool SourceObserved { get; } = sourceObserved;
        public string? Key { get; set; }
        public DateTimeOffset ChoiceAt { get; set; }
        public bool Confirmed { get; set; }
        public IReadOnlyList<CardDefinition> Cards { get; set; } = [];
    }
    private sealed class GenericPending(CardDefinition source, DateTimeOffset at, int? deck, int? hand)
    {
        public CardDefinition Source { get; } = source;
        public DateTimeOffset At { get; } = at;
        public int? Deck { get; } = deck;
        public int? Hand { get; } = hand;
        public CardDefinition? Card { get; set; }
        public DateTimeOffset ChoiceAt { get; set; }
        public bool Confirmed { get; set; }
    }
}
