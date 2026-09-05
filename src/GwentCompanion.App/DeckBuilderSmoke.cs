using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

/// <summary>Explicit, offline UI smoke mode. Never starts capture, opens a website, or writes the user's cache.</summary>
internal static partial class DeckBuilderSmoke
{
    public static async Task<int> RunAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) return 1;
        var project = Path.Combine(root.FullName, "GwentCompanion");
        var output = Path.Combine(project, "diagnostics", "deck-builder-ui-smoke");
        Directory.CreateDirectory(output);
        DeckBuilderWindow? builder = null;
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(project, "cache/gwent-one-cards.json"));
            await CheckFillFallback(project, output, catalog);
            var library = DeckLibrary.Load(Path.Combine(project, "cache/deck-library.json"));
            var libraryPath = Path.Combine(output, "deck-library.json");
            ImageSource? Art(CardDefinition card)
            {
                var path = Path.Combine(project, "cache/portraits", card.Id + ".jpg");
                if (!File.Exists(path)) return null;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
            }
            var openTimer = System.Diagnostics.Stopwatch.StartNew();
            builder = new DeckBuilderWindow(library, libraryPath, catalog, null, () => { }, Art);
            await Settled(builder); openTimer.Stop();
            File.WriteAllText(Path.Combine(output, "main-builder-timing.txt"),
                $"Full 2,651-list main builder construction and first settled proposal: {openTimer.ElapsedMilliseconds}ms.");
            Check(openTimer.Elapsed < TimeSpan.FromSeconds(2), "Full-library main deck builder opened too slowly: " + openTimer.Elapsed);
            Check(builder.CardSortChoice.SelectedIndex == 0, "Card collection must default to provisions, not recommendation frequency.");
            void Add(string name)
            {
                builder.CardSearch.Text = name;
                var row = ((IEnumerable)builder.Collection.Rows!).Cast<object>()
                    .Single(r => ((CardDefinition)r.GetType().GetProperty("Card")!.GetValue(r)!).Name == name);
                Call(builder, "CollectionActivated", null, row);
            }
            Add("Knut the Callous"); Add("Golden Nekker"); await Settled(builder);
            var original = Result(builder);
            Check(original.Cards.Sum(c => c.Count) == 25 && original.Errors.Count == 0, builder.BuilderStatus.Text);
            int FixedCount() => ((System.Collections.IDictionary)typeof(DeckBuilderWindow).GetField("_fixed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(builder)!).Count;
            Call(builder, "UndoEditClicked", builder, new RoutedEventArgs()); await Settled(builder);
            Check(FixedCount() == 1, "Undo did not restore the fixed core.");
            Call(builder, "RedoEditClicked", builder, new RoutedEventArgs()); await Settled(builder);
            Check(FixedCount() == 2 && Result(builder).Cards.Sum(c => c.Count) == 25, "Redo did not re-complete the core.");
            Call(builder, "KeepAllClicked", builder, new RoutedEventArgs()); await Settled(builder);
            builder.AutoFill.IsChecked = false; await Settled(builder);
            Check(Result(builder).Cards.Sum(c => c.Count) == 25, "Fix all did not preserve suggestions in manual mode.");
            Call(builder, "UndoEditClicked", builder, new RoutedEventArgs()); await Settled(builder);
            Call(builder, "UndoEditClicked", builder, new RoutedEventArgs()); await Settled(builder);
            Check(FixedCount() == 2 && builder.AutoFill.IsChecked == true, "Undo fix-all did not restore suggestion boundaries.");
            builder.CardSearch.Text = "bloodthirst"; builder.MaximumProvision.Text = "9"; builder.CardKindFilter.SelectedIndex = 1;
            var filtered = ((IEnumerable)builder.Collection.Rows!).Cast<object>().Select(r => (CardDefinition)r.GetType().GetProperty("Card")!.GetValue(r)!).ToArray();
            Check(filtered.Length > 0 && filtered.All(c => c.Provision <= 9 && c.Kind == CardKind.Unit &&
                DeckSearchCatalog.Normalize(c.Name + " " + string.Join(' ', c.Categories) + " " + c.AbilityText).Contains("bloodthirst")), "Ability/type/provision intersection failed.");
            builder.MinimumProvision.Text = "bad";
            Check(!((IEnumerable)builder.Collection.Rows!).Cast<object>().Any() && builder.CollectionStatus.Text.Contains("nonnegative"), "Bad provision bounds were silently ignored.");
            Call(builder, "ResetCollectionClicked", builder, new RoutedEventArgs());
            builder.AvailableCopiesOnly.IsChecked = true;
            Check(!((IEnumerable)builder.Collection.Rows!).Cast<object>().Any(r => ((CardDefinition)r.GetType().GetProperty("Card")!.GetValue(r)!).Name == "Golden Nekker"), "Fixed gold copy was advertised as available.");
            builder.AvailableCopiesOnly.IsChecked = false; builder.CardSortChoice.SelectedIndex = 3;
            Check(builder.CollectionStatus.Text.Contains("related list") && builder.CollectionStatus.ToolTip?.ToString() == "Related library lists; not win-rate evidence." &&
                System.Windows.Automation.AutomationProperties.GetHelpText(builder.CollectionStatus).Contains("Not win-rate evidence"),
                "Recommendation ranking did not retain its sample count, concise hover and accessible explanation.");
            var recommended = ((IEnumerable)builder.Collection.Rows!).Cast<object>().Select(r => (CardDefinition)r.GetType().GetProperty("Card")!.GetValue(r)!).ToArray();
            var basis = library.Decks.Where(d => d.CardCount >= 25 && d.Faction == original.Leader!.Faction && d.Leader == original.Leader.Name &&
                (original.Stratagem is null || d.Stratagem?.Id == original.Stratagem.Id) &&
                d.Cards.Any(c => c.Card.Name == "Knut the Callous") && d.Cards.Any(c => c.Card.Name == "Golden Nekker")).DistinctBy(DeckLibrary.Fingerprint).ToArray();
            int Support(CardDefinition c) => c.Name is "Knut the Callous" or "Golden Nekker" ? 0 : basis.Count(d => d.CountOf(c.Id) > 0);
            Check(recommended.SequenceEqual(recommended.OrderByDescending(Support).ThenBy(c => c, DeckBuilderOrder.Comparer)), "Recommendations did not use matching-deck frequency with provision-order ties.");
            var excluded = (HashSet<DeckCopyKey>)typeof(DeckBuilderWindow).GetField("_excluded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(builder)!;
            var excludedId = recommended.First(c => Support(c) > 0).Id;
            excluded.Add(new DeckCopyKey(excludedId, 1)); Call(builder, "RefreshRelatedCards");
            var expectedBasis = basis.Count(d => d.CountOf(excludedId) == 0);
            var actualBasis = (int)typeof(DeckBuilderWindow).GetField("_relatedBasisCount", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(builder)!;
            var relatedScores = (Dictionary<string, double>)typeof(DeckBuilderWindow).GetField("_relatedCards", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(builder)!;
            Check((expectedBasis == 0 || expectedBasis == actualBasis) && relatedScores.GetValueOrDefault(excludedId) == 0,
                "Recommendation basis ignored excluded copies; fuzzy fallback is allowed when no exact core remains.");
            excluded.Clear(); Call(builder, "RefreshRelatedCards"); Call(builder, "RenderCollection");
            var inspected = ((IEnumerable)builder.Collection.Rows!).Cast<object>().First();
            Call(builder, "CardInspected", null, inspected);
            Check(builder.InspectedCardName.Text.Length > 0 && builder.InspectedCardAbility.Text.Length > 0 && FixedCount() == 2, "Inspection changed the deck or omitted card text.");
            builder.DeckName.Text = "Knut · Golden Nekker · UI test";
            builder.CardSearch.Text = "";
            builder.CardSortChoice.SelectedIndex = 0;
            Render(builder, Path.Combine(output, "builder.png"), 1008, 730);
            Render(builder, Path.Combine(output, "builder-compact.png"), 728, 530);
            Check(builder.Collection.ActualHeight >= 70 && builder.DraftCards.ActualHeight >= 70, "Short editor squeezed card lists out.");
            Check(!builder.EditorSetup.IsExpanded && !Find<CheckBox>(builder.EditorSetup).Contains(builder.AutoFill) && builder.CardDetailsButton.Visibility == Visibility.Visible,
                "Compact editor hid auto-fill or card details with setup.");
            Check(builder.EditorFactionName.Text == original.Leader!.Faction && builder.EditorLeaderName.Text == original.Leader.Name &&
                builder.EditorStratagemName.Text == original.Stratagem?.Name && !Find<System.Windows.Controls.Border>(builder.EditorSetup).Contains(builder.EditorDeckHeader),
                "Collapsed setup lost the visible faction, leader or stratagem.");
            Render(builder, Path.Combine(output, "builder-wide.png"), 1320, 900);
            Check(builder.EditorInspector.Visibility == Visibility.Visible, "Wide inspector was hidden.");
            Check(builder.EditorColumns.ActualHeight > 600 && builder.Collection.ActualHeight > 440 && builder.DraftCards.ActualHeight > 500 && builder.EditorInspector.ActualHeight > 600,
                "Collapsed setup/footer did not return substantial height to all three editor columns.");
            var deckTop = builder.DraftCards.TranslatePoint(new Point(), builder.EditorColumns).Y;
            var deckBottom = builder.DraftCards.TranslatePoint(new Point(0, builder.DraftCards.ActualHeight), builder.EditorColumns).Y;
            var selectedActionTop = builder.KeepButton.TranslatePoint(new Point(), builder.EditorColumns).Y;
            var editorRoot = (FrameworkElement)builder.Content;
            var columnsBottom = builder.EditorColumns.TranslatePoint(new Point(0, builder.EditorColumns.ActualHeight), editorRoot).Y;
            Check(selectedActionTop < deckTop && builder.EditorColumns.ActualHeight - deckBottom < 2 && editorRoot.ActualHeight - columnsBottom < 2,
                "Selected-card actions or footer recreated dead space beneath the deck display.");
            Check(((SolidColorBrush)builder.EditorDeckHeader.Background).Color == (Color)ColorConverter.ConvertFromString("#101419") &&
                ((SolidColorBrush)builder.EditorInspector.Background).Color != ((SolidColorBrush)builder.EditorDeckHeader.Background).Color,
                "Faction color must be limited to the artwork inspector, not the header or card lists.");
            var undoPosition = builder.UndoEditButton.TranslatePoint(new Point(), (UIElement)builder.Content);
            Check(undoPosition.X >= 0 && undoPosition.Y >= 0 && undoPosition.X + builder.UndoEditButton.ActualWidth < 1320,
                "Editor history actions moved outside the viewport after resizing.");
            Check(builder.CardDetailsButton.Visibility == Visibility.Collapsed && builder.BuilderExplanation.Visibility == Visibility.Collapsed &&
                builder.BuilderReason.Text.Length > 0 && System.Windows.Automation.AutomationProperties.GetHelpText(builder.BuilderStatus).Length > 0,
                "Wide editor duplicated the inspector action or lost its accessible completion explanation.");
            builder.EditorSetup.IsExpanded = true; Render(builder, Path.Combine(output, "builder-template-search.png"), 1008, 730);
            builder.TemplateSearchBox.Clear(); Call(builder, "OpenTemplatePopup");
            Check(builder.TemplateChoice.Items.Count > 20 && builder.TemplateChoice.MaxHeight >= 250 && builder.TemplatePopup.Child is Border,
                "Template search did not create a useful scrollable result overlay.");
            var popularFaction = library.Decks.GroupBy(d => d.Faction).OrderByDescending(g => g.Count()).First();
            builder.TemplateSearchBox.Text = popularFaction.Key; Call(builder, "OpenTemplatePopup");
            Check(builder.TemplateChoice.Items.Count > 1, "Template search did not filter the result overlay.");
            Check(builder.TemplateResultsText.Text.Contains("matching template") && builder.TemplateSearchBox.Text == popularFaction.Key,
                "Template result count/search context is not visible together.");
            builder.TemplateChoice.SelectedIndex = Math.Min(8, builder.TemplateChoice.Items.Count - 1);
            builder.TemplateChoice.ScrollIntoView(builder.TemplateChoice.SelectedItem); builder.TemplateChoice.UpdateLayout();
            Check(builder.UseTemplateButton.IsEnabled && builder.TemplateSearchBox.Text == popularFaction.Key,
                "Scrolling/selecting a template lost the search query.");
            var templateSurface = (FrameworkElement)builder.TemplatePopup.Child; builder.TemplatePopup.IsOpen = false; builder.TemplatePopup.Child = null;
            var templateWindow = new Window { Content = templateSurface, Background = (Brush)new BrushConverter().ConvertFromString("#101419")! };
            Render(templateWindow, Path.Combine(output, "template-results-popup.png"), 760, 360);
            templateWindow.Content = null; templateWindow.Close(); builder.TemplatePopup.Child = templateSurface;
            builder.EditorSetup.IsExpanded = false;

            builder.AutoFill.IsChecked = false; await Settled(builder);
            Check(Result(builder).Cards.Sum(c => c.Count) == 2 && Result(builder).Leader?.Id == original.Leader?.Id,
                "Manual toggle lost its visible leader or retained suggestions.");
            builder.AutoFill.IsChecked = true; await Settled(builder);
            var saved = (DeckDefinition)Call(builder, "SaveLocal")!;
            Check(DeckLibrary.Load(libraryPath).Find(saved.Id) is not null, "UI save did not persist.");
            Call(builder, "LoadTemplate", saved); await Settled(builder);
            Check(Result(builder).Cards.Sum(c => c.Count) == 25 && builder.AutoFill.IsChecked == false,
                "Template did not load as fixed, editable cards.");
            Check(builder.SaveDeckButton.Content?.ToString() == "Save as version" && builder.OverwriteDeckButton.Visibility == Visibility.Visible && builder.OverwriteDeckButton.IsEnabled,
                "Editing a cached deck did not expose separate version and overwrite choices.");
            Render(builder, Path.Combine(output, "builder-save-modes.png"), 1008, 730);
            var shuffled = saved with { Id = "smoke-shuffled", Name = "Shuffled template", Cards = saved.Cards.Reverse().ToArray() };
            Check(builder.OpenDraft(shuffled), "Existing editor did not switch template."); await Settled(builder);
            var displayed = ((IEnumerable)builder.DraftCards.Rows!).Cast<object>().Select(r => (CardDefinition)r.GetType().GetProperty("Card")!.GetValue(r)!).ToArray();
            Check(displayed.SequenceEqual(displayed.OrderBy(c => c, DeckBuilderOrder.Comparer)), "Manual template rows retained input/insertion order.");
            builder.DeckName.Text = "Keep unsaved name";
            Check(!builder.OpenDraft(saved, () => false) && builder.DeckName.Text == "Keep unsaved name", "Declining a deck switch discarded the draft.");
            Check(builder.OpenDraft(saved, () => true), "Confirmed deck switch failed."); await Settled(builder);
            // Exercise selecting a physical copy, removing it, and restoring it from the collection.
            var firstRow = ((IEnumerable)builder.DraftCards.Rows!).Cast<object>().First();
            var firstCard = (CardDefinition)firstRow.GetType().GetProperty("Card")!.GetValue(firstRow)!;
            Call(builder, "DraftActivated", null, firstRow); Call(builder, "RemoveClicked", builder, new RoutedEventArgs());
            await Settled(builder); Check(Result(builder).Cards.Sum(c => c.Count) == 24, "Manual remove did not remove one copy.");
            Check(Result(builder).Errors.All(error => builder.BuilderStatus.Text.Contains(error)) && !builder.SaveDeckButton.IsEnabled,
                "Streamlined status hid a validation error or enabled an illegal save.");
            Check(builder.TemplateDifference.Text.Contains(firstCard.Name) && builder.TemplateDifference.Text.Contains("Removed:"), "Template comparison omitted the removed copy.");
            Render(builder, Path.Combine(output, "builder-template-edit.png"), 1320, 900);
            Add(firstCard.Name); await Settled(builder); saved = (DeckDefinition)Call(builder, "SaveLocal")!;
            var popup = new DeckExportWindow(library, libraryPath, saved, catalog, () => { }, initializeBrowser: false);
            Check(!popup.CreateButton.IsEnabled && !popup.ImportButton.IsEnabled, "Export actions enabled without official browser context.");
            Render(popup, Path.Combine(output, "export.png"), 1088, 730);
            popup.Overview.Text = "A locally saved deck built from fixed core cards.";
            popup.GamePlan.Text = "Develop engines, then use the available self-wound combinations.";
            popup.Mulligans.Text = "Review the suggested cards before exporting.";
            popup.Matchups.Text = "Optional matchup notes stay in this export pop-up.";
            Find<Expander>(popup).Single().IsExpanded = true;
            Call(popup, "SaveDetails");
            Render(popup, Path.Combine(output, "export-details.png"), 1088, 730);
            Render(popup, Path.Combine(output, "export-compact.png"), 758, 550);
            Check(DeckLibrary.Load(libraryPath).Find(saved.Id)?.Details?.Overview == popup.Overview.Text, "Popup details were not saved.");
            popup.Close(); builder.Close();
            File.WriteAllText(Path.Combine(output, "result.txt"),
                "PASS: wide/compact editor and export; provision-default collection and sorted manual templates; recommendation frequency, leader/stratagem and exclusions; visible deck header and inspector-only faction color; switching/cancelling dirty drafts; ability search, undo/redo, fix-all, validation, template differences, save/reload and optional notes. Browser disabled; no remote writes or authentication tested.");
            return 0;
        }
        catch (Exception error)
        { File.WriteAllText(Path.Combine(output, "result.txt"), "FAIL: " + error); return 1; }
        finally { if (builder is not null) { typeof(DeckBuilderWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(builder, false); builder.Close(); } }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType()
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static DeckAutoFillResult Result(DeckBuilderWindow window) => (DeckAutoFillResult)typeof(DeckBuilderWindow)
        .GetField("_result", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static async Task Settled(DeckBuilderWindow window)
    {
        var deadline = DateTimeOffset.Now.AddSeconds(20);
        while (window.BuilderStatus.Text.StartsWith("Updating", StringComparison.Ordinal))
        { if (DateTimeOffset.Now > deadline) throw new TimeoutException(window.BuilderStatus.Text); await Task.Delay(40); }
        Check(!window.BuilderStatus.Text.StartsWith("Could not", StringComparison.Ordinal), window.BuilderStatus.Text);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { if (child is T target) yield return target; foreach (var descendant in Find<T>(child)) yield return descendant; }
    }
    internal static void Render(Window window, string path, int width, int height)
    {
        var content = (FrameworkElement)window.Content;
        if (window is DeckBuilderWindow editor) editor.ApplyEditorLayout(width, height);
        content.Width = width - content.Margin.Left - content.Margin.Right;
        content.Height = height - content.Margin.Top - content.Margin.Bottom;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        { context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height)); }
        image.Render(drawing); image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
}
