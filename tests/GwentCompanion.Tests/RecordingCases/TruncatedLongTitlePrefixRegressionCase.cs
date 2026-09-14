using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using System.IO;

internal sealed class TruncatedLongTitlePrefixRegressionCase : IRecordingValidationCase
{
    public string Id => "truncated-long-title-prefix";

    public async Task RunAsync(string project, RecordingValidationCaseDefinition definition)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json"));
        using var reader = new ScreenStateRecognizer();
        var titles = new PreviewTitleRecognizer(catalog);
        var ledger = new MatchVisionLedger();
        var events = new List<VisionEvidenceEvent>();
        var at = DateTimeOffset.UnixEpoch;
        foreach (var (file, seconds) in new[] { ("evidence-01.png", 0d), ("evidence-02.png", .2) })
        {
            var frame = definition.Load(file);
            var screen = await reader.AnalyzeAsync(frame);
            var sightings = await titles.RecognizeAsync(frame, screen, reader);
            var dettlaff = sightings.SingleOrDefault(item => item.Card.Id == "202888" &&
                item.Side == PlayerSide.Opponent && item.Source == CardSightSource.PlayPreview)
                ?? throw new InvalidOperationException("The unique clipped 'DETTLAFF VAN DER' popup prefix was discarded.");
            if (!dettlaff.NeedsTemporalConfirmation || !dettlaff.IsSupplementalTitle)
                throw new InvalidOperationException("A clipped long-name prefix bypassed repeated-frame confirmation.");
            events.AddRange(ledger.Observe(at.AddSeconds(seconds), screen, sightings,
                boardWasScanned: false, artworkWasScanned: true));
        }
        if (events.Count != 1 || events[0].Sighting.Card.Id != "202888")
            throw new InvalidOperationException("Two repeated unique long-title prefixes did not produce exactly one Dettlaff play episode.");

        var card = catalog.Single(item => item.Id == "202888");
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null) { MatchHudVisible = true };
        var projected = new CardSighting(card, PlayerSide.Opponent, CardSightSource.DeckRevealCandidate,
            new(.80, .13, .91, .40), .42, 1, NeedsTemporalConfirmation: true);
        var ordinary = projected with { Source = CardSightSource.PlayPreview, Distance = .34 };
        var mixed = new MatchVisionLedger();
        if (mixed.Observe(at, board, [projected], boardWasScanned: false).Count != 0 ||
            mixed.Observe(at.AddSeconds(1), board, [ordinary], boardWasScanned: false).SingleOrDefault()?.Sighting.Card.Id != card.Id)
            throw new InvalidOperationException("A projected-lane match followed by the same ordinary preview identity did not corroborate bidirectionally.");
        var candidatesOnly = new MatchVisionLedger();
        candidatesOnly.Observe(at, board, [projected], boardWasScanned: false);
        if (candidatesOnly.Observe(at.AddSeconds(1), board, [projected], boardWasScanned: false).Count != 0)
            throw new InvalidOperationException("Two quarantined projected-card candidates fabricated a play.");
    }
}
