using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

internal static partial class DeckBuilderSmoke
{
    internal static async Task<int> RunSeasonToolsAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) return 1;
        var project = Path.Combine(root.FullName, "GwentCompanion");
        var folder = Path.Combine(project, "diagnostics/season-tools-ui-smoke"); Directory.CreateDirectory(folder);
        DeckBuilderWindow? builder = null; DeckBuilderWindow? repairBuilder = null; Window? preview = null; Window? scrollWindow = null;
        try
        {
            var path = Path.Combine(project, "cache/deck-library.json"); var hash = SHA256.HashData(File.ReadAllBytes(path));
            var catalogPath = Path.Combine(project, "cache/gwent-one-cards.json");
            var isolatedCatalog = Path.Combine(folder, "gwent-one-cards.json"); File.Copy(catalogPath, isolatedCatalog, true);
            foreach (var suffix in new[] { ".baseline", ".previous" })
            {
                if (File.Exists(catalogPath + suffix)) File.Copy(catalogPath + suffix, isolatedCatalog + suffix, true);
                else if (File.Exists(isolatedCatalog + suffix)) File.Delete(isolatedCatalog + suffix);
            }
            var changes = CardBalanceChanges.Load(isolatedCatalog); Check(changes.Available, "A local comparison is required for this offline smoke.");
            var catalog = GwentOneCardCatalog.Load(isolatedCatalog); var library = DeckLibrary.Load(path);
            var template = library.Decks.First(d => d.Faction == "Skellige" && d.Leader == "Onslaught" && d.CardCount == 25);
            builder = new DeckBuilderWindow(library, Path.Combine(folder, "unused-library.json"), catalog, template, () => { }); await Settled(builder);
            Call(builder, "ResetCollectionClicked", builder, new RoutedEventArgs());
            CardDefinition Card(object row) => (CardDefinition)row.GetType().GetProperty("Card")!.GetValue(row)!;
            CardDefinition[] Collection() => ((IEnumerable)builder.Collection.Rows!).Cast<object>().Select(Card).ToArray();
            var eligible = Collection(); var byId = changes.Cards.ToDictionary(c => c.CardId);
            for (var filter = 1; filter <= 4; filter++)
            {
                builder.CardUpdateFilter.SelectedIndex = filter;
                var expected = eligible.Where(c => byId.TryGetValue(c.Id, out var change) && (filter switch { 2 => change.StatBuff, 3 => change.StatNerf, 4 => !change.StatBuff && !change.StatNerf, _ => true }));
                Check(Collection().Select(c => c.Id).Order().SequenceEqual(expected.Select(c => c.Id).Order()), "Update filter mismatch: " + filter);
                Check(builder.CollectionStatus.Text.Contains(changes.ToVersion!), "Comparison version missing.");
            }
            builder.CardUpdateFilter.SelectedIndex = 2; Check(Collection().Length > 0, "No real Skellige/neutral buffs exposed.");
            var changed = Collection()[0]; builder.CardSearch.Text = changed.Name;
            Check(Collection().Any(c => c.Id == changed.Id) && Collection().All(c => byId[c.Id].StatBuff), "Search discarded the update filter.");
            Call(builder, "CardInspected", null, ((IEnumerable)builder.Collection.Rows!).Cast<object>().First());
            Check(builder.InspectedCardMeta.Text.Contains("→") && builder.InspectedCardMeta.Text.Contains(changes.FromVersion!), "Inspector omitted old/new values.");
            builder.CardSearch.Clear();
            Render(builder, Path.Combine(folder, "changed-cards-wide.png"), 1320, 900);
            Render(builder, Path.Combine(folder, "changed-cards-compact.png"), 728, 530);
            Check(builder.Collection.ActualHeight >= 70 && builder.DraftCards.ActualHeight >= 70, "New filters squeezed the compact lists out.");
            Call(builder, "ResetCollectionClicked", builder, new RoutedEventArgs());
            Check(builder.CardUpdateFilter.SelectedIndex == 0 && Collection().Length == eligible.Length, "Reset did not clear update filters.");
            var realDraft = new CurrentCardValues(catalog).Deck(template);
            if (realDraft.ProvisionTotal > 150 + realDraft.LeaderProvisionBonus)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var realRepair = await Task.Run(() => DeckRepair.Suggest(realDraft, library.Decks, catalog));
                Check(realRepair.Options.Count > 0 && realRepair.Options.All(o => DeckBuildValidation.Errors(o.Deck, archetypes: true).Count == 0), "Real over-budget Onslaught list could not be repaired.");
                preview = builder.CreateRepairDialog(realDraft, realRepair);
                Render(preview, Path.Combine(folder, "real-repair-preview.png"), 610, 520); preview.Close(); preview = null;
                File.WriteAllText(Path.Combine(folder, "real-repair.txt"), $"{realDraft.Name}: {realDraft.ProvisionTotal}/{150 + realDraft.LeaderProvisionBonus}p; {realRepair.Options.Count} proposals; minimum {realRepair.Options[0].Swaps} substitutions; {watch.ElapsedMilliseconds}ms.");
            }

            var leader = new CardDefinition("repair-leader", "Repair leader", "Skellige", CardKind.Leader, 15, CanBeInStartingDeck: false);
            var cards = Enumerable.Range(0, 25).Select(i => new CardDefinition("repair-" + i, "Repair " + i, "Skellige", CardKind.Unit,
                i == 0 ? 24 : i == 1 ? 8 : 6, 5, i < 2, AbilityText: "")).ToArray();
            var replacement = cards[0] with { Id = "replacement", Name = "Related replacement", Provision = 19 };
            CardDefinition[] repairCatalog = [.. cards, replacement, leader];
            var source = new DeckDefinition("repair-ui", "Older deck", "Skellige", leader.Name, 15, cards.Select(c => new DeckCard(c)).ToArray());
            var donor = source with { Id = "repair-donor", Cards = source.Cards.Select(c => c.Card.Id == cards[0].Id ? new DeckCard(replacement) : c).ToArray() };
            var options = DeckRepair.Suggest(source, [source, donor], repairCatalog); Check(options.Options.Count > 0, "No UI repair fixture.");
            repairBuilder = new DeckBuilderWindow(library, Path.Combine(folder, "unused-repair.json"), repairCatalog, source, () => { }); await Settled(repairBuilder);
            Check(!repairBuilder.SaveDeckButton.IsEnabled, "Invalid original can be saved.");
            preview = repairBuilder.CreateRepairDialog(source, options);
            Render(preview, Path.Combine(folder, "repair-preview.png"), 610, 520);
            Check(Find<TextBlock>(preview).Any(t => t.Text.Contains("Remove:") && t.Text.Contains("Add:")), "Preview lacks swaps.");
            Check(Result(repairBuilder).Cards.Any(c => c.Card.Id == cards[0].Id), "Preview changed the draft before confirmation.");
            preview.Close(); preview = null;
            Call(repairBuilder, "ApplyRepair", options.Options[0]); await Settled(repairBuilder);
            Check(repairBuilder.SaveDeckButton.IsEnabled && Result(repairBuilder).Cards.Any(c => c.Card.Id == replacement.Id), "Apply did not repair the budget.");
            Call(repairBuilder, "UndoEditClicked", repairBuilder, new RoutedEventArgs()); await Settled(repairBuilder);
            Check(!repairBuilder.SaveDeckButton.IsEnabled && Result(repairBuilder).Cards.Any(c => c.Card.Id == cards[0].Id) && Result(repairBuilder).Cards.All(c => c.Card.Id != replacement.Id), "Repair is not one-step undoable.");
            Call(repairBuilder, "RedoEditClicked", repairBuilder, new RoutedEventArgs()); await Settled(repairBuilder);
            Check(repairBuilder.SaveDeckButton.IsEnabled, "Repair redo failed.");
            Check(!File.Exists(Path.Combine(folder, "unused-repair.json")), "Repair silently saved the library.");

            var scroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Width = 1000, Height = 1200, Background = new SolidColorBrush(Color.FromRgb(23, 27, 32)), Child = new TextBlock { Text = "Scroll test", Margin = new Thickness(20) } } };
            scrollWindow = new Window { Content = scroller };
            Render(scrollWindow, Path.Combine(folder, "scrollbars.png"), 320, 260);
            var bars = VisualChildren<ScrollBar>(scroller).ToArray(); Check(bars.Length == 2, "Both scrollbars not rendered.");
            foreach (var bar in bars)
            {
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                Check(track.Orientation == bar.Orientation && track.Thumb.ActualWidth > 0 && track.Thumb.ActualHeight > 0, "Scroll track missing or wrong orientation.");
                Check(((SolidColorBrush)bar.Background).Color.R < 40 && ((SolidColorBrush)track.Thumb.Background).Color.R < 150, "Native white scrollbar returned.");
            }
            scroller.ScrollToVerticalOffset(80); scroller.ScrollToHorizontalOffset(70); scroller.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Check(scroller.VerticalOffset == 80 && scroller.HorizontalOffset == 70, "Scrollbar value binding broke scrolling.");
            var vertical = bars.Single(b => b.Orientation == Orientation.Vertical);
            var verticalTrack = (Track)vertical.Template.FindName("PART_Track", vertical);
            verticalTrack.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 20) { RoutedEvent = Thumb.DragDeltaEvent });
            await Dispatcher.Yield(DispatcherPriority.Background); scroller.UpdateLayout();
            Check(scroller.VerticalOffset > 80, "Dragging the dark thumb did not scroll.");
            ScrollBar.PageDownCommand.Execute(null, vertical);
            await Dispatcher.Yield(DispatcherPriority.Background); scroller.UpdateLayout();
            Check(scroller.VerticalOffset > 180, "Dark track page command failed.");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Live library changed.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: all update filters, search intersection, old/new inspector, compact/wide layouts, preview without mutation, legal repair/apply/Undo/Redo without autosave, vertical/horizontal dark scrollbar bindings, thumb drag and page commands. No network, game capture or live library writes.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
        finally
        {
            preview?.Close(); scrollWindow?.Close();
            foreach (var window in new[] { builder, repairBuilder }.OfType<DeckBuilderWindow>())
            { typeof(DeckBuilderWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false); window.Close(); }
        }
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var descendant in VisualChildren<T>(child)) yield return descendant; }
    }
}
