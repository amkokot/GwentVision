using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal sealed class BattleStationsPlayChainRegressionCase : IRecordingValidationCase
{
    public string Id => "battle-stations-play-chain";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        var battle = catalog.Single(card => card.Id == "203242");
        var hunter = catalog.Single(card => card.Id == "202547");
        var arbalest = catalog.Single(card => card.Id == "162305");
        var driver = catalog.Single(card => card.Id == "201612");
        using var screenReader = new ScreenStateRecognizer();
        var titleReader = new PreviewTitleRecognizer(catalog);

        async Task<(GwentVisualObservation Screen, CardSighting Sighting)> Preview(int evidence, string id)
        {
            var pixels = definition.Load($"evidence-{evidence:D2}.png");
            var screen = await screenReader.AnalyzeAsync(pixels);
            var sighting = (await titleReader.RecognizeAsync(pixels, screen, screenReader)).Single(item =>
                item.Card.Id == id && item.Side == PlayerSide.Opponent && item.Source == CardSightSource.PlayPreview);
            return (screen with { MatchHudVisible = true }, sighting);
        }

        // The older distinct-card regression now lives in the discoverable case
        // suite instead of being coupled to a full raw recording directory.
        var oldBattle = await Preview(1, battle.Id);
        var oldHunter = await Preview(2, hunter.Id);
        var oldArbalest = await Preview(3, arbalest.Id);
        var at = DateTimeOffset.UnixEpoch;
        VisionEvidenceEvent Event(CardSighting sighting, double seconds) =>
            new(at.AddSeconds(seconds), sighting, "Retained reviewed play-preview pixels");
        var oldHands = new HandCommitTracker();
        if (oldHands.Observe(at, oldBattle.Screen, [Event(oldBattle.Sighting, 0)]).Count != 0)
            throw new InvalidOperationException("Battle Stations became one of its own bounded hand-play children.");
        var firstOldChild = oldHands.Observe(at.AddSeconds(10), oldHunter.Screen, [Event(oldHunter.Sighting, 10)]);
        var secondOldChild = oldHands.Observe(at.AddSeconds(12), oldArbalest.Screen, [Event(oldArbalest.Sighting, 12)]);
        if (firstOldChild.Single().Sighting.Card.Id != hunter.Id || secondOldChild.Single().Sighting.Card.Id != arbalest.Id)
            throw new InvalidOperationException("Battle Stations did not retain both distinct bounded hand-play children.");

        // In the reported match, Battle Stations played two Slave Drivers from
        // hand. Any Driver copy created during Deploy is only spawned because
        // Slave Driver is not a Soldier; the later enlarged Driver is therefore
        // the separately paid second hand card.
        var latestBattle = await Preview(4, battle.Id);
        var firstDriver = await Preview(5, driver.Id);
        var secondDriver = await Preview(8, driver.Id);
        var firstBlankPixels = definition.Load("evidence-06.png");
        var secondBlankPixels = definition.Load("evidence-07.png");
        var firstBlankScreen = (await screenReader.AnalyzeAsync(firstBlankPixels)) with { MatchHudVisible = true };
        var secondBlankScreen = (await screenReader.AnalyzeAsync(secondBlankPixels)) with { MatchHudVisible = true };
        if ((await titleReader.RecognizeAsync(firstBlankPixels, firstBlankScreen, screenReader)).Any(item =>
                item.Card.Id == driver.Id && item.Source == CardSightSource.PlayPreview) ||
            (await titleReader.RecognizeAsync(secondBlankPixels, secondBlankScreen, screenReader)).Any(item =>
                item.Card.Id == driver.Id && item.Source == CardSightSource.PlayPreview))
            throw new InvalidOperationException("A retained blank interval still reads as a Slave Driver preview.");

        var ledger = new MatchVisionLedger();
        var emitted = new List<VisionEvidenceEvent>();
        emitted.AddRange(ledger.Observe(at, latestBattle.Screen, [latestBattle.Sighting], false, artworkWasScanned: true));
        emitted.AddRange(ledger.Observe(at.AddSeconds(4.790), firstDriver.Screen, [firstDriver.Sighting], false, artworkWasScanned: true));
        emitted.AddRange(ledger.Observe(at.AddSeconds(10.706), firstBlankScreen, [], false, artworkWasScanned: true));
        emitted.AddRange(ledger.Observe(at.AddSeconds(12.006), secondBlankScreen, [], false, artworkWasScanned: true));
        emitted.AddRange(ledger.Observe(at.AddSeconds(12.497), secondDriver.Screen, [secondDriver.Sighting], false, artworkWasScanned: false));
        var driverEvents = emitted.Where(item => item.Sighting.Card.Id == driver.Id).ToArray();
        if (driverEvents.Length != 2)
            throw new InvalidOperationException($"The two visible Slave Driver hand plays collapsed to {driverEvents.Length} preview episode(s).");

        // One isolated full-scan miss remains insufficient to split an otherwise
        // continuous popup, preserving the anti-flicker behavior.
        var flicker = new MatchVisionLedger();
        var flickerEvents = new List<VisionEvidenceEvent>();
        flickerEvents.AddRange(flicker.Observe(at, firstDriver.Screen, [firstDriver.Sighting], false, artworkWasScanned: true));
        flickerEvents.AddRange(flicker.Observe(at.AddSeconds(5), firstBlankScreen, [], false, artworkWasScanned: true));
        flickerEvents.AddRange(flicker.Observe(at.AddSeconds(5.5), secondDriver.Screen, [secondDriver.Sighting], false, artworkWasScanned: true));
        if (flickerEvents.Count(item => item.Sighting.Card.Id == driver.Id) != 1)
            throw new InvalidOperationException("One missing artwork scan split a continuous preview episode.");

        var handEvidence = new HandCommitTracker();
        var battleEvent = emitted.Single(item => item.Sighting.Card.Id == battle.Id);
        if (handEvidence.Observe(at, latestBattle.Screen, [battleEvent]).Count != 0)
            throw new InvalidOperationException("Battle Stations was charged as one of its own children.");
        var paidFirstDriver = handEvidence.Observe(at.AddSeconds(4.790), firstDriver.Screen, [driverEvents[0]]);
        var paidSecondDriver = handEvidence.Observe(at.AddSeconds(12.497), secondDriver.Screen, [driverEvents[1]]);
        if (paidFirstDriver.Single().Sighting.Card.Id != driver.Id || paidSecondDriver.Single().Sighting.Card.Id != driver.Id)
            throw new InvalidOperationException("The two Slave Driver episodes were not retained as Battle Stations' two hand plays.");

        var user = new LiveDeckTracker(PlayerSide.User);
        var opponent = new LiveDeckTracker(PlayerSide.Opponent);
        opponent.SetFactionPrior("Nilfgaard");
        var copies = new ThinningCopyTracker();
        if (!HandCommitTracker.Apply(paidFirstDriver.Concat(paidSecondDriver).ToArray(), new(), new(), copies,
                user, opponent, null) ||
            opponent.DeckBuildingObservations.Single(item => item.Card.Id == driver.Id).ObservedCopies != 2)
            throw new InvalidOperationException("Two independently paid Slave Drivers did not establish two original deck copies.");
    }
}
