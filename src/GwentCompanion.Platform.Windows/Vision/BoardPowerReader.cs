using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Reads printed on-screen numbers, never catalog power or a value fitted to the scoreboard.
/// Two independent frames must agree. Unknown armor/status/base power stays unknown.</summary>
public sealed class BoardPowerReader
{
    private readonly HudDigitReader _digits;
    private sealed record Vote(string CardId, PlayerSide Side, NormalizedRegion Region, int Power, bool? Damaged, DateTimeOffset At);
    private List<Vote> _previous = [];
    public BoardPowerReader()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "vision-assets", "board-power-digits.json");
        _digits = new(File.Exists(file) ? JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(file)) ?? [] : [], boardPower: true);
    }
    public static NormalizedRegion NumberRegion(NormalizedRegion card)
    {
        var w = card.Right - card.Left; var h = card.Bottom - card.Top;
        return new(Math.Max(0, card.Left + w * .015), Math.Max(0, card.Top - h * .005),
            Math.Min(1, card.Left + w * .27), Math.Min(1, card.Top + h * .18));
    }
    public int? ReadCandidate(PixelFrame frame, NormalizedRegion card) => _digits.Read(frame, NumberRegion(card));
    public IReadOnlyList<CardStateMeasurement> Observe(PixelFrame frame, DateTimeOffset at,
        IReadOnlyList<CardSighting> sightings, GwentVisualObservation screen)
    {
        if (screen.IsCardSelectionOverlay || screen.View == GwentViewKind.MoveHistory) { _previous.Clear(); return []; }
        var next = new List<Vote>(); var output = new List<CardStateMeasurement>();
        foreach (var sight in sightings.Where(s => s.Source == CardSightSource.Board && s.Card.Kind == CardKind.Unit && s.Distance <= .3))
        {
            var region = NumberRegion(sight.Region);
            if (screen.HasCardTooltip && (screen.TooltipRegion is null || Intersects(region, screen.TooltipRegion.Value))) continue;
            var reading = _digits.ReadDetailed(frame, NumberRegion(sight.Region));
            if (reading is null || reading.Value is < 1 or > 999) continue;
            var value = reading.Value;
            bool? damaged = reading.Tone switch
            {
                HudNumberTone.Damaged => true,
                HudNumberTone.Base or HudNumberTone.Boosted => false,
                _ => null
            };
            var matching = _previous.Where(v => v.CardId == sight.Card.Id && v.Side == sight.Side &&
                Math.Abs(v.Region.Left + v.Region.Right - sight.Region.Left - sight.Region.Right) < .04 &&
                Math.Abs(v.Region.Top + v.Region.Bottom - sight.Region.Top - sight.Region.Bottom) < .05).ToArray();
            if (matching.Length == 1 && matching[0].Power == value && matching[0].Damaged == damaged &&
                at > matching[0].At && at - matching[0].At <= TimeSpan.FromSeconds(8))
                output.Add(new(sight.Card.Id, sight.Side, sight.Region,
                    Power: new(value, at, .9, EvidenceKind.Visual, "Repeated board power glyphs; no catalog/score fitting"),
                    Damaged: damaged is { } isDamaged ? new(isDamaged, at, .9, EvidenceKind.Visual,
                        "Repeated board power glyph color; red means damaged, white/green means not damaged") : null));
            next.Add(new(sight.Card.Id, sight.Side, sight.Region, value, damaged, at));
        }
        // Board passes can be 4–6 seconds apart. A partial pass must not erase
        // votes for unrelated visible positions; a contradictory value replaces them.
        _previous = next.Concat(_previous.Where(v=>at>v.At && at-v.At<=TimeSpan.FromSeconds(8) &&
            !next.Any(n=>n.Side==v.Side && Math.Abs(n.Region.Left+n.Region.Right-v.Region.Left-v.Region.Right)<.04 &&
                Math.Abs(n.Region.Top+n.Region.Bottom-v.Region.Top-v.Region.Bottom)<.05))).Take(72).ToList();
        return output;
    }
    private static bool Intersects(NormalizedRegion a, NormalizedRegion b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    public void Reset() => _previous.Clear();
}
