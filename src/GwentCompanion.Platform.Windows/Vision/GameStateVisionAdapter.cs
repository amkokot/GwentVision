using GwentCompanion.Core.GameState;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>The exact same state input is used by the live app and saved-recognition replay.</summary>
public static class GameStateVisionAdapter
{
    public static GameStateUpdate Apply(GameStateTracker tracker, CardVisionResult result, GameStateMeasurements? context = null)
    {
        context ??= new();
        // Explicit reviewed/context measurements precede automatic readings at the same boundary.
        context = context with { Cards = (context.Cards ?? []).Concat(result.CardMeasurements ?? []).ToArray() };
        return tracker.Observe(new(result.SampledAt, result.Screen, result.Sightings, result.Events,
            result.BoardWasScanned, result.GraveyardInspection, context));
    }
}
