using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;

namespace GwentCompanion.Core.Data;

/// <summary>Bounded RAM, append-only state checkpoints. Existing screenshot/event logs remain independent.</summary>
public sealed class GameStateJournal : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() } };
    private readonly StreamWriter _writer;
    private readonly string _latestPath;
    private DateTimeOffset? _lastWrittenAt;
    private long _lastWrittenRevision = -1;
    private GamePosition? _lastPosition;
    public int CheckpointsWritten { get; private set; }

    public GameStateJournal(string sessionDirectory)
    {
        Directory.CreateDirectory(sessionDirectory);
        // A new capture session should never silently overwrite an earlier state journal.
        _writer = new StreamWriter(new FileStream(Path.Combine(sessionDirectory, "position-history.gvn"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _latestPath = Path.Combine(sessionDirectory, "game-state-final.json");
    }

    public async Task AppendAsync(GameStateUpdate update, bool force = false)
    {
        if (!update.Accepted || update.After.Revision == _lastWrittenRevision) return;
        if (!force && update.Events.Length == 0 && _lastWrittenAt is { } last && update.After.At - last < TimeSpan.FromSeconds(2)) return;
        var position = CalculationPositionAdapter.FromObserved(update.After);
        var changes = PositionNotation.Changes(_lastPosition, position);
        if (changes.Length > 0 || update.Events.Length > 0)
        {
            await _writer.WriteLineAsync($"; revision {update.After.Revision} {update.After.At:O}").ConfigureAwait(false);
            foreach (var item in update.Events.Where(item => item.Kind is "PlayPreview" or "HistoricalAction" or "RoundObserved"))
                await _writer.WriteLineAsync($"; {item.Kind} {item.Side} {item.CardId}").ConfigureAwait(false);
            if (changes.Length > 0) await _writer.WriteLineAsync(changes).ConfigureAwait(false);
        }
        _lastPosition = position;
        await _writer.FlushAsync().ConfigureAwait(false);
        _lastWrittenAt = update.After.At; _lastWrittenRevision = update.After.Revision; CheckpointsWritten++;
    }

    public async Task SaveFinalAsync(GameStateSnapshot state)
    {
        var temporary = _latestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Json)).ConfigureAwait(false);
        File.Move(temporary, _latestPath, overwrite: true);
        var compactPath = Path.Combine(Path.GetDirectoryName(_latestPath)!, "position-final.gvn");
        await File.WriteAllTextAsync(compactPath + ".tmp", PositionNotation.Write(CalculationPositionAdapter.FromObserved(state))).ConfigureAwait(false);
        File.Move(compactPath + ".tmp", compactPath, overwrite: true);
    }
    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
