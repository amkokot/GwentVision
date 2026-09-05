using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Data;

/// <summary>Members are exact composition fingerprints, never aliases or positional variation numbers.</summary>
public sealed record DeckVariationGroup(string Id, string Name, string[] Members);
public sealed record DeckVariationPolicy(string Metric, double Threshold, IReadOnlyDictionary<string, int> Prices)
{
    public const string CurrentMetric = "shared-copy-provisions / max-listed-provisions; construction-gated; complete-link-v1";
}

public sealed partial class DeckLibrary
{
    private readonly List<DeckVariationGroup> _variationGroups = [];
    private bool _variationGroupsDirty = true;
    public IReadOnlyList<DeckVariationGroup> VariationGroups => _variationGroups;
    public DeckVariationPolicy? VariationPolicy { get; private set; }

    /// <summary>Existing memberships stay stable. A new variant must fit every member, preventing similarity chains.</summary>
    public void EnsureVariationGroups(IEnumerable<CardDefinition>? catalog = null)
    {
        if (!_variationGroupsDirty && VariationPolicy is not null) return;
        if (VariationPolicy is null)
        {
            var metric = new DeckVariationSimilarity(Decks, catalog);
            VariationPolicy = new(DeckVariationPolicy.CurrentMetric, DeckVariationSimilarity.Threshold, metric.Prices);
        }
        else
        {
            var prices = new Dictionary<string, int>(VariationPolicy.Prices);
            foreach (var card in _records.SelectMany(r => r.Deck.Cards).Select(c => c.Card)) prices.TryAdd(card.Id, Math.Max(1, card.Provision));
            VariationPolicy = VariationPolicy with { Prices = prices };
        }
        var similarity = new DeckVariationSimilarity(VariationPolicy.Prices);
        var records = _records.ToDictionary(r => r.Fingerprint);
        var assigned = _variationGroups.SelectMany(g => g.Members).ToHashSet(StringComparer.Ordinal);
        foreach (var record in _records.OrderBy(r => r.Fingerprint, StringComparer.Ordinal))
        {
            if (assigned.Contains(record.Fingerprint)) continue;
            var match = _variationGroups.Where(g => g.Members.All(fp => similarity.IsVariation(record.Deck, records[fp].Deck, VariationPolicy.Threshold)))
                .OrderByDescending(g => g.Members.Min(fp => similarity.Compare(record.Deck, records[fp].Deck).Ratio))
                .ThenBy(g => g.Id, StringComparer.Ordinal).FirstOrDefault();
            if (match is null) _variationGroups.Add(new("family-" + record.Fingerprint, record.Deck.Name, [record.Fingerprint]));
            else _variationGroups[_variationGroups.IndexOf(match)] = match with { Members = match.Members.Append(record.Fingerprint).ToArray() };
        }
        _variationGroupsDirty = false;
    }

    public DeckVariationGroup? VariationGroupFor(string deckId)
    {
        EnsureVariationGroups(); var record = Find(deckId);
        return record is null ? null : _variationGroups.FirstOrDefault(g => g.Members.Contains(record.Fingerprint, StringComparer.Ordinal));
    }
    public LibraryDeck[] GroupVariants(DeckVariationGroup group) => group.Members.Select(fp => _records.First(r => r.Fingerprint == fp)).ToArray();
    public string VariationLabel(string deckId)
    {
        var group = VariationGroupFor(deckId); var record = Find(deckId);
        if (group is null || record is null) return "";
        var position = Array.IndexOf(group.Members, record.Fingerprint) + 1;
        return $"{group.Name} · variation {position}/{group.Members.Length} · {record.Fingerprint[..8]}";
    }
    private void ValidateVariationGroups()
    {
        var members = _variationGroups.SelectMany(g => g.Members).ToArray();
        var fingerprints = _records.Select(r => r.Fingerprint).ToHashSet(StringComparer.Ordinal);
        if (_variationGroups.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != _variationGroups.Count ||
            members.Distinct(StringComparer.Ordinal).Count() != members.Length || members.Any(fp => !fingerprints.Contains(fp)) ||
            _variationGroups.Any(g => string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name) || g.Members.Length == 0))
            throw new InvalidDataException("Invalid or overlapping variation group membership.");
        if (VariationPolicy is not null && (VariationPolicy.Metric != DeckVariationPolicy.CurrentMetric ||
            VariationPolicy.Threshold is < .8 or > 1 || !double.IsFinite(VariationPolicy.Threshold) || VariationPolicy.Prices is null || VariationPolicy.Prices.Any(p => p.Value is < 1 or > 100)))
            throw new InvalidDataException("Unsupported variation policy or provision weights.");
    }
}
