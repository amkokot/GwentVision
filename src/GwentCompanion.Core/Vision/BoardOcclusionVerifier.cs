using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Vision;

public static class BoardOcclusionVerifier
{
    /// <summary>Two pale adjacent artworks can resemble the tooltip's beige-paper colour mask.</summary>
    public static GwentVisualObservation Refine(GwentVisualObservation screen, IReadOnlyList<CardSighting> sightings, CardDefinition? hovered)
    {
        if (!screen.HasCardTooltip || screen.TooltipConfidence >= .25 || hovered is not null ||
            screen.TooltipRegion is not { } tip || screen.View != GwentViewKind.Board || screen.IsCardSelectionOverlay) return screen;
        var cards = sightings.Where(s => s.Source == CardSightSource.Board && s.Card.Kind == CardKind.Unit && s.Distance <= .4 &&
            (s.Region.Left+s.Region.Right)/2 >= tip.Left && (s.Region.Left+s.Region.Right)/2 <= tip.Right &&
            (s.Region.Top+s.Region.Bottom)/2 >= tip.Top && (s.Region.Top+s.Region.Bottom)/2 <= tip.Bottom).ToArray();
        // Require independently located, non-overlapping copies, not two templates for the same card rectangle.
        if (cards.Any(a => cards.Any(b => a.Side == b.Side && a.Card.Id == b.Card.Id &&
            (a.Region.Right <= b.Region.Left + .008 || b.Region.Right <= a.Region.Left + .008))))
            return screen with { HasCardTooltip = false, TooltipRegion = null, TooltipConfidence = 0 };
        return screen;
    }
}
