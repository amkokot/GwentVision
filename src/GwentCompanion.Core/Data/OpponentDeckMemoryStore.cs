using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Data;

public sealed record LearnedOpponentEncounter(string SessionId, DateTimeOffset At, string? Faction,
    string? StartingLeader, int? LeaderBonus, string? StratagemId, int? StartingSize, int MinimumSize,
    IReadOnlyList<ObservedCard> Cards, IReadOnlyList<ResolvedDeckCondition> Conditions,
    OpponentProvisionBudget Budget, IReadOnlyList<DeckMutation>? DeckChanges = null,
    IReadOnlyList<ZoneCardEvidence>? ZoneEvidence = null, IReadOnlyList<HiddenTrapEvidence>? HiddenTraps = null,
    int? MatchMmr = null, PostMatchMmr? PostMatchMmr = null, string? Patch = null,
    IReadOnlyList<DeckCompositionClue>? CompositionClues = null,
    IReadOnlyList<OpponentSequenceEvidence>? SequenceEvidence = null,
    PostMatchRank? PostMatchRank = null);
public sealed record ReviewedDeckHeader(string? Faction, string? Leader, int? LeaderBonus, string? StratagemId);
public sealed record LearnedOpponentDeck(string Id, string Name, DateTimeOffset UpdatedAt,
    IReadOnlyList<LearnedOpponentEncounter> Encounters, bool Complete = false, string? VariantOf = null,
    bool NeedsReview = false, string? LibraryFingerprint = null, string? AssociationReason = null,
    IReadOnlyList<ObservedCard>? ReviewedCards = null, IReadOnlyList<DeckCard>? SuggestedCards = null,
    ReviewedDeckHeader? ReviewedHeader = null)
{
    [JsonIgnore] public int EncounterCount => Encounters.Select(e => e.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    [JsonIgnore] public DateTimeOffset FirstSeen => Encounters.Select(e => e.At).DefaultIfEmpty(UpdatedAt).Min();
    [JsonIgnore] public DateTimeOffset LastSeen => Encounters.Select(e => e.At).DefaultIfEmpty(UpdatedAt).Max();
    [JsonIgnore] public IReadOnlyList<ObservedCard> Cards => ReviewedCards ?? Encounters.SelectMany(item => item.Cards)
        .GroupBy(item => item.Card.Id).Select(group => group.MaxBy(item => item.ObservedCopies)!).ToArray();
    [JsonIgnore] public IReadOnlyList<ObservedCard> DraftCards => ReviewedCards ?? Cards.Concat((SuggestedCards ?? [])
        .Select(c => new ObservedCard(c.Card, CardProvenance.Unknown, 0, UpdatedAt, "Unseen algorithm suggestion; NOT observed", c.Count)))
        .GroupBy(c => c.Card.Id).Select(g => g.OrderByDescending(c => c.ObservedCopies).ThenBy(c => c.Provenance == CardProvenance.Unknown).First()).ToArray();
    [JsonIgnore] public string? Faction => ReviewedHeader is not null ? ReviewedHeader.Faction : Encounters.LastOrDefault(item => item.Faction is not null)?.Faction;
    [JsonIgnore] public string? Leader => ReviewedHeader is not null ? ReviewedHeader.Leader : Encounters.LastOrDefault(item => item.StartingLeader is not null)?.StartingLeader;
    [JsonIgnore] public string? StratagemId => ReviewedHeader is not null ? ReviewedHeader.StratagemId : Encounters.LastOrDefault(item => item.StratagemId is not null)?.StratagemId;
    [JsonIgnore] public int? LeaderBonus => ReviewedHeader is not null ? ReviewedHeader.LeaderBonus : Encounters.LastOrDefault(item => item.LeaderBonus is not null)?.LeaderBonus;
    [JsonIgnore] public int MinimumSize => Math.Max(25, Encounters.Select(item => item.MinimumSize).DefaultIfEmpty(25).Max());
    [JsonIgnore] public int? StartingSize => Encounters.LastOrDefault(item => item.StartingSize is not null)?.StartingSize;
    [JsonIgnore] public IReadOnlyList<ResolvedDeckCondition> Conditions => Encounters.SelectMany(item => item.Conditions)
        .GroupBy(item => item.Condition).Select(group => group.OrderBy(item => item.Suggested).ThenByDescending(item => item.At).First()).ToArray();
    [JsonIgnore] public OpponentProvisionBudget Budget => OpponentProvisionCalculator.Calculate(Cards,
        LeaderBonus is { } bonus ? 150 + bonus : ReviewedHeader is not null ? null : Encounters.Select(item => item.Budget.Capacity).Max(), StartingSize ?? MinimumSize,
        Conditions.Any(item => item.Condition == DeckCondition.Musicians));
}
public sealed record LearnedDeckMatch(LearnedOpponentDeck Deck, int SharedCopies, int NewCopies,
    double Score, IReadOnlyList<string> Conflicts)
{
    public bool Possible => Conflicts.Count == 0;
    public string Summary => $"Faced {Deck.EncounterCount}× · {SharedCopies} shared · {NewCopies} newly seen · " +
        (Possible ? Deck.Complete ? "complete reference compatible" : "incomplete: missing identities remain possible" : string.Join("; ", Conflicts));
}

/// <summary>Raw encounters and reviewed drafts; inferred library associations stay separate from observed cards.</summary>
public sealed class OpponentDeckMemoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() } };
    public IReadOnlyList<LearnedOpponentDeck> Records { get; private set; } = [];
    public static OpponentDeckMemoryStore Load(string path)
    {
        var result = new OpponentDeckMemoryStore();
        if (!File.Exists(path)) return result;
        // Do not overwrite malformed/unknown-schema memory with an empty collection.
        var file = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Opponent memory could not be read.");
        if (file.Schema != 1) throw new InvalidDataException("Unsupported opponent-memory schema.");
        result.Records = file.Decks; return result;
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new MemoryFile(1, Records), Options));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public LearnedOpponentDeck Record(LearnedOpponentEncounter encounter, string name, string? mergeId = null,
        bool reviewedComplete = false, string? variantOf = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateEncounter(encounter);
        encounter = encounter with { Patch = encounter.Patch ?? DeckPatchMetadata.Current(encounter.At).Label };
        var existing = mergeId is null ? null : Records.Single(item => item.Id == mergeId);
        if (mergeId is null)
        {
            // Re-saving a match updates it; it is not another encounter. Across matches,
            // auto-merge only reviewed COMPLETE exact compositions with compatible headers.
            var sameSession = Records.Where(r => r.Encounters.Any(e => e.SessionId.Equals(encounter.SessionId, StringComparison.OrdinalIgnoreCase)) && Match(r, encounter).Possible).ToArray();
            if (sameSession.Length == 1) existing = sameSession[0];
            else if (sameSession.Length == 0 && reviewedComplete)
            {
                var exact = Records.Where(r => r.Complete && SameCards(r.Cards, encounter.Cards) && Match(r, encounter).Possible).ToArray();
                if (exact.Length == 1) existing = exact[0];
            }
        }
        if (existing is not null)
        {
            var match = Match(existing, encounter, Records);
            if (!match.Possible) throw new InvalidOperationException("Keep this as a separate variant: " + string.Join("; ", match.Conflicts));
            if (match.SharedCopies < 3 && existing.Encounters.All(item => item.SessionId != encounter.SessionId))
                throw new InvalidOperationException("Fewer than three shared copies: save separately until the match is better supported.");
        }
        var previous = existing?.Encounters.FirstOrDefault(e => e.SessionId.Equals(encounter.SessionId, StringComparison.OrdinalIgnoreCase));
        if (previous is not null) encounter = encounter with
        {
            At = previous.At, // Re-saving a match must not rejuvenate its prevalence.
            Patch = previous.Patch ?? DeckPatchMetadata.Current(previous.At).Label,
            MatchMmr = encounter.MatchMmr ?? previous.MatchMmr,
            PostMatchMmr = encounter.PostMatchMmr ?? previous.PostMatchMmr,
            PostMatchRank = encounter.PostMatchRank ?? previous.PostMatchRank,
            StartingSize = encounter.StartingSize ?? previous.StartingSize, StartingLeader = encounter.StartingLeader ?? previous.StartingLeader,
            LeaderBonus = encounter.LeaderBonus ?? previous.LeaderBonus, StratagemId = encounter.StratagemId ?? previous.StratagemId,
            Faction = encounter.Faction ?? previous.Faction,
            Cards = previous.Cards.Concat(encounter.Cards).GroupBy(c => c.Card.Id).Select(g => g.MaxBy(c => c.ObservedCopies)!).ToArray()
        };
        var encounters = (existing?.Encounters ?? []).Where(item => !item.SessionId.Equals(encounter.SessionId, StringComparison.OrdinalIgnoreCase)).Append(encounter).ToArray();
        var record = new LearnedOpponentDeck(existing?.Id ?? Guid.NewGuid().ToString("N"), name.Trim(), encounter.At,
            encounters, reviewedComplete || existing?.Complete == true, existing?.VariantOf ?? variantOf,
            existing?.NeedsReview ?? false, existing?.LibraryFingerprint, existing?.AssociationReason,
            existing?.ReviewedCards is { } reviewed ? reviewed.Concat(encounter.Cards).GroupBy(c => c.Card.Id).Select(g => g.MaxBy(c => c.ObservedCopies)!).ToArray() : null,
            existing?.SuggestedCards);
        var combined = AsEncounter(record);
        var conflict = ConstraintsConflict(combined);
        if (conflict.Count > 0) throw new InvalidOperationException("Cannot combine: " + string.Join("; ", conflict));
        if (record.Complete && (record.StartingSize is null || record.Cards.Sum(item => item.ObservedCopies) != record.StartingSize ||
            record.LeaderBonus is null || record.Cards.Where(item => item.Card.Kind == CardKind.Unit).Sum(item => item.ObservedCopies) < 13))
            throw new InvalidOperationException("A complete list needs a confirmed starting size, every physical copy, at least 13 units and the original leader. Leave Complete unchecked for partial memory.");
        Records = Records.Where(item => item.Id != record.Id).Append(record).OrderByDescending(item => item.UpdatedAt).ToArray();
        return record;
    }

    public IReadOnlyList<LearnedDeckMatch> FindMatches(LearnedOpponentEncounter encounter) => Records
        .Select(record => Match(record, encounter, Records)).OrderByDescending(item => item.Possible)
        .ThenByDescending(item => item.Score).ThenByDescending(item => item.Deck.UpdatedAt).ToArray();

    /// <summary>Durable raw capture, including contradictory detections for later correction. Never grants Complete.</summary>
    public LearnedOpponentDeck Capture(LearnedOpponentEncounter encounter, string name, string? mergeId,
        string? libraryFingerprint, string reason)
    {
        ValidateEncounter(encounter);
        encounter = encounter with { Patch = encounter.Patch ?? DeckPatchMetadata.Current(encounter.At).Label };
        var previousRecord = Records.FirstOrDefault(r => r.Encounters.Any(e => e.SessionId == encounter.SessionId));
        var existing = previousRecord ?? Records.FirstOrDefault(r => r.Id == mergeId);
        if (previousRecord is null && existing is not null && !Match(existing, encounter).Possible)
            throw new InvalidOperationException("Conflicting encounter cannot be merged automatically.");
        var previous = existing?.Encounters.FirstOrDefault(e => e.SessionId == encounter.SessionId);
        // Later result-panel animation updates rating only; it must not undo review edits or add an occurrence.
        if (previous is not null) encounter = previous with { PostMatchMmr = encounter.PostMatchMmr ?? previous.PostMatchMmr,
            PostMatchRank = encounter.PostMatchRank ?? previous.PostMatchRank,
            MatchMmr = encounter.MatchMmr ?? previous.MatchMmr };
        var record = existing is null
            ? new LearnedOpponentDeck(Guid.NewGuid().ToString("N"), name, encounter.At, [encounter],
                NeedsReview: libraryFingerprint is null, LibraryFingerprint: libraryFingerprint, AssociationReason: reason)
            : existing with { UpdatedAt = new[] { existing.UpdatedAt, encounter.At }.Max(),
                LibraryFingerprint = existing.LibraryFingerprint ?? libraryFingerprint,
                AssociationReason = libraryFingerprint is null ? existing.AssociationReason : reason,
                NeedsReview = libraryFingerprint is null && existing.NeedsReview,
                Encounters = existing.Encounters.Where(e => e.SessionId != encounter.SessionId).Append(encounter).ToArray(),
                ReviewedCards = previous is null && existing.ReviewedCards is { } reviewed
                    ? reviewed.Concat(encounter.Cards).GroupBy(c => c.Card.Id).Select(g => g.MaxBy(c => c.ObservedCopies)!).ToArray() : existing.ReviewedCards };
        Records = Records.Where(r => r.Id != record.Id).Append(record).OrderByDescending(r => r.UpdatedAt).ToArray();
        return record;
    }

    public LearnedOpponentDeck Review(string id, string name, IReadOnlyList<ObservedCard> cards, bool needsReview, ReviewedDeckHeader? header = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160) throw new ArgumentException("Use a name of 1–160 characters.");
        var existing = Records.Single(r => r.Id == id);
        var reviewed = existing with { Name = name.Trim(), ReviewedCards = cards.Select(c => c.Provenance == CardProvenance.Unknown
                ? c with { Provenance = CardProvenance.ProbableStartingDeck, Evidence = "User saved proposed starting list; not an observed card" } : c).ToArray(), NeedsReview = needsReview,
            Complete = false, LibraryFingerprint = null, ReviewedHeader = header ?? existing.ReviewedHeader,
            AssociationReason = "User-edited partial list; raw per-match observations retained separately." };
        var encounter = AsEncounter(reviewed);
        ValidateEncounter(encounter);
        var conflicts = ConstraintsConflict(encounter);
        if (conflicts.Count > 0 && !needsReview) throw new InvalidOperationException(string.Join("; ", conflicts) + ". Correct the cards or keep Needs review checked.");
        Records = Records.Select(r => r.Id == id ? reviewed : r).ToArray();
        return reviewed;
    }

    public LearnedOpponentDeck Suggest(string id, IReadOnlyList<DeckCard> cards)
    {
        var old = Records.Single(r => r.Id == id);
        if (old.ReviewedCards is not null) return old; // Never overwrite the user's later edit.
        var suggested = cards.Where(c => c.Card.CanBeInStartingDeck && c.Card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact &&
                c.Count > 0 && c.Count <= (c.Card.IsGold ? 1 : 2)).GroupBy(c => c.Card.Id).Select(g => g.MaxBy(c => c.Count)!).Take(100).ToArray();
        var record = old with { SuggestedCards = suggested };
        Records = Records.Select(r => r.Id == id ? record : r).ToArray(); return record;
    }

    public static LearnedDeckMatch Match(LearnedOpponentDeck record, LearnedOpponentEncounter encounter,
        IReadOnlyList<LearnedOpponentDeck>? corpus = null)
    {
        var conflicts = new List<string>();
        void Compare(string? a, string? b, string label)
        { if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !a.Equals(b, StringComparison.OrdinalIgnoreCase)) conflicts.Add(label + " differs"); }
        Compare(record.Faction, encounter.Faction, "Faction"); Compare(record.Leader, encounter.StartingLeader, "Starting leader");
        Compare(record.StratagemId, encounter.StratagemId, "Starting stratagem");
        if (record.StartingSize is { } size && encounter.StartingSize is { } otherSize && size != otherSize) conflicts.Add("Starting size differs");
        var known = record.Cards.ToDictionary(item => item.Card.Id);
        var shared = encounter.Cards.Sum(item => Math.Min(item.ObservedCopies, known.GetValueOrDefault(item.Card.Id)?.ObservedCopies ?? 0));
        var newCopies = encounter.Cards.Sum(item => Math.Max(0, item.ObservedCopies - (known.GetValueOrDefault(item.Card.Id)?.ObservedCopies ?? 0)));
        if (record.Complete && newCopies > 0) conflicts.Add("Complete list lacks an observed copy");
        var combined = record.Encounters.Append(encounter).ToArray();
        var union = record with { Encounters = combined, ReviewedCards = record.Cards.Concat(encounter.Cards)
            .GroupBy(c => c.Card.Id).Select(g => g.MaxBy(c => c.ObservedCopies)!).ToArray() };
        conflicts.AddRange(ConstraintsConflict(AsEncounter(union)));
        // Missing entries in an INCOMPLETE record carry ZERO penalty. Shared, rare
        // identities add support; score is a retrieval score, never a probability.
        var score = encounter.Cards.Where(item => known.ContainsKey(item.Card.Id)).Sum(item =>
        {
            var support = corpus?.Count(deck => deck.Cards.Any(card => card.Card.Id == item.Card.Id)) ?? 1;
            return Math.Min(item.ObservedCopies, known[item.Card.Id].ObservedCopies) *
                (1 + Math.Log(1 + (corpus?.Count ?? 1) / (double)Math.Max(1, support)));
        });
        if (record.Leader is not null && record.Leader == encounter.StartingLeader) score += 2;
        if (record.StratagemId is not null && record.StratagemId == encounter.StratagemId) score += .5;
        score += shared * shared / 25d; // Long exact overlap is increasingly informative.
        return new(record, shared, newCopies, score, conflicts.Distinct().ToArray());
    }

    public static LearnedOpponentEncounter AsEncounter(LearnedOpponentDeck record) => new("combined", record.UpdatedAt,
        record.Faction, record.Leader, record.LeaderBonus, record.StratagemId, record.StartingSize, record.MinimumSize,
        record.Cards, record.Conditions, record.Budget);

    private static IReadOnlyList<string> ConstraintsConflict(LearnedOpponentEncounter encounter)
    {
        var result = new List<string>(); var knowledge = new OpponentKnowledge();
        foreach (var condition in encounter.Conditions)
            if (condition.Suggested) knowledge.Suggest(condition.Condition, condition.At, condition.Evidence);
            else knowledge.Resolve(condition.Condition, condition.At, condition.Evidence);
        var rules = knowledge.Assess(encounter.Cards);
        if (new[] { rules.Shupe, rules.GoldenNekker, rules.Renfri, rules.Devotion, rules.Musicians! }.Any(item => item.Reason.StartsWith("CONFLICT"))) result.Add("Deck-building condition conflicts");
        var size = encounter.Cards.Sum(item => item.ObservedCopies);
        if (encounter.StartingSize is { } total && (size > total || knowledge.MinimumSize(encounter.Cards) > total)) result.Add("Too many cards for confirmed starting size");
        if (encounter.Budget.Conflict) result.Add("Combined provision budget exceeded");
        if (encounter.Faction is { } faction && encounter.Cards.Any(item => !FactionCompatibility.IsPlayableBy(item.Card, faction))) result.Add("Starting-card faction conflict");
        return result;
    }
    private static void ValidateEncounter(LearnedOpponentEncounter encounter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encounter.SessionId);
        if (encounter.MatchMmr is < 0 or > 10000) throw new InvalidOperationException("MMR must be between 0 and 10000, or left unknown.");
        if (encounter.PostMatchMmr is { } mmr && (mmr.RatingAfter is < 0 or > 10000 || mmr.Change is < -200 or > 200 ||
            mmr.RatingBefore is < 0 or > 10000)) throw new InvalidOperationException("Invalid observed post-match MMR.");
        if (encounter.PostMatchRank?.Rank is < 0 or > 30) throw new InvalidOperationException("Invalid observed post-match rank.");
        if (encounter.Cards.Count == 0) throw new InvalidOperationException("No starting-deck evidence to save yet.");
        if (encounter.Cards.Any(item => !StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) || !item.Card.CanBeInStartingDeck ||
            item.Card.Kind is not (CardKind.Unit or CardKind.Special or CardKind.Artifact) || item.ObservedCopies < 1 || item.ObservedCopies > (item.Card.IsGold ? 1 : 2)))
            throw new InvalidOperationException("Only valid starting-deck copy evidence may enter learned memory; generated cards and guesses stay in the audit.");
    }
    private static bool SameCards(IEnumerable<ObservedCard> left, IEnumerable<ObservedCard> right)
    {
        string Key(IEnumerable<ObservedCard> cards) => string.Join(';', cards.GroupBy(c => c.Card.Id).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + ":" + g.Max(c => c.ObservedCopies)));
        return Key(left) == Key(right);
    }
    private sealed record MemoryFile(int Schema, IReadOnlyList<LearnedOpponentDeck> Decks);
}
