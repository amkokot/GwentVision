using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>A review draft, never live-match evidence. Overlapping pages take maxima, not sums.</summary>
public sealed class DeckScanDraft
{
    private readonly Dictionary<string, DeckCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manual = new(StringComparer.OrdinalIgnoreCase);
    public const int ConsensusWindow = 8;
    private readonly Queue<Dictionary<string, int>> _recent = new();
    private readonly Queue<(string? Leader, string? Stratagem)> _headers = new();
    private bool _manualLeader, _manualStratagem;
    public CardDefinition? Leader { get; private set; }
    public CardDefinition? Stratagem { get; private set; }
    public IReadOnlyList<DeckCard> Cards => DeckBuilderOrder.Sort(_cards.Values).ToArray();
    public int CardCount => _cards.Values.Sum(item => item.Count);
    public void Load(IEnumerable<DeckCard> cards)
    {
        Reset(); foreach (var item in cards) _cards[item.Card.Id] = item;
    }
    public void Reset() { _cards.Clear(); _manual.Clear(); BreakSequence(); Leader = Stratagem = null; _manualLeader = _manualStratagem = false; }
    public void BreakSequence() { _recent.Clear(); _headers.Clear(); }

    public void SetLeader(CardDefinition? leader)
    {
        if (leader is not null && leader.Kind != CardKind.Leader) throw new ArgumentException("Expected a leader ability.");
        Leader = leader; _manualLeader = true;
    }
    // Changing the picker faction clears an incompatible choice without disabling later header recognition.
    public void ClearLeader() { Leader = null; _headers.Clear(); _manualLeader = false; }
    public void SetStratagem(CardDefinition? stratagem)
    {
        if (stratagem is not null && stratagem.Kind != CardKind.Stratagem) throw new ArgumentException("Expected a stratagem.");
        Stratagem = stratagem; _manualStratagem = true;
    }
    public void ObserveHeader(CardDefinition? leader, CardDefinition? stratagem)
    {
        _headers.Enqueue((leader?.Id, stratagem?.Id));
        while (_headers.Count > ConsensusWindow) _headers.Dequeue();
        if (leader?.Kind == CardKind.Leader && Leader is null && !_manualLeader &&
            _headers.Count(item => item.Leader == leader.Id) >= 2 && _headers.All(item => item.Leader is null || item.Leader == leader.Id)) Leader = leader;
        if (stratagem?.Kind == CardKind.Stratagem && Stratagem is null && !_manualStratagem &&
            _headers.Count(item => item.Stratagem == stratagem.Id) >= 2 && _headers.All(item => item.Stratagem is null || item.Stratagem == stratagem.Id)) Stratagem = stratagem;
    }

    public int Observe(IEnumerable<DeckCard> page)
    {
        var rows = page.Where(item => item.Card.CanBeInStartingDeck && item.Count > 0)
            .GroupBy(item => item.Card.Id).Select(group => new DeckCard(group.First().Card,
                Math.Min(group.First().Card.IsGold ? 1 : 2, group.Sum(item => item.Count)))).ToArray();
        var added = 0;
        _recent.Enqueue(rows.ToDictionary(item => item.Card.Id, item => item.Count, StringComparer.OrdinalIgnoreCase));
        while (_recent.Count > ConsensusWindow) _recent.Dequeue();
        foreach (var item in rows)
        {
            // Two reads within a bounded window must agree on each copy. Scroll/one-frame OCR gaps
            // do not discard evidence, but a single x2 reading still cannot create a second copy.
            var count = Math.Min(item.Count, _recent.Select(page => page.GetValueOrDefault(item.Card.Id))
                .OrderByDescending(value => value).Skip(1).FirstOrDefault());
            if (_manual.Contains(item.Card.Id) || count <= 0) continue;
            if (count > 0 && (!_cards.TryGetValue(item.Card.Id, out var old) || count > old.Count))
            { _cards[item.Card.Id] = item with { Count = count }; added++; }
        }
        return added;
    }

    public void SetCount(CardDefinition card, int count)
    {
        if (!card.CanBeInStartingDeck || card.Kind is not (CardKind.Unit or CardKind.Special or CardKind.Artifact))
            throw new ArgumentException("Choose a collectible unit, special or artifact.");
        if (count < 0 || count > (card.IsGold ? 1 : 2)) throw new ArgumentOutOfRangeException(nameof(count));
        _manual.Add(card.Id);
        if (count == 0) _cards.Remove(card.Id); else _cards[card.Id] = new(card, count);
    }
    public DeckDefinition Build(string name, CardDefinition leader)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160) throw new InvalidDataException("Enter a deck name (1–160 characters).");
        if (leader.Kind != CardKind.Leader) throw new InvalidDataException("Select the deck's starting leader ability.");
        if (CardCount < 25) throw new InvalidDataException($"Only {CardCount}/25 cards. Scroll farther or add missing cards before saving.");
        return new("manual-" + Guid.NewGuid().ToString("N"), name.Trim(), leader.Faction, leader.Name, leader.Provision,
            Cards, LastEdited: DateTimeOffset.Now, Stratagem: Stratagem);
    }
}
