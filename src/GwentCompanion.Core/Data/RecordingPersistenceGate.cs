namespace GwentCompanion.Core.Data;

/// <summary>Prevents a stopped recording's retained path from receiving reset state for the next match.</summary>
public static class RecordingPersistenceGate
{
    public static bool CanWriteLiveLedger(string? reviewEvidencePath, bool captureRunning, string? sessionPath) =>
        reviewEvidencePath is null && captureRunning && !string.IsNullOrWhiteSpace(sessionPath);
}
