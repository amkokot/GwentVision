using System.IO;
using System.Xml.Linq;
using GwentCompanion.Core.Data;

internal static class UiShellTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string ProjectRoot(string root) => File.Exists(Path.Combine(root, "GwentCompanion.sln"))
        ? root
        : Path.Combine(root, "GwentCompanion");

    public static void Navigation(string root)
    {
        var document = XDocument.Load(Path.Combine(ProjectRoot(root), "src", "GwentCompanion.App", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var elements = document.Descendants().ToArray();
        XElement Named(string name) => elements.Single(item => (string?)item.Attribute(x + "Name") == name);
        foreach (var name in new[] { "LibraryNavigation", "GameplayNavigation", "AnalysisNavigation", "ReferenceNavigation", "LiveModeBar", "ReferenceModeBar" })
            Check(Named(name).AncestorsAndSelf().All(item => item.Attribute("ToolTip") is null), "Redundant navigation hover returned: " + name);
        var tabs = Named("GameplayNavigation").Elements().Select(item => (string?)item.Attribute("Tag")).ToArray();
        Check(tabs.SequenceEqual(new[] { "Deck", "Reference" }), "Analysis and Reference are the two main gameplay entries.");
        Check(!elements.Any(item => item.Name.LocalName is "TabControl" or "TabItem"), "No hidden/nested legacy tab bars.");
        Check(int.Parse(Named("LibraryNavigation").Attribute("Grid.Row")!.Value) < int.Parse(Named("GameplayNavigation").Attribute("Grid.Row")!.Value), "Library is separate and above gameplay navigation.");
        Check(!elements.Any(item => (string?)item.Attribute(x + "Name") == "WorkspaceNavigation"), "Compact and expanded layouts reuse the same navigation, rather than divergent tab labels.");
        Check(!elements.Any(item => item.Name.LocalName == "Expander" && ((string?)item.Attribute("Header"))?.StartsWith("Threats") == true) &&
            !Named("HoverThreatBanner").Ancestors().Contains(Named("Pages")), "Reach must have one top-level hover surface, not a duplicate page section.");
        foreach (var name in new[] { "OpponentDeckCards", "DeckConstraintBadges" })
            Check(Named(name).Ancestors().Contains(Named("DeckPage")), "Keep deck candidates and rule indicators: " + name);
        foreach (var name in new[] { "CandidateCardSearch", "CandidateCardTray" })
            Check(Named(name).Ancestors().Contains(Named("CandidatesPage")), "Candidates have a dedicated expandable workspace lane: " + name);
        Check(Named("LiveModeBar").Elements().Select(item => (string?)item.Attribute("Tag")).SequenceEqual(new[] { "Plays", "Deck", "Candidates", "Pinned" }), "Compact analysis must retain the opt-in Overview alongside the standard three panels.");
        Check(Named("LiveOverviewNavigation").Attribute("Visibility")?.Value == "Collapsed", "Overview must be hidden until explicitly enabled.");
        Check(Named("EnableExperimentalAnalysisChoice").Attribute("IsChecked")?.Value == "False", "Experimental Overview layout must require explicit opt in.");
        Check(Named("SnapshotButton").Attribute("Click")?.Value == "Camera_OnClick", "Camera captures and pins a fresh image.");
        Check(Named("ReferenceModeBar").Elements().Select(item => (string?)item.Attribute("Tag")).SequenceEqual(new[] { "Reference", "MyDeck" }), "Reference keeps only opponent and known-player deck tabs after snapshots move to Analysis.");
        Check(Named("ReferenceModeBar").Elements().All(item => item.Name.LocalName == "RadioButton"), "Reference views are immediately clickable, not hidden in a dropdown.");
        Check(Named("GameplayNavigation").Elements().Append(Named("LibraryNavigation")).All(item => item.Attribute("Checked")?.Value == "Navigate_OnClick"), "Keyboard and accessibility selection navigate as well as pointer clicks.");
        var names = elements.Select(item => (string?)item.Attribute(x + "Name")).OfType<string>().ToArray();
        Check(names.Length == names.Distinct().Count(), "Unique control identities.");
        Check(!Named("PinnedPage").Descendants().Any(item => item.Name.LocalName == "Style" &&
            item.Attribute("TargetType")?.Value == "Button" && item.Attribute("BasedOn")?.Value == "{StaticResource {x:Type Button}}"),
            "Pinned implicit Button style must not resolve itself recursively.");
    }
    public static void Pins(string root)
    {
        var folder = Path.Combine(root, "GwentCompanion", "diagnostics", "pin-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new PinnedViewStore(folder); var original = Path.Combine(folder, "example.png");
        File.WriteAllBytes(original, [1, 2, 3]);
        var removed = store.Remove(original);
        Check(!File.Exists(original) && !Directory.Exists(Path.Combine(folder, ".removed")) && store.List().Count == 0, "Removal deletes the PNG without a hidden disk copy.");
        Check(store.Restore(removed) == original && File.ReadAllBytes(original).SequenceEqual(new byte[] { 1, 2, 3 }), "Undo preserves exact bytes.");
        try { store.Remove(Path.Combine(folder, "..", "outside.png")); throw new InvalidOperationException("Accepted outside path"); }
        catch (ArgumentException) { }
        try { store.Remove(Path.Combine(folder, "not-an-image.json")); throw new InvalidOperationException("Accepted non-PNG path"); }
        catch (ArgumentException) { }
        removed = store.Remove(original); File.WriteAllBytes(original, [9]);
        try { store.Restore(removed); throw new InvalidOperationException("Undo overwrote a new capture"); }
        catch (IOException) { }
        Check(File.ReadAllBytes(original).SequenceEqual(new byte[] { 9 }) && removed.Contents!.SequenceEqual(new byte[] { 1, 2, 3 }), "Failed undo preserves new file and old in-memory bytes.");
        var first = store.Save(stream => stream.Write(new byte[] { 4, 5 }));
        var second = store.Save(stream => stream.Write(new byte[] { 6, 7 }));
        Check(Path.GetFileName(first) == "pinned_1.png" && Path.GetFileName(second) == "pinned_2.png", "Simple numbered capture names");
        store.Remove(first);
        var third = store.Save(stream => stream.WriteByte(8));
        Check(Path.GetFileName(third) == "pinned_3.png" && File.ReadAllBytes(second).SequenceEqual(new byte[] { 6, 7 }), "Deletion does not renumber or overwrite other pins");
        try { store.Save(_ => throw new InvalidOperationException("encoder failed")); }
        catch (InvalidOperationException error) when (error.Message == "encoder failed") { }
        Check(!File.Exists(Path.Combine(folder, "pinned_4.png")), "Failed encoding left a broken pin");
        var big = Path.Combine(folder, "big.png");
        using (var stream = File.Create(big)) stream.SetLength(PinnedViewStore.UndoByteLimit + 1L);
        Check(store.Remove(big).Contents is null && !File.Exists(big), "Undo memory cap or disk deletion failed");
        var concurrent = Enumerable.Range(0, 8).AsParallel().Select(_ => store.Save(stream => stream.WriteByte(42))).ToArray();
        Check(concurrent.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8, "Concurrent capture overwrote a pin");
    }
    public static void CompactLive(string root)
    {
        var document = XDocument.Load(Path.Combine(ProjectRoot(root), "src/GwentCompanion.App/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var elements = document.Descendants().ToArray();
        XElement Named(string name) => elements.Single(item => (string?)item.Attribute(x + "Name") == name);
        Check(Named("SummonButtons").Name.LocalName == "WrapPanel", "Summons must wrap into compact buttons.");
        Check(Named("SummonWatchList").Attribute("Height") is null, "Summons must not reserve a fixed list height.");
        foreach (var name in new[] { "SummonWatchList", "ThreatChoicePanel", "ThreatDetailPanel" })
            Check(Named(name).Attribute("Visibility")?.Value == "Collapsed", "Optional detail starts closed: " + name);
        Check(!elements.Any(item => item.Attribute(x + "Name")?.Value is "BonusesExpander" or "ConditionalPlayList" or "SummonsExpander" ||
            item.Attribute("Header")?.Value == "Details / choose a card"), "Legacy clutter remains in Live.");
        foreach (var name in new[] { "ThreatChooseButton", "FollowHoverButton", "HoverThreatWarning" })
            Check(Named(name).Ancestors().Contains(Named("HoverThreatBanner")), "Keep fallback, visible override and detailed uncertainty in top Reach: " + name);
        Check(Named("DetailedReachChoice").Attribute("Visibility")?.Value == "Collapsed" &&
            Named("HoverThreatViewport").Attribute("Visibility")?.Value == "Collapsed" &&
            Named("ShowHoverThreats").Attribute("Visibility")?.Value == "Collapsed",
            "Dormant Reach controls returned to the main app.");
        Check(int.Parse(Named("LoadingShell").Attribute("Grid.RowSpan")!.Value) == Named("WindowLayout").Elements().Single(e => e.Name.LocalName == "Grid.RowDefinitions").Elements().Count() && Named("LoadingProgress").Attribute("IsIndeterminate")?.Value == "True",
            "Startup must show a full themed loading shell rather than an unresponsive partial UI.");
        Check(Named("RecordTrainingChoice").Attribute("IsChecked")?.Value == "False", "Training recording must be an explicit lightweight-mode toggle.");
        Check(Named("UserSynergyButtons").Name.LocalName == "WrapPanel" && Named("SynergyButtons").Name.LocalName == "WrapPanel",
            "Player and opponent synergy meters must both remain glanceable in Live.");
        Check(!elements.Any(item => (string?)item.Attribute(x + "Name") == "SynergyDetailPanel"),
            "The obsolete click-to-expand synergy panel returned.");
        Check(!elements.Any(item => (string?)item.Attribute(x + "Name") == "ThreatsExpander"), "Redundant middle Reach surface returned.");
        Console.WriteLine("PASS compact Live structure: wrapping summons, dormant Reach hidden, no Bonuses panel.");
    }
    public static void Zoom()
    {
        var center = PinnedZoomLayout.At(400, 400, 1920, 1080, 200, 200)!;
        Check(Math.Abs(center.SourcePixels.X + center.SourcePixels.Width / 2 - 960) < .00001, "Zoom center X mapping");
        Check(Math.Abs(center.SourcePixels.Y + center.SourcePixels.Height / 2 - 540) < .00001, "Zoom center Y mapping");
        Check(Math.Abs(center.Panel.Width / center.Lens.Width - 3) < .00001, "Zoom ratio is not 3x");
        Check(PinnedZoomLayout.At(400, 400, 1920, 1080, 200, 10) is null, "Letterbox hover produced a magnifier");
        Check(PinnedZoomLayout.At(0, 400, 1920, 1080, 0, 200) is null && PinnedZoomLayout.At(400, 400, 1920, 1080, double.NaN, 200) is null, "Invalid coordinates accepted");
        foreach (var image in new[] { (1920d,1080d), (900d,1600d), (3840d,2160d), (1600d,100d) })
        foreach (var viewport in new[] { (300d, 200d), (400d,800d), (100d,100d) })
        {
            var fit = PinnedZoomLayout.Fit(viewport.Item1, viewport.Item2, image.Item1, image.Item2);
            foreach (var x in new[] { fit.X, fit.X + fit.Width / 2, fit.X + fit.Width })
            foreach (var y in new[] { fit.Y, fit.Y + fit.Height / 2, fit.Y + fit.Height })
            {
                var zoom = PinnedZoomLayout.At(viewport.Item1, viewport.Item2, image.Item1, image.Item2, x, y)!;
                Check(zoom.SourcePixels.X >= -1e-8 && zoom.SourcePixels.Y >= -1e-8 && zoom.SourcePixels.X + zoom.SourcePixels.Width <= image.Item1 + 1e-8 &&
                    zoom.SourcePixels.Y + zoom.SourcePixels.Height <= image.Item2 + 1e-8, "Edge crop escaped source image");
                Check(zoom.Panel.X >= 0 && zoom.Panel.Y >= 0 && zoom.Panel.X + zoom.Panel.Width <= viewport.Item1 && zoom.Panel.Y + zoom.Panel.Height <= viewport.Item2,
                    "Magnifier escaped companion preview");
            }
        }
    }
}
