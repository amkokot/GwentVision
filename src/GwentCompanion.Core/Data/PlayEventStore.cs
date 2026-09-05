using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Data;

public sealed record BoardCardEvidence(string CardId, string CardName, PlayerSide Controller, NormalizedRegion Region);

public sealed record RecordedPlayEvent(
    string EventId,
    DateTimeOffset DetectedAt,
    string? CardId,
    string CardName,
    CardKind CardKind,
    double RecognitionConfidence,
    PlayerSide? InferredSide,
    double SideConfidence,
    NormalizedRegion CardRegion,
    string? BeforeImage,
    string DuringImage,
    string? AfterImage,
    string DetectionNotes,
    string BoardContextStatus = "Raw before/during/after frames recorded; structured board parsing pending.",
    IReadOnlyList<BoardCardEvidence>? BoardCards = null,
    DateTimeOffset? BoardSnapshotAt = null,
    string? BeforePosition = null,
    string? DuringPosition = null,
    string? AfterPosition = null);

public sealed class PlayEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string CreateEventDirectory(string sessionDirectory, string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        var safeId = string.Concat(eventId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safeId.Length == 0)
        {
            throw new ArgumentException("The event ID does not contain a valid filename.", nameof(eventId));
        }

        var directory = Path.Combine(sessionDirectory, "play-events", safeId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void SaveRecord(string eventDirectory, RecordedPlayEvent record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventDirectory);
        ArgumentNullException.ThrowIfNull(record);
        File.WriteAllText(
            Path.Combine(eventDirectory, "event.json"),
            JsonSerializer.Serialize(record, JsonOptions));
    }
}
