using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class BoardPowerTests
{
    private sealed record Sample(string Frame, string Card, int X, int Y, int Width, int Height, int Power)
    {
        public NormalizedRegion Region => new(X / 1280.0, Y / 720.0, (X + Width) / 1280.0, (Y + Height) / 720.0);
    }
    private const string Session = "20260828-112523";
    private static readonly Sample[] Training =
    [
        new("000001-112523653", "Kerack Frigate", 562, 96, 78, 99, 13),
        new("000001-112523653", "Volunteer", 640, 96, 78, 99, 11),
        new("000001-112523653", "Jan Calveit", 515, 202, 84, 108, 7),
        new("000001-112523653", "Lamp Djinn", 599, 202, 84, 108, 5),
        new("000001-112523653", "Sandor de Baccalà", 683, 202, 84, 108, 10),
        new("000001-112523653", "Griffin", 508, 325, 88, 119, 9),
        new("000001-112523653", "Gernichora's Fruit", 596, 325, 88, 119, 3),
        new("000001-112523653", "Barbegazi", 684, 325, 90, 119, 17),
        new("000001-112523653", "Lady of the Lake", 549, 454, 91, 134, 1),
        new("000001-112523653", "Siren", 640, 454, 91, 134, 4),
        new("000789-112653526", "Squirrel", 640, 112, 82, 102, 1),
        new("000789-112653526", "Gernichora's Fruit", 595, 351, 89, 127, 6),
        new("000789-112653526", "Siren", 638, 490, 98, 141, 9),
        new("000789-112653526", "Nekker Warrior", 736, 490, 98, 141, 7),
        new("002412-112955407", "Henry var Attre", 465, 222, 89, 114, 6),
        new("002412-112955407", "Fiend", 549, 351, 90, 127, 8),
        new("002412-112955407", "Gernichora's Fruit", 542, 490, 98, 140, 2),
        new("002412-112955407", "Golyat", 639, 490, 99, 140, 12)
        ,new("000519-112619159", "Kerack Frigate", 480, 96, 80, 98, 13)
        ,new("000519-112619159", "Volunteer", 640, 96, 80, 98, 2)
        ,new("000519-112619159", "Squirrel", 720, 96, 80, 98, 1)
        ,new("000519-112619159", "Artorius Vigo", 722, 202, 85, 107, 3)
        ,new("000519-112619159", "Lady of the Lake", 459, 454, 92, 134, 1)
        ,new("000519-112619159", "Siren", 550, 454, 91, 134, 4)
    ];
    // Independently read visible values; these images are not glyph calibration images.
    private static readonly Sample[] HeldOut =
    [
        new("000533-112622063", "Kerack Frigate", 480, 96, 80, 98, 13),
        new("000533-112622063", "Volunteer", 560, 96, 80, 98, 11),
        new("000533-112622063", "Volunteer", 640, 96, 80, 98, 2),
        new("000533-112622063", "Squirrel", 720, 96, 80, 98, 1),
        new("000533-112622063", "Jan Calveit", 473, 202, 83, 107, 7),
        new("000533-112622063", "Lamp Djinn", 556, 202, 83, 107, 5),
        new("000533-112622063", "Sandor de Baccalà", 639, 202, 83, 107, 10),
        new("000533-112622063", "Artorius Vigo", 722, 202, 85, 107, 3),
        new("000533-112622063", "Griffin", 508, 325, 88, 119, 9),
        new("000533-112622063", "Gernichora's Fruit", 596, 325, 88, 119, 5),
        new("000533-112622063", "Barbegazi", 684, 325, 88, 119, 17),
        new("000533-112622063", "Lady of the Lake", 459, 454, 92, 134, 1),
        new("000533-112622063", "Siren", 550, 454, 91, 134, 4),
        new("000533-112622063", "Siren", 641, 454, 91, 134, 4),
        new("000533-112622063", "Rotfiend", 732, 454, 91, 134, 5)
    ];
    private static PixelFrame Load(string root, string frame)
    {
        using var stream = File.OpenRead(Path.Combine(root, "GwentCompanion/sessions", Session, "frame-" + frame + ".jpg"));
        return BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);
    }
    public static void Train(string root)
    {
        var templates = new List<HudGlyph>();
        foreach (var s in Training)
        {
            try { templates.AddRange(HudDigitReader.Train(Load(root, s.Frame), BoardPowerReader.NumberRegion(s.Region), s.Power.ToString(), boardPower: true)); }
            catch (InvalidOperationException error) { throw new InvalidOperationException(s.Card + " " + s.Frame + ": " + error.Message); }
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion/assets/vision/board-power-digits.json"), JsonSerializer.Serialize(templates));
        Console.WriteLine($"Board power calibration: {templates.Count} glyphs; images separated from held-out validation.");
    }
    public static void Run(string root)
    {
        var reader = new BoardPowerReader(); var results = new List<object>(); var correct = 0; var unread = 0; var wrong = 0;
        foreach (var s in HeldOut)
        {
            var value = reader.ReadCandidate(Load(root, s.Frame), s.Region);
            if (value == s.Power) correct++; else if (value is null) unread++; else wrong++;
            results.Add(new { s.Frame, s.Card, s.Region, Expected = s.Power, Actual = value });
            Console.WriteLine($"Power {s.Card} {s.Power}: {value?.ToString() ?? "unread"}");
        }
        File.WriteAllText(Path.Combine(root, "GwentCompanion/diagnostics/v0.1.25-board-power.json"), JsonSerializer.Serialize(new
        { Scope = "Held-out frame, same match/resolution; manually reviewed artwork bounds, not end-to-end identity detection", Correct = correct, Unread = unread, Wrong = wrong, Results = results }, GameStateJournal.Json));
        if (wrong != 0 || correct < 10) throw new InvalidOperationException($"Board power regression: correct={correct}, unread={unread}, wrong={wrong}");
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        var sample = HeldOut.Single(s => s.Card == "Artorius Vigo"); var frame = Load(root, sample.Frame);
        var card = catalog.Single(c => c.Name == sample.Card); var at = DateTimeOffset.UtcNow;
        var sight = new CardSighting(card, PlayerSide.Opponent, CardSightSource.Board, sample.Region, .08, 1);
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
        if (reader.Observe(frame, at, [sight], screen).Count != 0) throw new InvalidOperationException("Single frame power accepted");
        var measures = reader.Observe(frame, at.AddSeconds(1), [sight], screen);
        if (measures.Single().Power?.Value != 3) throw new InvalidOperationException("Repeated power not confirmed");
        var tracker = new GameStateTracker(); tracker.Reset("board-power");
        var result = new CardVisionResult(at.AddSeconds(1), screen, [sight], [], true, CardMeasurements: measures);
        var observed = GameStateVisionAdapter.Apply(tracker, result).After.Cards.Single();
        if (observed.Power?.Value != 3 || observed.Damaged?.Value != false || observed.BasePower is not null || observed.Armor is not null)
            throw new InvalidOperationException("Adapter lost observed power/color or invented base power/armor");
        if (reader.Observe(frame, at.AddSeconds(7), [sight], screen).Count != 1) throw new InvalidOperationException("Actual 6-second board cadence lost confirmation");
        if (reader.Observe(frame, at.AddSeconds(16), [sight], screen).Count != 0) throw new InvalidOperationException("Stale numeric confirmation reused");
        if (reader.Observe(frame, at.AddSeconds(17), [sight], screen with { HasCardTooltip = true, TooltipRegion = sample.Region }).Count != 0)
            throw new InvalidOperationException("Covered number read");
        var redSample = HeldOut.Single(s => s.Card == "Squirrel" && s.Power == 1); var redFrame = Load(root, redSample.Frame);
        var redCard = catalog.Single(c => c.Name == redSample.Card); var redSight = new CardSighting(redCard, PlayerSide.Opponent,
            CardSightSource.Board, redSample.Region, .08, 1); var redReader = new BoardPowerReader();
        redReader.Observe(redFrame, at, [redSight], screen);
        var red = redReader.Observe(redFrame, at.AddSeconds(1), [redSight], screen).Single();
        if (red.Power?.Value != redSample.Power || red.Damaged?.Value != true)
            throw new InvalidOperationException("Repeated red board-power glyph did not establish damaged state");
        Console.WriteLine($"PASS board power: {correct} correct, {unread} unread, {wrong} wrong; temporal/state guards.");
    }
}
