namespace GwentCompanion.Core.Inference;

public enum DevotionProofKind
{
    EvolvedForm, Survival, WiderChoice, ExtraWeather, ExtraBleedingTick, PassBoost,
    UnconditionalConspiracy, RefreshedOrder, ThreeSpyingTargets, ExtraSummons,
    LargerBoost, HigherSetPower, BoostedDeckCopy, Immunity, ConditionalSelfSummon,
    ExtraArtifactTransform, CooldownReduction, CombinedRows, GraveyardEntrySummon,
    VeteranBasePower, UnconditionalDeathblow, NoSacrificeCoins, BoostedSpawn, UnboostedDamage
}

public sealed record DevotionInteraction(string CardId, string Name, DevotionProofKind Proof, string PositiveObservation);

/// <summary>Audited 14.8 catalog of Devotion payoffs. Entries describe positive evidence; card presence alone is never activation.</summary>
public static class DevotionInteractionCatalog
{
    public static IReadOnlyList<DevotionInteraction> All { get; } =
    [
        new("202604", "Auberon: Invader", DevotionProofKind.EvolvedForm, "final faction evolution is recognized"),
        new("202614", "Aen Elle Conqueror", DevotionProofKind.Survival, "its self-destroy Deploy is visibly cancelled"),
        new("131102", "Ge'els", DevotionProofKind.WiderChoice, "it plays a non-Special Wild Hunt card from deck"),
        new("203158", "Tir ná Lia", DevotionProofKind.ExtraWeather, "its Order also creates the carried Frost on both enemy rows"),
        new("202889", "Unseen Elder", DevotionProofKind.ExtraBleedingTick, "enemy Bleeding visibly ticks at the end of its controller's turn"),
        new("202608", "Winter Queen", DevotionProofKind.PassBoost, "its both-passed Frost boost is measured"),
        new("202663", "Amnesty", DevotionProofKind.UnconditionalConspiracy, "a non-Spying seized target receives the +2 Conspiracy boost"),
        new("202881", "Emhyr var Emreis", DevotionProofKind.RefreshedOrder, "its spent Order refreshes at end of turn"),
        new("202660", "Fergus var Emreis", DevotionProofKind.ThreeSpyingTargets, "three enemy units gain Spying from the Deploy"),
        new("202656", "Usurper: General", DevotionProofKind.EvolvedForm, "final faction evolution is recognized"),
        new("202652", "Kerack Marine", DevotionProofKind.LargerBoost, "its Order produces one measured +4 boost instead of +2"),
        new("202647", "King Belohun", DevotionProofKind.HigherSetPower, "a played sub-5 unit is measured at 6 rather than 5"),
        new("202886", "King Foltest", DevotionProofKind.BoostedDeckCopy, "the spawned bottom-deck base copy is later verified with +1"),
        new("203281", "Princess Adda", DevotionProofKind.Immunity, "Immunity is measured after Deploy"),
        new("203282", "Radovid: Judgment", DevotionProofKind.ConditionalSelfSummon, "leader charges reach zero and Radovid arrives without a play"),
        new("202643", "Viraxas: Outcast", DevotionProofKind.EvolvedForm, "final faction evolution is recognized"),
        new("202671", "Eithné: Mother", DevotionProofKind.EvolvedForm, "final faction evolution is recognized"),
        new("202885", "Eldain", DevotionProofKind.ExtraArtifactTransform, "a non-Trap artifact is included in the transformation"),
        new("202675", "Freixenet", DevotionProofKind.CooldownReduction, "Cooldown falls by the measured self-Vitality duration"),
        new("202680", "Oakcritters", DevotionProofKind.CombinedRows, "one Deploy both creates its copy and gives Bleeding"),
        new("202883", "Eist Tuirseach", DevotionProofKind.GraveyardEntrySummon, "a non-discard graveyard entry triggers its summon"),
        new("202616", "Harald: Warmonger", DevotionProofKind.EvolvedForm, "final faction evolution is recognized"),
        new("202620", "Skjordal Drummond", DevotionProofKind.VeteranBasePower, "its base power includes Veteran growth"),
        new("202622", "War of Clans", DevotionProofKind.UnconditionalDeathblow, "a graveyard Warrior is played without a Deathblow"),
        new("202630", "Jacques de Aldersberg", DevotionProofKind.EvolvedForm, "final faction evolution is recognized"),
        new("202640", "Mutants Maker", DevotionProofKind.NoSacrificeCoins, "three Coins arrive without an allied destruction"),
        new("202632", "Ulrich", DevotionProofKind.BoostedSpawn, "the spawned Firesworn base copy is measured at +2"),
        new("202891", "Whoreson Junior", DevotionProofKind.UnboostedDamage, "his Deploy damages a target that was not boosted")
    ];
}
