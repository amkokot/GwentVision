using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--export-browser-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckExportWindow.RunExportBrowserSmokeAsync()); return;
        }
        if (e.Args.Contains("--automatic-export-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckExportWindow.RunAutomaticExportSmokeAsync()); return;
        }
        if (e.Args.Contains("--post-match-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunPostMatchSmokeAsync()); return;
        }
        if (e.Args.Contains("--season-tools-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckBuilderSmoke.RunSeasonToolsAsync()); return;
        }
        if (e.Args.Contains("--card-data-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunCardDataSmokeAsync()); return;
        }
        if (e.Args.Contains("--deck-editor-gestures-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckBuilderSmoke.RunGesturesAsync()); return;
        }
        if (e.Args.Contains("--observed-editor-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunObservedEditorSmokeAsync()); return;
        }
        if (e.Args.Contains("--workspace-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(GwentCompanion.App.MainWindow.RunWorkspaceSmoke()); return;
        }
        if (e.Args.Contains("--library-variation-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(GwentCompanion.App.MainWindow.RunVariationSmoke()); return;
        }
        if (e.Args.Contains("--deck-builder-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckBuilderSmoke.RunAsync()); return;
        }
        if (!e.Args.Contains("--library-sandbox"))
        {
            MainWindow = new MainWindow(); MainWindow.Show(); return;
        }
        // Explicit UI-test mode: never open the main live pipeline or write the user's library.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Test workspace not found.");
        var project = Path.Combine(root.FullName, "GwentCompanion");
        var sandbox = Path.Combine(project, "diagnostics", "library-ui-sandbox");
        Directory.CreateDirectory(sandbox);
        var libraryPath = Path.Combine(sandbox, "deck-library.json");
        var library = DeckLibrary.Load(libraryPath);
        if (library.Decks.Length == 0)
        {
            var payload = Directory.GetFiles(Path.Combine(project, "cache", "decks"), "*.json").First();
            library.Merge([new PlayGwentDeckPageParser().ParseStateJson(File.ReadAllText(payload), new Uri("https://www.playgwent.com/en/decks/" + Path.GetFileNameWithoutExtension(payload)))]);
        }
        var window = new DeckLibraryWindow(library, libraryPath, Path.Combine(sandbox, "payloads"),
            GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json")), library.Decks.First(), () => { }, Path.Combine(sandbox, "scans"));
        window.Title += " · LIBRARY TEST";
        window.ScanButton.IsEnabled = false;
        window.ScanStatus.Text = "Isolated UI test: game capture disabled; writes go only to diagnostics/library-ui-sandbox.";
        MainWindow = window; window.Show();
    }
}

