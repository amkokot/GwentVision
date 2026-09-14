using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunSmokeAsync(string root)
    {
        var folder = Path.Combine(root, "diagnostics", "match-analysis-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        MatchAnalysisWindow? window = null;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        IEnumerable<T> Visuals<T>(DependencyObject node) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T item) yield return item;
                foreach (var descendant in Visuals<T>(child)) yield return descendant;
            }
        }
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "cache", "gwent-one-cards.json"));
            var leaders = GwentOneCardCatalog.StartingLeaders(catalog);
            var ownLeader = leaders.First(c => c.Faction == "Skellige");
            MatchCard[] Cards(string faction, MatchCardEvidence evidence, int count) => catalog
                .Where(c => c.CanBeInStartingDeck && c.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact && c.Faction == faction)
                .Take(count).Select(c => new MatchCard(c.Id, 1, evidence,
                    evidence == MatchCardEvidence.Observed ? CardProvenance.ProbableStartingDeck : CardProvenance.Unknown, 215)).ToArray();
            var mine = new MatchPlayer(ownLeader.Faction, ownLeader.Name, null,
                Cards(ownLeader.Faction, MatchCardEvidence.Observed, 7), Cards(ownLeader.Faction, MatchCardEvidence.SelectedReference, 20), []);
            var directory = Path.Combine(folder, "matches"); var store = new LocalMatchStore(directory); var install = Guid.NewGuid();
            for (var i = 0; i < 60; i++)
            {
                var faction = MatchAnalysis.Factions[i % 6]; var leader = leaders.First(c => c.Faction == faction);
                var other = new MatchPlayer(faction, leader.Name, null, Cards(faction, MatchCardEvidence.Observed, 9), [],
                    Cards(faction, MatchCardEvidence.Inferred, 13).Skip(9).ToArray());
                var outcome = (i % 10) switch { 8 => "DRAW", 9 => null, _ => i % 2 == 0 ? "VICTORY" : "DEFEAT" };
                var date = i < 40 ? new DateOnly(2026, 9, 10).AddDays(-i / 5) : new DateOnly(2026, 8, 30).AddDays(-(i - 40) / 5);
                store.Save(new(install, new Guid(i + 1, 0, 0, new byte[8]), date, "fixture", "rules", i < 40 ? "14.9" : "14.8",
                    true, 1, true, outcome is not null, outcome, 2410 + i, outcome is null ? null : outcome == "VICTORY" ? 8 : -7,
                    null, true, null, mine, other, [new(1, 42, 38, false), new(2, 15, 34, false)],
                    [], StartedAtUtc: new DateTimeOffset(date.ToDateTime(new TimeOnly(12, i % 5)), TimeSpan.Zero)));
            }
            var files = Directory.GetFiles(directory, "*.gvm", SearchOption.AllDirectories);
            var hashes = files.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            window = new MatchAnalysisWindow(directory, root);
            await window.RefreshAsync();
            Check(window._filtered.Length == 40 && ((PatchOption)window.PatchChoice.SelectedItem).Value == "14.9", "Default latest patch filter failed.");
            Check(window.HistoryList.Items.Count == PageSize && window.NextButton.IsEnabled, "History pagination failed.");
            Check(window.HistoryList.Items.Cast<HistoryVm>().Any(r => r.Mine.Icon is not null && r.Other.Icon is not null), "Leader icons did not load.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "history.png"), 1440, 1000);
            var expander = Visuals<Expander>(window.HistoryList).First();
            expander.IsExpanded = true;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (expander.Content is not ContentControl && DateTime.UtcNow < deadline) await Task.Delay(25);
            Check(expander.Content is ContentControl { Content: DetailsVm }, "Expanded match did not load both decks.");
            var details = (DetailsVm)((ContentControl)expander.Content).Content;
            Check(details.Mine.Label == "YOUR DECK" && details.Other.Label == "OPPONENT DECK" &&
                details.Other.Groups.Length == 1 && details.Other.Groups[0].Cards.Length == 13, "Opponent cards were not combined into one deck list.");
            var opponentRows = details.Other.Groups[0].Cards;
            Check(opponentRows.Count(c => c.Badge == "SEEN" && c.RowOpacity == 1) == 9 &&
                opponentRows.Count(c => c.Badge.EndsWith("% ?") && c.RowOpacity == 0.76) == 4,
                "Combined list lost the in-game observed/guess notation or fade.");
            Check(details.Mine.Groups.Length == 1 && details.Mine.Groups[0].Cards.Length == 20 &&
                details.Mine.Groups[0].Cards.All(c => c.Badge == "SAVED"), "Complete saved player deck was not primary.");
            var bear = catalog.Single(c => c.Name == "Bear Abomination");
            var startingCard = catalog.First(c => c.CanBeInStartingDeck && c.Faction == "Skellige");
            var tokenFixture = new MatchPlayer("Skellige", ownLeader.Name, null,
                [new(bear.Id, 1, MatchCardEvidence.Observed, CardProvenance.Spawned, 254),
                 new(bear.Id, 1, MatchCardEvidence.Observed, CardProvenance.ProbableStartingDeck, 254),
                 new(startingCard.Id, 1, MatchCardEvidence.Observed, CardProvenance.Created, 254),
                 new(startingCard.Id, 1, MatchCardEvidence.Observed, CardProvenance.ProbableStartingDeck, 254)], [],
                [new(bear.Id, 1, MatchCardEvidence.Inferred, CardProvenance.Unknown, 200),
                 new(startingCard.Id, 1, MatchCardEvidence.Inferred, CardProvenance.Unknown, 200)]);
            var eligible = window.StartingDeckCards(tokenFixture);
            Check(eligible.Length == 2 && eligible.All(c => c.CardId == startingCard.Id) &&
                tokenFixture.Observations.Length == 4 && tokenFixture.Hypothesis.Length == 2,
                "Starting-deck display admitted a token/generated card or changed the raw evidence.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "expanded.png"), 1440, 1100);
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "expanded-small.png"), 960, 800);
            var historyScroll = Visuals<ScrollViewer>(window.HistoryView).First();
            historyScroll.ScrollToVerticalOffset(310);
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "deck-cards.png"), 1440, 1000);
            Check(Visuals<Controls.DeckCardList>(window.HistoryList).Any(), "Expanded match did not reuse library card rows.");
            expander.IsExpanded = false;
            Check(expander.Content is null, "Collapsed match retained its loaded deck controls.");
            window.NextClicked(window, new RoutedEventArgs());
            Check(window.HistoryList.Items.Count == 10 && window.PreviousButton.IsEnabled && !window.NextButton.IsEnabled, "Next-page bounds incorrect.");
            window.DisplayChoice.SelectedIndex = 1;
            Check(window.BreakdownList.Items.Count == 6, "Faction exposure omits factions.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "factions.png"), 1440, 1000);
            window.MetricChoice.SelectedIndex = 1;
            window.GroupChoice.SelectedIndex = 1;
            Check(window.BreakdownList.Items.Count >= leaders.Length, "Leader view omitted empty leader groups.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "leaders.png"), 1440, 1000);
            window.SideChoice.SelectedIndex = 1;
            Check(window.BreakdownList.Items.Cast<BreakdownVm>().First().Identity.Title == "Skellige", "Player breakdown used opponent factions.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "player-leaders.png"), 1440, 1000);
            window.DisplayChoice.SelectedIndex = 2;
            Check(window.StatisticsList.Items.Count == 5 && window.RatingCharts.Items.Count == 6, "Gameplay metrics and faction charts did not populate.");
            Check(window.StatisticsList.Items.Cast<MetricVm>().All(m => !m.Label.Contains("detect", StringComparison.OrdinalIgnoreCase)), "Detector metrics leaked into gameplay statistics.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "statistics.png"), 1440, 1000);
            foreach (var chart in Visuals<Controls.MatchRatingChart>(window.RatingCharts))
                chart.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseMoveEvent });
            window.StatisticsView.ScrollToBottom();
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "statistics-review.png"), 1440, 1000);
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "statistics-small.png"), 960, 800);
            window.PatchChoice.SelectedIndex = 0;
            Check(window._filtered.Length == 60, "All-patches choice failed.");
            window.SetFullscreen(true); Check(window.WindowStyle == WindowStyle.None, "Fullscreen did not remove window chrome.");
            window.SetFullscreen(false); Check(window.WindowStyle == WindowStyle.SingleBorderWindow, "Window mode was not restored.");
            Check(hashes.All(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value), "Analysis changed match files.");
            window.Close(); window = new MatchAnalysisWindow(Path.Combine(folder, "empty"), root);
            await window.RefreshAsync();
            Check(window.EmptyPanel.Visibility == Visibility.Visible, "Empty data has no guidance.");
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "empty.png"), 1100, 780);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: patch/default/all filters; leader icons; paginated history; lazy two-deck expansion; observed/reference/inferred separation; faction and leader metrics; statistics; full-screen restore; empty state; original file hashes preserved. Synthetic fixtures only; no live data changes.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
        finally { window?.Close(); }
    }
}

public partial class MainWindow
{
    internal static Task<int> RunMatchAnalysisSmokeAsync() => MatchAnalysisWindow.RunSmokeAsync(FindDataRoot());
}
