namespace GwentCompanion.Core.Vision;

public enum GwentViewKind
{
    Board,
    MoveHistory,
}

public sealed record GwentVisualObservation(
    GwentViewKind View,
    bool HasCardTooltip,
    double HistoryButtonConfidence,
    double TooltipConfidence,
    NormalizedRegion? TooltipRegion,
    bool IsCardSelectionOverlay = false,
    double CardSelectionConfidence = 0,
    string? ScreenHeader = null,
    int? OpponentHandCount = null,
    int? OpponentDeckCount = null,
    int? UserHandCount = null,
    int? UserScore = null,
    int? OpponentScore = null,
    int? UserCoins = null,
    int? OpponentCoins = null,
    PostMatchMmr? PostMatchMmr = null, PostMatchRank? PostMatchRank = null, bool? MatchHudVisible = null, int? UserDeckCount = null,
    bool UnresolvedHandSelection = false, bool FrameGeometrySupported = true, string? FrameGeometryWarning = null);

public sealed class GwentVisualStateDetector
{
    public GwentVisualObservation Analyze(PixelFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // A gold patch in the battlefield is not a history panel. Require the
        // active green footer icon and the dark, fixed panel header together.
        var activeIcon = Ratio(frame, new NormalizedRegion(0.073, 0.813, 0.107, 0.878),
            color => color.Green > 130 && color.Green > color.Red * 1.12 && color.Green > color.Blue * 1.15);
        var darkHeader = Ratio(frame, new NormalizedRegion(0.01, 0.13, 0.19, 0.21),
            color => color.Red + color.Green + color.Blue < 240);
        var historyConfidence = activeIcon >= 0.015 && darkHeader >= 0.65
            ? Math.Clamp(activeIcon * 8 + darkHeader * 0.3, 0, 1)
            : 0;
        var tooltip = LocateTooltip(frame);
        var matchHudVisible = MatchHudVisible(frame);
        // Battlefield skins may legitimately be very dark or heavily textured.
        // The global darkness heuristic is only a fallback for HUD-free transitions;
        // fixed match counters authenticate an ordinary board independently of art.
        var selectionConfidence = matchHudVisible ? 0 : CardSelectionConfidence(frame);
        return new GwentVisualObservation(
            historyConfidence >= 0.075 ? GwentViewKind.MoveHistory : GwentViewKind.Board,
            tooltip.Confidence >= 0.18,
            historyConfidence,
            tooltip.Confidence,
            tooltip.Region,
            selectionConfidence >= 0.58,
            selectionConfidence, MatchHudVisible: matchHudVisible);
    }

    // During round draws the enlarged incoming card uses the play-preview lane,
    // but both fixed /10 hand counters disappear. Keep the sighting, not a play claim.
    public static bool MatchHudVisible(PixelFrame frame) =>
        Ratio(frame, new(.955, .020, .981, .052), c => Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 170) >= .08 ||
        Ratio(frame, new(.955, .947, .981, .979), c => Math.Min(c.Red, Math.Min(c.Green, c.Blue)) >= 170) >= .08;

    private static double CardSelectionConfidence(PixelFrame frame)
    {
        var dark = 0;
        var samples = 0;
        for (var y = 0; y < frame.Height; y += 6)
        {
            for (var x = 0; x < frame.Width; x += 6)
            {
                var color = frame.GetPixel(x, y);
                var luminance = (color.Red * 0.2126 + color.Green * 0.7152 + color.Blue * 0.0722) / 255;
                if (luminance < 0.13)
                {
                    dark++;
                }

                samples++;
            }
        }

        if (samples == 0)
        {
            return 0;
        }

        var darkRatio = dark / (double)samples;
        return Math.Clamp((darkRatio - 0.55) / 0.22, 0, 1);
    }

    private static double Ratio(PixelFrame frame, NormalizedRegion region, Func<PixelColor, bool> predicate)
    {
        var count = 0;
        var hits = 0;
        for (var y = region.PixelTop(frame.Height); y < region.PixelBottom(frame.Height); y += 2)
        for (var x = region.PixelLeft(frame.Width); x < region.PixelRight(frame.Width); x += 2)
        {
            count++;
            if (predicate(frame.GetPixel(x, y))) hits++;
        }
        return count == 0 ? 0 : hits / (double)count;
    }

    private static double GoldRatio(PixelFrame frame, NormalizedRegion region)
    {
        var left = region.PixelLeft(frame.Width);
        var top = region.PixelTop(frame.Height);
        var right = region.PixelRight(frame.Width);
        var bottom = region.PixelBottom(frame.Height);
        var matches = 0;
        var samples = 0;

        for (var y = top; y < bottom; y += 2)
        {
            for (var x = left; x < right; x += 2)
            {
                var color = frame.GetPixel(x, y);
                samples++;
                if (color.Red >= 130 &&
                    color.Green >= 95 &&
                    color.Blue <= 100 &&
                    color.Red >= color.Blue * 1.45 &&
                    color.Green >= color.Blue * 1.15)
                {
                    matches++;
                }
            }
        }

        return samples == 0 ? 0 : matches / (double)samples;
    }

    private static TooltipMatch LocateTooltip(PixelFrame frame)
    {
        const int columns = 24;
        const int rows = 14;
        var candidate = new bool[columns, rows];
        var ratios = new double[columns, rows];

        for (var row = 1; row < rows - 1; row++)
        {
            for (var column = 1; column < columns - 1; column++)
            {
                var region = new NormalizedRegion(
                    column / (double)columns,
                    row / (double)rows,
                    (column + 1d) / columns,
                    (row + 1d) / rows);
                ratios[column, row] = BeigeRatio(frame, region);
                candidate[column, row] = ratios[column, row] >= 0.32;
            }
        }

        var visited = new bool[columns, rows];
        TooltipMatch best = default;
        for (var row = 1; row < rows - 1; row++)
        {
            for (var column = 1; column < columns - 1; column++)
            {
                if (!candidate[column, row] || visited[column, row])
                {
                    continue;
                }

                var queue = new Queue<(int Column, int Row)>();
                queue.Enqueue((column, row));
                visited[column, row] = true;
                var count = 0;
                var totalRatio = 0d;
                var minColumn = column;
                var maxColumn = column;
                var minRow = row;
                var maxRow = row;

                while (queue.TryDequeue(out var tile))
                {
                    count++;
                    totalRatio += ratios[tile.Column, tile.Row];
                    minColumn = Math.Min(minColumn, tile.Column);
                    maxColumn = Math.Max(maxColumn, tile.Column);
                    minRow = Math.Min(minRow, tile.Row);
                    maxRow = Math.Max(maxRow, tile.Row);

                    foreach (var neighbor in Neighbors(tile.Column, tile.Row))
                    {
                        if (neighbor.Column <= 0 || neighbor.Column >= columns - 1 ||
                            neighbor.Row <= 0 || neighbor.Row >= rows - 1 ||
                            visited[neighbor.Column, neighbor.Row] || !candidate[neighbor.Column, neighbor.Row])
                        {
                            continue;
                        }

                        visited[neighbor.Column, neighbor.Row] = true;
                        queue.Enqueue(neighbor);
                    }
                }

                if (count < 3)
                {
                    continue;
                }

                // Tooltip prose forms a compact rectangle. Beige card art, battlefield
                // texture and broadcast overlays can connect into a much larger sparse
                // component; raw tile count used to let those regions win.
                var boundingTiles = (maxColumn - minColumn + 1) * (maxRow - minRow + 1);
                var compactness = count / (double)boundingTiles;
                var tileWidth = maxColumn - minColumn + 1;
                var tileHeight = maxRow - minRow + 1;
                var normalizedWidth = tileWidth / (double)columns;
                var normalizedHeight = tileHeight / (double)rows;
                // A tooltip is a paper text panel, never nearly the whole board.
                // Beige battlefields and card-choice grids can join into a giant
                // component; treating that as a tooltip makes OCR scan most of the
                // screen and can mistake an unrelated visible title for a hover.
                // Narrow selected-hand tooltips can occupy only two coarse columns;
                // size alone is not enough to reject those readable panels.
                // Legitimate tooltips can merge with the selected card artwork and
                // become fairly wide/tall on coarse cells. Reject only components
                // spanning both axes like a battlefield-wide beige wash; single-axis
                // limits removed valid Fleder/Feast-of-Blood hand tooltips.
                if (normalizedWidth > .80 && normalizedHeight > .75 ||
                    normalizedWidth * normalizedHeight > .70)
                    continue;
                var confidence = Math.Min(1, count / 8d) * (totalRatio / count) * (.35 + .65 * compactness);
                if (confidence <= best.Confidence)
                {
                    continue;
                }

                best = new TooltipMatch(
                    confidence,
                    new NormalizedRegion(
                        minColumn / (double)columns,
                        minRow / (double)rows,
                        (maxColumn + 1d) / columns,
                        (maxRow + 1d) / rows));
            }
        }

        // A real tooltip body can touch a beige battlefield in the coarse mask,
        // turning the connected component into a screen-sized wash. Recover the
        // local paper panel only when a compact pale rectangle has a dark header
        // with a small amount of bright title lettering directly above it.
        if(best.Confidence>0) return best;
        return LocateAnchoredTooltip(frame);
    }

    private static TooltipMatch LocateAnchoredTooltip(PixelFrame frame)
    {
        // Selected hand cards and right-side board hovers use these stable tooltip
        // lanes even though their pale body may touch the beige battlefield mask.
        foreach(var (body,header) in new[]
        {
            (new NormalizedRegion(.455,.755,.635,.985),new NormalizedRegion(.455,.655,.635,.765)),
            (new NormalizedRegion(.615,.805,.800,.985),new NormalizedRegion(.615,.705,.800,.815)),
            (new NormalizedRegion(.665,.335,.845,.455),new NormalizedRegion(.665,.235,.845,.345)),
        })
        {
            var paper=BeigeRatio(frame,body);
            var dark=Ratio(frame,header,c=>c.Red+c.Green+c.Blue<410);
            var letters=Ratio(frame,header,c=>Math.Min(c.Red,Math.Min(c.Green,c.Blue))>=155 &&
                Math.Max(c.Red,Math.Max(c.Green,c.Blue))-Math.Min(c.Red,Math.Min(c.Green,c.Blue))<90);
            // The title band spans the panel width. A vertical board card over a
            // beige field can satisfy the paper colour, but not this dark-band fill.
            if(paper>=.16 && dark>=.50 && letters is >=.004 and <=.16)
                return new(Math.Min(1,.25+paper*.45+dark*.2+letters),body);
        }
        return default;
    }

    private static double BeigeRatio(PixelFrame frame, NormalizedRegion region)
    {
        var left = region.PixelLeft(frame.Width);
        var top = region.PixelTop(frame.Height);
        var right = region.PixelRight(frame.Width);
        var bottom = region.PixelBottom(frame.Height);
        var matches = 0;
        var samples = 0;

        for (var y = top; y < bottom; y += 3)
        {
            for (var x = left; x < right; x += 3)
            {
                var color = frame.GetPixel(x, y);
                samples++;
                if (color.Red >= 105 &&
                    color.Green >= 82 &&
                    color.Blue >= 45 &&
                    color.Red - color.Blue is >= 20 and <= 105 &&
                    color.Green - color.Blue is >= 8 and <= 75 &&
                    color.Red - color.Green <= 65)
                {
                    matches++;
                }
            }
        }

        return samples == 0 ? 0 : matches / (double)samples;
    }

    private static IEnumerable<(int Column, int Row)> Neighbors(int column, int row)
    {
        yield return (column - 1, row);
        yield return (column + 1, row);
        yield return (column, row - 1);
        yield return (column, row + 1);
    }

    private readonly record struct TooltipMatch(double Confidence, NormalizedRegion? Region);
}
