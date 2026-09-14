using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private CardVisionPipeline? _visionPipeline;
    private RollingVisionBuffer<DiagnosticProgress>? _visionQueue;
    private StreamingVisionProcessor<DiagnosticProgress>? _streamingVision;
    private readonly PlayProvenanceResolver _playOrigins = new();
    private readonly ThinningCopyTracker _thinningCopies = new();
    private readonly HandCommitTracker _handCommits = new();
    private DateTimeOffset _lastQueuedAt;
    private long _resultCaptureUntilTicks;
    private long _latestCapturedMatchHudTicks;
    private bool _captureMatchHudSeen;
    private bool? _lastCaptureMatchHud;
    private PreviewPriorityWindow _previewPriority = new();
    private PreviewPriorityWindow _opponentPreviewPriority = new();
    private readonly GameplayWorkPriority _gameplayPriority = new();
    private Task? _visionWorker;
    private Task<ScreenStateRecognizer>? _ocrWarmupTask;
    private string? _visionError;
    private int _analyzedFrames;
    private double _visionLagSeconds;
    private int _droppedAnalysisFrames;
    private bool _closingAfterFlush;
    private bool _flushInProgress;
    private static readonly JsonSerializerOptions VisionJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Task ReloadVisionAsync(bool announce = true)
    {
        if (_visionLoadTask is { IsCompleted: false })
        {
            if (announce) ShowAnalysisStatus("Preparing recognition references… Play will wait until ready.");
            return _visionLoadTask;
        }
        return _visionLoadTask = LoadVisionCoreAsync(announce);
    }

    private async Task LoadVisionCoreAsync(bool announce)
    {
        if (_visionWorker is not null) throw new InvalidOperationException("Stop diagnostics before rebuilding recognition references.");
        if (_projectionQueue is { } projections) await projections.Idle;
        if (announce) ShowAnalysisStatus("Preparing recognition references… Play will wait until ready.");
        var decks = _cachedDecks;
        var catalog = _candidateCatalog?.ToArray();
        var knownPlayerCards = _selectedUserDeck?.Cards.Select(card => card.Card.Id).ToArray() ?? [];
        ScreenStateRecognizer? warmedScreen = null;
        try
        {
            if (_ocrWarmupTask is { } warmup)
            {
                warmedScreen = await warmup;
                _ocrWarmupTask = null;
            }
            var pipeline = await Task.Run(() =>
            {
                if (_analysisControlTest && !_testFailureInjected && Environment.GetCommandLineArgs().Contains("--analysis-fail-once"))
                { _testFailureInjected = true; throw new InvalidDataException("Simulated first-load failure (test only)."); }
                var cache = Path.Combine(FindDataRoot(), "cache");
                var cards = BuiltInCardCatalog.Merge((catalog ?? GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json")))
                    .Concat(decks.SelectMany(deck => deck.Cards).Select(item => item.Card)));
                // Exact title OCR remains catalog-wide. Expensive SIFT/art references are
                // loaded only for the known player list and current opponent candidates.
                var result = new CardVisionPipeline(VisionReferenceLibrary.Load(cards, cache), cards,
                    Path.Combine(cache, "recognition-features"), VisionReferenceScope.CandidateDecks, warmedScreen);
                // This disk/native feature-cache work used to run synchronously in
                // StartVision, freezing the Play button for several seconds.
                result.SetKnownPlayerDeck(knownPlayerCards);
                return result;
            });
            warmedScreen = null; // Ownership transferred to the pipeline.
            if (_windowClosing) { pipeline.Dispose(); return; }
            _visionPipeline?.Dispose();
            _visionPipeline = pipeline;
        }
        finally { warmedScreen?.Dispose(); RefreshAnalysisButton(); }
    }

    private void BeginOcrWarmup()
    {
        _ocrWarmupTask ??= Task.Run(() => new ScreenStateRecognizer());
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_cardDataBusy) { e.Cancel = true; CardDataStatus.Text = "Wait for the card update to finish before closing."; return; }
        if (_libraryTransferBusy) { e.Cancel = true; DeckDataStatusText.Text = "Wait for the library transfer to finish before closing."; return; }
        _windowClosing = true;
        _threatCancellation?.Cancel();
        _projectionQueue?.Dispose();
        if (!_closingAfterFlush && (_visionWorker is not null || !_matchStorageWrite.IsCompleted || _flushInProgress || _autoEncounterSave is { IsCompleted: false } || _autoStopTask is { IsCompleted: false }))
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (_flushInProgress) return;
            _flushInProgress = true;
            DiagnosticButton.IsEnabled = false;
            if (_autoStopTask is { IsCompleted: false } stopping) await stopping;
            if (_diagnosticSession is not null) await _diagnosticSession.StopAsync();
            await StopVisionAsync();
            if (_autoEncounterSave is { } save) await save;
            PersistCurrentMatch();
            await FlushMatchAcquisitionAsync();
            if (_valueWriter is not null) await _valueWriter.Idle;
            _closingAfterFlush = true;
            Close();
            return;
        }
        base.OnClosing(e);
    }

    private void StartVision()
    {
        BeginMatchAcquisition();
        _mmrStopGate.Reset(); _autoStopTask = null;
        _matchLifecycle.Reset(); _waitingOnMenu = false; _closedAtNextGame = false;
        _suspendReach = !ReachEnabledInMainApp;
        _visionPipeline!.Reset();
        _visionPipeline.SetKnownPlayerDeck(_selectedUserDeck?.Cards.Select(c => c.Card.Id) ?? []);
        _playOrigins.Reset();
        _deckCompositionClues.Clear();
        _gameState.Reset(_manualEncounterId, _selectedUserDeck);
        _lastGameStateUpdate = null;
        _calculationPosition = null;
        ResetThreats();
        _visionError = null;
        _analyzedFrames = 0;
        _visionLagSeconds = 0;
        _droppedAnalysisFrames = 0;
        _lastQueuedAt = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _resultCaptureUntilTicks, 0);
        Interlocked.Exchange(ref _latestCapturedMatchHudTicks, 0);
        _captureMatchHudSeen = false; _lastCaptureMatchHud = null;
        _previewPriority = new PreviewPriorityWindow();
        _opponentPreviewPriority = new PreviewPriorityWindow();
        // Preserve the ordinary 24-frame budget while reserving a short-lived
        // extension for high-priority result frames. At 1280x720 this bounds the
        // raw queue near 170 MiB even if the consumer is temporarily stalled.
        var queue = new RollingVisionBuffer<DiagnosticProgress>(capacity: 24,
            maximumAge: TimeSpan.FromSeconds(6), protectedCapacity: 48,
            protectedMinimumPriority: 4, protectedMaximumAge: TimeSpan.FromSeconds(15));
        _visionQueue = queue;
        _streamingVision = new StreamingVisionProcessor<DiagnosticProgress>(_visionPipeline!)
        { WorkPriority = _gameplayPriority, OnError = exception => _visionError = exception.Message,
            OnPreparedText = (result, progress) =>
            {
                // Result panels can be skipped quickly. Switch capture admission
                // before artwork/UI work, and keep a short tail through transitions.
                if ((PostMatchMmr.IsResultHeader(result.Screen.ScreenHeader) || result.Screen.ScreenHeader == "GAME OVER" ||
                     result.Screen.PostMatchExitCue || result.Screen.PostMatchMmrCandidate is not null) &&
                    result.SampledAt.UtcTicks >= Interlocked.Read(ref _latestCapturedMatchHudTicks))
                    ProtectResultCapture(DateTimeOffset.UtcNow.AddSeconds(20));
                QueueFastHover(result with { HoverInPlayerHand = result.HoverInPlayerHand || progress.PointerInPlayerHand });
            } };
        _visionWorker = Task.Run(() => RunVisionAsync(queue));
    }

    private async Task StopVisionAsync()
    {
        _suspendReach = true;
        _threatCancellation?.Cancel();
        _threatKey = null; _reachProgress = null;
        _liveHover = null; RefreshHoverBanner();
        _visionQueue?.Complete();
        try { if (_visionWorker is not null) await _visionWorker; }
        finally { _visionWorker = null; _visionQueue = null; }
    }

    private void DiagnosticSession_OnProgress(object? sender, DiagnosticProgress progress)
    {
        // Inspect motion at capture cadence, before throttling text input. Opponent action/settling
        // samples survive redundant player-hover frames when the bounded queue is under pressure.
        var sampledAt = progress.SampledAt ?? DateTimeOffset.Now;
        if (progress.Observation?.MatchHudVisible == true)
        {
            _captureMatchHudSeen = true;
            Interlocked.Exchange(ref _latestCapturedMatchHudTicks, sampledAt.UtcTicks);
            // A real board returning after a round transition or completed match
            // immediately restores full card detection.
            Interlocked.Exchange(ref _resultCaptureUntilTicks, 0);
        }
        else if (_captureMatchHudSeen && _lastCaptureMatchHud == true &&
            progress.Observation?.MatchHudVisible == false)
        {
            // Speculatively fast-path the first HUD-less frames. A normal round
            // transition returns to the full path as soon as its HUD reappears;
            // an actual result header extends this window below using wall time.
            ProtectResultCapture(sampledAt.AddSeconds(3));
        }
        if (progress.Observation?.MatchHudVisible is { } matchHud) _lastCaptureMatchHud = matchHud;
        var opponentMotion = _opponentPreviewPriority.Score(sampledAt, progress.OpponentPreviewChange);
        var motion = _previewPriority.Score(sampledAt, progress.PreviewChange);
        var resultCapture = sampledAt.UtcTicks <= Interlocked.Read(ref _resultCaptureUntilTicks);
        if (progress.PreviewChange >= .075) _gameplayPriority.SignalAction();
        if (progress.AnalysisFrame is not null && progress.SampledAt is { } at &&
            at > _lastQueuedAt && (resultCapture ||
                at - _lastQueuedAt >= TimeSpan.FromMilliseconds(opponentMotion >= .06 ? 100 : motion >= .06 ? 190 : 240)))
        {
            _lastQueuedAt = at;
            var priority = resultCapture ? 4 : opponentMotion >= .06 ? 3 + opponentMotion : motion >= .06 ? 2 + motion : motion;
            _visionQueue?.Offer(progress with { Preview = null, PointerInPlayerHand = GwentCompanion.Platform.Windows.Windows.PointerObservation.InPlayerHand(_gameWindow),
                TrackingPriority = priority, ResultsOnly = resultCapture }, at, priority);
            _droppedAnalysisFrames = _visionQueue?.Dropped ?? 0;
            _gameplayPriority.ObservePressure(_visionQueue?.Count ?? 0, TimeSpan.FromSeconds(_visionLagSeconds));
        }
        if (progress.Preview is null && progress.Error is null && progress.CapturedFrames % 10 != 0) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (progress.Preview is not null)
            {
                _lastPreview = progress.Preview;
                PreviewImage.Source = progress.Preview;
                PreviewPlaceholder.Visibility = System.Windows.Visibility.Collapsed;
            }
            DiagnosticStatusText.Text = progress.Error is null && _visionError is null
                ? $"Captured {progress.CapturedFrames:N0}; training frames {progress.SavedFrames:N0}; analyzed {_analyzedFrames:N0} · vision lag {_visionLagSeconds:F1}s" +
                  (_droppedAnalysisFrames > 0 ? $" · {_droppedAnalysisFrames} text-input samples skipped; recording retained." : ".") +
                  (_streamingVision?.SkippedArtworkFrames > 0 ? $" {_streamingVision.SkippedArtworkFrames} artwork passes skipped; text retained." : "") +
                  " Priority: opponent → player."
                : $"Warning: {progress.Error ?? _visionError}";
            if (!_analysisTransition && _diagnosticSession?.IsRunning == true)
            {
                AnalysisStatusText.Text = _waitingOnMenu ? "Waiting for a game · tracking will begin automatically." : progress.Error is null && _visionError is null
                    ? $"Analyzing · training recording {(_diagnosticSession.RetainTrainingFrames ? $"on at {_diagnosticSession.RetainedFramesPerSecond} FPS" : "off")} · {progress.SavedFrames:N0} frames retained · {_analyzedFrames:N0} analyzed"
                    : $"Analysis warning: {progress.Error ?? _visionError}";
                AnalysisStatusText.Visibility = System.Windows.Visibility.Visible;
            }
            FooterStatusText.Text = progress.SessionDirectory;
        }, DispatcherPriority.Background);
    }

    private void ProtectResultCapture(DateTimeOffset until)
    {
        var proposed = until.UtcTicks;
        while (true)
        {
            var current = Interlocked.Read(ref _resultCaptureUntilTicks);
            if (current >= proposed || Interlocked.CompareExchange(ref _resultCaptureUntilTicks, proposed, current) == current)
                return;
        }
    }

    private async Task RunVisionAsync(RollingVisionBuffer<DiagnosticProgress> reader)
    {
        var lastBoardScan = DateTimeOffset.MinValue;
        BitmapSource? previous = null;
        IReadOnlyList<CardSighting> board = [];
        StreamWriter? log = null;
        GameStateJournal? stateJournal = null;
        GwentCompanion.Core.GameState.GameStateUpdate? latestState = null;
        var stateJournalFailed = false;
        string? sessionDirectory = null;
        var maximumLagSeconds = 0.0;
        var uiApplyMilliseconds = 0.0;
        var maximumUiApplyMilliseconds = 0.0;
        var maximumUiWaitMilliseconds = 0.0;
        var pending = new List<(string Directory, RecordedPlayEvent Record)>();
        try
        {
            await foreach (var output in _streamingVision!.RunAsync(VisionInputs(reader)))
            {
                var progress = output.Context;
                sessionDirectory = progress.SessionDirectory;
                if (progress.AnalysisFrame is not { } source) continue;
                var result = output.Result with
                    { HoverInPlayerHand = output.Result.HoverInPlayerHand || progress.PointerInPlayerHand };
                var at = result.SampledAt;
                try
                {
                    _visionLagSeconds = Math.Max(0, (DateTimeOffset.Now - at).TotalSeconds);
                    maximumLagSeconds = Math.Max(maximumLagSeconds, _visionLagSeconds);
                    Interlocked.Increment(ref _analyzedFrames);
                    if (result.BoardWasScanned && !result.Screen.IsCardSelectionOverlay)
                    {
                        lastBoardScan = at;
                        board = result.Sightings.Where(item => item.Source == CardSightSource.Board).ToArray();
                    }
                    log ??= new StreamWriter(Path.Combine(progress.SessionDirectory, "vision-observations.jsonl"), append: false);
                    await log.WriteLineAsync(JsonSerializer.Serialize(result, VisionJson)).ConfigureAwait(false);
                    // Store state after the existing origin/zone bookkeeping, before attaching play context.
                    var dispatchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    latestState = await Dispatcher.InvokeAsync(() =>
                    {
                        using var tracking = _gameplayPriority.EnterRecognition();
                        maximumUiWaitMilliseconds = Math.Max(maximumUiWaitMilliseconds,
                            System.Diagnostics.Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds);
                        var applyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        ApplyVisionResult(result);
                        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(applyStarted).TotalMilliseconds;
                        uiApplyMilliseconds += elapsed;
                        maximumUiApplyMilliseconds = Math.Max(maximumUiApplyMilliseconds, elapsed);
                        return _lastGameStateUpdate;
                    }, DispatcherPriority.Background);
                    if (!stateJournalFailed && latestState is not null)
                    {
                        try
                        {
                            stateJournal ??= new GameStateJournal(progress.SessionDirectory);
                            await stateJournal.AppendAsync(latestState).ConfigureAwait(false);
                        }
                        catch (Exception exception) { stateJournalFailed = true; _visionError = "State journal: " + exception.Message; }
                    }
                    foreach (var item in pending)
                    {
                        SavePng(source, Path.Combine(item.Directory, "after.png"));
                        _playEventStore.SaveRecord(item.Directory, item.Record with { AfterImage = "after.png",
                            AfterPosition = latestState is null ? null : PositionNotation.Write(CalculationPositionAdapter.FromObserved(latestState.After)) });
                    }
                    pending.Clear();
                    foreach (var evidence in result.Events.Where(item => item.Sighting.Source == CardSightSource.PlayPreview))
                    {
                        var sighting = evidence.Sighting;
                        var eventId = $"{at:yyyyMMdd-HHmmssfff}-{sighting.Side}";
                        var directory = _playEventStore.CreateEventDirectory(progress.SessionDirectory, eventId);
                        if (previous is not null) SavePng(previous, Path.Combine(directory, "before.png"));
                        SavePng(source, Path.Combine(directory, "during.png"));
                        var record = new RecordedPlayEvent(eventId, at, sighting.Card.Id, sighting.Card.Name,
                            sighting.Card.Kind, 1 - sighting.Distance, sighting.Side, 0.98, sighting.Region,
                            previous is null ? null : "before.png", "during.png", null,
                            evidence.Description + " " + sighting.Evidence,
                            "Structured state is partial and timestamped. Before is the previous analyzed frame; after is the next, not guaranteed settled. Unread power/statuses and trigger chains remain unknown.",
                            board.Select(item => new BoardCardEvidence(item.Card.Id, item.Card.Name, item.Side, item.Region)).ToArray(),
                            lastBoardScan == DateTimeOffset.MinValue ? null : lastBoardScan,
                            BeforePosition: latestState is null ? null : PositionNotation.Write(CalculationPositionAdapter.FromObserved(latestState.Before)),
                            DuringPosition: latestState is null ? null : PositionNotation.Write(CalculationPositionAdapter.FromObserved(latestState.After)));
                        _playEventStore.SaveRecord(directory, record);
                        pending.Add((directory, record));
                    }
                    if (!result.Screen.IsCardSelectionOverlay) previous = source;
                }
                catch (Exception exception) { _visionError = exception.Message; }
            }
        }
        catch (Exception exception) { _visionError = exception.Message; }
        finally
        {
            if (log is not null) await log.DisposeAsync();
            if (stateJournal is not null)
            {
                try
                {
                    if (latestState is not null)
                    {
                        await stateJournal.AppendAsync(latestState, force: true).ConfigureAwait(false);
                        await stateJournal.SaveFinalAsync(latestState.After).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) { _visionError = "State journal: " + exception.Message; }
                finally { await stateJournal.DisposeAsync(); }
            }
            if (sessionDirectory is not null && _streamingVision is { } processor)
            {
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "vision-summary.json"), JsonSerializer.Serialize(new
                    {
                        Version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                        AnalyzedFrames = _analyzedFrames, DroppedTextInputFrames = reader.Dropped,
                        processor.PreparedFrames, processor.ArtworkPasses, processor.SkippedArtworkFrames,
                        processor.MaximumPendingFrames, processor.Errors, MaximumLagSeconds = maximumLagSeconds,
                        MeanUiApplyMilliseconds = uiApplyMilliseconds / Math.Max(1, _analyzedFrames),
                        MaximumUiApplyMilliseconds = maximumUiApplyMilliseconds,
                        MaximumUiWaitMilliseconds = maximumUiWaitMilliseconds,
                        OcrCalls = _visionPipeline?.OcrCalls, OcrCacheHits = _visionPipeline?.OcrCacheHits,
                        CachedOcrBytes = _visionPipeline?.CachedTextBytes, CachedReferenceImages = _visionPipeline?.CachedReferenceImages,
                        RecognitionStages = _visionPipeline?.Timings.Snapshot(), FeatureStages = _visionPipeline?.FeatureTimings,
                        PriorityOrder = new[] { "Opponent card tracking", "Player card tracking" },
                        ReachInMainApp = false,
                        LastError = _visionError,
                        Note = "Text is retained when artwork work is skipped. Capture-input drops can still lose brief events. These counters are not accuracy measurements."
                    }, VisionJson)).ConfigureAwait(false);
                }
                catch (Exception exception) { _visionError = exception.Message; }
            }
        }
    }

    private static async IAsyncEnumerable<VisionInput<DiagnosticProgress>> VisionInputs(RollingVisionBuffer<DiagnosticProgress> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var progress in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (progress.AnalysisFrame is not { } frame || progress.SampledAt is not { } at) continue;
            yield return new VisionInput<DiagnosticProgress>(BitmapFrameAdapter.ToPixelFrame(frame), at,
                progress.TrackingPriority, progress, progress.PointerInPlayerHand,
                ResultsOnly: progress.ResultsOnly);
        }
    }

    private void ApplyVisionResult(CardVisionResult result)
    {
        if (_reviewEvidencePath is null)
        {
            if (_closedAtNextGame) return; // Draining queued frames must not merge the next game.
            _matchLifecycle.Observe(result.Screen, result.SampledAt);
            if (_matchLifecycle.WaitingForGame && result.Screen.MatchHudVisible == false)
            {
                if (!_waitingOnMenu) ShowAnalysisStatus("Waiting for a game · tracking will begin automatically.");
                _waitingOnMenu = true;
                return;
            }
            if (_waitingOnMenu) { _waitingOnMenu = false; ShowAnalysisStatus("Game started · tracking."); }
            if (_matchLifecycle.NewGameStarted)
            {
                _closedAtNextGame = true;
                // Save the retained result against the old state, before any new
                // board, leader, card, or round can enter the previous match.
                result = new(result.SampledAt, new(GwentViewKind.Board, false, 0, 0, null,
                    IsCardSelectionOverlay: true, ScreenHeader: "PROGRESSION", MatchHudVisible: false,
                    PostMatchMmr: _matchLifecycle.BestRating, PostMatchCaptureEnded: true), [], [], false);
            }
        }
        PrepareMatchAcquisition(result);
        UpdateVisualObservation(result.Screen);
        _opponentTime = result.SampledAt;
        var previousHand = _opponentKnowledge.OpponentHand;
        var changed = _opponentKnowledge.ObserveScreen(result.Screen, result.SampledAt);
        if (result.OpponentLeader is { } leader)
        {
            // A replacement ability changes Reach, not the original deck-building
            // leader. Before the first play, or while faction-compatible, this is
            // also definitive starting-deck metadata for prediction and caching.
            var compatible = !_opponentTracker.HasStableFaction ||
                leader.Card.Faction.Equals(_opponentTracker.Faction, StringComparison.OrdinalIgnoreCase);
            var establishStarting = !_opponentKnowledge.HasOpponentPlay || compatible;
            var leaderChanged = _opponentKnowledge.ObserveVisibleLeader(leader.Card, leader.Confidence, establishStarting);
            _playOrigins.ObserveCurrentLeader(PlayerSide.Opponent, leader.Card);
            changed |= leaderChanged;
            if (_opponentKnowledge.StartingLeader == leader.Card.Name && compatible)
                _opponentTracker.SetFactionPrior(leader.Card.Faction);
            if (_opponentKnowledge.StartingLeader == leader.Card.Name && compatible)
                changed |= ObserveLeaderStartingDeckAssumptions(leader.Card, result.SampledAt);
            if (leaderChanged && _opponentKnowledge.StartingLeader == leader.Card.Name && OriginalLeaderChoice is not null)
            {
                InitializeKnowledgeChoices();
                _updatingKnowledge = true;
                try { OriginalLeaderChoice.SelectedItem = (OriginalLeaderChoice.ItemsSource as IEnumerable<CardDefinition>)?.FirstOrDefault(card => card.Id == leader.Card.Id); }
                finally { _updatingKnowledge = false; }
            }
        }
        if (_selectedUserDeck is { } userDeck && _candidateCatalog?.FirstOrDefault(card =>
                card.Kind == CardKind.Leader && card.Name.Equals(userDeck.Leader, StringComparison.OrdinalIgnoreCase)) is { } userLeader)
            _playOrigins.ObserveCurrentLeader(PlayerSide.User, userLeader);
        if (_selectedUserDeck?.Stratagem is { } userStratagem)
            _playOrigins.ObserveOpeningStratagem(PlayerSide.User, userStratagem);
        if (_opponentKnowledge.StartingStratagemId is { } stratagemId &&
            _candidateCatalog?.FirstOrDefault(card => card.Kind == CardKind.Stratagem && card.Id == stratagemId) is { } opponentStratagem)
            _playOrigins.ObserveOpeningStratagem(PlayerSide.Opponent, opponentStratagem);
        changed |= ObservePostMatchMmr(result.Screen.PostMatchMmr);
        changed |= ObservePostMatchRank(result.Screen.PostMatchRank);
        if (result.DevotionCue is { } devotion)
        {
            _opponentKnowledge.Suggest(DeckCondition.Devotion, devotion.At, devotion.Evidence);
            changed = true;
        }
        _opponentKnowledge.ObserveOpeningCounts(_opponentTracker.HasStableFaction ? _opponentTracker.Faction : null);
        foreach (var setup in _opponentKnowledge.OpeningStartingCards)
            changed |= _opponentTracker.ConsiderDirectPlay(setup.Card, .99, setup.At, setup.Evidence,
                CardProvenance.ProbableStartingDeck);
        if (result.GraveyardInspection is { } inspection) { ObserveZoneInspection(inspection, result.SampledAt); changed = true; }
        if (result.Description is { SourceId: not "202192" } description)
        {
            _descriptionHint = description;
            AddedCardChoice.ItemsSource = description.Candidates;
            AddedCardChoice.SelectedIndex = 0;
            DescriptionIsReveal.IsChecked = description.IsDeckReveal;
            DescriptionHintText.Text = description.SourceName + " · description names found: " + string.Join(", ", description.Candidates.Select(card => card.Name)) + ". Check the affected side before recording.";
            DescriptionHintText.ToolTip = "Review the affected side before recording.";
            System.Windows.Automation.AutomationProperties.SetHelpText(DescriptionHintText, description.Text);
            changed = true;
        }
        changed |= _opponentKnowledge.Sequences.ObserveFrame(result.SampledAt, result.Screen, result.Sightings,
            result.BoardWasScanned, _opponentKnowledge.Round, _opponentKnowledge.HasOpeningWindow,
            _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null,
            _opponentKnowledge.StartingLeader, _opponentKnowledge.CurrentLeader);
        _playOrigins.PrimeCoTemporalBoardCreations(result.Events,_selectedUserDeck);
        foreach (var evidence in result.Events)
        {
            var sighting = evidence.Sighting;
            var tracker = sighting.Side == PlayerSide.User ? _userTracker : _opponentTracker;
            _deckMutations.Observe(evidence, _selectedUserDeck?.Faction ?? _userTracker.Faction, _opponentTracker.Faction);
            var recentDeckCount=sighting.Side==PlayerSide.Opponent
                ? RecentDeckCount(result.Screen.OpponentDeckCount,_gameState.Current.Opponent.DeckCount,evidence.ObservedAt)
                : RecentDeckCount(result.Screen.UserDeckCount,_gameState.Current.User.DeckCount,evidence.ObservedAt);
            var baseOrigin = _playOrigins.Observe(evidence, _selectedUserDeck, recentDeckCount);
            var origin = _zones.OriginRisk(sighting, tracker.Observations) ?? _deckMutations.OriginRisk(sighting, evidence.ObservedAt, tracker.Observations) ??
                _opponentKnowledge.BoardOrigin(evidence, _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null) ?? Watch?.ArrivalOrigin(evidence, _opponentKnowledge.Opportunities) ?? baseOrigin;
            if (tracker.Observations.Any(item => item.Card.Id == sighting.Card.Id))
            {
                var repeatRisk = _deckMutations.OriginRisk(sighting, evidence.ObservedAt, tracker.Observations, additionalCopy: true);
                if (repeatRisk is not null) origin = repeatRisk;
                else if (baseOrigin.Provenance == CardProvenance.Replayed) origin = baseOrigin;
                else if (_playOrigins.HasCopyRisk(sighting, evidence.ObservedAt) ||
                         _deckMutations.HasRecentReplay(sighting.Side, evidence.ObservedAt))
                    origin = new(CardProvenance.Unknown,
                        "Repeated identity follows an observed create/copy/replay route; it cannot upgrade an earlier uncertain card into an original.");
            }
            if (!sighting.Card.CanBeInStartingDeck && !EvolvingCardCatalog.IsEvolved(sighting.Card.Id)) origin = baseOrigin;
            var identity = EvolvingCardCatalog.StartingIdentity(sighting, origin, _candidateCatalog ?? [], tracker.Faction);
            changed |= _opponentKnowledge.Sequences.ObserveEvent(evidence, identity.Origin.Provenance);
            _opponentKnowledge.Observe(evidence, _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null, identity.Origin.Provenance);
            Watch?.ObserveEvent(evidence);
            changed |= tracker.ConsiderDirectPlay(identity.Card, 1 - sighting.Distance, evidence.ObservedAt,
                evidence.Description + " " + identity.Origin.Reason, identity.Origin.Provenance);
            var copyOrigin = _deckMutations.OriginRisk(sighting, evidence.ObservedAt, tracker.Observations, additionalCopy: true) is not null ||
                             _playOrigins.HasCopyRisk(sighting, evidence.ObservedAt)
                ? CardProvenance.Unknown
                : identity.Origin.Provenance;
            changed |= _thinningCopies.ObserveEvent(evidence, copyOrigin, tracker,
                _deckMutations.HasRecentReplay(sighting.Side, evidence.ObservedAt));
            if (sighting.Source == CardSightSource.PlayPreview)
            {
                _recordedPlayEvents++;
                DetectedPlayText.Text = $"Last play preview: {sighting.Side} · {sighting.Card.Name}. {_recordedPlayEvents} recognized preview episode(s); not total plays or deck copies.";
            }
        }
        changed |= HandCommitTracker.Apply(_handCommits.Observe(result.SampledAt,result.Screen,result.Events),_playOrigins,_deckMutations,
            _thinningCopies,_userTracker,_opponentTracker,_selectedUserDeck,
            sight=>_zones.OriginRisk(sight,(sight.Side==PlayerSide.User?_userTracker:_opponentTracker).Observations) is not null);
        Watch?.ObserveFrame(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned, _opponentKnowledge.Round);
        changed |= _opponentKnowledge.ObserveStartingConservation(result.SampledAt,
            _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null,
            EffectiveOpponentDeckEvidence().Sum(item => item.ObservedCopies));
        changed |= _opponentKnowledge.SummonAbsence.ObserveFrame(result.SampledAt,result.Screen,result.Sightings,result.BoardWasScanned);
        changed |= _thinningCopies.ObserveFrame(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned,
            [_userTracker, _opponentTracker], sight =>
                _deckMutations.OriginRisk(sight, result.SampledAt, [], additionalCopy: true) is not null ||
                _playOrigins.HasCopyRisk(sight, result.SampledAt) ||
                _zones.Entries.Any(item => item.Side == sight.Side && item.Card.Id == sight.Card.Id &&
                    item.Route is ZoneEntryRoute.Generated or ZoneEntryRoute.HeulynSetup), _selectedUserDeck,
            sight => _deckMutations.OriginRisk(sight,result.SampledAt,[],additionalCopy:true) is null &&
                !_zones.Entries.Any(item=>item.Side==sight.Side && item.Card.Id==sight.Card.Id && item.Route is ZoneEntryRoute.Generated or ZoneEntryRoute.HeulynSetup)
                ? _playOrigins.RecentSpawnedInitiators(sight,result.SampledAt) : null);
        changed |= _opponentKnowledge.ObserveDevotionFrame(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned);
        UpdateStoredGameState(result);
        ObserveMatchAcquisition(result);
        if (changed || result.Events.Count > 0 || previousHand != _opponentKnowledge.OpponentHand)
        { RenderLiveInference(); PersistCurrentMatch(); }
        else RenderTacticalWatch();
        UpdateThreats(result);
        TryAutoCacheOpponent();
        TryAutoStopOnMmr(result);
    }

    private static int? RecentDeckCount(int? current, GwentCompanion.Core.GameState.StateFact<int>? prior,
        DateTimeOffset at) => current ?? (prior is { } fact && at>=fact.At && at-fact.At<=TimeSpan.FromSeconds(5)
            ? fact.Value : null);

    private bool ObserveLeaderStartingDeckAssumptions(CardDefinition leader, DateTimeOffset at)
    {
        var changed=false;
        foreach(var assumption in LeaderSpawnCatalog.StartingDeckAssumptions(leader,_candidateCatalog ?? []))
        {
            changed |= _opponentTracker.ConsiderDirectPlay(assumption.Card,.84,at,assumption.Reason,
                CardProvenance.ProbableStartingDeck);
            var existing=_opponentTracker.Observations.FirstOrDefault(item=>item.Card.Id==assumption.Card.Id);
            if(existing is not null && !StartingDeckRules.CountsAgainstStartingDeck(existing.Provenance))
                changed |= _opponentTracker.SetProvenance(assumption.Card.Id,CardProvenance.ProbableStartingDeck,assumption.Reason);
            changed |= _opponentTracker.SetObservedCopyLowerBound(assumption.Card.Id,assumption.Copies,
                $"copies assumed from observed {leader.Name} leader package");
        }
        return changed;
    }

    private void UpdateVisualObservation(GwentVisualObservation observation)
    {
        if (!observation.FrameGeometrySupported)
        {
            LiveViewText.Text = "Recognition paused · unsupported game layout";
            LiveDetailText.Text = observation.FrameGeometryWarning ?? "Use a 16:9 GWENT resolution.";
            return;
        }
        LiveViewText.Text = observation.IsCardSelectionOverlay ? "Selection / deck / graveyard / transition" :
            observation.View == GwentViewKind.MoveHistory ? "Move History" : "Board / other game view";
        LiveDetailText.Text = observation.ScreenHeader is { Length: > 0 } header ? $"Visible header: {header}" :
            "Recognition uses visible artwork and separate upper/lower right play previews.";
    }
}
