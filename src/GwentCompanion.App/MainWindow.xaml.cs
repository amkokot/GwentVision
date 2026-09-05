using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Windows;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private readonly GwentWindowService _windowService = new();
    private readonly Win32FrameCapture _frameCapture = new();
    private readonly DispatcherTimer _discoveryTimer;
    private readonly PlayGwentDeckCacheService _deckCacheService = new();
    private readonly PlayGwentArtCacheService _artCacheService = new();
    private readonly ObservedDeckStore _observedDeckStore = new();
    private readonly PlayEventStore _playEventStore = new();
    private readonly LiveDeckTracker _userTracker = new(PlayerSide.User);
    private readonly LiveDeckTracker _opponentTracker = new(PlayerSide.Opponent);
    private DiagnosticCaptureSession? _diagnosticSession;
    private GwentWindowSnapshot? _gameWindow;
    private DeckIndexEntry[] _deckIndexEntries = Array.Empty<DeckIndexEntry>();
    private DeckDefinition[] _cachedDecks = Array.Empty<DeckDefinition>();
    private DeckDefinition? _confirmedOpponentDeck;
    private DeckDefinition? _selectedUserDeck;
    private BitmapSource? _lastPreview;
    private int _recordedPlayEvents;
    private CardPointEstimate[] _pointCatalog = Array.Empty<CardPointEstimate>();
    private DeckListItem[] _deckListItems = Array.Empty<DeckListItem>();
    private CardPointModelListItem[] _pointModelItems = Array.Empty<CardPointModelListItem>();
    private DateTimeOffset _lastDiscoveryReport = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        if (_analysisControlTest) Title += " · OFFLINE REVIEW · ANALYSIS CONTROL TEST";
        ConfigureDeckSearch();
        ShowPage(UiPage.Deck);
        Topmost = false;
        var workArea = SystemParameters.WorkArea;
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 48));
        Left = Math.Max(workArea.Left, workArea.Right - Width - 24);
        Top = workArea.Top + 24;
        _discoveryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnDiscoveryTick,
            Dispatcher);
        _discoveryTimer.Stop(); // The interval constructor starts it; offline review must not discover the game.
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshCardDataStatus();
        if (_reviewEvidencePath is not null && !_analysisControlTest)
        {
            try { await LoadDeckIndexesAsync(loadVision: false); LoadOfflineReview(); }
            finally { HideLoadingShell(); }
            return;
        }
        if (!_analysisControlTest) { RefreshGameWindow(); _discoveryTimer.Start(); }
        else { SnapshotButton.IsEnabled = false; GameStatusText.Text = "OFFLINE REVIEW · capture fixture is this window only"; }
        SyncDecksButton.IsEnabled = false;
        _startupTask = InitializeAnalysisAsync();
        try { await _startupTask; }
        finally { HideLoadingShell(); }
        if (_windowClosing) return;
        SyncDecksButton.IsEnabled = !_analysisControlTest && !_analysisTransition &&
            _libraryWindow is null && _builderWindow is null && _diagnosticSession?.IsRunning != true;
        RefreshPinnedCaptures();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _hoverExpiry?.Stop();
        _discoveryTimer.Stop();
        if (_diagnosticSession is not null)
        {
            await _diagnosticSession.DisposeAsync();
            await StopVisionAsync();
            PersistCurrentMatch();
        }
        _visionPipeline?.Dispose();
        if (_valueWriter is not null) { await _valueWriter.Idle; _valueWriter.Dispose(); }
        _frameCapture.Dispose();

        base.OnClosed(e);
    }

    private void OnDiscoveryTick(object? sender, EventArgs e) { RefreshGameWindow(); RenderTacticalWatch(); }

    private void RefreshGameWindow()
    {
        try
        {
            _gameWindow = _windowService.Find();
            if (_gameWindow is null)
            {
                WriteDiscoveryReportIfDue();
                ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(119, 128, 138));
                GameStatusText.Text = "GWENT is not running";
                WindowDetailText.Text = "Start GWENT at any time; discovery is automatic. A privacy-limited window diagnostic is being saved.";
                SnapshotButton.IsEnabled = false;
                return;
            }

            ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(98, 179, 138));
            GameStatusText.Text = $"{(_diagnosticSession?.IsRunning == true ? "Analyzing" : "Connected")} · GWENT · {_gameWindow.DisplayMode}";
            WindowDetailText.Text =
                $"PID {_gameWindow.ProcessId} · client {_gameWindow.ClientBounds.Width}×{_gameWindow.ClientBounds.Height} · " +
                $"({_gameWindow.ClientBounds.Left}, {_gameWindow.ClientBounds.Top})";
            SnapshotButton.IsEnabled = !_gameWindow.IsMinimized;
        }
        catch (Exception exception)
        {
            ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(194, 90, 75));
            GameStatusText.Text = "GWENT window inspection failed";
            WindowDetailText.Text = exception.Message;
        }
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => RefreshGameWindow();

    private void DockButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_expandedWorkspace) { FooterStatusText.Text = "Use Display to move the workspace, or return to Compact before docking beside GWENT."; return; }
        if (RequireGameWindow(out var window))
        {
            DockBeside(window);
        }
    }

    private void ArrangeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_expandedWorkspace) { FooterStatusText.Text = "Return to Compact before arranging GWENT for a sidebar. The full workspace does not resize the game."; return; }
        if (!RequireGameWindow(out var window))
        {
            return;
        }

        if (window.IsFullscreen)
        {
            MessageBox.Show(
                this,
                "GWENT is currently fullscreen. Change Display Mode to Windowed or Borderless Windowed in GWENT's settings, then click Arrange again. The companion will not send Alt+Enter or other input to the game.",
                "Windowed mode required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            "Resize and move the GWENT window so this sidebar fits beside it? This only changes the ordinary Windows window rectangle.",
            "Arrange GWENT",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var sidebarPixels = (int)Math.Round(Width * dpi.DpiScaleX);
            _windowService.ArrangeWindowForSidebar(window, sidebarPixels);
            RefreshGameWindow();
            if (_gameWindow is not null)
            {
                DockBeside(_gameWindow);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not arrange GWENT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SnapshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!RequireGameWindow(out var window))
        {
            return;
        }

        try
        {
            var source = _frameCapture.Capture(window);
            _lastPreview = source;
            PreviewImage.Source = source;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PinCurrentButton.IsEnabled = true;
            UpdateVisualObservation(new GwentVisualStateDetector().Analyze(BitmapFrameAdapter.ToPixelFrame(source)));
            var directory = ResolveManualCaptureDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"snapshot-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.png");
            SavePng(source, path);
            DiagnosticStatusText.Text = $"Saved {Path.GetFileName(path)}";
            FooterStatusText.Text = path;
        }
        catch (Exception exception)
        {
            DiagnosticStatusText.Text = exception.Message;
        }
    }

    private async void DiagnosticButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_windowClosing || _analysisTransition || _reviewEvidencePath is not null && !_analysisControlTest) return;
        _analysisTransition = true;
        RefreshAnalysisButton();
        try
        {
            if (_diagnosticSession?.IsRunning == true || _visionWorker is not null)
            {
                await StopAnalysisCoreAsync("Analysis stopped · evidence retained.");
                return;
            }
            ShowAnalysisStatus("Preparing analysis… This click will start recording when ready.");
            if (_startupTask is not null) await _startupTask;
            if (_windowClosing) return;
            await EnsureVisionReadyAsync();
            if (_windowClosing) return;
            if (!TryAnalysisWindow(out var window))
            {
                ShowAnalysisStatus("Start GWENT, then click Play again.");
                return;
            }
            _userTracker.Reset();
            _opponentTracker.Reset();
            ResetOpponentKnowledge();
            _opponentEdits.Clear();
            if (_selectedUserDeck is not null)
            {
                _userTracker.SetFactionPrior(_selectedUserDeck.Faction);
            }
            _confirmedOpponentDeck = null;
            _recordedPlayEvents = 0;
            DetectedPlayText.Text = "No recognized play-preview episodes yet. Board sightings are listed separately.";
            RenderLiveInference();
            _diagnosticSession ??= new DiagnosticCaptureSession(
                _frameCapture,
                _analysisControlTest ? Path.Combine(FindDataRoot(), "diagnostics", "analysis-control-tests", Environment.ProcessId.ToString()) :
                Path.Combine(FindDataRoot(), "sessions"));
            _diagnosticSession.RetainTrainingFrames = RecordTrainingChoice.IsChecked == true;
            _diagnosticSession.Progress -= DiagnosticSession_OnProgress;
            _diagnosticSession.Progress += DiagnosticSession_OnProgress;
            StartVision();
            _diagnosticSession.Start(window);
            SyncDecksButton.IsEnabled = false;
            SetAnalysisButtonState(true);
            OpenSessionButton.IsEnabled = false;
            ShowAnalysisStatus((_diagnosticSession.RetainTrainingFrames ? "Analyzing · training recording on." : "Analyzing · training recording off.") +
                " Recognition runs in the background.");
        }
        catch (Exception exception)
        {
            // Starting recognition before capture fails must not leave an orphan
            // worker; stopping failure must also leave a retryable button.
            try { if (_diagnosticSession is not null) await _diagnosticSession.StopAsync(); }
            catch (Exception cleanup) { ShowAnalysisFailure("Capture cleanup failed.", cleanup); }
            try { await StopVisionAsync(); }
            catch (Exception cleanup) { ShowAnalysisFailure("Recognition cleanup failed.", cleanup); }
            ShowAnalysisFailure("Analysis could not start/stop. Click Play to retry.", exception);
        }
        finally
        {
            _analysisTransition = false;
            if (!_windowClosing)
            {
                SyncDecksButton.IsEnabled = !_analysisControlTest && _diagnosticSession?.IsRunning != true && _builderWindow is null && _libraryWindow is null;
                RefreshAnalysisButton();
                TryOpenPendingEncounterReview();
            }
        }
    }

    private void RenderLiveInference()
    {
        RenderDeckProjection();
    }


    private LeaderAbilitySnapshot InferLeaderState(LiveDeckTracker tracker)
    {
        var starting = InferStartingLeader(tracker);
        var other = InferStartingLeader(tracker.Side == PlayerSide.User ? _opponentTracker : _userTracker);
        return LeaderStateInference.Evaluate(
            starting.Name,
            starting.Faction,
            starting.Confidence,
            tracker.Observations,
            other.Name,
            other.Faction);
    }

    private (string? Name, string? Faction, double Confidence) InferStartingLeader(LiveDeckTracker tracker)
    {
        if (tracker.Side == PlayerSide.Opponent && _opponentKnowledge.StartingLeader is { } original)
            return (original, tracker.Faction, 1);
        if (tracker.Side == PlayerSide.User && _selectedUserDeck is not null)
        {
            return (_selectedUserDeck.Leader, _selectedUserDeck.Faction, 1);
        }

        if (tracker.Side == PlayerSide.Opponent && _confirmedOpponentDeck is not null)
        {
            return (_confirmedOpponentDeck.Leader, _confirmedOpponentDeck.Faction, 1);
        }

        // A single compatible cached deck is not proof of the opponent's ability.
        return (null, tracker.Faction, 0);
    }


    private void OpponentCandidateList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (OpponentCandidateList.SelectedItem is not LiveDeckCandidateItem item)
        {
            OpponentCandidateDetailText.Text = "Select a candidate to inspect its full card list.";
            CandidateDeckCards.Rows = null;
            ConfirmOpponentDeckButton.IsEnabled = false;
            return;
        }

        ConfirmOpponentDeckButton.IsEnabled = true;
        OpponentCandidateDetailText.Text = $"{item.Deck.Name} · {item.Deck.CardCount} cards · {item.Deck.ProvisionTotal}p";
        CandidateDeckCards.Rows = OpponentDeckProjector.Reference(item.Deck).Select(Strip).ToArray();
    }

    private void ConfirmOpponentDeckButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OpponentCandidateList.SelectedItem is not LiveDeckCandidateItem item)
        {
            return;
        }

        _confirmedOpponentDeck = item.Deck;
        RenderLiveInference();
        PersistCurrentMatch();
        ReferencePicker.IsExpanded = false;
        ShowPage(UiPage.Reference);
    }

    private void ClearOpponentDeckButton_OnClick(object sender, RoutedEventArgs e)
    {
        _confirmedOpponentDeck = null;
        RenderLiveInference();
        PersistCurrentMatch();
    }

    private static string ShortState(ConstraintState state) => state switch
    {
        ConstraintState.RuledOut => "ruled out",
        ConstraintState.Confirmed => "confirmed",
        ConstraintState.Likely => "likely",
        ConstraintState.Possible => "possible",
        _ => "unknown",
    };


    private void PersistCurrentMatch()
    {
        if (_reviewEvidencePath is not null) return;
        var sessionPath = _diagnosticSession?.CurrentSessionDirectory;
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            return;
        }

        var sessionId = Path.GetFileName(sessionPath);
        foreach (var tracker in new[] { _userTracker, _opponentTracker })
        {
            var observations = tracker.Observations.OrderBy(item => item.Card.Name).ToArray();
            if (tracker.Side==PlayerSide.User && _selectedUserDeck is {} reference)
                observations=observations.Select(item=>StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) && reference.CountOf(item.Card.Id)>0
                    ? item with {ObservedCopies=Math.Min(item.ObservedCopies,reference.CountOf(item.Card.Id))} : item).ToArray();
            if (observations.Length == 0)
            {
                continue;
            }

            var inference = new DeckInferenceEngine();
            var deckObservations = observations.Where(item=>StartingDeckRules.CountsAgainstStartingDeck(item.Provenance)).ToArray();
            var inferenceFaction = tracker.HasStableFaction ? tracker.Faction : null;
            var assessment = tracker.Side == PlayerSide.Opponent ? _opponentKnowledge.Assess(deckObservations) : StartingDeckRules.EvaluateObservedDeck(deckObservations);
            var compatible = inference.CompatibleDecks(_cachedDecks, deckObservations, inferenceFaction, assessment);
            var best = tracker.Side == PlayerSide.User ? _selectedUserDeck?.Id
                : _confirmedOpponentDeck is not null ? _confirmedOpponentDeck.Id
                : compatible.Count == 0
                    ? null
                    : inference.Rank(compatible, deckObservations, inferenceFaction, null, 1)[0].Deck.Id;
            var provisionFloor = StartingDeckRules.ProbableProvisionLowerBound(deckObservations);
            _observedDeckStore.Save(
                ResolveObservedDeckDirectory(),
                new ObservedDeckRecord(
                    sessionId,
                    tracker.Side,
                    DateTimeOffset.Now,
                    tracker.Faction,
                    compatible.Count,
                    best,
                    observations.Select(item => new StoredObservedCard(
                        item.Card.Id,
                        item.Card.Name,
                        item.Card.Faction,
                        item.Card.Provision,
                        item.Confidence,
                        item.Provenance,
                        tracker.IsFactionException(item.Card),
                        item.ObservedCopies)).ToArray(),
                    tracker.FactionConfidence,
                    provisionFloor,
                    RecognitionVersion: 2,
                    Devotion: assessment.Devotion.State,
                    DevotionEvidence: assessment.Devotion.Reason,
                    PinnedDeckId: tracker.Side == PlayerSide.Opponent ? _confirmedOpponentDeck?.Id : _selectedUserDeck?.Id,
                    PinnedDevotionAssumption: tracker.Side == PlayerSide.Opponent ? _lastProjection?.DevotionAssumption : null,
                    TrainingOnly: tracker.Side == PlayerSide.User,
                    ManualPicks: tracker.Side == PlayerSide.Opponent ? _opponentEdits.Included.Keys.Select(key => new StoredDeckAssumption(key.CardId, key.Copy)).ToArray() : null,
                    DismissedSuggestions: tracker.Side == PlayerSide.Opponent ? _opponentEdits.Excluded.Select(key => new StoredDeckAssumption(key.CardId, key.Copy)).ToArray() : null,
                    LearnedEvidence: tracker.Side == PlayerSide.Opponent ? CurrentEncounter() : null));
        }
    }

    private void OpenSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var path = _diagnosticSession?.CurrentSessionDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void PinCurrentButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_reviewEvidencePath is not null || _lastPreview is null)
        {
            return;
        }

        try
        {
            var path = SavePinned(_lastPreview);
            RefreshPinnedCaptures(path);
            DiagnosticStatusText.Text = $"Pinned {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            DiagnosticStatusText.Text = $"Could not pin view: {exception.Message}";
        }
    }

    private void RefreshPinnedCaptures(string? selectPath = null)
    {
        selectPath ??= (PinnedCaptureList.SelectedItem as PinnedCaptureItem)?.Path;
        var directory = ResolvePinnedCaptureDirectory();
        var items = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(path => new PinnedCaptureItem(Path.GetFileNameWithoutExtension(path), path))
                .ToArray()
            : Array.Empty<PinnedCaptureItem>();
        PinnedCaptureList.ItemsSource = items;
        PinnedCaptureList.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Path, selectPath, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        PinnedCountText.Text = $"{items.Length} pinned view(s) · Remove deletes the PNG; Undo keeps only the last removal in memory.";
        PinnedButton.ToolTip = $"Pinned views ({items.Length})";
        RemovePinnedButton.IsEnabled = items.Length > 0 && (_reviewEvidencePath is null || HasIsolatedPinnedReview);
    }

    private void PinnedCaptureList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        PinnedZoomButton.IsEnabled = PinnedCaptureList.SelectedItem is PinnedCaptureItem;
        if (PinnedCaptureList.SelectedItem is not PinnedCaptureItem item || !File.Exists(item.Path))
        {
            PinnedCaptureImage.Source = null;
            PinnedCapturePlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
        using var stream = File.OpenRead(item.Path);
        var bitmap = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        PinnedCaptureImage.Source = bitmap;
        PinnedCapturePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        { PinnedCaptureImage.Source = null; PinnedCapturePlaceholder.Visibility = Visibility.Visible; FooterStatusText.Text = "Cannot open pinned image: " + exception.Message; }
    }

    private void OpenPinnedFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = ResolvePinnedCaptureDirectory();
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
    }

    private void TopmostCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheckBox.IsChecked == true;
    }

    private void DockBeside(GwentWindowSnapshot window)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPixels = (int)Math.Round(Width * dpi.DpiScaleX);
        var work = window.MonitorWorkArea;
        var preferredLeft = window.Bounds.Right + 8;
        var leftPixels = preferredLeft + widthPixels <= work.Right
            ? preferredLeft
            : Math.Max(work.Left, work.Right - widthPixels);

        Left = leftPixels / dpi.DpiScaleX;
        Top = work.Top / dpi.DpiScaleY;
        Height = Math.Max(MinHeight, work.Height / dpi.DpiScaleY);
    }

    private bool RequireGameWindow(out GwentWindowSnapshot window)
    {
        RefreshGameWindow();
        if (_gameWindow is null)
        {
            MessageBox.Show(this, "Start GWENT first, then try again.", "GWENT not found", MessageBoxButton.OK, MessageBoxImage.Information);
            window = null!;
            return false;
        }

        window = _gameWindow;
        return true;
    }

    private async Task LoadDeckIndexesAsync(bool loadVision = true)
    {
        try
        {
            var gameRoot = FindGameRoot();
            var workbooks = SuppliedDeckWorkbooks.Find(gameRoot);
            _libraryReady = false;
            var cacheDirectory = ResolveDeckCacheDirectory();
            var loaded = await Task.Run(() =>
            {
                var library = DeckLibrary.Load(LibraryPath);
                var sourceEntries = WorkbookIndexCache.LoadOrRead(gameRoot, Path.Combine(cacheDirectory, "workbook-index.json"));
                var index = sourceEntries.Concat(library.ImportedLinks).OrderByDescending(e => e.LastEdited)
                    .ThenBy(e => e.RecencyRank).ToArray();
                // The library is canonical after its first import. Reopening the app must
                // not deserialize thousands of payloads, merge them, then rewrite 55 MB.
                // Explicit Sync/import paths merge and persist new payloads immediately.
                var cachedDecks = library.Decks;
                if (cachedDecks.Length == 0)
                {
                    cachedDecks = _deckCacheService.LoadCached(index, cacheDirectory, int.MaxValue).ToArray();
                    library.Merge(cachedDecks);
                }
                if (library.VariationPolicy is null)
                {
                    library.EnsureVariationGroups(GwentOneCardCatalog.Load(Path.Combine(gameRoot, "GwentCompanion/cache/gwent-one-cards.json")));
                    if (_reviewEvidencePath is null) library.Save(LibraryPath); // One-time schema/group migration, with backup.
                }
                return (Library: library, Entries: sourceEntries, Index: index, Cached: cachedDecks);
            });
            if (_windowClosing) return;
            var entries = loaded.Entries;
            var cached = loaded.Cached;
            _library = loaded.Library;
            _deckIndexEntries = loaded.Index;
            _libraryReady = true;
            _cachedDecks = _library.Decks.Select(CurrentDeck).ToArray();
            RestoreSelectedUserDeck();
            RebuildPointCatalog();
            // Deck search must be available even while vision is loading or unavailable.
            RefreshDeckList();
            RenderLiveInference();
            var observedCount = CreateObservedDeckItems().Length;
            DeckDataStatusText.Text =
                $"{entries.Select(entry => entry.DeckUri).Distinct().Count():N0} distinct deck links across {workbooks.Length} workbooks. " +
                (cached.Length > 0
                    ? $"Loaded {_cachedDecks.Length:N0} unique complete decks; duplicate compositions merged."
                    : "Index links are searchable; complete deck payloads are not cached yet.") +
                (observedCount > 0 ? $" Added {observedCount:N0} unlisted/variant observed partial deck(s)." : string.Empty);
            var indexStatus = DeckDataStatusText.Text;
            if (!loadVision) return;
            try { await ReloadVisionAsync(); DeckDataStatusText.Text = indexStatus; }
            catch (Exception exception) { DeckDataStatusText.Text = indexStatus + $" Vision unavailable: {exception.Message}"; }
        }
        catch (Exception exception)
        {
            DeckDataStatusText.Text = $"Workbook import failed: {exception.Message}";
            ShowAnalysisFailure("Deck library could not load; recognition will still be attempted.", exception);
        }
    }

    private async void SyncDecksButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_deckIndexEntries.Length == 0)
        {
            DeckDataStatusText.Text = "The workbook indexes are not loaded yet.";
            return;
        }

        SyncDecksButton.IsEnabled = false;
        try
        {
            var progress = new Progress<DeckCacheProgress>(item =>
            {
                DeckDataStatusText.Text =
                    $"Syncing spreadsheet decks: {item.Completed}/{item.Requested} checked · " +
                    $"{item.Loaded} valid · {item.Expired} expired · {item.Failed} failed.";
            });
            var result = await _deckCacheService.SyncAsync(
                _deckIndexEntries,
                ResolveDeckCacheDirectory(),
                int.MaxValue,
                progress);
            MergeLibrary(result.Decks);
            RefreshDeckList();
            var artProgress = new Progress<ArtCacheProgress>(item =>
            {
                DeckDataStatusText.Text =
                    $"Caching card references: {item.Completed}/{item.Requested} checked · " +
                    $"{item.Downloaded} downloaded · {item.Cached} cached · {item.Failed} failed.";
            });
            var art = await _artCacheService.SyncAsync(
                result.Decks,
                ResolveArtCacheDirectory(),
                artProgress);
            RestoreSelectedUserDeck();
            RebuildPointCatalog();
            _deckArt.Clear();
            RenderLiveInference();
            await ReloadVisionAsync();
            DeckDataStatusText.Text =
                $"{result.Decks.Count:N0} complete decks ready · {result.Downloaded:N0} downloaded · " +
                $"{result.LoadedFromCache:N0} cached · {result.Expired:N0} expired · {result.Errors.Count:N0} failed. " +
                $"Card art: {art.Requested:N0} ready, {art.Errors.Count:N0} failed.";
        }
        catch (Exception exception)
        {
            DeckDataStatusText.Text = $"Deck sync failed: {exception.Message}";
        }
        finally
        {
            SyncDecksButton.IsEnabled = true;
        }
    }

    private DeckListItem[] CreateDeckItems(IEnumerable<DeckDefinition> decks) =>
        DeckSearchCatalog.Build(_deckIndexEntries, decks, _library)
            .Select(entry =>
            {
                if (entry.Deck is not { } deck)
                    return new DeckListItem(entry.Name, entry.Context, entry.SourceUri, null, entry.SearchText, entry.IndexEntry);
                deck = CurrentDeck(deck);
                var rules = StartingDeckRules.EvaluateExactDeck(deck);
                var eligible = new[]
                    {
                        rules.Shupe.State == ConstraintState.Confirmed ? "Singleton" : null,
                        rules.Renfri.State == ConstraintState.Confirmed ? "Renfri-ok" : null,
                        rules.GoldenNekker.State == ConstraintState.Confirmed ? "GN-ok" : null,
                        rules.Devotion.State == ConstraintState.Confirmed ? "Devotion-ok" : null,
                        rules.EnslaveValue is not null ? $"Enslave {rules.EnslaveValue}" : null,
                    }
                    .Where(value => value is not null);
                var tags = string.Join(" · ", eligible);
                return new DeckListItem(
                    entry.Name,
                    $"{deck.Faction} · {deck.Leader} · {deck.CardCount} cards · " +
                    $"{deck.ProvisionTotal}/{rules.ProvisionCapacity} provisions" +
                    $" · Stratagem: {deck.Stratagem?.Name ?? "unknown"}" +
                    $" · {DeckPatchMetadata.Describe(deck.Patches)}" +
                    (tags.Length > 0 ? $" · {tags}" : string.Empty) +
                    (_library.Find(deck.Id) is { } record && (record.Aliases.Length > 1 || record.Sources.Length > 1)
                        ? $" · merged {record.Aliases.Length} identities / {record.Sources.Length} sources" : ""),
                    deck.SourceUri,
                    deck,
                    entry.SearchText + " " + DeckSearchCatalog.Normalize(tags), entry.IndexEntry);
            })
            .ToArray();

    private DeckListItem[] CreateObservedDeckItems() =>
        _observedDeckStore.Load(ResolveObservedDeckDirectory())
            .Where(record => record.RecognitionVersion >= 2)
            .Where(record => record.Side == PlayerSide.Opponent && !record.TrainingOnly)
            .Where(record => record.IsUnlistedOrVariant)
            .Select(record => record with { Cards = ObservedDeckStore.StartingCards(record.Cards, _candidateCatalog ??=
                GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache/gwent-one-cards.json"))) })
            .Select(record => new DeckListItem(
                $"Observed {record.Side.ToString().ToLowerInvariant()} deck · {record.RecordedAt:yyyy-MM-dd HH:mm}",
                $"Unlisted/variant · partial · {record.Faction ?? "faction unknown"} · " +
                $"{record.Cards.Sum(card => card.Provision * card.ObservedCopies)}p starting-card floor · {record.Cards.Sum(card => card.ObservedCopies)} card(s): " +
                string.Join(", ", record.Cards.Select(card => card.Name + (card.FactionException ? " (?)" : string.Empty))),
                null,
                null, FilterFaction: record.Faction, FilterLeader: record.LearnedEvidence?.StartingLeader,
                RecordedAt: record.RecordedAt, ObservedNames: record.Cards.Select(card => card.Name).ToArray(), PartialSlots: PartialLibrarySlots(record.Cards),
                DraftSourceKey: "observed:" + record.Side + ":" + record.SessionId, FilterStratagemId: record.LearnedEvidence?.StratagemId))
            .ToArray();

    private void RefreshDeckList()
    {
        _candidateCatalog ??= GwentOneCardCatalog.Load(Path.Combine(FindDataRoot(), "cache/gwent-one-cards.json"));
        _library.EnsureVariationGroups(_candidateCatalog);
        SetDeckListItems(CreateDeckItems(_cachedDecks).Concat(CreateObservedDeckItems()).Concat(LearnedDeckItems()).Concat(SavedObservedDraftItems()));
    }

    private void SetDeckListItems(IEnumerable<DeckListItem> items)
    {
        _deckListItems = items.ToArray();
        ApplyDeckFilter();
    }

    private void ApplyDeckFilter()
    {
        if (DeckList is null || DeckSearchBox is null)
        {
            return;
        }

        RefreshDeckSearchOptions();
        var terms = DeckSearchCatalog.Terms(DeckSearchBox.Text);
        var selected = DeckList.SelectedItem as DeckListItem;
        var matches = _deckListItems.Where(item => ShowObservedDecksChoice.IsChecked != false || !item.IsAutomaticObservation)
            .Where(item => DeckSearchCatalog.Matches(
            item.SearchText ?? DeckSearchCatalog.Normalize(item.Name + " " + item.Context), terms) &&
            (item.Deck is not null || item.ObservedNames is null ? DeckSearchCatalog.ContainsCards(item.Deck, DeckCardsFilter?.Text) :
                (DeckCardsFilter?.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .All(term => item.ObservedNames.Any(name => DeckSearchCatalog.Normalize(name).Contains(DeckSearchCatalog.Normalize(term), StringComparison.Ordinal)))))
            .Where(item => LibrarySearch.Faction.SelectedIndex <= 0 || (item.Deck?.Faction ?? item.IndexEntry?.Faction ?? item.FilterFaction) == LibrarySearch.Faction.SelectedItem as string)
            .Where(item => LibrarySearch.Leader.SelectedIndex <= 0 || (item.Deck?.Leader ?? item.IndexEntry?.Leader ?? item.FilterLeader) == LibrarySearch.Leader.SelectedItem as string)
            .Where(item => LibrarySearch.Filters.Matches(item.Deck, item.IndexEntry, item.Deck is null && item.IndexEntry is null))
            .Where(item => DeckSearchCatalog.ExcludesCards(item.Deck, LibrarySearch.ExcludedCards))
            .OrderByDescending(item => terms.Length == 0 ? 0 : DeckSearchCatalog.Normalize(item.Name) == DeckSearchCatalog.Normalize(DeckSearchBox.Text) ? 2 :
                DeckSearchCatalog.Normalize(item.Name).StartsWith(DeckSearchCatalog.Normalize(DeckSearchBox.Text), StringComparison.Ordinal) ? 1 : 0)
            .ThenByDescending(item => item.Deck is { } deck ? ReferenceDeckSearch.Date(deck) : item.IndexEntry?.LastEdited ?? item.RecordedAt)
            .ThenBy(item => item.Deck?.RecencyRank ?? item.IndexEntry?.RecencyRank ?? int.MaxValue).ToArray();
        var groupedMatches = GroupLibraryItems(matches);
        groupedMatches = LibrarySearch.SortOrder switch
        {
            1 => groupedMatches.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            2 => groupedMatches.OrderByDescending(item => item.Deck?.ProvisionTotal ?? -1).ToArray(),
            3 => groupedMatches.OrderBy(item => item.Deck?.ProvisionTotal ?? int.MaxValue).ToArray(),
            _ => groupedMatches
        };
        _updatingLibraryItems = true;
        try
        {
            DeckList.ItemsSource = groupedMatches;
            DeckList.SelectedItem = selected is null ? null : groupedMatches.FirstOrDefault(item => SameLibraryItem(item, selected));
        }
        finally { _updatingLibraryItems = false; }
        if (DeckList.SelectedItem is not null) DeckList.ScrollIntoView(DeckList.SelectedItem);
        RefreshLibraryPreview();
        if (DeckSearchStatusText is not null)
        {
            DeckSearchStatusText.Text = matches.Length > 0
                ? $"{groupedMatches.Length:N0} groups · {matches.Length:N0} lists" + (matches.Length < _deckListItems.Length ? $" / {_deckListItems.Length:N0}" : "")
                : _deckListItems.Length == 0 ? "No decks loaded. Import decks to begin." : "No matches. Try fewer filters.";
            DeckSearchStatusText.ToolTip = $"{matches.Count(item => item.Deck is not null):N0} cached variants. Card-name search covers cached decks only.";
        }
    }

    private void DeckSearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyDeckFilter();

    private void ClearDeckSearch_OnClick(object sender, RoutedEventArgs e) { DeckSearchBox.Clear(); DeckCardsFilter.Clear(); }

    private void RebuildPointCatalog()
    {
        var cards = BuiltInCardCatalog.Merge(_cachedDecks
            .SelectMany(deck => deck.Cards)
            .Select(item => item.Card)).Select(CurrentCard).ToArray();
        _pointCatalog = new CardPointEstimator().BuildCatalog(cards).ToArray();
        var exact = _pointCatalog.Count(item => item.Status == PointEstimateStatus.ExactImmediate);
        var bounded = _pointCatalog.Count(item => item.Status == PointEstimateStatus.BoundedImmediate);
        var contextual = _pointCatalog.Count(item => item.Status == PointEstimateStatus.NeedsBoardContext);
        PointModelStatusText.Text =
            $"{_pointCatalog.Length:N0} cached cards classified · {exact:N0} exact immediate · " +
            $"{bounded:N0} bounded · {contextual:N0} awaiting board context. Unknown maxima are never fabricated.";
        _pointModelItems = _pointCatalog
            .OrderBy(item => item.Status)
            .ThenBy(item => item.CardName)
            .Select(item => new CardPointModelListItem(
                item.CardName,
                PointRangeText(item),
                item.Explanation))
            .ToArray();
        ApplyPointFilter();

        if (_reviewEvidencePath is not null) return;
        var path = ResolvePointCatalogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                _pointCatalog,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() },
                }));
    }

    private void ApplyPointFilter()
    {
        if (PointModelList is null || PointSearchBox is null)
        {
            return;
        }

        var query = PointSearchBox.Text.Trim();
        PointModelList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _pointModelItems
            : _pointModelItems
                .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               item.Range.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               item.Explanation.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    private void PointSearchBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyPointFilter();

    private static string PointRangeText(CardPointEstimate estimate)
    {
        if (estimate.MinimumImmediatePoints is not null && estimate.MaximumImmediatePoints is not null)
        {
            return estimate.MinimumImmediatePoints == estimate.MaximumImmediatePoints
                ? $"Intrinsic: {estimate.MaximumImmediatePoints} · excludes board triggers · {estimate.Status}"
                : $"Intrinsic: {estimate.MinimumImmediatePoints}–{estimate.MaximumImmediatePoints} · excludes board triggers · {estimate.Status}";
        }

        return $"Printed body: {estimate.PrintedPower} · point estimate unavailable · {estimate.Status}";
    }

    private static PixelFrame LoadPixelFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        return BitmapFrameAdapter.ToPixelFrame(bitmap);
    }

    private async void UseSelectedUserDeckButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DeckList.SelectedItem is not DeckListItem item)
        {
            UserDeckStatusText.Text = "Select a deck first.";
            return;
        }
        var deck = item.Deck;
        if (deck is null)
        {
            if (item.IndexEntry is null) { UserDeckStatusText.Text = "This is a partial observation, not a complete starting deck."; return; }
            UseUserDeckButton.IsEnabled = false;
            try
            {
                UserDeckStatusText.Text = "Loading the selected deck into the local cache…";
                var result = await _deckCacheService.SyncAsync([item.IndexEntry], ResolveDeckCacheDirectory(), 1);
                deck = result.Decks.FirstOrDefault();
                if (deck is null) { UserDeckStatusText.Text = "The deck link is expired or unavailable. No reference was changed."; return; }
                MergeLibrary([deck]);
                deck = CurrentDeck(_library.Find(deck.Id)?.Deck ?? deck);
                RefreshDeckList();
            }
            catch (Exception exception) { UserDeckStatusText.Text = $"Could not load the selected deck: {exception.Message}"; return; }
            finally { UseUserDeckButton.IsEnabled = true; }
        }
        _selectedUserDeck = deck;
        _userTracker.SetFactionPrior(deck.Faction);
        SaveUserSettings();
        UserDeckStatusText.Text = $"My deck: {deck.Name} · {deck.Faction} · {deck.Leader}";
        RenderLiveInference();
        LibraryManagement.IsExpanded = false;
        ShowPage(UiPage.MyDeck);
    }

    private void ClearSelectedUserDeckButton_OnClick(object sender, RoutedEventArgs e)
    {
        _selectedUserDeck = null;
        _userTracker.ClearFactionPrior();
        SaveUserSettings();
        UserDeckStatusText.Text = "No own-deck reference selected.";
        RenderLiveInference();
    }

    private void RestoreSelectedUserDeck()
    {
        try
        {
            var path = ResolveSettingsPath();
            if (!File.Exists(path))
            {
                UserDeckStatusText.Text = "No own-deck reference selected.";
                return;
            }

            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path));
            _restoringReviewPreference = true;
            try
            {
                ReviewNewDecksChoice.IsChecked = settings?.ReviewNewOpponentDecks ?? true;
                DetailedReachChoice.IsChecked = settings?.DetailedReach ?? false;
                RecordTrainingChoice.IsChecked = settings?.RecordTraining ?? false;
                ShowObservedDecksChoice.IsChecked = settings?.ShowObservedDecks ?? true;
                AutoStopOnMmrChoice.IsChecked = settings?.AutoStopOnMmr ?? false;
                EnableExperimentalAnalysisChoice.IsChecked = settings?.EnableExperimentalAnalysis ?? false;
            }
            finally { _restoringReviewPreference = false; }
            _selectedUserDeck = settings?.SelectedUserDeckId is null
                ? null
                : _library.Find(settings.SelectedUserDeckId)?.Deck;
            if (_selectedUserDeck is { } selected) _selectedUserDeck = CurrentDeck(selected);
            UserDeckStatusText.Text = _selectedUserDeck is null
                ? "Saved own-deck reference is not in the current cache."
                : $"My deck: {_selectedUserDeck.Name} · {_selectedUserDeck.Faction} · {_selectedUserDeck.Leader}";
            if (_selectedUserDeck is not null)
            {
                _userTracker.SetFactionPrior(_selectedUserDeck.Faction);
            }
        }
        catch (JsonException)
        {
            UserDeckStatusText.Text = "Own-deck settings could not be read; select the deck again.";
        }
    }

    private void SaveUserSettings()
    {
        if (_reviewEvidencePath is not null) return;
        var path = ResolveSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new UserSettings(_selectedUserDeck?.Id, ReviewNewDecksChoice.IsChecked == true, DetailedReachChoice.IsChecked == true,
                    RecordTrainingChoice.IsChecked == true, ShowObservedDecksChoice.IsChecked != false, AutoStopOnMmrChoice.IsChecked == true,
                    EnableExperimentalAnalysisChoice.IsChecked == true),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindGameRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Gwent.exe")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the GWENT installation. Extract GwentVision into the game's installation folder.");
    }

    private static string FindDataRoot()
    {
        var installed = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(installed, "cache", "gwent-one-cards.json"))) return installed;
        return Path.Combine(FindGameRoot(), "GwentCompanion");
    }

    private static string ResolveManualCaptureDirectory()
    {
        return Path.Combine(FindDataRoot(), "snapshots");
    }

    private static string ResolveDeckCacheDirectory() =>
        Path.Combine(FindDataRoot(), "cache", "decks");

    private static string ResolveArtCacheDirectory() =>
        Path.Combine(FindDataRoot(), "cache", "portraits");

    private static string ResolveObservedDeckDirectory() =>
        Path.Combine(FindDataRoot(), "cache", "observed-decks");

    private static string ResolvePinnedCaptureDirectory() =>
        IsolatedPinnedReviewDirectory() ?? Path.Combine(FindDataRoot(), "pinned-views");

    private static string ResolveSettingsPath() =>
        Path.Combine(FindDataRoot(), "cache", "settings.json");

    private static string ResolvePointCatalogPath() =>
        Path.Combine(FindDataRoot(), "cache", "point-model", "cards.json");

    private void WriteDiscoveryReportIfDue()
    {
        if (DateTimeOffset.Now - _lastDiscoveryReport < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastDiscoveryReport = DateTimeOffset.Now;
        try
        {
            var directory = Path.Combine(FindDataRoot(), "diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "window-discovery.txt"),
                _windowService.BuildDiscoveryReport());
        }
        catch
        {
            // Discovery must keep running even when diagnostics cannot be written.
        }
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed record DeckListItem(string Name, string Context, Uri? SourceUri, DeckDefinition? Deck,
        string? SearchText = null, DeckIndexEntry? IndexEntry = null, string? FilterFaction = null,
        string? FilterLeader = null, DateTimeOffset? RecordedAt = null, IReadOnlyList<string>? ObservedNames = null,
        IReadOnlyList<ProjectedDeckSlot>? PartialSlots = null, string? MemoryId = null,
        string? GroupId = null, DeckListItem[]? Variations = null, string? DraftSourceKey = null,
        string? FilterStratagemId = null, DeckEditorDraft? SavedDraft = null)
    {
        public string? HeadingFaction => Deck?.Faction ?? IndexEntry?.Faction ?? FilterFaction;
        public string? HeadingLeader => Deck?.Leader ?? IndexEntry?.Leader ?? FilterLeader;
        public bool IsAutomaticObservation => SavedDraft is null && Deck is null && IndexEntry is null &&
            (DraftSourceKey is not null || MemoryId is not null);
        public string Summary => Deck is { } deck
            ? $"{deck.Faction} · {deck.Leader} · {deck.CardCount} cards · {deck.ProvisionTotal}p"
            : Context;
        public string PreviewHint => Context + "\n" + (SourceUri is not null && DeckLinkFileReader.CanonicalUrl(SourceUri) is not null
            ? "Double-click to open PlayGWENT ↗" : "Local record · no web link");
    }
    private sealed record LiveDeckCandidateItem(string Name, string Context, DeckDefinition Deck);
    private sealed record PinnedCaptureItem(string Name, string Path);
    private sealed record CardPointModelListItem(string Name, string Range, string Explanation);
    private sealed record UserSettings(string? SelectedUserDeckId, bool ReviewNewOpponentDecks = true, bool DetailedReach = false,
        bool RecordTraining = false, bool ShowObservedDecks = true, bool AutoStopOnMmr = false, bool EnableExperimentalAnalysis = false);

}
