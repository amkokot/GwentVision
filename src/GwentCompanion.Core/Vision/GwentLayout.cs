namespace GwentCompanion.Core.Vision;

public static class GwentLayout
{
    public static readonly NormalizedRegion OpponentHand = new(0.25, 0.00, 0.78, 0.17);
    public static readonly NormalizedRegion OpponentBoard = new(0.18, 0.13, 0.82, 0.48);
    public static readonly NormalizedRegion PlayerBoard = new(0.18, 0.48, 0.82, 0.80);
    public static readonly NormalizedRegion PlayerHand = new(0.20, 0.76, 0.82, 1.00);
    public static readonly NormalizedRegion HistoryPanel = new(0.025, 0.08, 0.225, 0.94);
    public static readonly NormalizedRegion HistoryButton = new(0.096, 0.815, 0.128, 0.875);
    public static readonly NormalizedRegion OpponentLeader = new(0.00, 0.02, 0.18, 0.30);
    // The small ability medallion is persistent and much cheaper to identify than
    // the animated leader model or the surrounding player portrait/name plate.
    // Tight around the ability plaque rather than the animated leader model. At
    // 1280x720 the actual badge is only about 24x49 px; a broad crop diluted its
    // emblem enough to miss low-detail abilities such as Guerilla Tactics.
    public static readonly NormalizedRegion OpponentLeaderAbility = new(0.019, 0.128, 0.043, 0.185);
    public static readonly NormalizedRegion OpponentLeaderAbilityWide = new(0.010, 0.135, 0.055, 0.210);
    public static readonly NormalizedRegion PlayerLeader = new(0.00, 0.69, 0.18, 1.00);
    public static readonly NormalizedRegion ScoreColumn = new(0.88, 0.20, 1.00, 0.78);
}
