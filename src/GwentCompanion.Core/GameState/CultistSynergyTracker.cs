using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.GameState;

public sealed record CultistSynergyEstimate(int Boost, bool Activated, bool Approximate, string Reason);

/// <summary>
/// Eclipse Chapter 1 grants independent growing listeners. Only plays trigger them;
/// category infusions and Initiate's enemy damage infusion are different effects.
/// Inferred grants/hidden-zone arrivals remain estimates until their tooltip is read.
/// </summary>
public sealed class CultistSynergyTracker
{
    public const string ScenarioId = "203063";
    private const string DeaconId = "203065";
    private readonly Dictionary<string, Recipient> _recipients = [];
    private readonly List<Scenario> _scenarios = [];
    private readonly List<PendingPlay> _plays = [];
    private readonly HashSet<string> _events = [];
    private readonly HashSet<PlayerSide> _activated = [];
    private readonly Dictionary<PlayerSide, int> _chapterOneCounts = [];
    private string? _session;
    private long _revision = -1;
    private int? _round;

    public void Reset()
    {
        _recipients.Clear(); _scenarios.Clear(); _plays.Clear(); _events.Clear(); _activated.Clear();
        _chapterOneCounts.Clear();
        _session = null; _revision = -1; _round = null;
    }

    public void Observe(GameStateUpdate update, IEnumerable<CardDefinition> catalog)
    {
        if (!update.Accepted || update.After.At is not { } at) return;
        var state = update.After;
        if (_session != state.SessionId) { Reset(); _session = state.SessionId; }
        if (state.Revision <= _revision) return;
        _revision = state.Revision;
        if (_round != state.Round?.Value)
        {
            // Hand/deck infusions survive rounds; board contacts do not prove Resilience.
            foreach (var key in _recipients.Keys.Where(id => state.Cards.FirstOrDefault(c => c.InstanceId == id)
                         ?.Location.Value.Zone is not (CardZone.Hand or CardZone.Deck)).ToArray()) _recipients.Remove(key);
            _scenarios.Clear(); _plays.Clear(); _round = state.Round?.Value;
        }
        Reconcile(state);
        BindScenarios(state);
        var definitions = catalog.Concat(state.Cards.Select(card => card.Card)).DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        foreach (var action in update.Events.Where(item => item.Kind == "PlayPreview" && item.Side is not null &&
                     item.CardId is not null && item.At == at))
        {
            if (!_events.Add(action.Id) || !definitions.TryGetValue(action.CardId!, out var card)) continue;
            if (card.Id == ScenarioId)
            {
                _scenarios.Add(new(action.Side!.Value, action.At));
                BindScenarios(state);
                continue;
            }
            if (card.Kind != CardKind.Unit) continue;
            // Capture listeners before the played card arrives; it cannot trigger itself.
            var listeners = update.Before.Cards.Where(c => Live(c, action.At) && c.Location.Value.Controller == action.Side &&
                    c.InstanceId != action.InstanceId && c.Status(CardStatus.Locked)?.Value != true)
                .Select(c => c.InstanceId).ToHashSet();
            var liveScenarios = _scenarios.Where(s => s.Side == action.Side && s.At < action.At &&
                state.Cards.Any(c => c.InstanceId == s.InstanceId && Live(c, action.At))).ToArray();
            _plays.Add(new(action, card, update.Before.Cards.Select(c => c.InstanceId).ToHashSet(), listeners,
                liveScenarios, state.Cards.Where(c => c.Location.Value.Controller == action.Side &&
                    (Live(c, action.At) || c.Location.Value.Zone is CardZone.Hand or CardZone.Deck)).ToArray()));
        }
        ResolvePlays(state);
        _plays.RemoveAll(play => at - play.Event.At > TimeSpan.FromSeconds(15));
    }

    /// <summary>Only bind an actual repeated tooltip to a unique, recent board contact.</summary>
    public bool ObserveHover(GameStateSnapshot state, CultistInfusionReading reading, DateTimeOffset at, bool inHand)
    {
        if (inHand || _session != state.SessionId || state.At != at || state.Phase != GamePhase.Playing) return false;
        var matches = state.Cards.Where(card => card.Card.Id == reading.CardId && Live(card, at)).ToArray();
        if (matches.Length != 1) return false; // Same-art copies, including across sides, cannot share one tooltip.
        var card = matches[0];
        var entry = Get(card);
        entry.Cultist |= reading.HasCultistCategory;
        // First apply any newly identified Cultist play, then reconcile the post-play values.
        ResolvePlays(state);
        if (reading.Boosts.Length > 0)
        {
            entry.Amounts.Clear(); entry.Amounts.AddRange(reading.Boosts);
            entry.MeasuredAt = at; entry.Approximate = false;
            _activated.Add(card.Location.Value.Controller);
        }
        // Missing OCR is not evidence of Purify. Only explicit status removal clears a grant.
        return true;
    }

    public CultistSynergyEstimate Read(GameStateSnapshot state, PlayerSide side)
    {
        var activated = _session == state.SessionId && _activated.Contains(side);
        if (!activated) return new(0, false, false,
            "The Eternal Eclipse's Chapter 1 has not been confirmed. Playing the scenario only resolves its Prologue; a gold Cultist must advance it.");
        var total = 0;
        var approximate = state.At is not { } || state.Rows.Count(row => row.Side == side && row.Coverage == RowCoverage.Complete &&
            row.ScannedAt is { } scan && scan <= state.At && state.At - scan <= GwentRules.DynamicFactLifetime) != 2;
        var details = new List<string>();
        foreach (var card in state.Cards.Where(card => card.Location.Value.Controller == side && state.At is { } now && Live(card, now)))
        {
            if (!_recipients.TryGetValue(card.InstanceId, out var entry))
            {
                if (card.Card.Kind == CardKind.Unit) approximate = true;
                continue;
            }
            if (card.Status(CardStatus.Locked)?.Value == true) continue;
            if (entry.Amounts.Count == 0) { if (entry.Cultist) approximate = true; continue; }
            var value = entry.Amounts.Sum(); total += value;
            approximate |= entry.Approximate || card.IdentityContinuityUncertain || card.Presence != CardPresence.Visible;
            details.Add($"{card.Card.Name}: +{value}" + (entry.Amounts.Count > 1 ? $" ({string.Join(" + ", entry.Amounts)}; stacked infusions)" : ""));
        }
        return new(total, true, approximate,
            "Chapter 1 activated. Total boost from current unlocked Eclipse recipients when another Cultist is played. " +
            "Bronze: pay the current value, then grow each active infusion by 1. Gold: pay without growing. " +
            (details.Count == 0 ? "No readable active recipient." : string.Join("; ", details) + ".") +
            (approximate ? " Estimate: partial board, unseen grants, or hidden-zone arrivals remain unresolved; hover an unambiguous unit to read its current infusions." : "") +
            " Initiate damage/Deathwish and the played card's own points are excluded.");
    }

    private void Reconcile(GameStateSnapshot state)
    {
        foreach (var pair in _recipients.ToArray())
        {
            var card = state.Cards.FirstOrDefault(c => c.InstanceId == pair.Key);
            if (card is null || card.Card.Id != pair.Value.CardId || card.Location.Value.Controller != pair.Value.Side ||
                card.Location.Value.Zone is not (CardZone.Board or CardZone.Hand or CardZone.Deck) || card.Presence == CardPresence.NotOnBoard)
            { _recipients.Remove(pair.Key); continue; }
            if (card.Status(CardStatus.Infused) is { Value: false, Confidence: >= .8 } removed &&
                removed.At >= pair.Value.MeasuredAt && removed.At >= pair.Value.GrantedAt && removed.At <= state.At)
            {
                pair.Value.Amounts.Clear(); pair.Value.Cultist = card.Card.HasCategory("Cultist");
                pair.Value.MeasuredAt = removed.At;
            }
        }
    }

    private void BindScenarios(GameStateSnapshot state)
    {
        foreach (var scenario in _scenarios.Where(s => s.InstanceId is null))
        {
            var candidates = state.Cards.Where(card => card.Card.Id == ScenarioId && card.Location.Value.Controller == scenario.Side &&
                card.Location.Value.Zone == CardZone.Board && card.FirstSeen >= scenario.At.AddSeconds(-2) &&
                card.FirstSeen <= scenario.At.AddSeconds(15) && !_scenarios.Any(s => s.InstanceId == card.InstanceId)).ToArray();
            if (candidates.Length == 1) scenario.InstanceId = candidates[0].InstanceId;
        }
    }

    private void ResolvePlays(GameStateSnapshot state)
    {
        foreach (var play in _plays.OrderBy(p => p.Event.At))
        {
            var action = play.Event;
            var side = action.Side!.Value;
            var arrivals = state.Cards.Where(c => c.Card.Id == play.Card.Id && c.Location.Value.Controller == side &&
                c.Location.Value.Zone == CardZone.Board && c.FirstSeen <= action.At.AddSeconds(15) &&
                (c.InstanceId == action.InstanceId || !play.BeforeIds.Contains(c.InstanceId) && c.FirstSeen >= action.At)).ToArray();
            var actor = arrivals.Length == 1 ? arrivals[0] : null;
            var cultist = play.Card.HasCategory("Cultist") || actor is not null && _recipients.GetValueOrDefault(actor.InstanceId)?.Cultist == true;
            if (!play.Resolved && cultist)
            {
                play.Resolved = true;
                if (!play.Card.IsGold)
                    foreach (var id in play.Listeners.Where(id => id != actor?.InstanceId))
                        if (_recipients.TryGetValue(id, out var entry) && entry.MeasuredAt < action.At)
                            for (var i = 0; i < entry.Amounts.Count; i++) entry.Amounts[i]++;
                if (play.Card.IsGold)
                    foreach (var scenario in play.LiveScenarios.Where(s => s.Stage < 2))
                    {
                        scenario.Stage++;
                        if (scenario.Stage == 1)
                        {
                            _activated.Add(side); play.Granted++;
                            _chapterOneCounts[side] = _chapterOneCounts.GetValueOrDefault(side) + 1;
                            foreach (var card in play.GrantTargets)
                                if (IsCultist(card) && Eligible(card)) Grant(card, action.At);
                            if (actor is not null) play.GrantedActor = actor.InstanceId;
                        }
                        else
                        {
                            // Chapter 2's generated Deacon did not exist at Chapter 1.
                            scenario.DeaconAt = action.At;
                        }
                    }
            }
            if (!play.Generated && play.Card.Id == DeaconId && _scenarios.FirstOrDefault(s => s.Side == side &&
                    s.DeaconPlayId is null && s.DeaconAt is { } spawned && action.At >= spawned &&
                    action.At - spawned <= TimeSpan.FromSeconds(15)) is { } generatingScenario)
            { play.Generated = true; generatingScenario.DeaconPlayId = action.Id; }
            if (actor is null || !play.Resolved || play.Entered) continue;
            play.Entered = true;
            if (!Eligible(actor)) continue;
            if (play.Granted > 0 && play.GrantedActor != actor.InstanceId)
                for (var i = 0; i < play.Granted; i++) Grant(actor, action.At);
            else if (play.Granted == 0 && _activated.Contains(side) && !play.Generated &&
                     actor.Card.HasCategory("Cultist") && !_recipients.ContainsKey(actor.InstanceId) &&
                     actor.Origin?.Value is null or CardProvenance.Unknown or CardProvenance.ConfirmedStartingDeck or CardProvenance.ProbableStartingDeck)
            {
                // Normal played Cultists may have been in hand/deck at activation.
                // This is explicitly approximate, never copied from the older board's counters.
                for (var i = 0; i < Math.Max(1, _chapterOneCounts.GetValueOrDefault(side)); i++) Grant(actor, action.At);
            }
        }
    }

    private bool IsCultist(GameCardInstance card) => card.Card.HasCategory("Cultist") || _recipients.GetValueOrDefault(card.InstanceId)?.Cultist == true;
    private static bool Eligible(GameCardInstance card) => card.Card.Kind == CardKind.Unit &&
        !LiveSynergyMeter.DeclaresMechanic(card.Card.AbilityText, "Disloyal") && card.Status(CardStatus.Veil)?.Value != true;
    private static bool Live(GameCardInstance card, DateTimeOffset at) => card.Location.Value.Zone == CardZone.Board &&
        card.Presence is CardPresence.Visible or CardPresence.LastKnown && card.LastSeen <= at &&
        at - card.LastSeen <= GwentRules.DynamicFactLifetime;
    private Recipient Get(GameCardInstance card)
    {
        if (!_recipients.TryGetValue(card.InstanceId, out var result))
            _recipients[card.InstanceId] = result = new(card.Card.Id, card.Location.Value.Controller, card.Card.HasCategory("Cultist"));
        return result;
    }
    private void Grant(GameCardInstance card, DateTimeOffset at)
    {
        var entry = Get(card);
        if (entry.MeasuredAt >= at) return; // Post-effect tooltip already reconciled this play.
        entry.Amounts.Add(1); entry.Approximate = true; entry.GrantedAt = at;
    }
    private sealed class Recipient(string cardId, PlayerSide side, bool cultist)
    {
        public string CardId { get; } = cardId;
        public PlayerSide Side { get; } = side;
        public bool Cultist { get; set; } = cultist;
        public List<int> Amounts { get; } = [];
        public DateTimeOffset MeasuredAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.MinValue;
        public bool Approximate { get; set; }
    }
    private sealed class Scenario(PlayerSide side, DateTimeOffset at)
    {
        public PlayerSide Side { get; } = side;
        public DateTimeOffset At { get; } = at;
        public string? InstanceId { get; set; }
        public int Stage { get; set; }
        public DateTimeOffset? DeaconAt { get; set; }
        public string? DeaconPlayId { get; set; }
    }
    private sealed class PendingPlay(GameStateEvent action, CardDefinition card, HashSet<string> beforeIds, HashSet<string> listeners,
        Scenario[] liveScenarios, GameCardInstance[] grantTargets)
    {
        public GameStateEvent Event { get; } = action;
        public CardDefinition Card { get; } = card;
        public HashSet<string> BeforeIds { get; } = beforeIds;
        public HashSet<string> Listeners { get; } = listeners;
        public Scenario[] LiveScenarios { get; } = liveScenarios;
        public GameCardInstance[] GrantTargets { get; } = grantTargets;
        public bool Resolved { get; set; }
        public bool Entered { get; set; }
        public bool Generated { get; set; }
        public int Granted { get; set; }
        public string? GrantedActor { get; set; }
    }
}
