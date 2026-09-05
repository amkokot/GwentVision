using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.GameState;

/// <summary>
/// Chronological, single-writer reconstruction from public pixels. Simulation never writes here.
/// A missed detection, history entry, preview or guessed deck card cannot move a card to a zone.
/// </summary>
public sealed class GameStateTracker
{
    public const int MaximumContacts = 256;
    public const int MaximumRecentEvents = 128;
    private int _nextContact;
    private int? _pendingRound;
    private int _roundVotes;
    private readonly Dictionary<string, GameCardInstance> _cards = [];
    private readonly Dictionary<(PlayerSide?, CardZone, string), ZoneIdentityEvidence> _zones = [];
    private readonly Queue<GameStateEvent> _events = new();
    public GameStateSnapshot Current { get; private set; } = Empty("unstarted");

    public void Reset(string sessionId, DeckDefinition? userDeck = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _cards.Clear(); _zones.Clear(); _events.Clear(); _nextContact = 0; _pendingRound = null; _roundVotes = 0;
        Current = Empty(sessionId) with { User = new(PlayerSide.User, StartingDeckReference: userDeck) };
    }

    private static GameStateSnapshot Empty(string sessionId) => new(1, GwentRules.Version, sessionId, 0, null,
        GamePhase.Unknown, null, null, new(PlayerSide.User), new(PlayerSide.Opponent), [], EmptyRows(), [], [],
        ["Artwork recognition is partial; a scanned row is not necessarily a fully enumerated row.",
         "Printed power is catalog metadata, not measured current power. Unread numbers/statuses remain unknown.",
         "Contact identity across movement/copies is heuristic. Missing cards are not assumed dead or in the graveyard.",
         "Previews and history are evidence of actions, not settled board state; unmodeled triggered effects remain unresolved."]);

    private static ImmutableArray<GameRowState> EmptyRows() => Enum.GetValues<PlayerSide>()
        .SelectMany(side => Enum.GetValues<BoardRow>().Select(row => new GameRowState(side, row, RowCoverage.Unknown, null, [], [])))
        .ToImmutableArray();

    public GameStateUpdate Observe(VisualGameStateFrame frame)
    {
        var before = Current;
        // Duplicate or delayed frames cannot replay a play, refresh evidence, or roll back the board.
        if (before.At is { } previous && frame.At <= previous) return new(before, before, [], false);
        var changes = new List<GameStateEvent>();
        var screen = frame.Screen;
        var obscured = screen.IsCardSelectionOverlay || screen.View == GwentViewKind.MoveHistory || screen.HasCardTooltip;
        var fullyObscured = screen.IsCardSelectionOverlay || screen.View == GwentViewKind.MoveHistory ||
            screen.HasCardTooltip && screen.TooltipRegion is null;
        bool Covered(NormalizedRegion region) => screen.HasCardTooltip && screen.TooltipRegion is { } tip &&
            region.Left < tip.Right + .02 && region.Right > tip.Left - .02 &&
            region.Top < tip.Bottom + .02 && region.Bottom > tip.Top - .12;
        var at = frame.At;
        void Add(string kind, PlayerSide? side, string? card, string? instance, string detail) =>
            changes.Add(new($"{before.Revision + 1}:{changes.Count}", at, kind, side, card, instance, detail));
        var round = before.Round;
        var phase = screen.IsCardSelectionOverlay ? GamePhase.Selection : before.Phase;
        var header = (screen.ScreenHeader ?? "").Trim().ToUpperInvariant();
        if (header == "FINAL ROUND") header = "ROUND 3";
        var roundMatch = Regex.Match(header, @"^ROUND\s+([123])$");
        if (roundMatch.Success)
        {
            var number = int.Parse(roundMatch.Groups[1].Value);
            _roundVotes = _pendingRound == number ? _roundVotes + 1 : 1; _pendingRound = number;
            if (_roundVotes >= 2 && (round is null || number > round.Value))
                round = Fact(number, at, .9, "Repeated visible round heading");
            phase = GamePhase.RoundTransition;
        }
        else { _pendingRound = null; _roundVotes = 0; }
        if (frame.Measurements?.Round is { Value: >= 1 and <= 3 } reviewedRound && reviewedRound.At <= at &&
            (round is null || reviewedRound.Value >= round.Value)) round = reviewedRound;
        if (header is "VICTORY" or "DEFEAT" or "DRAW" or "GAME OVER" || before.Phase == GamePhase.Ended) phase = GamePhase.Ended;
        var newRound = round?.Value != before.Round?.Value;
        var rows = before.Rows;
        if (newRound)
        {
            Add("RoundObserved", null, null, null, $"Round {round!.Value}; old contacts require new visual confirmation, including possible Resilience.");
            foreach (var card in _cards.Values.ToArray()) _cards[card.InstanceId] = card with { Presence = CardPresence.Uncertain };
            rows = EmptyRows();
        }

        var user = Merge(before.User, frame.Measurements?.User, at);
        var opponent = Merge(before.Opponent, frame.Measurements?.Opponent, at);
        if (frame.Measurements is { ReplaceStartingMetadata: true } metadata)
        {
            // An authoritative context refresh can explicitly clear a retracted reference/review.
            // Null dynamic measurements still mean "not scanned", not reset-to-zero.
            if (metadata.User is { } own) user = ReplaceMetadata(user, own, at);
            if (metadata.Opponent is { } other) opponent = ReplaceMetadata(opponent, other, at);
        }
        if (screen.UserHandCount is >= 0 and <= GwentRules.MaximumHandSize)
            user = user with { HandCount = Fact(screen.UserHandCount.Value, at, .9, "Repeated player hand HUD OCR") };
        if (screen.OpponentHandCount is >= 0 and <= GwentRules.MaximumHandSize)
            opponent = opponent with { HandCount = Fact(screen.OpponentHandCount.Value, at, .9, "Repeated opponent hand HUD OCR") };
        if (screen.OpponentDeckCount is >= 0 and <= 100)
            opponent = opponent with { DeckCount = Fact(screen.OpponentDeckCount.Value, at, .9, "Repeated opponent deck HUD OCR") };
        if (screen.UserDeckCount is >= 0 and <= 100)
            user = user with { DeckCount = Fact(screen.UserDeckCount.Value, at, .9, "Repeated player deck HUD OCR") };
        if (screen.UserScore is >= 0 and <= 9999)
            user = user with { Score = Fact(screen.UserScore.Value, at, .9, "Repeated player scoreboard OCR") };
        if (screen.OpponentScore is >= 0 and <= 9999)
            opponent = opponent with { Score = Fact(screen.OpponentScore.Value, at, .9, "Repeated opponent scoreboard OCR") };
        if (screen.UserCoins is >= 0 and <= GwentRules.MaximumCoins)
            user = user with { Coins = Fact(screen.UserCoins.Value, at, .9, "Repeated player coin HUD OCR") };
        if (screen.OpponentCoins is >= 0 and <= GwentRules.MaximumCoins)
            opponent = opponent with { Coins = Fact(screen.OpponentCoins.Value, at, .9, "Repeated opponent coin HUD OCR") };

        var boardUsable = frame.BoardWasScanned && !fullyObscured &&
            phase is not GamePhase.Ended and not GamePhase.RoundTransition;
        // A disappearing transition header is not by itself proof that play resumed.
        if (!roundMatch.Success && phase != GamePhase.Ended && !screen.IsCardSelectionOverlay &&
            (screen.OpponentHandCount is not null || frame.Sightings.Any(item => item.Source == CardSightSource.Board)))
        { phase = GamePhase.Playing; boardUsable = frame.BoardWasScanned && !fullyObscured; }

        if (boardUsable)
        {
            var sightings = new List<CardSighting>();
            foreach (var sight in frame.Sightings.Where(item => item.Source == CardSightSource.Board &&
                         item.Card.Kind is CardKind.Unit or CardKind.Artifact or CardKind.Stratagem &&
                         double.IsFinite(item.Distance) && item.Distance is >= 0 and <= .4 && Valid(item.Region) && !Covered(item.Region))
                         .OrderBy(item => item.Distance))
                if (!sightings.Any(other => other.Side == sight.Side && Overlap(other.Region, sight.Region) > .4)) sightings.Add(sight);

            var matched = new HashSet<string>();
            var visible = new List<GameCardInstance>();
            // Pair spatially closest contacts first; never assign two simultaneous copies to one contact.
            var pairs = sightings.SelectMany((sight, index) => _cards.Values.Where(card =>
                    card.Card.Id == sight.Card.Id && card.Location.Value.Controller == sight.Side &&
                    card.Location.Value.Zone == CardZone.Board && card.Presence != CardPresence.NotOnBoard &&
                    !newRound && at - card.LastSeen <= TimeSpan.FromSeconds(90))
                .Select(card => (Index: index, Card: card, Distance: Distance(card.Location.Value.Region, sight.Region))))
                .Where(pair => pair.Distance <= .22).OrderBy(pair => pair.Distance).ToArray();
            var assignments = new Dictionary<int, GameCardInstance>();
            foreach (var pair in pairs)
                if (!assignments.ContainsKey(pair.Index) && matched.Add(pair.Card.InstanceId)) assignments[pair.Index] = pair.Card;
            for (var i = 0; i < sightings.Count; i++)
            {
                var sight = sightings[i];
                assignments.TryGetValue(i, out var old);
                var location = new CardLocation(sight.Side, CardZone.Board, LocateRow(sight.Side, sight.Region), sight.Region);
                var ambiguous = sightings.Count(other => other.Side == sight.Side && other.Card.Id == sight.Card.Id) > 1 ||
                    (old is not null && at - old.LastSeen > TimeSpan.FromSeconds(3));
                var card = old is null
                    ? new GameCardInstance($"contact-{++_nextContact:D5}", sight.Card, Fact(location, at, 1 - sight.Distance, "Visible artwork/position; row geometry inferred"),
                        at, at, CardPresence.Visible, Statuses: [], IdentityContinuityUncertain: ambiguous)
                    : old with { Location = Fact(location, at, 1 - sight.Distance, "Visible artwork/position; row geometry inferred"), LastSeen = at,
                        Presence = CardPresence.Visible, IdentityContinuityUncertain = old.IdentityContinuityUncertain || ambiguous };
                card = ApplyMeasurements(card, frame.Measurements?.Cards, at);
                _cards[card.InstanceId] = card; matched.Add(card.InstanceId); visible.Add(card);
                if (old is null) Add("BoardContact", sight.Side, sight.Card.Id, card.InstanceId,
                    "Visible on board; play/summon/create/transform/theft route and original owner unresolved.");
                else if (old.Location.Value.Row != location.Row)
                    Add("RowChanged", sight.Side, sight.Card.Id, card.InstanceId, "Same-identity visual contact changed row; not proof of a particular movement ability.");
            }
            foreach (var old in _cards.Values.Where(card => !matched.Contains(card.InstanceId)).ToArray())
            {
                var presence = at - old.LastSeen >= TimeSpan.FromSeconds(3) ? CardPresence.Uncertain : CardPresence.LastKnown;
                if (presence == CardPresence.Uncertain && old.Presence != CardPresence.Uncertain)
                    Add("ContactLost", old.Location.Value.Controller, old.Card.Id, old.InstanceId, "Not re-detected; destination, destruction and continuing presence unknown.");
                _cards[old.InstanceId] = old with { Presence = presence };
            }
            rows = Enum.GetValues<PlayerSide>().SelectMany(side => Enum.GetValues<BoardRow>().Select(row =>
            {
                var measurement = frame.Measurements?.Rows?.FirstOrDefault(item => item.Side == side && item.Row == row);
                var cards = visible.Where(card => card.Location.Value.Controller == side && card.Location.Value.Row == row)
                    .OrderBy(card => card.Location.Value.Region!.Value.Left).ToArray();
                var coverage = measurement?.Coverage ?? RowCoverage.Partial;
                // Recognition identity count is not a row census. Unknown row geometry also defeats a full census.
                if (obscured || cards.Length > GwentRules.MaximumRowSize || visible.Any(card => card.Location.Value.Controller == side && card.Location.Value.Row is null))
                    coverage = RowCoverage.Partial;
                return new GameRowState(side, row, coverage, at, cards.Select(card => card.InstanceId).ToImmutableArray(),
                    measurement?.Effects?.ToImmutableArray() ?? [], measurement?.Effects is not null);
            })).ToImmutableArray();
        }
        // Age contacts even across skipped artwork passes, menus and obscuring panels, without asserting removal.
        foreach (var old in _cards.Values.Where(card => at - card.LastSeen > TimeSpan.FromSeconds(6) && card.Presence != CardPresence.Uncertain).ToArray())
            _cards[old.InstanceId] = old with { Presence = CardPresence.Uncertain };

        foreach (var evidence in frame.Events.Where(item => item.ObservedAt == at))
        {
            var sight = evidence.Sighting;
            Add(sight.Source switch { CardSightSource.PlayPreview => "PlayPreview", CardSightSource.History => "HistoricalAction",
                    CardSightSource.DeckReveal => "DeckRevealEvidence", _ => "BoardEvidence" },
                sight.Side, sight.Card.Id, null, evidence.Description ?? "Visible evidence");
            if (evidence.ResolvedDeckCopies is > 0 and <= 25)
                changes[^1] = changes[^1] with { ResolvedDeckCopies = evidence.ResolvedDeckCopies };
            if (sight.Card.Kind == CardKind.Leader && sight.Source != CardSightSource.History)
            {
                var leader = Fact(sight.Card.Name, at, Math.Clamp(1 - sight.Distance, 0, 1), "Visible current ability; does not replace original faction/leader");
                if (sight.Side == PlayerSide.User) user = user with { CurrentLeader = leader };
                else opponent = opponent with { CurrentLeader = leader };
            }
        }
        if (frame.GraveyardInspection is { } inspection)
        {
            foreach (var card in inspection.Cards.DistinctBy(card => card.Id))
            {
                var key = ((PlayerSide?)null, CardZone.Graveyard, card.Id);
                var old = _zones.GetValueOrDefault(key);
                _zones[key] = new(card.Id, card.Name, null, CardZone.Graveyard, old?.FirstSeen ?? at, at, 1, false,
                    CardProvenance.Unknown, "Visible graveyard name; inspected side, copy count and entry route are not established by this OCR.");
            }
            Add("ZoneInspection", null, null, null, "Partial graveyard page; scrolling does not add copies or prove a complete inventory.");
        }
        foreach (var zone in frame.Measurements?.Zones ?? [])
        {
            if (zone.LastSeen > at || zone.FirstSeen > zone.LastSeen || zone.MinimumCopies < 1 || zone.Zone is CardZone.Unknown or CardZone.Board) continue;
            var key = (zone.Side, zone.Zone, zone.CardId);
            var old = _zones.GetValueOrDefault(key);
            if (old is null || zone.LastSeen >= old.LastSeen)
                _zones[key] = zone with { FirstSeen = old is not null && old.FirstSeen < zone.FirstSeen ? old.FirstSeen : zone.FirstSeen,
                    MinimumCopies = Math.Max(zone.MinimumCopies, old?.MinimumCopies ?? 1) };
        }
        foreach (var change in changes) { _events.Enqueue(change); if (_events.Count > MaximumRecentEvents) _events.Dequeue(); }
        foreach (var card in _cards.Values.OrderByDescending(card => card.LastSeen).Skip(MaximumContacts).ToArray()) _cards.Remove(card.InstanceId);
        foreach (var zone in _zones.OrderByDescending(item => item.Value.LastSeen).Skip(256).ToArray()) _zones.Remove(zone.Key);
        Current = before with { Revision = before.Revision + 1, At = at, Phase = phase, Round = round,
            ActivePlayer = Keep(frame.Measurements?.ActivePlayer, before.ActivePlayer, at), User = user, Opponent = opponent,
            Cards = _cards.Values.OrderBy(card => card.InstanceId).ToImmutableArray(), Rows = rows,
            ZoneEvidence = _zones.Values.ToImmutableArray(), RecentEvents = _events.ToImmutableArray(), BoardObscured = obscured,
            DeckChanges = frame.Measurements?.DeckChanges?.Take(128).ToImmutableArray() ?? before.DeckChanges };
        return new(before, Current, changes.ToImmutableArray(), true);
    }

    private static GameCardInstance ApplyMeasurements(GameCardInstance card, IReadOnlyList<CardStateMeasurement>? measurements, DateTimeOffset at)
    {
        var measurement = measurements?.Where(item => item.CardId == card.Card.Id && item.Side == card.Location.Value.Controller &&
            Overlap(item.Region, card.Location.Value.Region!.Value) > .5).OrderBy(item => Distance(item.Region, card.Location.Value.Region!.Value)).FirstOrDefault();
        if (measurement is null) return card;
        var statuses = card.Statuses.IsDefault ? new Dictionary<CardStatus, StatusFact>() : card.Statuses.ToDictionary(item => item.Status);
        foreach (var status in measurement.Statuses ?? [])
            if (ValidFact(status.Active, at) is not null && status.RemainingTurns is null or >= 0 &&
                (!statuses.TryGetValue(status.Status, out var oldStatus) || status.Active.At >= oldStatus.Active.At)) statuses[status.Status] = status;
        return card with { Power = Keep(Number(measurement.Power, at, 1, 9999), card.Power, at),
            BasePower = Keep(Number(measurement.BasePower, at, 1, 9999), card.BasePower, at),
            Armor = Keep(Number(measurement.Armor, at, 0, 9999), card.Armor, at),
            Charges = Keep(Number(measurement.Charges, at, 0, 9999), card.Charges, at),
            Cooldown = Keep(Number(measurement.Cooldown, at, 0, 9999), card.Cooldown, at), Statuses = statuses.Values.ToImmutableArray(),
            Damaged = Keep(measurement.Damaged, card.Damaged, at) };
    }

    private static PlayerGameState Merge(PlayerGameState old, PlayerGameState? next, DateTimeOffset at)
    {
        if (next is null || next.Side != old.Side) return old;
        return old with { Faction = Keep(next.Faction, old.Faction, at),
            StartingLeader = Keep(next.StartingLeader, old.StartingLeader, at),
            CurrentLeader = Keep(next.CurrentLeader, old.CurrentLeader, at),
            OpeningStratagemId = Keep(next.OpeningStratagemId, old.OpeningStratagemId, at),
            HandCount = Keep(Number(next.HandCount, at, 0, 10), old.HandCount, at),
            DeckCount = Keep(Number(next.DeckCount, at, 0, 100), old.DeckCount, at),
            GraveyardCount = Keep(Number(next.GraveyardCount, at, 0, 999), old.GraveyardCount, at),
            Score = Keep(Number(next.Score, at, 0, 9999), old.Score, at), Coins = Keep(Number(next.Coins, at, 0, 9), old.Coins, at),
            LeaderCharges = Keep(Number(next.LeaderCharges, at, 0, 999), old.LeaderCharges, at),
            Passed = Keep(next.Passed, old.Passed, at), RoundsWon = Keep(Number(next.RoundsWon, at, 0, 2), old.RoundsWon, at),
            StartingDeckReference = next.StartingDeckReference ?? old.StartingDeckReference };
    }
    private static PlayerGameState ReplaceMetadata(PlayerGameState current, PlayerGameState context, DateTimeOffset at) =>
        context.Side != current.Side ? current : current with { Faction = ValidFact(context.Faction, at),
            StartingLeader = ValidFact(context.StartingLeader, at), OpeningStratagemId = ValidFact(context.OpeningStratagemId, at),
            StartingDeckReference = context.StartingDeckReference };
    private static StateFact<T>? ValidFact<T>(StateFact<T>? value, DateTimeOffset at) =>
        value is not null && value.At <= at && double.IsFinite(value.Confidence) && value.Confidence is >= .5 and <= 1 ? value : null;
    private static StateFact<T>? Keep<T>(StateFact<T>? value, StateFact<T>? previous, DateTimeOffset at) =>
        ValidFact(value, at) is { } valid && (previous is null || valid.At >= previous.At) ? valid : previous;
    private static StateFact<int>? Number(StateFact<int>? value, DateTimeOffset at, int min, int max) =>
        ValidFact(value, at) is { } valid && valid.Value >= min && valid.Value <= max ? valid : null;
    private static StateFact<T> Fact<T>(T value, DateTimeOffset at, double confidence, string source) => new(value, at, confidence, EvidenceKind.Visual, source);
    private static bool Valid(NormalizedRegion region) => double.IsFinite(region.Left + region.Top + region.Right + region.Bottom) &&
        region.Left >= 0 && region.Top >= 0 && region.Right <= 1 && region.Bottom <= 1 && region.Right > region.Left && region.Bottom > region.Top;
    private static double Distance(NormalizedRegion? a, NormalizedRegion b) => a is null ? double.MaxValue :
        Math.Abs((a.Value.Left + a.Value.Right - b.Left - b.Right) / 2) + Math.Abs((a.Value.Top + a.Value.Bottom - b.Top - b.Bottom) / 2);
    private static double Overlap(NormalizedRegion a, NormalizedRegion b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top)) /
        Math.Max(.000001, Math.Min((a.Right - a.Left) * (a.Bottom - a.Top), (b.Right - b.Left) * (b.Bottom - b.Top)));

    // Standard captured battlefield geometry. Boundary contacts stay unassigned; no invented exact slot index.
    public static BoardRow? LocateRow(PlayerSide side, NormalizedRegion region)
    {
        var y = (region.Top + region.Bottom) / 2;
        return side == PlayerSide.Opponent ? y switch { >= .13 and < .28 => BoardRow.Ranged, > .32 and < .465 => BoardRow.Melee, _ => null }
            : y switch { >= .465 and < .60 => BoardRow.Melee, > .64 and < .83 => BoardRow.Ranged, _ => null };
    }
}
