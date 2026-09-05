namespace GwentCompanion.Core.Inference;

/// <summary>
/// Controls attention in Plays, not recognition, arrival origins or deck inference.
/// A missing card after an independent trigger is more useful than an immediate package.
/// </summary>
public static class SummonWatchPresentation
{
    private static readonly HashSet<string> AutomaticArrivals =
    [
        "112210", // Roach
        "202397", // Knickers: random; non-arrival is never a reviewed miss
        "142211", // Aelirenn
        "202445", "203282", // Affan / Radovid: Judgment
        "202879", // Madoc
        "202367", "202608", "203217", // Flying Redanian / Winter Queen / Anglerfish
        "162310", "202415", "202908", // Nauzicaa Brigade / Highwaymen / Mage Assassin
        "203096", "203100", "203097", // Cerys: Fearless / King of Beggars / Milva
        "203224", "203191", "200088", // Redanian Secret Service / Ard Feainn / Hubert
        "122318", // Siege Master: arrival from hand
    ];

    private static readonly HashSet<string> ImmediatePackages =
    [
        // Deploy pulls matching copies or named partners. Conditional deploy bonuses
        // can still appear under Synergies/Bonuses; they do not need duplicate watches.
        "132310", "201559", "202273", "202334", "202521", "202549", "203089", "162301",
        "112202", "112203", "112204", // Base silver witcher trio, not their variants
        // Direct one-shot tutors/resurrections are bookkeeping, not future-arrival watches.
        "203223", "203200", "203286", "203145", "202998", "162105",
        "203055", "203054", "152211", "203075",
    ];

    public static bool ShouldDisplay(TacticalWatchRow row, IReadOnlySet<string> supported)
    {
        // Wanderers' hand movement is identity evidence, not a useful absence/arrival watch.
        if (!row.Rule.Summon || row.Rule.Card.Id == "202953" || ImmediatePackages.Contains(row.Rule.Card.Id)) return false;
        if (AutomaticArrivals.Contains(row.Rule.Card.Id)) return true;
        // A visible/referenced source with a future Order, Timer, Deathwish or return
        // remains useful. Do not advertise every possible source in a faction up front.
        return supported.Contains(row.Rule.Card.Id) || row.Promoted ||
            row.State is TacticalState.Ready or TacticalState.Pending or TacticalState.Missed or TacticalState.Seen or TacticalState.Unavailable;
    }
}
