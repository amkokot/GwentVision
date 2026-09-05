using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public sealed record LibraryDeck(DeckDefinition Deck, string Fingerprint, string[] Aliases,
    string[] OriginalNames, string[] Sources, bool CustomName = false, DeckDetails? Details = null,
    DeckExportReceipt? Export = null);
public sealed record LibraryMergeResult(int Added, int Merged);

/// <summary>One statistical example per exact composition. Names and links are provenance, not identity.</summary>
public sealed partial class DeckLibrary
{
    private readonly List<LibraryDeck> _records = [];
    public IReadOnlyList<LibraryDeck> Records => _records;
    public List<DeckIndexEntry> ImportedLinks { get; } = [];
    public DeckDefinition[] Decks => _records.Select(item => item.Deck).ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    { Converters = { new StringSetConverter() } };

    public static string Fingerprint(DeckDefinition deck)
    {
        var cards = deck.Cards.GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key.ToLowerInvariant() + ":" + group.Sum(item => item.Count));
        var value = DeckSearchCatalog.Normalize(deck.Faction) + "|" + DeckSearchCatalog.Normalize(deck.Leader) + "|" + string.Join(';', cards);
        if (deck.Stratagem is { } stratagem) value += "|stratagem:" + stratagem.Id.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public LibraryDeck? Find(string? id) => id is null ? null : _records.FirstOrDefault(item =>
        item.Deck.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || item.Aliases.Contains(id, StringComparer.OrdinalIgnoreCase));

    public void SetDetails(string id, DeckDetails details)
    {
        var record = Find(id) ?? throw new KeyNotFoundException("Deck is not cached.");
        _records[_records.IndexOf(record)] = record with { Details = details };
    }

    public void SetExport(string id, DeckExportReceipt receipt)
    {
        var record = Find(id) ?? throw new KeyNotFoundException("Deck is not cached.");
        var uri = receipt.DeckHash is { } hash && PlayGwentExport.ValidHash(hash)
            ? new Uri("https://www.playgwent.com/en/decks/" + hash) : null;
        _records[_records.IndexOf(record)] = record with { Export = receipt,
            Deck = record.Deck with { SourceUri = record.Deck.SourceUri ?? uri },
            Sources = uri is null ? record.Sources : Union(record.Sources, [uri.AbsoluteUri]) };
    }

    public LibraryMergeResult Merge(IEnumerable<DeckDefinition> decks)
    {
        var added = 0; var merged = 0;
        // Older payloads often lack stratagem metadata. Hash each existing
        // composition once per batch, not the entire library for every payload.
        // Metadata enrichment below never changes the base cards/faction/leader.
        var baseIndex = _records.Select((record, index) => (Key: Fingerprint(record.Deck with { Stratagem = null }), Index: index))
            .GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Select(item => item.Index).ToList());
        foreach (var input in decks)
        {
            if (input.Cards.Count == 0 || input.Cards.Any(item => item.Count <= 0))
                throw new InvalidDataException("A cached deck must contain positive card quantities.");
            var fingerprint = Fingerprint(input);
            var position = _records.FindIndex(item => item.Fingerprint == fingerprint);
            if (position < 0)
            {
                // Enrich an older unknown-stratagem entry only when there is one compatible identity.
                // Different known stratagems are distinct variants; unknown never chooses between them.
                var baseFingerprint = Fingerprint(input with { Stratagem = null });
                var compatible = (baseIndex.GetValueOrDefault(baseFingerprint) ?? []).Where(index =>
                    input.Stratagem is null || _records[index].Deck.Stratagem is null).Take(2).ToArray();
                if (compatible.Length == 1) position = compatible[0];
            }
            var sources = input.SourceUri is null ? Array.Empty<string>() : [input.SourceUri.AbsoluteUri];
            if (position < 0)
            {
                // A changed list is a variant, never a silent overwrite of the original composition.
                var deck = (Find(input.Id) is null ? input : input with { Id = "variant-" + fingerprint })
                    with { CachedAt = input.CachedAt ?? DateTimeOffset.UtcNow };
                _records.Add(new LibraryDeck(deck, fingerprint, [deck.Id], [input.Name], sources));
                var baseKey = Fingerprint(deck with { Stratagem = null });
                if (!baseIndex.TryGetValue(baseKey, out var indices)) baseIndex[baseKey] = indices = [];
                indices.Add(_records.Count - 1);
                added++;
                continue;
            }
            var old = _records[position];
            var latest = new[] { old.Deck.LastEdited, input.LastEdited }.Max();
            var aliasOwner = Find(input.Id);
            var enriched = old.Deck with { LastEdited = latest, RecencyRank = Math.Min(old.Deck.RecencyRank, input.RecencyRank),
                SourceUri = old.Deck.SourceUri ?? input.SourceUri, Stratagem = old.Deck.Stratagem ?? input.Stratagem,
                SourceUpdatedAt = new[] { old.Deck.SourceUpdatedAt, input.SourceUpdatedAt }.Max(),
                Patches = DeckPatchMetadata.Merge(old.Deck.Patches, input.Patches),
                Occurrences = DeckOccurrences.Merge(old.Deck.Occurrences, input.Occurrences),
                CachedAt = new[] { old.Deck.CachedAt, input.CachedAt }.Where(date => date is not null).DefaultIfEmpty(null).Min() };
            _records[position] = old with
            {
                Deck = enriched, Fingerprint = Fingerprint(enriched),
                Aliases = Union(old.Aliases, aliasOwner is null || aliasOwner == old ? [input.Id] : []),
                OriginalNames = Union(old.OriginalNames, [input.Name]), Sources = Union(old.Sources, sources),
            };
            var newFingerprint = _records[position].Fingerprint;
            if (old.Fingerprint != newFingerprint)
                for (var groupIndex = 0; groupIndex < _variationGroups.Count; groupIndex++)
                    _variationGroups[groupIndex] = _variationGroups[groupIndex] with
                    { Members = _variationGroups[groupIndex].Members.Select(fp => fp == old.Fingerprint ? newFingerprint : fp).ToArray() };
            merged++;
        }
        if (added > 0 || merged > 0) _variationGroupsDirty = true;
        return new(added, merged);
    }

    public DeckDefinition Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160)
            throw new ArgumentException("Use a deck name between 1 and 160 characters.");
        var record = Find(id) ?? throw new KeyNotFoundException("Deck is not cached.");
        var updated = record with { Deck = record.Deck with { Name = name.Trim() }, CustomName = true,
            OriginalNames = Union(record.OriginalNames, [record.Deck.Name]) };
        _records[_records.IndexOf(record)] = updated;
        return updated.Deck;
    }

    /// <summary>Replace one exact cached composition. Evidence belonging to the old list is not carried forward.</summary>
    public DeckDefinition Replace(string id, DeckDefinition replacement)
    {
        if (replacement.Cards.Count == 0 || replacement.Cards.Any(item => item.Count <= 0))
            throw new InvalidDataException("A cached deck must contain positive card quantities.");
        var record = Find(id) ?? throw new KeyNotFoundException("Deck is not cached.");
        var fingerprint = Fingerprint(replacement);
        if (_records.Any(item => item != record && item.Fingerprint == fingerprint))
            throw new InvalidOperationException("That exact composition already exists in the library. Select that version instead.");
        if (Find(replacement.Id) is { } identityOwner && identityOwner != record)
            throw new InvalidOperationException("That deck identity already belongs to another library version.");

        var deck = replacement with
        {
            SourceUri = null,
            CachedAt = DateTimeOffset.UtcNow,
            Patches = replacement.Patches,
            Occurrences = replacement.Occurrences,
        };
        var updated = new LibraryDeck(deck, fingerprint, [deck.Id], Union(record.OriginalNames, [record.Deck.Name, deck.Name]),
            [], true, record.Details, null);
        _records[_records.IndexOf(record)] = updated;
        RemoveImportLinks(record);
        RemoveVariationMember(record.Fingerprint);
        _variationGroupsDirty = true;
        return deck;
    }

    /// <summary>Remove one exact cached composition and its library-owned import links.</summary>
    public LibraryDeck Remove(string id)
    {
        var record = Find(id) ?? throw new KeyNotFoundException("Deck is not cached.");
        _records.Remove(record);
        RemoveImportLinks(record);
        RemoveVariationMember(record.Fingerprint);
        _variationGroupsDirty = true;
        return record;
    }

    private void RemoveImportLinks(LibraryDeck record)
    {
        var sources = record.Sources.Append(record.Deck.SourceUri?.AbsoluteUri)
            .Where(source => !string.IsNullOrWhiteSpace(source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ImportedLinks.RemoveAll(link => sources.Contains(link.DeckUri.AbsoluteUri));
    }

    private void RemoveVariationMember(string fingerprint)
    {
        for (var index = _variationGroups.Count - 1; index >= 0; index--)
        {
            var group = _variationGroups[index];
            var members = group.Members.Where(member => member != fingerprint).ToArray();
            if (members.Length == 0) _variationGroups.RemoveAt(index);
            else if (members.Length != group.Members.Length) _variationGroups[index] = group with { Members = members };
        }
    }

    public void RemoveInferredEncounters(IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.Select(id => "match-" + id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < _records.Count; i++)
        {
            var old = _records[i];
            var removed = (old.Deck.Occurrences ?? []).Where(o => o.Kind == "OpponentMatchInferred" && ids.Contains(o.Id)).Select(o => o.Id).ToHashSet();
            if (removed.Count == 0) continue;
            _records[i] = old with { Deck = old.Deck with {
                Occurrences = old.Deck.Occurrences!.Where(o => o.Kind != "OpponentMatchInferred" || !removed.Contains(o.Id)).ToArray(),
                Patches = (old.Deck.Patches ?? []).Where(p => !removed.Any(id => p.Source == "Occurrence OpponentMatchInferred: " + id)).ToArray() } };
        }
    }

    public void AddLinks(IEnumerable<DeckIndexEntry> links)
    {
        foreach (var link in links)
        {
            var position = ImportedLinks.FindIndex(item => item.SourceId == link.SourceId && item.DeckUri == link.DeckUri);
            if (position < 0) ImportedLinks.Add(link);
            else ImportedLinks[position] = ImportedLinks[position] with
            { Patches = DeckPatchMetadata.Merge(ImportedLinks[position].Patches, link.Patches),
                Occurrences = DeckOccurrences.Merge(ImportedLinks[position].Occurrences, link.Occurrences) };
        }
    }

    public static DeckLibrary Load(string path)
    {
        var library = new DeckLibrary();
        if (!File.Exists(path)) return library;
        using var stream = File.OpenRead(path);
        var version = ReadVersion(stream); stream.Position = 0;
        LibraryDeck[] records; DeckIndexEntry[] links;
        if (version is 4 or 5)
        {
            var compact = JsonSerializer.Deserialize<CompactLibraryState>(stream, JsonOptions)
                ?? throw new InvalidDataException("Empty deck library; original file has been preserved.");
            records = compact.Records.Select(record => record.Expand(compact.Cards)).ToArray(); links = compact.ImportedLinks;
            library._variationGroups.AddRange(compact.VariationGroups ?? []); library.VariationPolicy = compact.VariationPolicy;
        }
        else
        {
            var state = JsonSerializer.Deserialize<LibraryState>(stream, JsonOptions)
                ?? throw new InvalidDataException("Empty deck library; original file has been preserved.");
            if (state.Version is not (1 or 2 or 3)) throw new InvalidDataException("Unsupported deck-library version; original file has been preserved.");
            records = state.Records; links = state.ImportedLinks;
        }
        foreach (var record in records)
        {
            if (record.Fingerprint != Fingerprint(record.Deck) || library.Find(record.Deck.Id) is not null ||
                library._records.Any(item => item.Fingerprint == record.Fingerprint))
                throw new InvalidDataException("Invalid deck-library identity; original file has been preserved.");
            library._records.Add(record);
        }
        library.AddLinks(links);
        library.ValidateVariationGroups();
        return library;
    }

    public void Save(string path)
    {
        EnsureVariationGroups();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var cards = new List<CardDefinition>(); var cardKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            int Store(CardDefinition card)
            {
                var key = CardStorageKey(card);
                if (cardKeys.TryGetValue(key, out var existing)) return existing;
                var index = cards.Count; cards.Add(card); cardKeys[key] = index; return index;
            }
            var records = _records.Select(record => CompactLibraryDeck.Store(record, Store)).ToArray();
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, new CompactLibraryState(5, cards.ToArray(), records, ImportedLinks.ToArray(), _variationGroups.ToArray(), VariationPolicy), JsonOptions);
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string[] Union(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Concat(right).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static int ReadVersion(Stream stream)
    {
        var prefix = new byte[Math.Min(512, checked((int)Math.Min(stream.Length, 512)))];
        _ = stream.Read(prefix); var reader = new Utf8JsonReader(prefix);
        while (reader.Read()) if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("Version") &&
            reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)) return version;
        throw new InvalidDataException("Deck library has no supported version header; original file has been preserved.");
    }

    private static string CardStorageKey(CardDefinition card) => JsonSerializer.Serialize(new CardKey(card.Id, card.Name, card.Faction,
        card.Kind, card.Provision, card.Power, card.IsGold, card.Categories.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        card.ArtUri?.AbsoluteUri, card.SecondaryFactions.Order(StringComparer.OrdinalIgnoreCase).ToArray(), card.AbilityText,
        card.CanBeInStartingDeck, card.PrintedArmor));

    public sealed record LibraryState(int Version, LibraryDeck[] Records, DeckIndexEntry[] ImportedLinks);
    private sealed record CompactLibraryState(int Version, CardDefinition[] Cards, CompactLibraryDeck[] Records, DeckIndexEntry[] ImportedLinks,
        DeckVariationGroup[]? VariationGroups = null, DeckVariationPolicy? VariationPolicy = null);
    private sealed record CompactLibraryDeck(CompactDeck Deck, string Fingerprint, string[] Aliases, string[] OriginalNames,
        string[] Sources, bool CustomName, DeckDetails? Details = null, DeckExportReceipt? Export = null)
    {
        public static CompactLibraryDeck Store(LibraryDeck record, Func<CardDefinition, int> store) => new(
            CompactDeck.Store(record.Deck, store), record.Fingerprint, record.Aliases, record.OriginalNames, record.Sources, record.CustomName,
            record.Details, record.Export);
        public LibraryDeck Expand(IReadOnlyList<CardDefinition> cards) =>
            new(Deck.Expand(cards), Fingerprint, Aliases, OriginalNames, Sources, CustomName, Details, Export);
    }
    private sealed record CompactDeck(string Id, string Name, string Faction, string Leader, int LeaderProvisionBonus,
        CompactDeckCard[] Cards, Uri? SourceUri, DateTimeOffset? LastEdited, int RecencyRank, int? Stratagem,
        DateTimeOffset? SourceUpdatedAt, DateTimeOffset? CachedAt, IReadOnlyList<DeckPatch>? Patches,
        IReadOnlyList<DeckOccurrence>? Occurrences)
    {
        public static CompactDeck Store(DeckDefinition deck, Func<CardDefinition, int> store) => new(deck.Id, deck.Name, deck.Faction,
            deck.Leader, deck.LeaderProvisionBonus, deck.Cards.Select(card => new CompactDeckCard(store(card.Card), card.Count)).ToArray(),
            deck.SourceUri, deck.LastEdited, deck.RecencyRank, deck.Stratagem is null ? null : store(deck.Stratagem),
            deck.SourceUpdatedAt, deck.CachedAt, deck.Patches, deck.Occurrences);
        public DeckDefinition Expand(IReadOnlyList<CardDefinition> cards) => new(Id, Name, Faction, Leader, LeaderProvisionBonus,
            Cards.Select(card => new DeckCard(cards[card.Card], card.Count)).ToArray(), SourceUri, LastEdited, RecencyRank,
            Stratagem is { } stratagem ? cards[stratagem] : null, SourceUpdatedAt, CachedAt, Patches, Occurrences);
    }
    private sealed record CompactDeckCard(int Card, int Count);
    private sealed record CardKey(string Id, string Name, string Faction, CardKind Kind, int Provision, int Power, bool IsGold,
        string[] Categories, string? ArtUri, string[] SecondaryFactions, string? AbilityText, bool CanBeInStartingDeck, int? PrintedArmor);
    private sealed class StringSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            (JsonSerializer.Deserialize<string[]>(ref reader, options) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
