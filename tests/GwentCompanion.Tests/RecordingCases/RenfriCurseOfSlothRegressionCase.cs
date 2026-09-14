using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class RenfriCurseOfSlothRegressionCase : IRecordingValidationCase
{
    public string Id => "renfri-curse-of-sloth";

    public Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var assets = Path.Combine(project, "assets", "vision", "leaders");
        var sloth = catalog.Single(card => card.Id == "203175");
        var renfri = catalog.Single(card => card.Id == "203088");
        var invigorate = catalog.Single(card => card.Name == "Invigorate");
        var bronzeUnit = catalog.First(card => card.Kind == CardKind.Unit && !card.IsGold && card.CanBeInStartingDeck);
        var goldUnit = catalog.First(card => card.Kind == CardKind.Unit && card.IsGold && card.CanBeInStartingDeck);
        var bronzeSpecial = catalog.First(card => card.Kind == CardKind.Special && !card.IsGold && card.CanBeInStartingDeck);

        if (GwentOneCardCatalog.StartingLeaders(catalog).Any(card => card.Id == sloth.Id) ||
            !GwentOneCardCatalog.CurrentLeaderAbilities(catalog).Any(card => card.Id == sloth.Id))
            throw new InvalidOperationException("A Renfri curse was exposed as a starting deck choice or omitted from current-leader recognition.");
        var rule = LeaderSpawnCatalog.HandPlayRule(sloth);
        if (rule is not { MaximumPlays: 1, Kind: CardKind.Unit, BronzeOnly: true, DrawsAfterward: 1 } ||
            !rule.Matches(bronzeUnit) || rule.Matches(goldUnit) || rule.Matches(bronzeSpecial))
            throw new InvalidOperationException("Curse of Sloth's bounded bronze-unit hand play and one-card draw were not parsed exactly.");

        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null)
            { MatchHudVisible = true };
        using var recognizer = new LeaderAbilityRecognizer(catalog, assets);
        var at = DateTimeOffset.UnixEpoch;
        LeaderAbilityReading? reading = null;
        // The first retained frame precedes the visible replacement plaque.
        for (var index = 0; index < 3; index++)
            reading = recognizer.Observe(definition.Load("evidence-01.png"), board, at.AddMilliseconds(index * 300));
        if (reading?.Card.Id == sloth.Id)
            throw new InvalidOperationException("Curse of Sloth was announced before its replacement plaque appeared.");
        recognizer.ObserveEvents([new VisionEvidenceEvent(at.AddSeconds(3),
            new CardSighting(renfri, PlayerSide.Opponent, CardSightSource.PlayPreview,
                new(.815, .136, .915, .399), .08, 1, "Retained Renfri source"), "Retained Renfri source")]);
        reading = null;
        var files = new[] { "evidence-02.png", "evidence-03.png", "evidence-04.png" };
        for (var index = 0; index < files.Length; index++)
            reading = recognizer.Observe(definition.Load(files[index]), board, at.AddSeconds(4 + index)) ?? reading;
        if (reading?.Card.Id != sloth.Id)
            throw new InvalidOperationException($"The retained Renfri replacement badge should read Curse of Sloth; got {reading?.Card.Name ?? "nothing"}.");

        var knowledge = new OpponentKnowledge { StartingLeader = invigorate.Name };
        knowledge.ObserveVisibleLeader(sloth, reading.Confidence, false);
        if (knowledge.StartingLeader != invigorate.Name || knowledge.CurrentLeaderId != sloth.Id)
            throw new InvalidOperationException("Renfri's replacement overwrote the starting leader or failed to become the current leader.");

        var origins = new PlayProvenanceResolver();
        origins.ObserveCurrentLeader(PlayerSide.Opponent, sloth);
        if (origins.CurrentLeaderHandPlaySource(PlayerSide.Opponent, bronzeUnit)?.Id != sloth.Id ||
            origins.CurrentLeaderHandPlaySource(PlayerSide.Opponent, goldUnit) is not null ||
            origins.CurrentLeaderHandPlaySource(PlayerSide.Opponent, bronzeSpecial) is not null)
            throw new InvalidOperationException("Curse of Sloth's available target class was not retained conservatively.");
        var empty = new LiveDeckTracker(PlayerSide.Opponent);
        if (empty.Observations.Count != 0)
            throw new InvalidOperationException("Merely observing an unused Curse of Sloth invented a bronze play.");
        return Task.CompletedTask;
    }
}
