using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Data;

public sealed record PortableDeckGroup(string Id, string Name, LibraryDeck[] Variants);
public sealed record PortableDeckLibrary(string Format, int Version, DeckVariationPolicy Similarity,
    PortableDeckGroup[] Groups, DeckIndexEntry[] ImportedLinks);
public sealed record LibraryTransferPreview(DeckLibrary Library, int Added, int Merged, int Groups, int KeptLocalDetails);

public sealed partial class DeckLibrary
{
    public const string TransferFormat = "gwent-vision-library";
    public const long MaximumTransferBytes = 128L * 1024 * 1024;

    public void ExportTransfer(string path)
    {
        EnsureVariationGroups();
        static CardDefinition PortableCard(CardDefinition card) => card.ArtUri is { IsAbsoluteUri: true, Scheme: "http", IsDefaultPort: true } uri &&
            uri.UserInfo.Length == 0 && uri.Host is "www.playgwent.com" or "gwent.one"
                ? card with { ArtUri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri } : card;
        static LibraryDeck PortableRecord(LibraryDeck record) => record with { Export = null, Deck = record.Deck with
        { Cards = record.Deck.Cards.Select(c => c with { Card = PortableCard(c.Card) }).ToArray(),
            Stratagem = record.Deck.Stratagem is null ? null : PortableCard(record.Deck.Stratagem) } };
        var transfer = new PortableDeckLibrary(TransferFormat, 1, VariationPolicy!, _variationGroups.Select(g =>
            new PortableDeckGroup(g.Id, g.Name, GroupVariants(g).Select(PortableRecord).ToArray())).ToArray(), ImportedLinks.ToArray());
        ValidateTransfer(transfer);
        // Website receipts are account-local; source links are portable, imported/guide-created flags are not.
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = File.Create(temporary)) JsonSerializer.Serialize(stream, transfer, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
            if (new FileInfo(temporary).Length > MaximumTransferBytes) throw new InvalidDataException("Library transfer exceeds the 128 MiB limit.");
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Builds a detached, validated result for UI confirmation; the receiving library is never modified here.</summary>
    public LibraryTransferPreview PreviewTransfer(string path)
    {
        if (new FileInfo(path).Length > MaximumTransferBytes) throw new InvalidDataException("Library transfer exceeds the 128 MiB limit.");
        using var stream = File.OpenRead(path);
        var transfer = JsonSerializer.Deserialize<PortableDeckLibrary>(stream, JsonOptions) ?? throw new InvalidDataException("Empty library transfer.");
        ValidateTransfer(transfer);
        var staged = new DeckLibrary(); staged._records.AddRange(_records); staged.ImportedLinks.AddRange(ImportedLinks);
        staged._variationGroups.AddRange(_variationGroups); staged.VariationPolicy = VariationPolicy;
        staged.EnsureVariationGroups();
        if (_records.Count == 0) staged.VariationPolicy = transfer.Similarity;
        var prices = new Dictionary<string, int>(staged.VariationPolicy!.Prices);
        foreach (var pair in transfer.Similarity.Prices) prices.TryAdd(pair.Key, pair.Value);
        staged.VariationPolicy = staged.VariationPolicy with { Prices = prices };
        var metric = new DeckVariationSimilarity(prices);
        var added = 0; var merged = 0; var keptDetails = 0;
        var byFingerprint = staged._records.Select((r, i) => (r.Fingerprint, i)).ToDictionary(x => x.Fingerprint, x => x.i);
        foreach (var incoming in transfer.Groups.SelectMany(g => g.Variants))
        {
            if (byFingerprint.TryGetValue(incoming.Fingerprint, out var index))
            {
                var old = staged._records[index];
                if (old.Details is not null && incoming.Details is not null && old.Details != incoming.Details) keptDetails++;
                staged._records[index] = old with
                {
                    Deck = old.Deck with { Name = old.CustomName ? old.Deck.Name : incoming.CustomName ? incoming.Deck.Name : old.Deck.Name,
                        Patches = DeckPatchMetadata.Merge(old.Deck.Patches, incoming.Deck.Patches),
                        Occurrences = DeckOccurrences.Merge(old.Deck.Occurrences, incoming.Deck.Occurrences),
                        LastEdited = new[] { old.Deck.LastEdited, incoming.Deck.LastEdited }.Max(),
                        SourceUpdatedAt = new[] { old.Deck.SourceUpdatedAt, incoming.Deck.SourceUpdatedAt }.Max(),
                        SourceUri = old.Deck.SourceUri ?? incoming.Deck.SourceUri },
                    Aliases = Union(old.Aliases, incoming.Aliases.Where(alias => staged.Find(alias) is null || staged.Find(alias)?.Fingerprint == old.Fingerprint)),
                    OriginalNames = Union(old.OriginalNames, incoming.OriginalNames), Sources = Union(old.Sources, incoming.Sources),
                    Details = old.Details ?? incoming.Details, CustomName = old.CustomName || incoming.CustomName
                }; merged++;
            }
            else
            {
                var id = staged.Find(incoming.Deck.Id) is null ? incoming.Deck.Id : "portable-" + incoming.Fingerprint;
                while (staged.Find(id) is not null) id += "-copy";
                var safeAliases = incoming.Aliases.Where(alias => staged.Find(alias) is null).Append(id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                byFingerprint[incoming.Fingerprint] = staged._records.Count;
                staged._records.Add(incoming with { Deck = incoming.Deck with { Id = id }, Aliases = safeAliases, Export = null }); added++;
            }
        }
        foreach (var incoming in transfer.Groups)
        {
            var orderedMembers = incoming.Variants.Select(v => v.Fingerprint).ToArray();
            var members = orderedMembers.ToHashSet(StringComparer.Ordinal);
            var existing = staged._variationGroups.Where(g => g.Members.Any(members.Contains)).OrderBy(g => g.Id, StringComparer.Ordinal).ToArray();
            var union = existing.SelectMany(g => g.Members).Concat(orderedMembers).Distinct(StringComparer.Ordinal).ToArray();
            var lists = union.Select(fp => staged._records[byFingerprint[fp]].Deck).ToArray();
            for (var a = 0; a < lists.Length; a++) for (var b = a + 1; b < lists.Length; b++)
                if (!metric.IsVariation(lists[a], lists[b], staged.VariationPolicy.Threshold))
                    throw new InvalidDataException($"Incoming group '{incoming.Name}' conflicts with local grouping/prices. No changes imported; its members cannot be joined without breaking the similarity boundary.");
            var primary = existing.FirstOrDefault();
            var groupId = primary?.Id ?? incoming.Id;
            if (primary is null && staged._variationGroups.Any(g => g.Id == groupId)) groupId = "transfer-" + members.Order(StringComparer.Ordinal).First();
            foreach (var previous in existing) staged._variationGroups.Remove(previous);
            staged._variationGroups.Add(new(groupId, primary?.Name ?? incoming.Name, union));
        }
        staged.AddLinks(transfer.ImportedLinks);
        staged._variationGroupsDirty = false; staged.ValidateVariationGroups();
        return new(staged, added, merged, transfer.Groups.Length, keptDetails);
    }

    private static void ValidateTransfer(PortableDeckLibrary transfer)
    {
        if (transfer.Format != TransferFormat || transfer.Version != 1 || transfer.Groups is null || transfer.ImportedLinks is null || transfer.Similarity is null)
            throw new InvalidDataException("Unsupported Gwent Vision library-transfer format/version.");
        if (transfer.Groups.Length > 10000 || transfer.ImportedLinks.Length > 100000 || transfer.Groups.Any(g => g is null || g.Variants is null || g.Variants.Length is < 1 or > 1000))
            throw new InvalidDataException("Invalid or oversized variation groups.");
        var records = transfer.Groups.SelectMany(g => g.Variants).ToArray();
        if (records.Length > 10000) throw new InvalidDataException("Too many variants in one transfer.");
        var validator = new DeckLibrary { VariationPolicy = transfer.Similarity };
        foreach (var record in records)
        {
            if (record is null || record.Deck is not { } deck || string.IsNullOrWhiteSpace(deck.Id) || deck.Id.Length > 256 ||
                string.IsNullOrWhiteSpace(deck.Name) || deck.Name.Length > 512 || deck.Cards is null || deck.Cards.Count is < 1 or > 100 ||
                record.Aliases is null || record.OriginalNames is null || record.Sources is null)
                throw new InvalidDataException("Invalid deck record.");
            foreach (var copy in deck.Cards.Concat(deck.Stratagem is null ? [] : [new DeckCard(deck.Stratagem)]))
            {
                if (copy?.Card is not { } card || copy.Count is < 1 or > 2 || card.Provision is < 0 or > 100 ||
                    card.Id is null || !Regex.IsMatch(card.Id, "\\A[a-zA-Z0-9_-]{1,100}\\z") || string.IsNullOrWhiteSpace(card.Name))
                    throw new InvalidDataException("Invalid card ID, provisions or physical-copy count.");
                if (card.ArtUri is { } art && (!art.IsAbsoluteUri || art.UserInfo.Length > 0 || !art.IsDefaultPort ||
                    art.Scheme != "https" || art.Host is not ("www.playgwent.com" or "gwent.one")))
                    throw new InvalidDataException("Artwork must use the known HTTPS PlayGWENT/gwent.one hosts.");
            }
            if (deck.CardCount > 100 || record.Fingerprint != Fingerprint(deck) || record.Export is not null)
                throw new InvalidDataException("Composition fingerprint mismatch or account-local export state in transfer.");
            static bool Unsafe(Uri uri) => !uri.IsAbsoluteUri || uri.UserInfo.Length > 0 ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && DeckLinkFileReader.CanonicalUrl(uri) is not null));
            if (deck.SourceUri is { } source && Unsafe(source) || record.Sources.Any(s => !Uri.TryCreate(s, UriKind.Absolute, out var uri) || Unsafe(uri)))
                throw new InvalidDataException("Only credential-free HTTPS sources or canonicalizable legacy PlayGWENT links are allowed.");
            if (validator.Find(deck.Id) is not null || validator._records.Any(r => r.Fingerprint == record.Fingerprint))
                throw new InvalidDataException("Duplicate deck identity in transfer.");
            validator._records.Add(record);
        }
        validator._variationGroups.AddRange(transfer.Groups.Select(g => new DeckVariationGroup(g.Id, g.Name, g.Variants.Select(v => v.Fingerprint).ToArray())));
        validator.ValidateVariationGroups();
        if (transfer.Similarity.Prices is null || records.SelectMany(r => r.Deck.Cards).Any(c => !transfer.Similarity.Prices.ContainsKey(c.Card.Id)))
            throw new InvalidDataException("Missing shared provision weights.");
        foreach (var link in transfer.ImportedLinks)
            if (link is null || link.DeckUri is null || DeckLinkFileReader.CanonicalUrl(link.DeckUri) is null)
                throw new InvalidDataException("Invalid indexed deck link in transfer.");
        var metric = new DeckVariationSimilarity(transfer.Similarity.Prices);
        foreach (var group in transfer.Groups)
            for (var a = 0; a < group.Variants.Length; a++) for (var b = a + 1; b < group.Variants.Length; b++)
                if (!metric.IsVariation(group.Variants[a].Deck, group.Variants[b].Deck, transfer.Similarity.Threshold))
                    throw new InvalidDataException("Transferred group breaks its declared similarity boundary: " + group.Name);
    }
}
