using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

internal static partial class DeckBuilderSmoke
{
    internal static async Task<int> RunGesturesAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) return 1;
        var project = Path.Combine(root.FullName, "GwentCompanion"); var folder = Path.Combine(project, "diagnostics/editor-gestures-smoke"); Directory.CreateDirectory(folder);
        DeckBuilderWindow? builder = null;
        try
        {
            var path = Path.Combine(project, "cache/deck-library.json"); var hash = SHA256.HashData(File.ReadAllBytes(path));
            var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json")); var library = DeckLibrary.Load(path);
            var template = library.Decks.First(d => d.Faction == "Skellige" && d.Leader == "Onslaught" && d.CardCount == 25 && d.Cards.Any(c => c.Count == 2));
            builder = new DeckBuilderWindow(library, Path.Combine(folder, "unused-library.json"), catalog, template, () => { }); await Settled(builder);
            object[] Rows() => ((IEnumerable)builder.DraftCards.Rows!).Cast<object>().ToArray();
            int Count() => Result(builder).Cards.Sum(c => c.Count);
            CardDefinition Card(object row) => (CardDefinition)row.GetType().GetProperty("Card")!.GetValue(row)!;
            void Activate(object row, ModifierKeys modifiers) => Call(builder, "ActivateDraftCard", row, modifiers);
            void Undo() => Call(builder, "UndoEditClicked", builder, new RoutedEventArgs());
            // Two quick manual clicks both apply; neither is swallowed by the auto-fill debounce.
            Activate(Rows()[0], ModifierKeys.None); Check(Count() == 24, "Plain click did not immediately remove a copy.");
            Activate(Rows()[0], ModifierKeys.None); Check(Count() == 23, "Second immediate click was swallowed.");
            Undo(); Check(Count() == 24, "Undo did not restore one click."); Undo(); Check(Count() == 25, "Undo did not restore the original deck.");
            var rows = Rows(); Activate(rows[0], ModifierKeys.Control); Activate(rows[2], ModifierKeys.Control);
            Check(Count() == 25 && builder.RemoveButton.Content.ToString() == "Remove 2", "Ctrl-click removed cards or did not toggle selection.");
            Activate(rows[2], ModifierKeys.Control); Check(builder.RemoveButton.Content.ToString() == "Remove", "Ctrl-click did not deselect a copy.");
            Activate(rows[0], ModifierKeys.Control); Activate(rows[0], ModifierKeys.Control); Activate(rows[3], ModifierKeys.Shift);
            Check(builder.RemoveButton.Content.ToString() == "Remove 4" && Count() == 25, "Shift range selection changed composition or selected the wrong copies.");
            Call(builder, "RecommendationFocusClicked", builder, new RoutedEventArgs());
            Check(builder.CardSortChoice.SelectedIndex == 3 && builder.RecommendationFocusPanel.Visibility == Visibility.Visible &&
                builder.RecommendationFocusText.Text.Contains(Card(rows[0]).Name) && Count() == 25, "Related-to-selection mutated the deck or omitted selected anchors.");
            Render(builder, Path.Combine(folder, "selection-wide.png"), 1320, 900);
            Render(builder, Path.Combine(folder, "selection-compact.png"), 728, 530);
            Check(builder.Collection.ActualHeight >= 70 && builder.DraftCards.ActualHeight >= 70, "Selection controls squeezed compact lists out.");
            Call(builder, "RemoveClicked", builder, new RoutedEventArgs()); Check(Count() == 21, "Bulk remove failed.");
            Undo(); Check(Count() == 25, "Bulk removal was not one undoable edit.");
            Call(builder, "ClearRecommendationFocusClicked", builder, new RoutedEventArgs());
            Check(builder.RecommendationFocusPanel.Visibility == Visibility.Collapsed, "Whole-deck focus did not restore.");
            // Physical copies: selecting both bronzes removes both, and Undo restores both.
            rows = Rows(); var pair = rows.GroupBy(r => Card(r).Id).First(g => g.Count() == 2).ToArray();
            Activate(pair[0], ModifierKeys.Control); Activate(pair[1], ModifierKeys.Control); Call(builder, "RemoveClicked", builder, new RoutedEventArgs());
            Check(Result(builder).Cards.All(c => c.Card.Id != Card(pair[0]).Id) && Count() == 23, "Bulk remove left one selected bronze copy.");
            Undo(); Check(Result(builder).Cards.Single(c => c.Card.Id == Card(pair[0]).Id).Count == 2, "Undo lost a bronze copy.");
            builder.CardSortChoice.SelectedIndex = 3;
            var scores = (Dictionary<string, double>)typeof(DeckBuilderWindow).GetField("_relatedCards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(builder)!;
            Check(scores.Count > 0 && scores.Values.Any(v => v > 0), "Actual full Onslaught deck has no ranked alternatives.");
            var collection = ((IEnumerable)builder.Collection.Rows!).Cast<object>().Select(Card).ToArray();
            Check(scores.GetValueOrDefault(collection[0].Id) > 0 && collection.SequenceEqual(collection.OrderByDescending(c => scores.GetValueOrDefault(c.Id)).ThenBy(c => c, DeckBuilderOrder.Comparer)),
                "Full-deck collection is not sorted by related-card support.");
            Render(builder, Path.Combine(folder, "full-deck-related.png"), 1320, 900);
            builder.AutoFill.IsChecked = true; await Settled(builder);
            var autoCard = Card(Rows()[0]); Activate(Rows()[0], ModifierKeys.None); await Settled(builder);
            Check(Result(builder).Cards.All(c => c.Card.Id != autoCard.Id) || Result(builder).Cards.Single(c => c.Card.Id == autoCard.Id).Count < template.CountOf(autoCard.Id),
                "Auto-fill immediately restored the clicked-away copy.");
            Undo(); await Settled(builder);
            // All six headings share the same control used in Library, Reference, memory and templates.
            var panel = new StackPanel { Background = FactionPalette.Brush("#101419") };
            foreach (var faction in catalog.Where(c => c.Kind == CardKind.Leader && c.Faction != "Neutral").Select(c => c.Faction).Distinct())
            {
                var leader = GwentOneCardCatalog.StartingLeaders(catalog).First(c => c.Faction == faction && DeckHeading.LeaderArt(c.Faction, c.Name) is not null);
                panel.Children.Add(new DeckHeading { Title = faction + " deck", Subtitle = leader.Name + " · 25 cards · 165p", Faction = faction, Leader = leader.Name, Margin = new Thickness(5) });
            }
            var headings = new Window { Content = panel, Background = FactionPalette.Brush("#101419") };
            Render(headings, Path.Combine(folder, "faction-headings.png"), 540, 560);
            // InputHitTest rejects the deliberately unshown Window through IsVisible. Check the
            // rendered hit geometry directly, plus the heading's actual hit-test setting.
            Check(panel.Children.OfType<DeckHeading>().All(h => h.IsHitTestVisible && VisualTreeHelper.HitTest(h, new Point(20, 20)) is not null),
                "Deck heading stopped forwarding pointer hits to its result row.");
            Check(Find<Image>(headings).Count(i => i.Visibility == Visibility.Visible && i.Source is not null) == 6, "Cached leader art was not displayed for all six factions.");
            Render(headings, Path.Combine(folder, "faction-headings-narrow.png"), 210, 560);
            Check(Find<Image>(headings).All(i => i.Visibility == Visibility.Collapsed), "Leader icons consumed space in narrow headings.");
            headings.Close();
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Live library changed during gesture smoke.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: rapid direct removal, Ctrl-toggle/Shift-range selection, focus without mutation, bulk/bronze removal and one-step Undo, actual full-deck related alternatives, auto-fill exclusions, six faction headings with cached ability art and narrow fallback. Compact/wide renders; live library unchanged.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
        finally { if (builder is not null) { typeof(DeckBuilderWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(builder, false); builder.Close(); } }
    }
}
