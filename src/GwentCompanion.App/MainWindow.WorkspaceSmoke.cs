using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Domain;
using GwentCompanion.App.Controls;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static int RunWorkspaceSmoke()
    {
        var project = Path.Combine(FindGameRoot(), "GwentCompanion");
        var folder = Path.Combine(project, "diagnostics/workspace-ui-smoke"); Directory.CreateDirectory(folder);
        var publicData = Path.Combine(project, "release/GwentVision");
        var fixtureData = File.Exists(Path.Combine(publicData, "cache/deck-library.json")) ? publicData : project;
        var fixtureLibrary = Path.Combine(fixtureData, "cache/deck-library.json");
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            var before = SHA256.HashData(File.ReadAllBytes(LibraryPath));
            var window = new MainWindow(); window.Loaded -= window.OnLoaded;
            // Native placement is exercised on an unshown HWND. Never starts the game, discovery, capture or browser.
            var initial = DesktopDisplays.Bounds(window);
            var display = DesktopDisplays.Nearest(initial, DesktopDisplays.All());
            window.SetExpandedWorkspace(true);
            Check(window._expandedWorkspace, "Expand did not enter workspace mode.");
            window.MoveWorkspaceToDisplay(display.Id);
            Check(DesktopDisplays.Nearest(DesktopDisplays.Bounds(window), DesktopDisplays.All()).Id == display.Id, "Display-menu placement failed.");
            window.SetFullScreen(true);
            Check(window.WindowStyle == WindowStyle.None && window._fullScreen, "F11 style transition failed.");
            var fullBounds = DesktopDisplays.Bounds(window);
            Check(Math.Abs(fullBounds.Width - display.Bounds.Width) <= 2 && Math.Abs(fullBounds.Height - display.Bounds.Height) <= 2, "Full screen did not use the monitor bounds.");
            window.SetFullScreen(false); window.SetExpandedWorkspace(false);
            Check(window.WindowStyle == WindowStyle.SingleBorderWindow && !window._fullScreen, "Exit did not restore chrome.");
            Check(DesktopDisplays.Bounds(window) == DesktopDisplays.Fit(initial, display.WorkArea), "Compact window position/size was not restored.");
            var left = new DesktopDisplays.Display("left", new Rect(-2560, -300, 2560, 1440), new Rect(-2560, -300, 2560, 1400), false);
            var primary = new DesktopDisplays.Display("primary", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), true);
            Check(DesktopDisplays.Nearest(new Rect(-2200, -100, 1200, 900), [primary, left]) == left, "Negative-origin monitor selection failed.");
            var repaired = DesktopDisplays.Fit(new Rect(-2400, -200, 1500, 1200), primary.WorkArea);
            Check(primary.WorkArea.Contains(repaired), "Disconnected-monitor restore remained off-screen.");

            window._library = DeckLibrary.Load(fixtureLibrary); window._cachedDecks = window._library.Decks;
            window._candidateCatalog = GwentOneCardCatalog.Load(Path.Combine(fixtureData, "cache/gwent-one-cards.json"));
            window._library.EnsureVariationGroups(window._candidateCatalog);
            window.SetDeckListItems(window.CreateDeckItems(window._cachedDecks));
            window.ShowPage(UiPage.Library);
            window.DeckList.SelectedItem = window.DeckList.Items.Cast<DeckListItem>().First(i => i.Variations?.Length > 1);
            var selected = (DeckListItem)window.DeckList.SelectedItem;
            var deck = selected.Deck!;
            var rows = OpponentDeckProjector.Reference(deck).Select(window.Strip).ToArray();
            window.OpponentDeckCards.Rows = rows;
            window.UserReferenceCards.Rows = rows;
            window._lastProjection = new OpponentDeckProjector().Build(window._cachedDecks, [], deck.Faction, catalog: window._candidateCatalog);
            window.RenderCandidateTray();
            Check(window.CandidateCardTray.Rows!.Cast<object>().Count() > 40, "Expanded candidates still have the former 40-card limit.");
            window.UserReferenceSummary.Text = "DEMONSTRATION · " + deck.Name + " · " + deck.Leader;
            window.OpponentDeckSummary.Text = "DEMONSTRATION · hypothesized slots from sample observations";
            window.OpponentFactionText.Text = deck.Faction;
            window.GameStatusText.Text = "DEMONSTRATION · sample data · no game capture";
            window.DeckDataStatusText.Text = "Read-only sample library · " + window._cachedDecks.Length + " public decks";
            window.HideLoadingShell(); window._expandedWorkspace = true;
            void Render(string name, int width, int height, UiPage page)
            {
                window.ShowPage(page); window.UpdateWorkspaceLayout(width);
                DeckBuilderSmoke.Render(window, Path.Combine(folder, name + ".png"), width, height);
            }
            Render("analysis-1920", 1920, 1040, UiPage.Deck);
            Render("live-analysis", 1920, 1040, UiPage.Deck);
            Check(window.LibraryNavigation.Visibility == Visibility.Visible && window.GameplayNavigation.Visibility == Visibility.Visible &&
                Grid.GetRow(window.LibraryNavigation) < Grid.GetRow(window.GameplayNavigation) &&
                window.GameplayNavigation.Children.OfType<RadioButton>().Select(b => b.Content.ToString()).SequenceEqual(new[] { "Analysis", "Reference" }), "Navigation labels/order or out-of-game separation changed.");
            Check(window.DeckPage.Visibility == Visibility.Visible && window.CandidatesPage.Visibility == Visibility.Visible && window.PinnedPage.Visibility == Visibility.Visible && window.PlaysPage.Visibility == Visibility.Collapsed, "Analysis columns must be opponent, candidates, then snapshots without Overview.");
            Check(window.DeckPage.ActualWidth > 350 && window.PinnedPage.ActualWidth > 350 && window.CandidateCardTray.ActualHeight > 200, "Analysis panes were squeezed out.");
            Check(Grid.GetColumn(window.DeckPage) == 0 && Grid.GetColumn(window.CandidatesPage) == 1 && Grid.GetColumn(window.PinnedPage) == 2,
                "Expanded analysis order must be opponent deck, candidate cards, then snapshots.");
            window.EnableExperimentalAnalysisChoice.IsChecked = true;
            Render("analysis-experimental-1920", 1920, 1040, UiPage.Plays);
            Render("experimental-overview", 1920, 1040, UiPage.Plays);
            Check(window.PlaysPage.Visibility == Visibility.Visible && window.DeckPage.Visibility == Visibility.Visible && window.CandidatesPage.Visibility == Visibility.Visible && window.PinnedPage.Visibility == Visibility.Collapsed,
                "Opt-in Overview layout must restore Overview, opponent deck and candidates without snapshots.");
            Check(Grid.GetColumn(window.PlaysPage) == 0 && Grid.GetColumn(window.DeckPage) == 1 && Grid.GetColumn(window.CandidatesPage) == 2,
                "Opt-in Overview layout lane order changed.");
            window.EnableExperimentalAnalysisChoice.IsChecked = false;
            Render("analysis-standard-after-optin-1920", 1920, 1040, UiPage.Deck);
            Check(window.WorkspaceToggle.Content is System.Windows.Shapes.Path && window.LiveModeBar.Visibility == Visibility.Collapsed, "Wide navigation/icon did not simplify.");
            var synergyTest = new WrapPanel();
            window.AddHistoryMeter(synergyTest, "Skellige", PlayerSide.User);
            Check(synergyTest.Children.Count == 0, "History synergies leaked onto an unrelated faction.");
            window.AddHistoryMeter(synergyTest, "Nilfgaard", PlayerSide.User);
            window.AddHistoryMeter(synergyTest, "Syndicate", PlayerSide.Opponent);
            ((Button)synergyTest.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.SpyingSideChoice.SelectedIndex == 0 && window.SpyingMemoryPanel.Visibility == Visibility.Visible, "NG Spying did not open the player's memory.");
            ((Button)synergyTest.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.BountySideChoice.SelectedIndex == 1 && window.BountyMemoryPanel.Visibility == Visibility.Visible && window.SynergyHistory.Parent == window.SynergyHistoryHost, "SY Bounty did not open the opponent's synergy history.");
            window.BountyMemoryPanel.Visibility = window.SpyingMemoryPanel.Visibility = Visibility.Collapsed;
            window._playsSections = new PlaysSections([], [], []);
            window._selectedUserDeck = window._cachedDecks.First(d => d.Faction == "Nilfgaard");
            window._confirmedOpponentDeck = window._cachedDecks.First(d => d.Faction == "Syndicate");
            window.RenderSynergies();
            Check(window.UserSynergyButtons.Children.OfType<Button>().Any(b => b.Content is string s && s.StartsWith("Spying ")) &&
                window.SynergyButtons.Children.OfType<Button>().Any(b => b.Content is string s && s.StartsWith("Bounty ")),
                "Faction routing omitted Spying or Bounty from the actual synergy panels.");
            window._selectedUserDeck = window._confirmedOpponentDeck = null; window.RenderSynergies(); window._playsSections = null;
            Render("analysis-1280", 1280, 720, UiPage.Deck);
            Render("library-1440", 1440, 900, UiPage.Library);
            Render("deck-library", 1440, 900, UiPage.Library);
            Check(Grid.GetColumn(window.LibraryDeckCards) == 2 && window.LibraryDeckCards.ActualWidth > 400, "Wide library preview was not laid out beside its list.");
            Check(window.FindName("WorkspaceHeading") is null && !window.LibraryDeckMetadata.IsExpanded &&
                window.LibraryDeckMetadataText.Text.Contains("Stratagem:"), "Streamlined workspace retained its duplicate heading or lost deck details.");
            window.LibraryDeckMetadata.IsExpanded = true;
            Render("library-details", 1440, 900, UiPage.Library);
            window.LibraryDeckMetadata.IsExpanded = false;
            window.CycleLibraryVariation(1);
            Check(((DeckListItem)window.DeckList.SelectedItem).Deck!.Id != deck.Id, "Workspace arrow did not switch the exact variation.");
            Render("library-variation", 1440, 900, UiPage.Library);
            window.LibraryManagement.IsExpanded = true;
            Render("library-manage", 1440, 900, UiPage.Library); window.LibraryManagement.IsExpanded = false;
            window.LibrarySearch.ExcludedCardsBox.Text = deck.Cards[0].Card.Name;
            Check(window.DeckList.Items.Cast<DeckListItem>().All(i => (i.Variations ?? [i]).All(v => v.Deck is { } d && DeckSearchCatalog.ExcludesCards(d, window.LibrarySearch.ExcludedCards))), "Library exclusion left a disallowed variation visible.");
            window.LibrarySearch.SortBox.SelectedIndex = 1;
            var sortedHeadings = window.DeckList.Items.Cast<DeckListItem>().Select(i => i.Name).ToArray();
            Check(sortedHeadings.SequenceEqual(sortedHeadings.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "A–Z ordering did not sort the visible family headings.");
            var filters = (Border)window.LibrarySearch.FilterPopup.Child;
            RenderStandalone(filters, Path.Combine(folder, "library-filters.png"), 282);
            ((ScrollViewer)filters.Child).ScrollToEnd();
            RenderStandalone(filters, Path.Combine(folder, "library-filter-cards.png"), 282);
            window.LibrarySearch.ClearAll();
            window.DeckList.SelectedItem = window.DeckList.Items.Cast<DeckListItem>().First(i => i.Variations?.Length > 1);
            window._confirmedOpponentDeck = deck;
            window.UpdateReferenceCandidates();
            Render("reference-1440", 1440, 900, UiPage.Reference);
            Render("opponent-reference", 1440, 900, UiPage.Reference);
            Check(window.ReferenceModeBar.Visibility == Visibility.Collapsed && window.ReferencePage.Visibility == Visibility.Visible &&
                window.MyDeckPage.Visibility == Visibility.Visible && window.PinnedPage.Visibility == Visibility.Collapsed &&
                window.PinnedOpponentCards.ActualHeight > 200 && window.PinnedOpponentSummary.Text.Contains(deck.Name), "Reference did not expose its two deck-reference panes.");
            window.ReferencePicker.IsExpanded = true;
            Render("reference-picker", 1440, 900, UiPage.Reference);
            Check(window.CandidateDeckCards.ActualHeight >= 50 && window.OpponentCandidateList.ActualHeight >= 50, "Reference chooser hid its deck list or preview.");
            window.ReferencePicker.IsExpanded = false;
            Render("tools-1440", 1440, 900, UiPage.Advanced);
            Render("settings", 900, 820, UiPage.Settings);
            Check(Contrast(window.StartingSizeInput.Foreground, window.StartingSizeInput.Background) >= 4.5, "Advanced textbox contrast regressed.");
            window._workspaceZoom = 1.3; Render("analysis-large-text", 1920, 1040, UiPage.Deck);
            Render("narrow-fallback", 1000, 800, UiPage.Deck);
            Check(!window._wideWorkspace && window.CandidatesPage.Visibility == Visibility.Collapsed && window.PinnedPage.Visibility == Visibility.Collapsed, "Narrow/high-DPI fallback overlapped pages.");
            Check(window.LiveModeBar.Visibility == Visibility.Visible, "Compact analysis lost navigation to its other panes.");
            Render("compact-candidates", 410, 760, UiPage.Candidates);
            Check(window.CandidatesPage.Visibility == Visibility.Visible && window.DeckPage.Visibility == Visibility.Collapsed && window.PinnedPage.Visibility == Visibility.Collapsed, "Compact candidate navigation overlapped another analysis page.");
            Render("compact-snapshots", 410, 760, UiPage.Pinned);
            Check(window.PinnedPage.Visibility == Visibility.Visible && window.DeckPage.Visibility == Visibility.Collapsed && window.CandidatesPage.Visibility == Visibility.Collapsed, "Compact snapshot navigation overlapped another analysis page.");
            var demonstrationSnapshot = Path.Combine(folder, "live-analysis.png");
            var pinnedItem = new PinnedCaptureItem("demonstration-snapshot", demonstrationSnapshot);
            window.PinnedCaptureList.ItemsSource = new[] { pinnedItem };
            window.PinnedCaptureList.SelectedItem = pinnedItem;
            window.PinnedCountText.Text = "1 pinned view · Remove deletes the PNG; Undo keeps only the last removal in memory.";
            window.RemovePinnedButton.IsEnabled = false;
            window.PinnedButton.ToolTip = "Pinned views (1)";
            window.FooterStatusText.Text = "Snapshot saved · demonstration capture selected for review";
            DeckBuilderSmoke.Render(window, Path.Combine(folder, "snapshot-review.png"), 410, 760);
            window._expandedWorkspace = true;
            Render("live-analysis-with-snapshot", 1920, 1040, UiPage.Deck);
            window._expandedWorkspace = false;
            window.ReferencePicker.IsExpanded = true;
            Render("reference-compact", 410, 620, UiPage.Reference);
            Check(window.ReferenceModeBar.Visibility == Visibility.Visible && window.ReferencePickerScroll.ActualHeight <= window.ReferencePage.ActualHeight - 80,
                "Compact reference chooser overflowed its available space.");
            window.ReferencePicker.IsExpanded = false;
            Render("compact-return", 410, 760, UiPage.Library);
            Check(Grid.GetColumn(window.LibraryDeckCards) == 0 && window.WorkspaceDisplayButton.Visibility == Visibility.Collapsed, "Compact arrangement did not restore.");
            Check(ReferenceEquals(window.UserReferenceCards.Rows, rows) && ReferenceEquals(window.OpponentDeckCards.Rows, rows), "Layout changes replaced live data instances.");
            window.CheckCompactTooltips(folder);
            foreach (var faction in new[] { "Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate", "Neutral" })
            {
                var palette = FactionPalette.For(faction);
                Check(Contrast(FactionPalette.Brush(palette.Accent), FactionPalette.Brush(palette.Surface)) >= 4.5 &&
                    Contrast(FactionPalette.Brush("#F1F3F5"), FactionPalette.Brush(palette.Surface)) >= 4.5, "Faction typography contrast failed: " + faction);
            }

            // Render real popup templates without opening native popups over the user's desktop.
            var combo = new ComboBox { Width = 290, ItemsSource = new[] { "All factions", "Skellige", "Nilfgaard" }, SelectedIndex = 1, IsEditable = true };
            var contrastWindow = new Window { Content = new StackPanel { Margin = new Thickness(15) }, Width = 420, Height = 600 };
            var stack = (StackPanel)contrastWindow.Content;
            stack.Children.Add(new TextBlock { Text = "POP-UP CONTRAST · real control templates", Margin = new Thickness(0,0,0,12) }); stack.Children.Add(combo);
            combo.ApplyTemplate();
            var editable = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
            Check(Contrast(editable.Foreground, editable.Background) >= 4.5, "Editable combo has insufficient text contrast.");
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var popupSurface = (Border)popup.Child;
            Check(Contrast(combo.Foreground, popupSurface.Background) >= 4.5, "Combo dropdown has insufficient contrast.");
            // Closed dropdowns do not generate containers until displayed. Exercise the shared item template directly.
            var choices = new ListBox { Background = popupSurface.Background, BorderBrush = popupSurface.BorderBrush, Margin = new Thickness(0,8,0,12) };
            foreach (var label in new[] { "Skellige · selected", "Nilfgaard", "Northern Realms" })
                choices.Items.Add(new ComboBoxItem { Content = label, IsSelected = label.StartsWith("Skellige") });
            stack.Children.Add(choices);
            stack.Children.Add(new TextBox { Text = "Optional export notes remain readable.", Height = 65, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) });
            var menu = window.CreateWorkspaceDisplayMenu(); menu.ApplyTemplate();
            DeckBuilderSmoke.Render(contrastWindow, Path.Combine(folder, "popup-contrast.png"), 520, 660);
            menu.Measure(new Size(500, 700)); menu.Arrange(new Rect(0, 0, 500, menu.DesiredSize.Height)); menu.UpdateLayout();
            var menuImage = new RenderTargetBitmap(500, Math.Max(1, (int)Math.Ceiling(menu.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            menuImage.Render(menu);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(menuImage));
            using (var file = File.Create(Path.Combine(folder, "display-menu.png"))) encoder.Save(file);
            Check(Contrast(menu.Foreground, menu.Background) >= 4.5, "Display menu contrast failed.");
            foreach (var item in choices.Items.Cast<ComboBoxItem>())
            {
                Check(Contrast(item.Foreground, item.Background) >= 4.5, "Dropdown row contrast failed.");
                var surface = (Border)item.Template.FindName("ChoiceSurface", item);
                foreach (var text in VisualDescendants<TextBlock>(item))
                    Check(Contrast(text.Foreground, surface.Background) >= 4.5, "Rendered dropdown text inherited unreadable colors.");
            }
            contrastWindow.Close(); window.Close();
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(LibraryPath))), "Smoke modified the user's library.");
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: hidden full-screen/compact transitions; monitor geometry; opponent/candidate/snapshot analysis lanes; two-pane Reference; compact subviews and scrollable chooser; NG/SY side-specific synergy histories; variation cycling; shared data retained; popup and inspector contrast >=4.5:1; live library unchanged. No visible app, capture or browser started. Physical mixed-DPI dual-display interaction still requires a user check.");
            return 0;
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(folder, "result.txt"), "FAIL: " + error); return 1; }
    }
    private static double Contrast(Brush foreground, Brush background)
    {
        static double L(Brush brush)
        {
            var color = ((SolidColorBrush)brush).Color;
            static double C(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        var f = L(foreground); var b = L(background); return (Math.Max(f,b) + .05) / (Math.Min(f,b) + .05);
    }
    private static void RenderStandalone(FrameworkElement element, string path, int width)
    {
        element.Measure(new Size(width, 1000)); element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }
}
