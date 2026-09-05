using System.Collections.Immutable;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.GameState;

public enum EvidenceKind { Visual, Reviewed, Inferred, Reference }

/// <summary>Null means unknown. A previous measurement is retained with its actual time, never refreshed by reuse.</summary>
public sealed record StateFact<T>(T Value, DateTimeOffset At, double Confidence, EvidenceKind Kind, string Source)
{
    public bool IsFresh(DateTimeOffset now, TimeSpan age) => At <= now && now - At <= age;
}

public enum GamePhase { Unknown, Playing, RoundTransition, Selection, Ended }
public enum BoardRow { Melee, Ranged }
public enum CardZone { Unknown, Board, Hand, Deck, Graveyard, Banished }
public enum CardPresence { Visible, LastKnown, Uncertain, NotOnBoard }
public enum RowCoverage { Unknown, Partial, Complete }
public enum CardStatus { Shield, Locked, Poison, Bleeding, Vitality, Doomed, Resilience, Defender, Immune, Spying, Veil, Bounty, Infused }
public enum GameActionKind { Play, Summon, Create, Spawn, Discard, Destroy, Banish, Transform, Move, Unknown }

public sealed record StatusFact(CardStatus Status, StateFact<bool> Active, int? RemainingTurns = null);
public sealed record RowEffectState(string Name, StateFact<int>? RemainingTurns, string? SourceInstanceId = null);
public sealed record CardLocation(PlayerSide Controller, CardZone Zone, BoardRow? Row, NormalizedRegion? Region);

/// <summary>A visual contact, not a proven starting-deck copy. Owner and controller are deliberately separate.</summary>
public sealed record GameCardInstance(
    string InstanceId, CardDefinition Card, StateFact<CardLocation> Location,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, CardPresence Presence,
    StateFact<PlayerSide>? Owner = null, StateFact<CardProvenance>? Origin = null,
    StateFact<int>? Power = null, StateFact<int>? BasePower = null, StateFact<int>? Armor = null,
    StateFact<int>? Charges = null, StateFact<int>? Cooldown = null,
    ImmutableArray<StatusFact> Statuses = default, bool IdentityContinuityUncertain = false,
    StateFact<bool>? Damaged = null)
{
    public StateFact<bool>? Status(CardStatus status) => Statuses.IsDefault ? null : Statuses.FirstOrDefault(item => item.Status == status)?.Active;
}

public sealed record GameRowState(PlayerSide Side, BoardRow Row, RowCoverage Coverage,
    DateTimeOffset? ScannedAt, ImmutableArray<string> VisibleInstanceIds, ImmutableArray<RowEffectState> Effects,
    bool EffectsKnown = false);

public sealed record PlayerGameState(PlayerSide Side,
    StateFact<string>? Faction = null, StateFact<string>? StartingLeader = null,
    StateFact<string>? CurrentLeader = null, StateFact<string>? OpeningStratagemId = null,
    StateFact<int>? HandCount = null, StateFact<int>? DeckCount = null, StateFact<int>? GraveyardCount = null,
    StateFact<int>? Score = null, StateFact<int>? Coins = null, StateFact<int>? LeaderCharges = null,
    StateFact<bool>? Passed = null, StateFact<int>? RoundsWon = null,
    DeckDefinition? StartingDeckReference = null);

/// <summary>Scrolling sightings are identity lower bounds, not a full inventory or another copy on each page.</summary>
public sealed record ZoneIdentityEvidence(string CardId, string CardName, PlayerSide? Side, CardZone Zone,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int MinimumCopies, bool OrderKnown,
    CardProvenance Origin, string Source);

public sealed record GameStateEvent(string Id, DateTimeOffset At, string Kind, PlayerSide? Side,
    string? CardId, string? InstanceId, string Detail, int? ResolvedDeckCopies = null);

public sealed record GameStateSnapshot(int SchemaVersion, string RulesVersion, string SessionId, long Revision,
    DateTimeOffset? At, GamePhase Phase, StateFact<int>? Round, StateFact<PlayerSide>? ActivePlayer,
    PlayerGameState User, PlayerGameState Opponent, ImmutableArray<GameCardInstance> Cards,
    ImmutableArray<GameRowState> Rows, ImmutableArray<ZoneIdentityEvidence> ZoneEvidence,
    ImmutableArray<GameStateEvent> RecentEvents, ImmutableArray<string> Limitations, bool BoardObscured = false)
{
    public PlayerGameState Player(PlayerSide side) => side == PlayerSide.User ? User : Opponent;
    public ImmutableArray<DeckMutation> DeckChanges { get; init; } = [];
}

public sealed record GameStateUpdate(GameStateSnapshot Before, GameStateSnapshot After,
    ImmutableArray<GameStateEvent> Events, bool Accepted);

/// <summary>Future numeric/status detectors use this same input boundary. Absence of a measurement is not zero/false.</summary>
public sealed record CardStateMeasurement(string CardId, PlayerSide Side, NormalizedRegion Region,
    StateFact<int>? Power = null, StateFact<int>? BasePower = null, StateFact<int>? Armor = null,
    StateFact<int>? Charges = null, StateFact<int>? Cooldown = null, IReadOnlyList<StatusFact>? Statuses = null,
    StateFact<bool>? Damaged = null);

public sealed record RowStateMeasurement(PlayerSide Side, BoardRow Row, RowCoverage Coverage,
    IReadOnlyList<RowEffectState>? Effects = null);

public sealed record GameStateMeasurements(
    IReadOnlyList<CardStateMeasurement>? Cards = null, IReadOnlyList<RowStateMeasurement>? Rows = null,
    PlayerGameState? User = null, PlayerGameState? Opponent = null,
    StateFact<int>? Round = null, StateFact<PlayerSide>? ActivePlayer = null,
    IReadOnlyList<ZoneIdentityEvidence>? Zones = null, IReadOnlyList<DeckMutation>? DeckChanges = null,
    bool ReplaceStartingMetadata = false);

public sealed record VisualGameStateFrame(DateTimeOffset At, GwentVisualObservation Screen,
    IReadOnlyList<CardSighting> Sightings, IReadOnlyList<VisionEvidenceEvent> Events, bool BoardWasScanned,
    VisibleZoneInspection? GraveyardInspection = null, GameStateMeasurements? Measurements = null);
