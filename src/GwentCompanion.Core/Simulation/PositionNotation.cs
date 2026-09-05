using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.Core.Simulation;

/// <summary>
/// GVN1: a compact position and line-replacement log. U/O = sides, M/R/H/D/G/X = zones.
/// A card is id#instance/power/base/armor/statuses/charges/cooldown. ? is unknown; - is empty.
/// Zone order is card order. No catalog text, images, probabilities or timestamps are copied here.
/// </summary>
public static class PositionNotation
{
    public static string Write(GamePosition position)
    {
        var effects = position.RowEffects is null ? "?" : position.RowEffects.Value.Length == 0 ? "-" :
            string.Join('+', position.RowEffects.Value.OrderBy(effect => effect.AffectedSide).ThenBy(effect => effect.Row).ThenBy(effect => effect.Name)
                .Select(effect => $"{Side(effect.AffectedSide)}{(effect.Row == BoardRow.Melee ? "M" : "R")}:{Effect(effect.Name)}:{Number(effect.RemainingTurns)}"));
        var values = position.CardValues is null ? "?" : position.CardValues.Value.Length == 0 ? "-" :
            string.Join('+', position.CardValues.Value.OrderBy(value => value.Side).ThenBy(value => value.CardId).ThenBy(value => value.Kind)
                .Select(value => $"{Side(value.Side)}:{Id(value.CardId)}:{Id(value.Kind)}:{Number(value.Minimum)}:{Number(value.Maximum)}:{Id(value.StoredCardId)}"));
        var lines = new List<string> { "V|1", $"R|{Number(position.Round)}|{(position.ActivePlayer is { } side ? Side(side) : "?")}|{(position.RowEffectsKnownInactive ? "0" : "?")}|{effects}|{values}" };
        foreach (var player in new[] { position.User, position.Opponent })
            lines.Add($"{Side(player.Side)}|{Id(player.CurrentLeaderId)}|{Number(player.LeaderCharges)}|{Number(player.Coins)}|{(player.Passed is { } passed ? passed ? "1" : "0" : "?")}|{(player.PassiveEffectsKnownInactive ? "0" : "?")}" +
                (player.LastPlayedUnitId is not null || player.StartingDeckIds is not null || player.Devotion is not null
                    ? $"|{Number(player.RoundsWon)}|{Id(player.LastPlayedUnitId)}|{(player.StartingDeckIds is null ? "?" : player.StartingDeckIds.Count == 0 ? "-" : string.Join('+', player.StartingDeckIds.Order()))}|{(player.Devotion is null ? "?" : player.Devotion.Value ? "1" : "0")}" :
                    player.RoundsWon is { } wins ? $"|{wins}" : ""));
        foreach (var zone in position.Zones)
        {
            var cards = zone.Cards.Select(card => $"{Id(card.CardId)}#{Id(card.InstanceId)}/{Number(card.Power)}/{Number(card.BasePower)}/{Number(card.Armor)}/" +
                (card.Statuses is null ? "?" : card.Statuses.Count == 0 ? "-" : string.Join('+', card.Statuses.Order().Select(status => status.ToString()))) +
                $"/{Number(card.Charges)}/{Number(card.Cooldown)}" +
                (card.Original is null && card.StatusTurns is null && card.ExtraThrive == 0 ? "" :
                    $"/{(card.Original is null ? "?" : card.Original.Value ? "1" : "0")}/" +
                    (card.StatusTurns is null ? "?" : card.StatusTurns.Count == 0 ? "-" : string.Join('+', card.StatusTurns.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")))) +
                (card.ExtraThrive > 0 ? $"/{card.ExtraThrive}" : ""));
            lines.Add($"{Key(zone)}|{(zone.Complete ? "=" : "?")}|{Number(zone.TotalCount)}|{(zone.Cards.Length == 0 ? "-" : string.Join(' ', cards))}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Only changed lines are needed. Concatenating these patches reconstructs the latest position.</summary>
    public static string Changes(GamePosition? before, GamePosition after)
    {
        var previous = before is null ? new Dictionary<string, string>() : Write(before).Split('\n').ToDictionary(Key);
        return string.Join('\n', Write(after).Split('\n').Where(line => !previous.TryGetValue(Key(line), out var old) || old != line));
    }

    public static GamePosition Read(string notation)
    {
        if (notation.Length > 1_000_000) throw new FormatException("Position text exceeds the safety limit.");
        var lines = new Dictionary<string, string>();
        foreach (var raw in notation.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith(';')) continue;
            lines[Key(line)] = line;
        }
        if (lines.GetValueOrDefault("V") != "V|1") throw new FormatException("Expected GVN version 1.");
        var round = Fields("R", 4, 5, 6);
        var number = ParseNumber(round[1]);
        if (number is not null and not (>= 1 and <= 3)) throw new FormatException("Invalid round.");
        PlayerSide? active = round[2] == "?" ? null : ParseSide(round[2]);
        PositionResources Resources(string key)
        {
            var values = lines[key].Split('|');
            if (values.Length is not 6 and not 7 and not 10) throw new FormatException("Invalid resources.");
            var wins = values.Length >= 7 ? ParseNumber(values[6]) : null;
            if (wins > 2) throw new FormatException("Invalid round wins.");
            return new(ParseSide(key), values[1] == "?" ? null : Id(values[1]), ParseNumber(values[2]), ParseNumber(values[3]),
                values[4] switch { "?" => null, "0" => false, "1" => true, _ => throw new FormatException("Invalid pass flag.") }, Inactive(values[5]), wins,
                values.Length == 10 && values[7] != "?" ? Id(values[7]) : null,
                values.Length != 10 || values[8] == "?" ? null : values[8] == "-" ? ImmutableHashSet<string>.Empty : values[8].Split('+').Select(Id).ToImmutableHashSet(),
                values.Length != 10 ? null : values[9] switch { "?" => null, "0" => false, "1" => true, _ => throw new FormatException("Invalid Devotion flag.") });
        }
        var zones = ImmutableArray.CreateBuilder<PositionZone>();
        foreach (var side in new[] { "U", "O" })
        foreach (var suffix in new[] { "M", "R", "H", "D", "G", "X" })
        {
            var values = Fields(side + suffix, 4);
            var zone = suffix switch { "M" or "R" => CardZone.Board, "H" => CardZone.Hand, "D" => CardZone.Deck, "G" => CardZone.Graveyard, _ => CardZone.Banished };
            var cards = values[3] == "-" ? ImmutableArray<PositionCard>.Empty : values[3].Split(' ').Select(ParseCard).ToImmutableArray();
            zones.Add(new(ParseSide(side), zone, suffix == "M" ? BoardRow.Melee : suffix == "R" ? BoardRow.Ranged : null,
                cards, values[1] switch { "=" => true, "?" => false, _ => throw new FormatException("Invalid zone coverage.") }, ParseNumber(values[2])));
        }
        if (lines.Count != 16) throw new FormatException("Unexpected position fields.");
        ImmutableArray<PositionRowEffect>? effects = round.Length == 4 || round[4] == "?" ? null : round[4] == "-" ? [] :
            round[4].Split('+').Select(part =>
            {
                var fields = part.Split(':');
                if (fields.Length != 3 || fields[0].Length != 2) throw new FormatException("Invalid row effect.");
                return new PositionRowEffect(ParseSide(fields[0][..1]), fields[0][1] switch
                { 'M' => BoardRow.Melee, 'R' => BoardRow.Ranged, _ => throw new FormatException("Invalid effect row.") },
                    Effect(fields[1]), ParseNumber(fields[2]));
            }).ToImmutableArray();
        ImmutableArray<PositionCardValue>? cardValues = round.Length < 6 || round[5] == "?" ? null : round[5] == "-" ? [] :
            round[5].Split('+').Select(part =>
            {
                var fields = part.Split(':');
                if (fields.Length != 6) throw new FormatException("Invalid card value.");
                return new PositionCardValue(ParseSide(fields[0]), Id(fields[1]), Id(fields[2]), ParseNumber(fields[3]), ParseNumber(fields[4]),
                    fields[5] == "?" ? null : Id(fields[5]));
            }).ToImmutableArray();
        return new(zones.ToImmutable(), Resources("U"), Resources("O"), number, active, Inactive(round[3]), effects, cardValues);

        string[] Fields(string key, params int[] counts)
        {
            if (!lines.TryGetValue(key, out var value)) throw new FormatException("Missing position field " + key);
            var parts = value.Split('|'); if (!counts.Contains(parts.Length)) throw new FormatException("Invalid position field " + key);
            return parts;
        }
    }

    private static PositionCard ParseCard(string text)
    {
        var fields = text.Split('/'); if (fields.Length is not 7 and not 9 and not 10) throw new FormatException("Invalid card token.");
        var identity = fields[0].Split('#'); if (identity.Length != 2 || identity.Any(item => item == "?")) throw new FormatException("Invalid identity.");
        ImmutableHashSet<CardStatus>? statuses = fields[4] == "?" ? null : fields[4] == "-" ? ImmutableHashSet<CardStatus>.Empty :
            fields[4].Split('+').Select(item => Enum.TryParse<CardStatus>(item, out var status) && Enum.IsDefined(status) ? status : throw new FormatException("Unknown status.")).ToImmutableHashSet();
        bool? original = fields.Length == 7 ? null : fields[7] switch { "?" => null, "0" => false, "1" => true, _ => throw new FormatException("Invalid original flag.") };
        ImmutableDictionary<CardStatus, int>? turns = fields.Length == 7 || fields[8] == "?" ? null : fields[8] == "-" ? ImmutableDictionary<CardStatus, int>.Empty :
            fields[8].Split('+').Select(part => part.Split(':')).ToImmutableDictionary(pair =>
                pair.Length == 2 && Enum.TryParse<CardStatus>(pair[0], out var status) && status is CardStatus.Bleeding or CardStatus.Vitality ? status : throw new FormatException("Invalid duration status."),
                pair => ParseNumber(pair[1]) ?? throw new FormatException("Missing duration."));
        return new(Id(identity[1]), Id(identity[0]), ParseNumber(fields[1]), ParseNumber(fields[2]), ParseNumber(fields[3]), statuses, ParseNumber(fields[5]), ParseNumber(fields[6]), original, turns,
            fields.Length == 10 ? ParseNumber(fields[9]) ?? throw new FormatException("Missing Thrive value") : 0);
    }
    private static string Id(string? value) => value is null ? "?" : Regex.IsMatch(value, @"^[A-Za-z0-9_-]+$") ? value : throw new FormatException("Unsafe identifier.");
    private static string Effect(string value) => value.Replace(' ', '_') is { } token && Regex.IsMatch(token, @"^[A-Za-z_-]+$") ? token.Replace('_', ' ') : throw new FormatException("Unsafe row effect.");
    private static int? ParseNumber(string value) => value == "?" ? null : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 99999 ? number : throw new FormatException("Invalid number.");
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "?";
    private static bool Inactive(string value) => value switch { "0" => true, "?" => false, _ => throw new FormatException("Invalid effect flag.") };
    private static string Side(PlayerSide side) => side == PlayerSide.User ? "U" : "O";
    private static PlayerSide ParseSide(string value) => value switch { "U" => PlayerSide.User, "O" => PlayerSide.Opponent, _ => throw new FormatException("Invalid side.") };
    private static string Key(string line) => line.Split('|')[0];
    private static string Key(PositionZone zone) => Side(zone.Side) + (zone.Zone switch
    { CardZone.Board => zone.Row == BoardRow.Melee ? "M" : "R", CardZone.Hand => "H", CardZone.Deck => "D", CardZone.Graveyard => "G", CardZone.Banished => "X", _ => throw new FormatException("Unknown zone.") });
}
