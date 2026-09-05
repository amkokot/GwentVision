using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.GameState;

public sealed record ZoneHypothesis(string CardId, PlayerSide Side, CardZone Zone, int Copies,
    bool Reviewed, DateTimeOffset At, string Reason);

/// <summary>Small identity/copy ledger, separate from facts. Round cleanup is inferred, never mislabeled as an inspection.
/// Missing artwork alone cannot destroy a card. Inspections merge page identities rather than adding copies.</summary>
public sealed class ZoneInventoryTracker
{
    private string? _session;
    private int? _round;
    private readonly Dictionary<(PlayerSide, string), (int Count, CardDefinition Card, bool? Doomed, bool? Resilience)> _roundBoard = [];
    private readonly Dictionary<(PlayerSide, string, CardZone), ZoneHypothesis> _entries = [];
    private readonly HashSet<string> _events = [];
    private readonly Dictionary<(PlayerSide, string), DateTimeOffset> _lastArrivals = [];
    public IReadOnlyList<ZoneHypothesis> Entries => _entries.Values.OrderBy(entry => entry.Side).ThenBy(entry => entry.CardId).ToArray();
    public void Reset()
    { _session = null; _round = null; _roundBoard.Clear(); _entries.Clear(); _events.Clear(); _lastArrivals.Clear(); }
    public void Observe(GameStateUpdate update)
    {
        if (!update.Accepted || update.After.At is not { } at) return;
        var state = update.After;
        if (_session != state.SessionId) { Reset(); _session = state.SessionId; }
        if (_roundBoard.Count > 0 && state.Round?.Value > 1 && (_round is null || state.Round.Value > _round))
        {
            foreach (var (key, value) in _roundBoard)
            {
                if (value.Resilience == true) continue;
                var zone = value.Doomed == true ? CardZone.Banished : CardZone.Graveyard;
                var count = Math.Max(value.Count, _entries.GetValueOrDefault((key.Item1, key.Item2, zone))?.Copies ?? 0);
                Set(key.Item1, key.Item2, zone, count, false, at,
                    "Inferred round cleanup; not a full inventory. Unknown Resilience/Doomed, earlier removal, replay or theft may change destination/copies.");
            }
            _roundBoard.Clear();
        }
        _round = state.Round?.Value ?? _round;
        if (!state.BoardObscured && state.Phase == GamePhase.Playing)
        foreach (var group in state.Cards.Where(card => card.Presence == CardPresence.Visible && card.LastSeen == at)
                     .GroupBy(card => (card.Location.Value.Controller, card.Card.Id)))
        {
            var visible = group.ToArray(); var first = visible[0]; var key = group.Key;
            var oldCount = _roundBoard.GetValueOrDefault(key).Count;
            bool? Known(CardStatus status) => visible.All(card => card.Status(status)?.Value == true) ? true :
                visible.All(card => card.Status(status)?.Value == false) ? false : null;
            _roundBoard[key] = (Math.Max(oldCount, visible.Length), first.Card, Known(CardStatus.Doomed), Known(CardStatus.Resilience));
            // A newly observed arrival can consume ONE previously inferred graveyard copy. Ambiguous reacquisition does not add another.
            if (oldCount == 0 && _entries.TryGetValue((key.Controller, key.Id, CardZone.Graveyard), out var grave))
            {
                Set(key.Controller, key.Id, CardZone.Graveyard, Math.Max(0, grave.Copies - visible.Length), false, at,
                    "Identity seen back on board; may be a resurrection or another copy.");
                _lastArrivals[key] = at;
            }
        }
        foreach (var action in update.Events.Where(item => item.Kind == "PlayPreview" && item.Side is not null && item.CardId is not null))
        {
            if (!_events.Add(state.SessionId + "/" + action.Id)) continue;
            // Identity itself may be a summon/replay; the next visual board and reviews resolve the route.
            _lastArrivals[(action.Side!.Value, action.CardId!)] = at;
        }
        foreach (var evidence in state.ZoneEvidence.Where(item => item.Side is not null))
        {
            var key = (evidence.Side!.Value, evidence.CardId, evidence.Zone);
            if (_lastArrivals.GetValueOrDefault((key.Value, key.CardId)) > evidence.LastSeen) continue;
            if (_entries.TryGetValue(key, out var old) && old.At >= evidence.LastSeen) continue;
            Set(key.Value, key.CardId, key.Zone, evidence.MinimumCopies, evidence.Source.Contains("review", StringComparison.OrdinalIgnoreCase),
                evidence.LastSeen, evidence.Source + " · observed identity, inventory may still be partial.");
        }
        if (_events.Count > 512) _events.Clear();
        foreach (var key in _entries.OrderByDescending(pair => pair.Value.At).Skip(256).Select(pair => pair.Key).ToArray()) _entries.Remove(key);
    }
    public void ObserveSpecial(CardDefinition card, PlayerSide side, DateTimeOffset at)
    {
        if (card.Kind != CardKind.Special) return;
        var old = _entries.GetValueOrDefault((side, card.Id, CardZone.Graveyard));
        Set(side, card.Id, CardZone.Graveyard, Math.Max(1, old?.Copies ?? 0), false, at,
            "Expected graveyard after special resolves; later banish/Echo/replay can change this.");
    }
    public void Review(PlayerSide side, string cardId, CardZone zone, int copies, DateTimeOffset at)
    {
        if (zone is not CardZone.Graveyard and not CardZone.Deck and not CardZone.Banished) throw new ArgumentException("Review a deck, graveyard or banished zone.");
        Set(side, cardId, zone, Math.Clamp(copies, 0, 10), true, at, "Manual current-zone correction; copies explicitly reviewed.");
        if (zone is CardZone.Deck or CardZone.Banished)
            Set(side, cardId, CardZone.Graveyard, 0, true, at, "Manual return/banishment supersedes inferred graveyard membership.");
    }
    private void Set(PlayerSide side, string id, CardZone zone, int copies, bool reviewed, DateTimeOffset at, string reason) =>
        _entries[(side, id, zone)] = new(id, side, zone, copies, reviewed, at, reason);
}
