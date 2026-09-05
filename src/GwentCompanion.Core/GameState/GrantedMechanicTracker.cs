using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.GameState;

public sealed record GrantedMechanicEstimate(PlayerSide Side, string Mechanic, int Minimum, int Maximum, int Estimated,
    string Reason);

/// <summary>
/// Tracks board mechanics granted by observed Deploy/Infuse effects separately from
/// printed card text. Unread targets remain explicit ranges and all grants expire at
/// the next round, so an uncertain infusion is never presented as a permanent fact.
/// </summary>
public sealed class GrantedMechanicTracker
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "Assimilate", "Barricade", "Berserk", "Deathwish", "Flanking", "Grace", "Harmony",
        "Inspired", "Intimidate", "Symbiosis", "Thrive"
    };
    private static readonly Regex DeployClause = new(
        @"Deploy(?:\s*\((?<row>Melee|Ranged)\))?\s*:\s*(?<body>.*?)(?=(?:\s+Deploy(?:\s*\(|\s*:)|\s+(?:Order|Deathwish|Cooldown|Timer|Fee)\s*(?:\(|:)|$))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Infusion = new(
        @"Infuse\s+(?:(?<count>\d+)\s+)?(?<target>[^.;]*?)\s+with\s+""(?<mechanic>[A-Za-z]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, PendingGrant> _pending = [];
    private readonly Dictionary<string, GrantedMechanicEstimate> _resolved = [];
    private readonly HashSet<string> _events = [];
    private string? _session;
    private int? _round;

    public void Reset()
    {
        _pending.Clear(); _resolved.Clear(); _events.Clear(); _session = null; _round = null;
    }

    public void Observe(GameStateUpdate update, IEnumerable<CardDefinition> catalog)
    {
        if (!update.Accepted || update.After.At is not { } at) return;
        var state = update.After;
        if (_session != state.SessionId) { Reset(); _session = state.SessionId; }
        if (_round != state.Round?.Value)
        {
            _pending.Clear(); _resolved.Clear(); _events.Clear(); _round = state.Round?.Value;
        }
        var cards = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        foreach (var action in update.Events.Where(item => item.Kind == "PlayPreview" && item.Side is not null && item.CardId is not null))
        {
            if (!_events.Add(action.Id) || !cards.TryGetValue(action.CardId!, out var source) || source.Kind != CardKind.Unit) continue;
            var index = 0; var sourceSide = action.Side!.Value;
            foreach (var rule in Parse(source))
            {
                var side = rule.EnemyTarget ? Other(sourceSide) : sourceSide;
                var key = state.SessionId + "/" + action.Id + "/" + index++;
                _pending[key] = new(key, action.At, sourceSide, side, source, rule);
            }
        }
        foreach (var pair in _pending.ToArray())
        {
            var pending = pair.Value;
            var source = state.Cards.Where(card => card.Card.Id == pending.Source.Id &&
                    card.Location.Value.Controller == pending.SourceSide && card.Location.Value.Zone == CardZone.Board &&
                    card.LastSeen >= pending.At && card.LastSeen <= pending.At.AddSeconds(20) &&
                    (pending.Rule.Row is null || card.Location.Value.Row == pending.Rule.Row))
                .OrderByDescending(card => card.LastSeen).FirstOrDefault();
            if (source is null)
            {
                if (at - pending.At > TimeSpan.FromSeconds(20)) _pending.Remove(pair.Key);
                continue;
            }
            var confirmed = state.Cards.Count(card => card.Location.Value.Controller == pending.TargetSide &&
                card.Location.Value.Zone == CardZone.Board && card.InstanceId != source.InstanceId &&
                card.Status(CardStatus.Infused) is { Value: true } status && status.At >= pending.At);
            confirmed = Math.Min(confirmed, pending.Rule.Count);
            var eligible = state.Cards.Count(card => card.Location.Value.Controller == pending.TargetSide &&
                card.Location.Value.Zone == CardZone.Board && card.InstanceId != source.InstanceId &&
                card.Card.Kind == CardKind.Unit && card.Presence is CardPresence.Visible or CardPresence.LastKnown &&
                (!pending.Rule.ExcludesExisting || !LiveSynergyMeter.DeclaresMechanic(card.Card.AbilityText, pending.Rule.Mechanic)));
            var maximum = pending.Rule.Count;
            var minimum = Math.Max(confirmed, !pending.Rule.Conditional && eligible >= maximum ? maximum : 0);
            var estimated = maximum;
            var uncertainty = confirmed == maximum ? "Observed Infused target status." : minimum == maximum ?
                "Required-row Deploy resolved with enough eligible visible targets; target identity was not readable." :
                pending.Rule.Conditional ? "Conditional Deploy resolved on the required row; target/effect activation was not readable." :
                "Deploy resolved on the required row; the specific Infused target was not readable.";
            _resolved[pair.Key] = new(pending.TargetSide, pending.Rule.Mechanic, minimum, maximum, estimated,
                $"{pending.Source.Name}: grants {pending.Rule.Count} {pending.Rule.Mechanic}. {uncertainty}");
            _pending.Remove(pair.Key);
        }
    }

    public GrantedMechanicEstimate? Read(PlayerSide side, string mechanic)
    {
        var rows = _resolved.Values.Where(item => item.Side == side && item.Mechanic.Equals(mechanic, StringComparison.OrdinalIgnoreCase)).ToArray();
        return rows.Length == 0 ? null : new(side, mechanic, rows.Sum(item => item.Minimum), rows.Sum(item => item.Maximum),
            rows.Sum(item => item.Estimated), string.Join(" ", rows.Select(item => item.Reason).Distinct()));
    }

    private static IEnumerable<GrantRule> Parse(CardDefinition card)
    {
        var text = card.AbilityText ?? "";
        foreach (Match clause in DeployClause.Matches(text))
        foreach (Match infusion in Infusion.Matches(clause.Groups["body"].Value))
        {
            var mechanic = infusion.Groups["mechanic"].Value;
            if (!Supported.Contains(mechanic)) continue;
            var target = infusion.Groups["target"].Value;
            if (target.Contains("deck", StringComparison.OrdinalIgnoreCase) || target.Contains("graveyard", StringComparison.OrdinalIgnoreCase)) continue;
            var count = infusion.Groups["count"].Success ? int.Parse(infusion.Groups["count"].Value) : 1;
            var row = clause.Groups["row"].Value.ToLowerInvariant() switch
            { "melee" => BoardRow.Melee, "ranged" => BoardRow.Ranged, _ => (BoardRow?)null };
            var enemy = target.Contains("enemy", StringComparison.OrdinalIgnoreCase);
            var excludesExisting = target.Contains("non-" + mechanic, StringComparison.OrdinalIgnoreCase);
            var conditional = clause.Groups["body"].Value.Contains("If ", StringComparison.OrdinalIgnoreCase) ||
                clause.Groups["body"].Value.Contains("would not", StringComparison.OrdinalIgnoreCase);
            yield return new(mechanic, Math.Clamp(count, 1, GwentRules.MaximumRowSize), row, enemy, conditional, excludesExisting);
        }
    }

    private static PlayerSide Other(PlayerSide side) => side == PlayerSide.User ? PlayerSide.Opponent : PlayerSide.User;
    private sealed record GrantRule(string Mechanic, int Count, BoardRow? Row, bool EnemyTarget, bool Conditional, bool ExcludesExisting);
    private sealed record PendingGrant(string Key, DateTimeOffset At, PlayerSide SourceSide, PlayerSide TargetSide,
        CardDefinition Source, GrantRule Rule);
}
