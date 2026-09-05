using System.Text.Json;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Data;

/// <summary>Read already-recognized evidence, not screenshots or game state. No recapture or OCR.</summary>
public static class SavedVisionEvents
{
    public static IReadOnlyList<VisionEvidenceEvent> Read(string path, IEnumerable<CardDefinition> catalog, string? sessionId = null)
    {
        var cards = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id);
        var families = new CardAppearanceFamilies(cards.Values);
        var events = new List<VisionEvidenceEvent>();
        if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var document = JsonDocument.Parse(line);
                ReadResult(document.RootElement);
            }
        }
        else
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var element in document.RootElement.EnumerateArray()) ReadResult(element);
            else ReadResult(document.RootElement);
        }
        return events.OrderBy(item => item.ObservedAt).ToArray();

        void ReadResult(JsonElement element)
        {
            // A benchmark export can also contain isolated fixtures from other games.
            if (sessionId is not null && element.TryGetProperty("Frame", out var frame) &&
                !(frame.GetString() ?? "").Replace('\\', '/').StartsWith(sessionId + "/", StringComparison.Ordinal)) return;
            var result = element.TryGetProperty("Result", out var wrapped) ? wrapped : element;
            if (!result.TryGetProperty("Events", out var list)) return;
            foreach (var item in list.EnumerateArray())
            {
                var sight = item.GetProperty("Sighting");
                var id = sight.GetProperty("Card").GetProperty("Id").GetString();
                if (id is null) continue;
                if (!cards.TryGetValue(id, out var card))
                {
                    // Replay must preserve the same non-executable visual family as live recognition.
                    if (!id.StartsWith("visual-family:", StringComparison.Ordinal) || !cards.TryGetValue(id[14..], out var member)) continue;
                    card = families.Normalize(member);
                    if (card.Id != id) continue; // Do not trust arbitrary serialized card definitions.
                }
                var region = sight.GetProperty("Region");
                var bounds = new NormalizedRegion(region.GetProperty("Left").GetDouble(), region.GetProperty("Top").GetDouble(),
                    region.GetProperty("Right").GetDouble(), region.GetProperty("Bottom").GetDouble());
                events.Add(new(item.GetProperty("ObservedAt").GetDateTimeOffset(),
                    new CardSighting(card, Enum.Parse<PlayerSide>(sight.GetProperty("Side").GetString()!),
                        Enum.Parse<CardSightSource>(sight.GetProperty("Source").GetString()!), bounds,
                        sight.GetProperty("Distance").GetDouble(), sight.GetProperty("Margin").GetDouble(),
                        sight.GetProperty("Evidence").GetString() ?? "Saved evidence"), item.GetProperty("Description").GetString() ?? "Saved evidence"));
            }
        }
    }
}
