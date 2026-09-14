using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunMatchStorageSmokeAsync()
    {
        var folder = Path.Combine(FindGameRoot(), "GwentCompanion", "diagnostics", "match-storage-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        MainWindow? window = null; DataContributionConsentWindow? consent = null;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            window = new MainWindow(); window.Loaded -= window.OnLoaded;
            window._matchStorageDirectoryOverride = Path.Combine(folder, "matches");
            window._matchIdentity = InstallationIdentity.LoadOrCreate(Path.Combine(folder, "installation.json"));
            window._dataContributionPreferences = DataContributionPreferences.Initial.Decide(true, DateTimeOffset.UtcNow);
            window._candidateCatalog = GwentOneCardCatalog.Load(CardDataPath);
            var card = window._candidateCatalog.First(c => c.Kind == CardKind.Unit && c.CanBeInStartingDeck);
            var at = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
            window.BeginMatchAcquisition();
            window._gameState.Reset("smoke");
            window.ApplyVisionResult(new(at.AddSeconds(-2), new(GwentViewKind.Board, false, 0, 0, null,
                ScreenHeader: "STANDARD MODE", MatchHudVisible: false, PostMatchExitCue: true,
                PostMatchMmr: new(2500, null, true, "menu")), [], [], false));
            Check(window._matchAcquisition!.Snapshot() is null && window._observedPostMatchMmr is null && window._waitingOnMenu &&
                window._automaticShareDuePeriod is null,
                "Starting on the menu created match data, scheduled sharing, consumed a stale rating, or failed to wait.");
            var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
                OpponentHandCount: 8, UserHandCount: 8, UserScore: 20, OpponentScore: 30, MatchHudVisible: true);
            window.ApplyVisionResult(new(at, board with { ScreenHeader = "ROUND 1" }, [], [], false));
            window.ApplyVisionResult(new(at.AddSeconds(1), board with { ScreenHeader = "ROUND 1" }, [], [], false));
            Check(window._automaticShareDuePeriod == "2026-09",
                "The first real match after the 18th did not reach the receipt-gated monthly sharing hook.");
            var sight = new CardSighting(card, PlayerSide.Opponent, CardSightSource.PlayPreview, new(.4, .2, .5, .4), .02, .4);
            window.ApplyVisionResult(new(at.AddSeconds(2), board, [], [new(at.AddSeconds(2), sight, "fixture")], false));
            window.ApplyVisionResult(new(at.AddSeconds(3), board with { ScreenHeader = "DEFEAT", MatchHudVisible = false,
                PostMatchRoundScores = [new(1, 20, 30), new(2, 42, 51)] }, [], [], false));
            await window.FlushMatchAcquisitionAsync();
            var files = Directory.GetFiles(window.MatchStorageDirectory, "*.gvm", SearchOption.AllDirectories);
            Check(files.Length == 1, "First game did not produce exactly one match file.");
            var match = LocalMatchStore.Read(files[0]);
            Check(match.Result == "DEFEAT" && match.MmrAfter is null && match.CaptureStopped, "Final detector metadata was not flushed.");
            Check(match.Actions.Length == 0 && match.StartedAtUtc == at, "Match time missing or unnecessary sequence stored.");
            Check(match.Rounds.Length == 2 && match.Rounds.All(r => r.FinalConfirmed) && match.Rounds[1].UserScore == 42,
                "Confirmed result-table scores did not reach stored match data.");
            Check(match.Opponent.Observations.Any(c => c.CardId == card.Id), "Visual evidence did not reach observations.");
            window.ApplyVisionResult(new(at.AddSeconds(8), board with { ScreenHeader = "STANDARD MODE", MatchHudVisible = false,
                PostMatchMmrCandidate = new(2392, null, true, "menu", 2440, Confirmed: false, ReadCount: 2) }, [], [], false));
            var nextCard = window._candidateCatalog.First(c => c.Kind == CardKind.Unit && c.CanBeInStartingDeck && c.Id != card.Id);
            var nextSighting = sight with { Card = nextCard };
            window.ApplyVisionResult(new(at.AddSeconds(10), board with { ScreenHeader = "ROUND 1", UserScore = 999 }, [],
                [new(at.AddSeconds(10), nextSighting, "next game")], false));
            window.ApplyVisionResult(new(at.AddSeconds(11), board with { ScreenHeader = "ROUND 1" }, [], [], false));
            await window.FlushMatchAcquisitionAsync();
            files = Directory.GetFiles(window.MatchStorageDirectory, "*.gvm", SearchOption.AllDirectories);
            Check(files.Length == 1 && window._closedAtNextGame, "Next game did not close the previous match capture.");
            var closed = LocalMatchStore.Read(files[0]);
            Check(closed.MatchId == match.MatchId && closed.MmrAfter == 2392 && closed.MmrUnconfirmed &&
                closed.Rounds.All(r => r.UserScore != 999) && closed.Opponent.Observations.All(c => c.CardId != nextCard.Id),
                "Best fallback rating was lost or next-game evidence leaked into the previous match.");
            window.HideLoadingShell(); window.ShowPage(UiPage.Settings);
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "settings.png"), 510, 850);
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "settings-compact.png"), 370, 850);
            consent = new DataContributionConsentWindow();
            DeckBuilderSmoke.Render(consent, Path.Combine(folder, "data-consent.png"), 570, 570);
            Check(consent.PublishAnonymousCurve && consent.AutomaticMonthlyUpload,
                "Consent dialog defaults do not match the disclosed opt-in choices.");
            Check(window.SettingsPage.Visibility == Visibility.Visible, "Settings page unavailable.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: menu startup waits; confirmed final scores saved; next game closes capture with best unconfirmed rating; no new-game card/score leakage; persistent identity; Settings and first-start consent rendered. No capture, network, or live data writes.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
        finally
        {
            if (window is not null)
            {
                window._projectionQueue?.Dispose();
                await window._matchStorageWrite;
                window.Close();
            }
            consent?.Close();
        }
    }
}
