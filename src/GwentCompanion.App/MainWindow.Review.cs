using System.IO;
using System.Windows.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private readonly string? _reviewEvidencePath = ReviewArgument();

    private static string? ReviewArgument()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--analysis-control-test") || arguments.Contains("--review-live-insights")) return "";
        var index = Array.IndexOf(arguments, "--review-evidence");
        return index < 0 ? null : index + 1 < arguments.Length ? arguments[index + 1] : "";
    }

    private void LoadOfflineReview()
    {
        Title += " · OFFLINE REVIEW";
        // Review never discovers/captures the game, writes match records or changes saved deck selection.
        SnapshotButton.IsEnabled = false; PinCurrentButton.IsEnabled = false;
        DiagnosticButton.IsEnabled = false;
        SyncDecksButton.IsEnabled = false;
        GameStatusText.Text = "OFFLINE REVIEW · capture disabled";
        WindowDetailText.Text = "Saved recognized events only; this is not a live match.";
        ShowPage(UiPage.Deck);
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json"));
            if (Environment.GetCommandLineArgs().Contains("--review-live-insights")) { LoadLiveInsightsFixture(catalog); return; }
            var events = SavedVisionEvents.Read(_reviewEvidencePath!, catalog);
            foreach (var evidence in events)
            {
                var sight = evidence.Sighting;
                var tracker = sight.Side == PlayerSide.User ? _userTracker : _opponentTracker;
                _opponentTime = evidence.ObservedAt;
                _deckMutations.Observe(evidence, _selectedUserDeck?.Faction ?? _userTracker.Faction, _opponentTracker.Faction);
                var baseOrigin = _playOrigins.Observe(evidence, _selectedUserDeck);
                var origin = _deckMutations.OriginRisk(sight, evidence.ObservedAt, tracker.Observations) ??
                    _opponentKnowledge.BoardOrigin(evidence, _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null) ?? baseOrigin;
                if (!sight.Card.CanBeInStartingDeck && !EvolvingCardCatalog.IsEvolved(sight.Card.Id)) origin = baseOrigin;
                var identity = EvolvingCardCatalog.StartingIdentity(sight, origin, catalog, tracker.Faction);
                _opponentKnowledge.Observe(evidence, _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null, identity.Origin.Provenance);
                Watch?.ObserveEvent(evidence);
                tracker.ConsiderDirectPlay(identity.Card, 1 - sight.Distance, evidence.ObservedAt,
                    evidence.Description + " " + identity.Origin.Reason, identity.Origin.Provenance);
                if (sight.Source == CardSightSource.PlayPreview) _recordedPlayEvents++;
            }
            RenderLiveInference();
            LiveViewText.Text = "Offline review: " + Path.GetFileName(_reviewEvidencePath);
            LiveDetailText.Text = $"{events.Count} saved evidence events replayed. Vision was not re-run; no live capture or training record writes.";
            DetectedPlayText.Text = $"{_recordedPlayEvents} recognized preview episodes in this recording.";
            var arguments = Environment.GetCommandLineArgs();
            var positionIndex = Array.IndexOf(arguments, "--review-threat-position");
            if (positionIndex >= 0 && positionIndex + 1 < arguments.Length)
            {
                // Explicit supplied-state UI fixture; does not claim this position came from the recording.
                var position = PositionNotation.Read(File.ReadAllText(arguments[positionIndex + 1]));
                var hover = position.Zone(PlayerSide.User, CardZone.Hand).Cards.Single(card => card.InstanceId == "hover");
                var candidates = position.Zone(PlayerSide.Opponent, CardZone.Hand).Cards.Select(card =>
                    new ThreatCandidate(catalog.Single(item => item.Id == card.CardId), .7, "UI fixture hypothesis")).ToArray();
                RenderThreatReport(catalog.Single(card => card.Id == hover.CardId),
                    new ThreatAnalyzer(catalog).Analyze(new(position, hover.InstanceId, ["Supplied UI fixture; not automatic recognition"]), 1, candidates));
                ShowPage(UiPage.Plays);
                GameStatusText.Text = "OFFLINE REVIEW · supplied threat fixture";
                _selectedSummon = null; RenderSummons();
            }
        }
        catch (Exception exception)
        {
            WindowDetailText.Text = "Cannot read evidence: " + exception.Message;
        }
    }
}
