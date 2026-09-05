using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>Manual work in progress, never a complete library list or match observation.</summary>
public sealed record DeckEditorDraft(string SourceKey, string Name, string? Faction, string? LeaderId,
    string? StratagemId, IReadOnlyList<DeckCard> Cards, DateTimeOffset UpdatedAt);

public static class DeckEditorDraftStore
{
    private sealed record DraftFile(int Schema, IReadOnlyList<DeckEditorDraft> Drafts);
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() } };

    public static IReadOnlyList<DeckEditorDraft> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var file = JsonSerializer.Deserialize<DraftFile>(File.ReadAllText(path), Options);
        if (file is null || file.Schema != 1 || file.Drafts is null) throw new InvalidDataException("Unsupported or damaged deck draft cache.");
        foreach (var draft in file.Drafts) Validate(draft);
        if (file.Drafts.Select(d => d.SourceKey).Distinct(StringComparer.Ordinal).Count() != file.Drafts.Count)
            throw new InvalidDataException("Duplicate source keys in deck draft cache.");
        return file.Drafts.OrderByDescending(d => d.UpdatedAt).ToArray();
    }

    public static void Save(string path, DeckEditorDraft draft)
    {
        Validate(draft);
        if (draft.Cards.Any(c => !StartingDeckRules.IsStartingCard(c.Card)))
            throw new InvalidDataException("Generated cards, tokens and leader abilities cannot be saved as starting-deck cards.");
        var drafts = Load(path).Where(d => d.SourceKey != draft.SourceKey).Append(draft).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new DraftFile(1, drafts), Options));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(DeckEditorDraft draft)
    {
        if (draft is null || string.IsNullOrWhiteSpace(draft.SourceKey) || string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Trim().Length > 160)
            throw new InvalidDataException("Use a draft name of 1–160 characters and a valid source.");
        if (draft.Cards is null || draft.Cards.Count > 100 || draft.Cards.Any(c => c is null || c.Card is null || string.IsNullOrWhiteSpace(c.Card.Id) || c.Count is < 1 or > 100) ||
            draft.Cards.Select(c => c.Card.Id).Distinct().Count() != draft.Cards.Count)
            throw new InvalidDataException("Invalid draft card counts.");
    }
}
