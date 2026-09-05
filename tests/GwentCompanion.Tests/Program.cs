using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Windows;
using GwentCompanion.Platform.Windows.Vision;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Contains("--all-recording-pixel-audit")) { await AllRecordingPixelAudit.Run(ResolveGameRoot(), args); return 0; }
if (args.Contains("--recording-validation-regression")) { await RecordingValidationCorpusTests.Run(ResolveProjectRoot()); return 0; }
if (args.Contains("--contributor-validation-regression")) { await ContributorValidationCases.RunAsync(ResolveProjectRoot()); return 0; }
if (args.Contains("--recording-fix-regression")) { try { RecordingFixTests.Run(ResolveGameRoot()); return 0; } catch(Exception e) { Console.Error.WriteLine(e); return 1; } }
if (args.Contains("--tutor-origin-regression")) { try { RecordingFixTests.TutorOrigin(ResolveGameRoot()); return 0; } catch(Exception e) { Console.Error.WriteLine(e); return 1; } }
if (args.Contains("--recording-pile-glyph")) { RecordingFixTests.PileGlyph(ResolveGameRoot()); return 0; }
if (args.Contains("--recording-fix-pixels")) { try { await RecordingFixTests.Pixels(ResolveGameRoot()); return 0; } catch(Exception e) { Console.Error.WriteLine(e); return 1; } }
if (args.Contains("--recording-audit")) { await RecordingAudit.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--recording-mmr-audit")) { await RecordingAudit.Mmr(ResolveGameRoot()); return 0; }
if (args.Contains("--vision-efficiency-profile")) { await VisionEfficiencyTests.Profile(ResolveGameRoot(), args); return 0; }
if (args.Contains("--vision-efficiency-regression")) { await VisionEfficiencyTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--vision-efficiency-review")) { await VisionEfficiencyTests.Run(ResolveGameRoot(),true); return 0; }
if (args.Contains("--vision-efficiency-stream")) { await VisionEfficiencyTests.Stream(ResolveGameRoot(),args); return 0; }
if (args.Contains("--frame-compatibility-regression")) { VisionFrameCompatibilityTests.Run(); return 0; }
if (args.Contains("--post-match-workflow-regression")) { await PostMatchWorkflowTests.Run(ResolveGameRoot()); return 0; }

if (args.Contains("--title-style-probe")) { await TitleStyleTests.Probe(ResolveGameRoot()); return 0; }
if (args.Contains("--title-style-audit")) { await TitleStyleTests.Audit(ResolveGameRoot()); return 0; }
if (args.Contains("--title-style-regression")) { await TitleStyleTests.Run(ResolveGameRoot()); return 0; }

if (args.Contains("--match-defect-probe")) { await MatchDefectTests.Probe(ResolveGameRoot()); return 0; }
if (args.Contains("--match-defect-regression")) { await MatchDefectTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--deck-glyph-fixture")) { MatchDefectTests.DeckGlyphFixtures(ResolveGameRoot()); return 0; }
if (args.Contains("--tutor-pixel-regression")) { await MatchDefectTests.TutorPixels(ResolveGameRoot()); return 0; }
if (args.Contains("--match-artwork-regression")) { MatchDefectTests.Artwork(ResolveGameRoot()); return 0; }

if (args.Contains("--builder-fill-filter-regression")) { BuilderFillFilterTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--card-data-regression")) { await CardDataUpdateTests.RunAsync(ResolveGameRoot(), args.Contains("--live-card-data")); return 0; }
if (args.Contains("--season-tools-regression")) { await SeasonToolsTests.RunAsync(ResolveGameRoot(), args.Contains("--balance-audit"), args.Contains("--cache-baseline")); return 0; }
if (args.Contains("--sequential-prediction-regression")) { OpponentSequenceTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--current-pixel-replay")) { await CurrentPixelReplay.Run(ResolveGameRoot(), args); return 0; }
if (args.Contains("--leader-icon-regression")) { LeaderAbilityRecognizerTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--current-leader-probe")) { LeaderAbilityRecognizerTests.Probe(ResolveGameRoot(), args); return 0; }
if (args.Contains("--startup-profile")) { StartupPerformanceTests.Run(ResolveGameRoot(), args.Contains("--vision")); return 0; }
if (args.Contains("--video-review-scan")) { return VideoArchiveScan.Run(args, ResolveGameRoot()); }
if (args.Contains("--video-review-recognize")) { return await VideoArchiveScan.RecognizeAsync(args, ResolveGameRoot()); }
if (args.Contains("--shinmiri-detector-evaluation")) { return await ShinmiriDetectorEvaluation.RunAsync(ResolveGameRoot(), args); }
if (args.Contains("--video-training-promote")) { return VideoArchiveScan.Promote(args, ResolveGameRoot()); }
if (args.Contains("--video-window-extract")) { return VideoArchiveScan.ExtractWindow(args, ResolveGameRoot()); }
if (args.Contains("--hand-motion-audit")) { return VideoArchiveScan.AuditHandMotion(args); }
if (args.Contains("--coin-hud-audit")) { return await VideoArchiveScan.AuditCoins(args, ResolveGameRoot()); }
if (args.Contains("--preview-power-probe")) { return await VideoArchiveScan.ProbePreviewPowerAsync(args); }
if (args.Contains("--interaction-watch-export")) { return VideoArchiveScan.ExportInteractionWatch(args, ResolveGameRoot()); }
if (args.Contains("--interaction-gap-audit")) { return VideoArchiveScan.AuditInteractionGaps(args, ResolveGameRoot()); }
if (args.Contains("--simulation-gap-audit")) { SimulationCoverageAudit.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--video-caption-scan")) { return CaptionArchiveScan.Run(args, ResolveGameRoot()); }
if (args.Contains("--current-accounting-audit")) { CurrentAccountingAudit.Run(ResolveGameRoot(), args); return 0; }
if (args.Contains("--current-match-audit-regression")) { CurrentMatchAuditTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--latest-fixes-regression")) { await LatestFixesTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--latest-fixes-pixels")) { LatestFixesTests.Pixels(ResolveGameRoot()); return 0; }
if (args.Contains("--recent-gang-pixels")) { LatestFixesTests.RecentGangPixels(ResolveGameRoot()); return 0; }
if (args.Contains("--art-label-probe")) { LatestFixesTests.LabelProbe(ResolveGameRoot()); return 0; }
if (args.Contains("--recent-provision-evidence")) { LatestFixesTests.ReconcileProvisionReplay(ResolveGameRoot()); return 0; }

if (args.Contains("--recent-refinement-audit")) { RecentMatchRefinementTests.Audit(ResolveGameRoot(), args); return 0; }
if (args.Contains("--recent-refinement-regression")) { RecentMatchRefinementTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--recent-provision-pixels")) { ProvisionEncounterTests.Pixels(ResolveGameRoot(),false,"20260828-165436"); return 0; }
if (args.Contains("--recent-brewess-window")) { ProvisionEncounterTests.Pixels(ResolveGameRoot(),false,"20260828-165436","165750000","165850000"); return 0; }
if (args.Contains("--match-data-audit")) { await MatchDataAudit.RunAsync(ResolveGameRoot(), args.Contains("--pixels")); return 0; }
if (args.Contains("--provision-encounter-regression")) { try { ProvisionEncounterTests.Run(ResolveGameRoot()); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
if (args.Contains("--provision-pixels")) { ProvisionEncounterTests.Pixels(ResolveGameRoot(), args.Contains("--baseline")); return 0; }
if (args.Contains("--provision-neighbor-probe")) { ProvisionEncounterTests.NeighborProbe(ResolveGameRoot()); return 0; }
if (args.Contains("--match-pixel-probe")) { await MatchDataAudit.ProbeAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--streaming-regression")) { await StreamingVisionTests.RunAsync(); Console.WriteLine("PASS streaming text callbacks and authoritative evidence"); return 0; }
if (args.Contains("--priority-reach-regression")) { await PriorityReachTests.RunAsync(); return 0; }

if (args.Contains("--compact-live-regression")) { try { var project = ResolveProjectRoot(); UiShellTests.Navigation(project); UiShellTests.CompactLive(project); SummonFocusTests.Run(project); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
if (args.Contains("--synergy-routing-regression")) { PlaysSectionTests.Run(ResolveGameRoot()); Console.WriteLine("PASS faction synergy routing and compactness"); return 0; }
if (args.Contains("--pirate-armor-regression")) { PirateArmorTests.Run(); return 0; }
if (args.Contains("--cultist-synergy-regression")) { CultistSynergyTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--deck-builder-export-regression")) { DeckBuilderExportTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--deck-variation-audit")) { DeckVariationAudit.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--deck-variation-regression")) { try { DeckVariationTests.Run(ResolveGameRoot()); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
if (args.Contains("--library-import-dedup-regression")) { try { LibraryImportDedupTests.Run(ResolveGameRoot()); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
if (args.Contains("--deck-related-regression")) { try { DeckRelatedCardsTests.Run(); return 0; } catch (Exception error) { Console.Error.WriteLine(error); return 1; } }
if (args.Contains("--responsiveness-regression")) { await ResponsivenessTests.RunAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--archetype-activation-regression")) { ArchetypeActivationTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--pinned-regression"))
{
    UiShellTests.Navigation(ResolveGameRoot()); UiShellTests.Pins(ResolveGameRoot()); UiShellTests.Zoom();
    Console.WriteLine("PASS Pinned: numbered files, actual deletion, bounded Undo, collision/path safety, letterbox/edge/DPI-independent zoom geometry."); return 0;
}
if (args.Contains("--live-insights-regression") || args.Contains("--build-create-profiles") || args.Contains("--live-ledger-replay"))
{
    try
    {
        if (args.Contains("--build-create-profiles")) LiveInsightsTests.Profiles(ResolveGameRoot());
        else if (args.Contains("--live-ledger-replay")) LiveInsightsTests.Replay(ResolveGameRoot());
        else LiveInsightsTests.Run(ResolveGameRoot());
        return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Contains("--feature-cache-regression")) { FeatureCacheTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--vision-optimization-regression")) { await VisionOptimizationTests.RunAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--appearance-decode-check")) { VisionOptimizationTests.CheckAppearanceDecoders(ResolveGameRoot()); return 0; }
if (args.Contains("--latest-interaction-regression"))
{
    try { LatestInteractionTests.Run(ResolveGameRoot()); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Contains("--latest-match-audit"))
{
    try { var index = Array.IndexOf(args, "--input"); LatestMatchAudit.Run(ResolveGameRoot(), index >= 0 ? args[index + 1] : null); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Contains("--train-board-power") || args.Contains("--board-power-regression"))
{
    try { if (args.Contains("--trace-glyphs")) HudDigitReader.Trace = Console.WriteLine; if (args.Contains("--train-board-power")) BoardPowerTests.Train(ResolveGameRoot()); else BoardPowerTests.Run(ResolveGameRoot()); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Contains("--latest-recommendation-replay"))
{
    var root = ResolveGameRoot(); var index = Array.IndexOf(args, "--input");
    RecommendationTests.LatestReplay(root, DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json")).Decks,
        index < 0 ? null : args[index + 1]); return 0;
}
if (args.Contains("--sync-supplied-workbooks")) return await SuppliedWorkbookSync.RunAsync(ResolveGameRoot(), args.Contains("--apply"));
if (args.Contains("--synergy-package-audit")) { SynergyPackageAudit.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--expanded-import-regression")) { DeckImportTests.ExpandedLibrary(ResolveGameRoot()); return 0; }
if (args.Contains("--candidate-significance-regression")) { CandidateSignificanceTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--patch-prediction-regression")) { PatchPredictionTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--candidate-significance-evaluation")) { CandidateSignificanceTests.Evaluate(ResolveGameRoot(), args.Contains("--validation")); return 0; }
if (args.Contains("--import-regression"))
{
    DeckImportTests.Links(); DeckImportTests.Library(); await DeckImportTests.CacheAsync(ResolveGameRoot());
    Console.WriteLine("Deck import regression passed."); return 0;
}

if (args.Contains("--game-state-regression")) { await GameStateTests.RunAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--state-hud-regression")) { await GameStateTests.LatestHudAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--calculation-regression")) { CalculationPositionTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--candidate-point-regression")) { CandidatePointEvaluatorTests.Run(); return 0; }
if (args.Contains("--reach-projection-regression")) { ReachProjectionTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--play-engine-regression")) { PlayEngineTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--hover-regression")) { await PlayEngineTests.HoverPixelsAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--recorded-score-regression")) { await RecordedScoreTests.RunAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--score-ocr-probe")) { await RecordedScoreTests.ProbeOcrAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--sequence-regression")) { RecordedSequenceTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--reach-footage-regression")) { RecordedReachFootageTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--leader-spawn-regression")) { LeaderSpawnTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--card-cache-audit")) { await CardCacheAudit.RunAsync(ResolveGameRoot(), args.Contains("--fill-missing-art"), args.Contains("--compare-public-catalog")); return 0; }
if (args.Contains("--train-score-digits")) { RecordedScoreTests.TrainDigits(ResolveGameRoot()); return 0; }
if (args.Contains("--score-heldout-regression")) { await RecordedScoreTests.HeldOutScoresAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--fresh-score-board-probe")) { await RecordedScoreTests.FreshBoardAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--scan-score-calibration")) { await RecordedScoreTests.ScanScoresAsync(ResolveGameRoot()); return 0; }

if (args.Contains("--oak-effect-probe")) { OakEffectProbe.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--oak-effect-regression")) { OakEffectProbe.Run(ResolveGameRoot()); await OakEffectProbe.IntegratedAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--companion-regression")) { CompanionCardTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--candidate-prior-regression")) { CandidatePriorTests.Run(ResolveGameRoot()); return 0; }
if (args.Contains("--post-match-mmr-regression")) { await PostMatchMmrTests.RunAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--recommendation-regression"))
{
    var root = ResolveGameRoot();
    DeckProjectionTests.Projection(); DeckProjectionTests.Meta();
    MatchReviewTests.Inference(root); RecommendationTests.Run(root);
    CandidatePriorTests.Run(root);
    PatchPredictionTests.Run(root); CandidateSignificanceTests.Run(root);
    CompanionCardTests.Run(root); SummonFocusTests.Run(root);
    AutoPopulationTests.Run(root);
    OpponentKnowledgeTests.Memory(root); EncounterFrequencyTests.Run(root);
    Console.WriteLine("Recommendation regression passed (latest recorded match only)."); return 0;
}
if (args.Contains("--detector-update-regression")) { DetectorUpdateTests.Confirmation(); DetectorUpdateTests.Origins(ResolveGameRoot()); DetectorUpdateTests.RefillingHandPlays(ResolveGameRoot()); await DetectorUpdateTests.RecordedTitlesAsync(ResolveGameRoot()); await DetectorUpdateTests.NamedSpawnCandidatePixelsAsync(ResolveGameRoot()); return 0; }
if (args.Contains("--preview-title-regression")) { await TestPreviewTitles(ResolveGameRoot()); await MatchReviewTests.TitlesAsync(ResolveGameRoot()); return 0; }

if (args.Length >= 2 && args[0] == "--builder-audit")
    return await DeckBuilderAudit.RunAsync(ResolveGameRoot(), args[1]);

if (args.Contains("--tactical-regression"))
{
    var root = ResolveGameRoot();
    TacticalWatchTests.Rules(root); TacticalWatchTests.Zones(root); TacticalWatchTests.Recordings(root);
    SummonFocusTests.Run(root);
    Console.WriteLine("Tactical regression passed."); return 0;
}

if (args.Contains("--opponent-regression"))
{
    var root = ResolveGameRoot();
    OpponentKnowledgeTests.Rules(root); OpponentKnowledgeTests.Budgets(); OpponentKnowledgeTests.Memory(root);
    OpponentKnowledgeTests.Mutations(root); await OpponentKnowledgeTests.HudAsync(root); OpponentKnowledgeTests.Recordings(root);
    Console.WriteLine("Opponent regression passed."); return 0;
}
if (args.Contains("--backfill-deck-metadata"))
    return await DeckMetadataBackfill.RunAsync(ResolveGameRoot(), args.Contains("--apply"));
if (args.Contains("--leader-regression"))
{
    DeckBuilderTests.LeaderChoices(ResolveGameRoot());
    await DeckBuilderTests.ScoiataelHeaderAsync(ResolveGameRoot());
    Console.WriteLine("Leader regression passed.");
    return 0;
}

if (args.Length >= 2 && args[0] is "--builder-replay" or "--builder-probe")
    return await DeckBuilderReplay.RunAsync(ResolveGameRoot(), args[1], args[0] == "--builder-probe");
if (args.Contains("--builder-regression"))
{
    DeckBuilderTests.NameRules(ResolveGameRoot());
    DeckBuilderTests.HeaderPersistence(ResolveGameRoot());
    await DeckBuilderTests.QuantitiesAsync(ResolveGameRoot());
    return await DeckBuilderReplay.RunAsync(ResolveGameRoot(), Path.Combine(ResolveGameRoot(), "GwentCompanion", "deck-scans", "20260827-140327-db4212"), false);
}

if (args.Contains("--export-builder-badge"))
{
    var root = ResolveGameRoot();
    using var source = File.OpenRead(Path.Combine(root, "GwentCompanion", "deck-scans", "20260827-140327-db4212", "page-001.jpg"));
    var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var crop = new CroppedBitmap(decoder.Frames[0], new System.Windows.Int32Rect(322, 566, 16, 19));
    var path = Path.Combine(root, "GwentCompanion", "assets", "vision", "deck-builder-copies-x2.png");
    using var output = File.Create(path);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop)); encoder.Save(output);
    Console.WriteLine("Exported only the manually verified static x2 UI glyph; no card artwork.");
    return 0;
}

if (args.Length >= 2 && args[0] == "--probe-deck")
{
    var links = DeckLinkFileReader.FromText(args[1]);
    if (links.Count != 1) throw new ArgumentException("Supply one direct public deck URL.");
    var path = Path.Combine(ResolveGameRoot(), "GwentCompanion", "diagnostics", "v0.1.5-import-probe");
    var result = await new PlayGwentDeckCacheService().SyncAsync(links, path, 1);
    Console.WriteLine(JsonSerializer.Serialize(new { result.Requested, result.Downloaded, result.Expired, result.Errors,
        Decks = result.Decks.Select(deck => new { deck.Id, deck.Faction, deck.Leader, deck.CardCount, deck.ProvisionTotal }) }));
    return result.Decks.Count == 1 ? 0 : 1;
}

if (args.Contains("--capture-regression")) return CaptureRegression.Run();
if (args.Length == 2 && args[0] == "--capture-test-window") return CaptureRegression.Run(int.Parse(args[1]));

if (args.Contains("--sync-catalog-art"))
{
    var cache = Path.Combine(ResolveGameRoot(), "GwentCompanion", "cache");
    var cards = GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json"));
    var catalog = new DeckDefinition("catalog", "Public artwork catalog", "Neutral", "", 0,
        cards.Where(card => card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact or CardKind.Stratagem)
            .Select(card => new DeckCard(card)).ToArray());
    var progress = new Progress<ArtCacheProgress>(item =>
    {
        if (item.Completed % 100 == 0 || item.Completed == item.Requested)
            Console.WriteLine($"ART {item.Completed}/{item.Requested}: {item.Downloaded} downloaded; {item.Cached} cached; {item.Failed} failed");
    });
    var result = await new PlayGwentArtCacheService().SyncAsync([catalog], Path.Combine(cache, "portraits"), progress);
    foreach (var error in result.Errors) Console.WriteLine(error);
    Console.WriteLine($"DONE requested={result.Requested} downloaded={result.Downloaded} cached={result.LoadedFromCache} failed={result.Errors.Count}");
    return result.Errors.Count == 0 ? 0 : 1;
}

if (args.Length >= 4 && args[0] == "--cache-appearance")
{
    var root = ResolveGameRoot();
    var cardId = args[1];
    if (!cardId.All(char.IsAsciiDigit)) throw new ArgumentException("Expected a numeric card ID.");
    var path = Path.Combine(root, "GwentCompanion", "sessions", args[2]);
    if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine(root, args[2]));
    if (!File.Exists(path)) throw new FileNotFoundException("Appearance source frame not found.", path);
    var rect = args[3].Split(',').Select(int.Parse).ToArray();
    using var source = File.OpenRead(path);
    var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var crop = new CroppedBitmap(decoder.Frames[0], new System.Windows.Int32Rect(rect[0], rect[1], rect[2], rect[3]));
    var directory = Path.Combine(root, "GwentCompanion", "cache", "observed-art", cardId);
    Directory.CreateDirectory(directory);
    var name = Path.GetFileNameWithoutExtension(path);
    if (args.Length > 4)
    {
        if (!args[4].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new ArgumentException("Appearance suffix must be alphanumeric.");
        name += "-" + args[4];
    }
    using var output = File.Create(Path.Combine(directory, name + ".jpg"));
    var encoder = new JpegBitmapEncoder { QualityLevel = 95 }; encoder.Frames.Add(BitmapFrame.Create(crop)); encoder.Save(output);
    File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new { CardId = cardId, Source = args[2], PixelRectangle = rect, LabelSource = "Manually verified visible artwork; evaluate on different frames" }));
    Console.WriteLine($"Cached verified artwork appearance for {cardId}.");
    return 0;
}

if (args.Contains("--export-header-template"))
{
    var root = ResolveGameRoot();
    using var source = File.OpenRead(Path.Combine(root, "GwentCompanion", "sessions", "20260826-225210", "frame-000489-225259566.jpg"));
    var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var crop = new CroppedBitmap(decoder.Frames[0], new System.Windows.Int32Rect(415, 16, 132, 27));
    var directory = Path.Combine(root, "GwentCompanion", "assets", "vision");
    Directory.CreateDirectory(directory);
    using var output = File.Create(Path.Combine(directory, "graveyard-header.png"));
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop)); encoder.Save(output);
    Console.WriteLine("Exported text-only graveyard-header template.");
    return 0;
}

var cacheArgument = Array.FindIndex(args, value => value is "--cache-art-ids" or "--cache-premium-ids" or "--cache-premium-reference");
if (cacheArgument >= 0)
{
    var root = Path.Combine(ResolveGameRoot(), "GwentCompanion", "cache");
    var cards = GwentOneCardCatalog.Load(Path.Combine(root, "gwent-one-cards.json"));
    var referenceMode = args[cacheArgument] == "--cache-premium-reference";
    var ids = referenceMode
        ? JsonSerializer.Deserialize<GwentCompanion.Core.GameState.GameStateSnapshot>(File.ReadAllText(args[cacheArgument + 1]),
            GameStateJournal.Json)!.User.StartingDeckReference!.Cards
            .Select(item => item.Card.Id).ToHashSet(StringComparer.Ordinal)
        : args[cacheArgument + 1].Split(',').ToHashSet(StringComparer.Ordinal);
    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    foreach (var card in cards.Where(card => ids.Contains(card.Id)))
    {
        var premium = args[cacheArgument] != "--cache-art-ids";
        if (referenceMode && Directory.Exists(Path.Combine(root,"premium-frames",card.Id)) &&
            Directory.GetFiles(Path.Combine(root,"premium-frames",card.Id),"*.jpg").Length>=12) continue;
        var path = Path.Combine(root, premium ? "premium" : "portraits", card.Id + (premium ? ".webm" : ".jpg"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var url = premium ? new Uri($"https://gwent.one/video/card/premium/{card.Id}.webm") : card.ArtUri!;
            byte[] bytes;
            try { bytes = await http.GetByteArrayAsync(url); }
            catch (System.Net.Http.HttpRequestException exception) when (premium && exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine($"No public premium video for {card.Name}; static reference retained.");
                continue;
            }
            await File.WriteAllBytesAsync(path, bytes);
        }
        var count = premium ? VisionReferenceLibrary.ExtractPremiumFrames(path, Path.Combine(root, "premium-frames", card.Id)) : 1;
        Console.WriteLine($"Cached {card.Name}: {count} reference image(s)");
    }
    return 0;
}

if (args.Any(value => value is "--vision-replay" or "--vision-frame" or "--vision-regression"))
    return await VisionReplay.RunAsync(args, ResolveGameRoot(), ResolveNotes(args));

if (args.Contains("--live-window", StringComparer.OrdinalIgnoreCase))
{
    var snapshot = new GwentWindowService().Find();
    if (snapshot is null)
    {
        Console.Error.WriteLine("GWENT window not found by the companion service.");
        return 2;
    }

    Console.WriteLine(
        $"Found PID {snapshot.ProcessId}; handle=0x{snapshot.Handle:X}; " +
        $"title={snapshot.Title}; bounds={snapshot.Bounds.Left},{snapshot.Bounds.Top}," +
        $"{snapshot.Bounds.Right},{snapshot.Bounds.Bottom}; client={snapshot.ClientBounds.Left}," +
        $"{snapshot.ClientBounds.Top},{snapshot.ClientBounds.Right},{snapshot.ClientBounds.Bottom}; " +
        $"mode={snapshot.DisplayMode}");
    return 0;
}

if (args.Contains("--live-capture", StringComparer.OrdinalIgnoreCase))
{
    var snapshot = new GwentWindowService().Find();
    if (snapshot is null)
    {
        Console.Error.WriteLine("GWENT window not found by the companion service.");
        return 2;
    }

    using var capture = new Win32FrameCapture();
    var frame = capture.Capture(snapshot);
    var output = Path.Combine(ResolveGameRoot(), "GwentCompanion", "snapshots", "live-capture-test.png");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(frame));
    using (var stream = File.Create(output))
    {
        encoder.Save(stream);
    }

    Console.WriteLine($"Captured {frame.PixelWidth}x{frame.PixelHeight} to {output}");
    return 0;
}

var syncArgument = Array.FindIndex(
    args,
    value => string.Equals(value, "--sync-recent", StringComparison.OrdinalIgnoreCase));
if (syncArgument >= 0)
{
    var maximum = syncArgument + 1 < args.Length && int.TryParse(args[syncArgument + 1], out var requested)
        ? requested
        : 80;
    var notes = ResolveNotes(args);
    var entries = Directory.GetFiles(notes, "*.xlsx", SearchOption.TopDirectoryOnly)
        .SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries)
        .ToArray();
    var cache = Path.Combine(ResolveGameRoot(), "GwentCompanion", "cache", "decks");
    var progress = new Progress<DeckCacheProgress>(item =>
        Console.WriteLine(
            $"{item.Completed}/{item.Requested}: {item.Loaded} valid, " +
            $"{item.Expired} expired, {item.Failed} failed"));
    var result = await new PlayGwentDeckCacheService().SyncAsync(entries, cache, maximum, progress);
    Console.WriteLine(
        $"DONE requested={result.Requested} decks={result.Decks.Count} downloaded={result.Downloaded} " +
        $"cache={result.LoadedFromCache} expired={result.Expired} failed={result.Errors.Count}");
    foreach (var error in result.Errors.Take(10))
    {
        Console.Error.WriteLine(error);
    }

    return result.Decks.Count > 0 ? 0 : 3;
}

if (args.Contains("--sync-art", StringComparer.OrdinalIgnoreCase))
{
    var notes = ResolveNotes(args);
    var entries = Directory.GetFiles(notes, "*.xlsx", SearchOption.TopDirectoryOnly)
        .SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries)
        .ToArray();
    var root = ResolveGameRoot();
    var deckCache = Path.Combine(root, "GwentCompanion", "cache", "decks");
    var decks = new PlayGwentDeckCacheService().LoadCached(entries, deckCache, 500);
    var artCache = Path.Combine(root, "GwentCompanion", "cache", "portraits");
    var progress = new Progress<ArtCacheProgress>(item =>
        Console.WriteLine(
            $"{item.Completed}/{item.Requested}: {item.Downloaded} downloaded, " +
            $"{item.Cached} cached, {item.Failed} failed"));
    var result = await new PlayGwentArtCacheService().SyncAsync(decks, artCache, progress);
    Console.WriteLine(
        $"DONE requested={result.Requested} downloaded={result.Downloaded} " +
        $"cache={result.LoadedFromCache} failed={result.Errors.Count}");
    foreach (var error in result.Errors.Take(10))
    {
        Console.Error.WriteLine(error);
    }

    return result.Requested > 0 && result.Errors.Count == 0 ? 0 : 4;
}

var replayArgument = Array.FindIndex(
    args,
    value => string.Equals(value, "--replay-session", StringComparison.OrdinalIgnoreCase));
if (replayArgument >= 0)
{
    var root = ResolveGameRoot();
    var session = replayArgument + 1 < args.Length
        ? args[replayArgument + 1]
        : Directory.GetDirectories(Path.Combine(root, "GwentCompanion", "sessions"))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .First();
    if (!Path.IsPathRooted(session))
    {
        session = Path.Combine(root, "GwentCompanion", "sessions", session);
    }

    var notes = ResolveNotes(args);
    var entries = Directory.GetFiles(notes, "*.xlsx", SearchOption.TopDirectoryOnly)
        .SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries)
        .ToArray();
    var decks = new PlayGwentDeckCacheService().LoadCached(
        entries,
        Path.Combine(root, "GwentCompanion", "cache", "decks"),
        500);
    var portraits = Path.Combine(root, "GwentCompanion", "cache", "portraits");
    var references = decks
        .SelectMany(deck => deck.Cards)
        .Select(item => item.Card)
        .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Select(card => (Card: card, Path: Path.Combine(portraits, $"{card.Id}.jpg")))
        .Where(item => File.Exists(item.Path))
        .Select(item => new CardArtReference(item.Card, VisualDescriptor.Create(LoadFrame(item.Path))))
        .ToArray();
    var detector = new GwentVisualStateDetector();
    var locator = new MoveHistoryCardLocator();
    var matcher = new CardArtMatcher(references);
    var userTracker = new LiveDeckTracker(PlayerSide.User);
    var opponentTracker = new LiveDeckTracker(PlayerSide.Opponent);
    var historyFrames = 0;
    var candidateCount = 0;
    foreach (var path in Directory.GetFiles(session, "*.jpg").OrderBy(path => path))
    {
        var frame = LoadFrame(path);
        var observation = detector.Analyze(frame);
        if (observation.HasCardTooltip)
        {
            var detailMatches = matcher.Rank(
                frame,
                new NormalizedRegion(0.052, 0.174, 0.296, 0.826),
                3);
            Console.WriteLine(
                $"DETAIL {Path.GetFileName(path)} tooltip={observation.TooltipConfidence:F3} :: " +
                string.Join(" | ", detailMatches.Select(match => $"{match.Card.Name} [{match.Card.Faction}] {match.Distance:F3}")));
        }

        if (observation.View != GwentViewKind.MoveHistory)
        {
            continue;
        }

        historyFrames++;
        var candidates = locator.Locate(frame);
        candidateCount += candidates.Count;
        var sampledAt = DateTimeOffset.Now;
        Console.WriteLine($"FRAME {Path.GetFileName(path)} history={observation.HistoryButtonConfidence:F3} candidates={candidates.Count}");
        foreach (var candidate in candidates)
        {
            var matches = matcher.Rank(frame, candidate.Region, 3);
            var tracker = candidate.Region.Left < 0.08 ? userTracker : opponentTracker;
            var accepted = tracker.Consider(
                candidate,
                matches,
                sampledAt,
                IsLikelyHovered(candidate, observation));
            Console.WriteLine(
                $"  {(candidate.Region.Left < 0.08 ? "USER" : "OPP ")} " +
                $"y={candidate.Region.Top:F3} cardness={candidate.Score:F3} :: " +
                string.Join(" | ", matches.Select(match => $"{match.Card.Name} [{match.Card.Faction}] {match.Distance:F3}")) +
                (accepted ? "  ACCEPTED" : string.Empty));
        }
    }

    Console.WriteLine($"DONE session={Path.GetFileName(session)} historyFrames={historyFrames} candidates={candidateCount}");
    Console.WriteLine(
        $"USER faction={userTracker.Faction ?? "?"} cards=" +
        string.Join(", ", userTracker.Observations.Select(item => item.Card.Name)));
    Console.WriteLine(
        $"OPPONENT faction={opponentTracker.Faction ?? "?"} cards=" +
        string.Join(", ", opponentTracker.Observations.Select(item => item.Card.Name)));
    return 0;
}

var scanEnlargedArgument = Array.FindIndex(
    args,
    value => string.Equals(value, "--scan-enlarged", StringComparison.OrdinalIgnoreCase));
if (scanEnlargedArgument >= 0)
{
    var root = ResolveGameRoot();
    var session = scanEnlargedArgument + 1 < args.Length
        ? args[scanEnlargedArgument + 1]
        : Directory.GetDirectories(Path.Combine(root, "GwentCompanion", "sessions"))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .First();
    if (!Path.IsPathRooted(session))
    {
        session = Path.Combine(root, "GwentCompanion", "sessions", session);
    }

    var detector = new EnlargedCardPlayDetector();
    var viewDetector = new GwentVisualStateDetector();
    var notes = ResolveNotes(args);
    var entries = Directory.GetFiles(notes, "*.xlsx", SearchOption.TopDirectoryOnly)
        .SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries)
        .ToArray();
    var decks = new PlayGwentDeckCacheService().LoadCached(
        entries,
        Path.Combine(root, "GwentCompanion", "cache", "decks"),
        500);
    var portraits = Path.Combine(root, "GwentCompanion", "cache", "portraits");
    var references = BuiltInCardCatalog.Merge(decks
            .SelectMany(deck => deck.Cards)
            .Select(item => item.Card))
        .Select(card => (Card: card, Path: Path.Combine(portraits, $"{card.Id}.jpg")))
        .Where(item => File.Exists(item.Path))
        .Select(item => new CardArtReference(item.Card, VisualDescriptor.Create(LoadFrame(item.Path))))
        .ToArray();
    var matcher = new CardArtMatcher(references);
    var hits = 0;
    foreach (var path in Directory.GetFiles(session, "*.jpg").OrderBy(path => path))
    {
        var frame = LoadFrame(path);
        if (viewDetector.Analyze(frame).View != GwentViewKind.Board)
        {
            continue;
        }

        var candidates = detector.LocateAll(frame);
        if (candidates.Count == 0)
        {
            continue;
        }

        hits++;
        Console.WriteLine($"{Path.GetFileName(path)} candidates={candidates.Count}");
        foreach (var candidate in candidates)
        {
            var matches = matcher.Rank(frame, candidate.Region, 3);
            Console.WriteLine(
                $"  score={candidate.Score:F3} edge={candidate.BoundaryContrast:F3} " +
                $"closure={candidate.EdgeClosure:F3} texture={candidate.InteriorTexture:F3} " +
                $"side={candidate.SideHint?.ToString() ?? "?"} " +
                $"region={candidate.Region.Left:F3},{candidate.Region.Top:F3},{candidate.Region.Right:F3},{candidate.Region.Bottom:F3} :: " +
                string.Join(" | ", matches.Select(match => $"{match.Card.Name} {match.Distance:F3}")));
        }
    }

    Console.WriteLine($"DONE enlarged-candidate-frames={hits}");
    return 0;
}

var failures = new List<string>();
Run("Starting-deck rules", TestStartingDeckRules, failures);
Run("Provision provenance", TestProvisionProvenance, failures);
Run("Deck inference", TestDeckInference, failures);
Run("Leader replacement state", TestLeaderReplacementState, failures);
Run("Conservative point estimation", TestPointEstimation, failures);
Run("Shared visual game state and rules", () => GameStateTests.RunAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("NG Cultist synergy", () => CultistSynergyTests.Run(ResolveGameRoot()), failures);
Run("Deck builder and export", () => DeckBuilderExportTests.Run(ResolveGameRoot()), failures);
Run("Deck variation groups and portable library", () => DeckVariationTests.Run(ResolveGameRoot()), failures);
Run("Same-patch library import evidence deduplication", () => LibraryImportDedupTests.Run(ResolveGameRoot()), failures);
Run("Related-card alternatives for full decks", DeckRelatedCardsTests.Run, failures);
Run("Latest pixel state HUD", () => GameStateTests.LatestHudAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Compact calculation notation and rules fidelity", () => CalculationPositionTests.Run(ResolveGameRoot()), failures);
Run("Board-aware candidate point evaluation", CandidatePointEvaluatorTests.Run, failures);
Run("Catalog-wide greedy reach projections", () => ReachProjectionTests.Run(ResolveGameRoot()), failures);
Run("Tactical play and reach engine", () => PlayEngineTests.Run(ResolveGameRoot()), failures);
Run("Recorded chosen-action reach validation", () => RecordedReachFootageTests.Run(ResolveGameRoot()), failures);
Run("Live side and faction tracking", TestLiveDeckTracking, failures);
Run("Separate observed-deck caches", TestObservedDeckStore, failures);
Run("Workbook imports", () => TestWorkbookImports(ResolveNotes(args)), failures);
Run("PlayGWENT deck parser", TestPlayGwentDeckParser, failures);
Run("Visual calibration", () => TestVisualCalibration(ResolveGameRoot()), failures);
Run("Card-art matcher", () => TestCardArtMatcher(ResolveGameRoot(), ResolveNotes(args)), failures);
Run("Vision episode and provenance separation", TestVisionLedger, failures);
Run("Public catalog and token metadata", () => TestPublicCardCatalog(ResolveGameRoot()), failures);
Run("Deck search catalog", () => TestDeckSearch(ResolveGameRoot(), ResolveNotes(args)), failures);
Run("Rolling vision buffer and scan fairness", TestRollingVision, failures);
Run("Unlabelled preview review clips", TestPreviewReview, failures);
Run("Exact visible preview titles", () => TestPreviewTitles(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Compact recording validation corpus", () => RecordingValidationCorpusTests.Run(ResolveProjectRoot()).GetAwaiter().GetResult(), failures);
Run("Extensible contributor validation", () => ContributorValidationCases.RunAsync(ResolveProjectRoot()).GetAwaiter().GetResult(), failures);
Run("Conservative adaptive title style", () => TitleStyleTests.Run(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Opponent review and confirmed MMR stop", () => PostMatchWorkflowTests.Run(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Bounded streaming text/artwork merge", () => StreamingVisionTests.RunAsync().GetAwaiter().GetResult(), failures);
Run("Resolution and board-layout failure containment", VisionFrameCompatibilityTests.Run, failures);
Run("Latest Tempest and priority-aware efficiency", () => VisionEfficiencyTests.Run(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Opponent-first scheduling and incremental Reach", () => PriorityReachTests.RunAsync().GetAwaiter().GetResult(), failures);
Run("Generated-card and spy provenance", () => PlayProvenanceTests.Run(ResolveGameRoot()), failures);
Run("Shared deck category and patch filters", MatchReviewTests.Filters, failures);
Run("Devotion likelihood and created-card bookkeeping", () => MatchReviewTests.Inference(ResolveGameRoot()), failures);
Run("Evolving forms and Conqueror Devotion cues", () => MatchReviewTests.DevotionEffects(ResolveGameRoot()), failures);
Run("Recorded Oakcritters effect and negative controls", () => OakEffectProbe.Run(ResolveGameRoot()), failures);
Run("Live OCR/effect result propagation", () => OakEffectProbe.IntegratedAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Latest match preview-title attribution", () => MatchReviewTests.TitlesAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Weak preview temporal confirmation", DetectorUpdateTests.Confirmation, failures);
Run("Bounded flat-counter hand-play refill evidence", () => DetectorUpdateTests.RefillingHandPlays(ResolveGameRoot()), failures);
Run("Generalized match defects and conditional carryover", () => MatchDefectTests.Run(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Board-first creation provenance", () => DetectorUpdateTests.Origins(ResolveGameRoot()), failures);
Run("Held-out Miner and Swordmaster recovery", () => DetectorUpdateTests.RecordedTitlesAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Generated-choice popup and named-output candidates", () => DetectorUpdateTests.NamedSpawnCandidatePixelsAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Provision-sorted deck projection and pin reconciliation", DeckProjectionTests.Projection, failures);
Run("Recent prevalence versus specific co-occurrence", DeckProjectionTests.Meta, failures);
Run("Deck-reference and Devotion persistence", () => DeckProjectionTests.Persistence(ResolveGameRoot()), failures);
Run("Recorded-game deck composition replay", () => DeckProjectionTests.Recordings(ResolveGameRoot()), failures);
Run("Opponent interaction catalog and resolved conditions", () => OpponentKnowledgeTests.Rules(ResolveGameRoot()), failures);
Run("Endgame original provision bounds", OpponentKnowledgeTests.Budgets, failures);
Run("Partial opponent memory and exact variants", () => OpponentKnowledgeTests.Memory(ResolveGameRoot()), failures);
Run("Generated-in-deck provenance and descriptions", () => OpponentKnowledgeTests.Mutations(ResolveGameRoot()), failures);
Run("Recorded opponent HUD counters", () => OpponentKnowledgeTests.HudAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Recorded opponent knowledge integration", () => OpponentKnowledgeTests.Recordings(ResolveGameRoot()), failures);
Run("Tactical watch conditions and promotion", () => TacticalWatchTests.Rules(ResolveGameRoot()), failures);
Run("Zone origin ambiguity and hidden traps", () => TacticalWatchTests.Zones(ResolveGameRoot()), failures);
Run("Recorded tactical snapshot replay", () => TacticalWatchTests.Recordings(ResolveGameRoot()), failures);
Run("Compact three-page UI structure", () => UiShellTests.Navigation(ResolveGameRoot()), failures);
Run("Recoverable pinned-view removal", () => UiShellTests.Pins(ResolveGameRoot()), failures);
Run("Reference query chronology and faction/leader filters", () => ReferenceSearchTests.Run(ResolveGameRoot()), failures);
Run("Faction-filtered Plays sections and local synergy checks", () => PlaysSectionTests.Run(ResolveGameRoot()), failures);
Run("Informative summons without routine thinning clutter", () => SummonFocusTests.Run(ResolveGameRoot()), failures);
Run("Measured companions and visible thinning-copy handoff", () => CompanionCardTests.Run(ResolveGameRoot()), failures);
Run("Unseen-card recommendation confidence and latest match", () => RecommendationTests.Run(ResolveGameRoot()), failures);
Run("Early tentative population and reference variants", () => AutoPopulationTests.Run(ResolveGameRoot()), failures);
Run("Opponent encounter frequency, dates and rating context", () => EncounterFrequencyTests.Run(ResolveGameRoot()), failures);
Run("Spreadsheet patch labels, monthly defaults and preserved history", () => DeckPatchTests.Run(ResolveGameRoot()), failures);
Run("Persistent library merging, rename and card-only search", DeckImportTests.Library, failures);
Run("Headerless spreadsheet and safe link imports", DeckImportTests.Links, failures);
Run("Scrolling deck draft consensus and correction", DeckImportTests.Draft, failures);
Run("Validated HTTP imports and cancellation recovery", () => DeckImportTests.CacheAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Deck-builder numeric columns and conservative glyph correction", () => DeckBuilderTests.NameRules(ResolveGameRoot()), failures);
Run("Leader and stratagem header consensus, persistence and merging", () => DeckBuilderTests.HeaderPersistence(ResolveGameRoot()), failures);
Run("Metadata backfill identity and conflict safeguards", () => DeckMetadataBackfill.Regression(ResolveGameRoot()), failures);
Run("Starting leader choices and conservative header corrections", () => DeckBuilderTests.LeaderChoices(ResolveGameRoot()), failures);
Run("Recorded Scoia'tael header replay", () => DeckBuilderTests.ScoiataelHeaderAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Builder ordering and bounded scrolling consensus safeguards", () => DeckBuilderAudit.SafetyAndOrder(ResolveGameRoot()), failures);
Run("Comprehensive recorded deck-builder audit", () =>
{
    if (DeckBuilderAudit.RunAsync(ResolveGameRoot(), "verified").GetAwaiter().GetResult() != 0)
        throw new InvalidOperationException("A recorded builder composition, copy count, header or order failed the audit.");
}, failures);
Run("Held-out deck-builder quantities at multiple UI scales", () => DeckBuilderTests.QuantitiesAsync(ResolveGameRoot()).GetAwaiter().GetResult(), failures);
Run("Recorded deck-builder composition replay", () =>
{
    if (DeckBuilderReplay.RunAsync(ResolveGameRoot(), Path.Combine(ResolveGameRoot(), "GwentCompanion", "deck-scans", "20260827-140327-db4212"), false).GetAwaiter().GetResult() != 0)
        throw new InvalidOperationException("Deck-builder replay differs from the user-confirmed composition.");
}, failures);

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failures.Count} test group(s) failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine();
Console.WriteLine("All test groups passed.");
return 0;

static async Task TestPreviewTitles(string root)
{
    var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
    var reader = new PreviewTitleRecognizer(cards);
    using var screenReader = new ScreenStateRecognizer();
    foreach (var (file, expected) in new (string File, string? Expected)[]
    {
        ("20260827-081435/frame-000752-081550234.jpg", "201701"),
        ("20260827-081435/frame-001204-081635436.jpg", "200221"),
        ("20260827-081435/frame-004089-082123937.jpg", "202216"),
        ("20260901-124026/frame-006123-125039542.jpg", "202985"),
        ("20260827-081435/frame-005001-082255131.jpg", "202594"),
        ("20260827-081435/frame-002752-081910223.jpg", "202856"),
        ("20260827-081435/frame-005044-082259423.jpg", "203208"),
        ("20260827-081435/frame-005771-082412135.jpg", "203093"),
        ("20260827-081435/frame-000836-081558636.jpg", null),
        ("20260827-081435/frame-005459-082340931.jpg", null),
        ("20260827-081435/frame-006078-082442826.jpg", null),
        ("20260827-101339/frame-000923-101511297.jpg", "200039"),
        ("20260827-101339/frame-004715-102130490.jpg", "203071"),
        ("20260827-101339/frame-005267-102225700.jpg", "203169"),
        ("20260827-101339/frame-005488-102247791.jpg", null),
        ("20260826-225210/frame-000492-225259878.jpg", null),
        ("20260826-225210/frame-000489-225259566.jpg", null),
    })
    {
        var original = LoadFrame(Path.Combine(root, "GwentCompanion", "sessions", file));
        foreach (var width in new[] { original.Width, 960 }.Distinct())
        {
            using var source = OpenCvSharp.Mat.FromPixelData(original.Height, original.Width, OpenCvSharp.MatType.CV_8UC4, original.BgraPixels);
            using var resized = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.Resize(source, resized, new OpenCvSharp.Size(width, (int)Math.Round(original.Height * width / (double)original.Width)),
                0, 0, OpenCvSharp.InterpolationFlags.Area);
            var pixels = new byte[resized.Width * resized.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
            var frame = new PixelFrame(resized.Width, resized.Height, pixels);
            var screen = await screenReader.AnalyzeAsync(frame);
            var result = await reader.RecognizeAsync(frame, screen, screenReader);
            if (width == original.Width && expected is not null && !result.Any(item => item.Card.Id == expected))
            {
                reader.Trace = Console.WriteLine;
                await reader.RecognizeAsync(frame, screen, screenReader);
                reader.Trace = null;
            }
            // The pipeline now reads text BEFORE reducing the artwork resolution.
            // A degraded input may abstain, but it must not invent a different identity.
            Assert(expected is null ? result.Count == 0 :
                (width < original.Width && result.Count == 0) ||
                (result.Count == 1 && result[0].Card.Id == expected && result[0].Side == PlayerSide.Opponent),
                $"Preview title mismatch in {file} at width {width}: {string.Join(',', result.Select(item => item.Card.Name))}");
        }
    }
}

static void TestRollingVision()
{
    var at = DateTimeOffset.UnixEpoch;
    var priority = new PreviewPriorityWindow();
    Assert(priority.Score(at, .3) == .3 && priority.Score(at.AddMilliseconds(200), 0) > .2,
        "Settled frames following a brief preview change also need retention priority.");
    Assert(priority.Score(at.AddSeconds(1), 0) == 0, "Retention priority must expire after motion settles.");
    var schedule = new VisionScanSchedule();
    Assert(schedule.NextIncludesBoard(at), "First scan should include the board.");
    for (var i = 1; i <= 3; i++)
        Assert(!schedule.NextIncludesBoard(at.AddSeconds(i * 4)), "Even a slow board pass must leave three preview passes.");
    Assert(schedule.NextIncludesBoard(at.AddSeconds(16)), "Board scanning must resume after preview passes.");
    var queue = new RollingVisionBuffer<int>(3, TimeSpan.FromSeconds(6));
    queue.Offer(1, at, .8);
    queue.Offer(2, at.AddSeconds(1), .01);
    queue.Offer(3, at.AddSeconds(2), .02);
    queue.Offer(4, at.AddSeconds(3), .01);
    queue.Complete();
    var items = Drain(queue).GetAwaiter().GetResult();
    Assert(items.SequenceEqual([1, 3, 4]), "Under pressure, retain the brief changed preview, newest frame and chronological order.");
    Assert(queue.Dropped == 1, "Dropped samples must be reported.");
    var expired = new RollingVisionBuffer<int>(3);
    expired.Offer(1, at, 1);
    expired.Offer(2, at.AddSeconds(7), 0);
    expired.Complete();
    Assert(Drain(expired).GetAwaiter().GetResult().SequenceEqual([2]), "Stale samples must not accumulate indefinitely.");
    static async Task<List<int>> Drain(RollingVisionBuffer<int> queue)
    {
        var result = new List<int>();
        await foreach (var item in queue.ReadAllAsync()) result.Add(item);
        return result;
    }
}

static void TestPreviewReview()
{
    var at = DateTimeOffset.UnixEpoch;
    var selector = new PreviewReviewSelector<string>();
    selector.Observe("before", at, new PreviewMotion(0, 0));
    selector.Observe("change", at.AddSeconds(.4), new PreviewMotion(.2, .2));
    selector.Observe("during", at.AddSeconds(.6), new PreviewMotion(0, 0));
    var clips = selector.Observe("after", at.AddSeconds(1.2), new PreviewMotion(0, 0));
    Assert(clips.Count == 2 && clips.Select(item => item.Side).Distinct().Count() == 2, "Both preview lanes need independent clips.");
    Assert(clips.All(item => item.Before.Value == "before" && item.During.Value == "during" && item.After?.Value == "after"),
        "Unknown changes must retain pre-roll and delayed during/after frames without a recognized card label.");
    selector.Observe("cooldown", at.AddSeconds(1.4), new PreviewMotion(.2, .2));
    Assert(selector.Flush().Count == 0, "Animation must not create an unbounded flood of review clips.");
    selector.Observe("tail-before", at.AddSeconds(3.5), new PreviewMotion(0, 0));
    selector.Observe("tail", at.AddSeconds(4), new PreviewMotion(0, .2));
    var tail = selector.Flush();
    Assert(tail.Count == 1 && tail[0].After is null, "An interrupted clip must be retained and marked incomplete.");
}

static void TestDeckSearch(string root, string notes)
{
    DeckIndexEntry Entry(string hash, string name, int rank = 0) => new(hash, "Creator's Library", "Skellige",
        rank + 1, "Skellige", "Battle Trance", name, new Uri($"https://www.playgwent.com/en/decks/{hash}"),
        null, null, "", null, null, rank);
    var portal = Card("202201", "Portal", CardKind.Artifact, 10);
    var deck = new DeckDefinition("abc", "Cached name", "Skellige", "Battle Trance", 17, [new(portal)],
        new Uri("https://www.playgwent.com/en/decks/abc"));
    var index = new[] { Entry("abc", "Crow’s Alchemy"), Entry("abc", "Alternate label", 1), Entry("xyz", "Uncached older deck", 500) };
    var catalog = DeckSearchCatalog.Build(index, [deck]);
    Assert(catalog.Count == 2, "Cached decks enrich the index; uncached links must not disappear or duplicate cached entries.");
    Assert(catalog.Count(item => item.Deck is not null) == 1, "Only the actually cached deck may claim a full list.");
    Assert(catalog.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("SKELLIGE   portal"))),
        "Multiple words must match across faction and contained card names.");
    Assert(catalog.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("Crows alchemy"))),
        "Curly apostrophes must not break a plain-text search.");
    Assert(catalog.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("alternate label"))),
        "Duplicate source names must remain searchable.");
    Assert(catalog.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("older creator"))),
        "An uncached older deck must be searchable by its source metadata.");
    Assert(catalog.All(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("  "))),
        "Clearing search must restore all entries.");
    Assert(!catalog.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("zzzxnotadeck"))),
        "A nonmatch must stay empty, not silently discard the user's filter.");
    Assert(DeckSearchCatalog.Normalize("Scoia’tael café") == "scoiatael cafe", "Search normalization must handle accents.");
    Assert(DeckSearchCatalog.Build([], [deck]).Count == 1, "An orphaned cached deck must remain searchable.");
    var entries = Directory.GetFiles(notes, "*.xlsx").SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries).ToArray();
    var cached = new PlayGwentDeckCacheService().LoadCached(entries, Path.Combine(root, "GwentCompanion", "cache", "decks"), int.MaxValue);
    var actual = DeckSearchCatalog.Build(entries, cached);
    Assert(actual.Count > cached.Count && actual.Count > 80, "The actual workbook search must not be limited to cached/recent decks.");
    Assert(actual.Any(item => DeckSearchCatalog.Matches(item.SearchText, DeckSearchCatalog.Terms("skellige portal"))),
        "The supplied corpus must find the known Portal deck by faction plus card.");
    Console.WriteLine($"  Searchable decks: {actual.Count}; complete cached decks: {cached.Count}.");
}

static void TestVisionLedger()
{
    var ledger = new MatchVisionLedger();
    var card = Card("example", "Example", CardKind.Unit, 6, "Skellige");
    var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null);
    var at = DateTimeOffset.UnixEpoch;
    var sighting = new CardSighting(card, PlayerSide.User, CardSightSource.PlayPreview, new NormalizedRegion(.82, .41, .92, .67), .1, .8);
    Assert(ledger.Observe(at, board, [sighting]).Count == 1, "First preview should create one event.");
    Assert(ledger.Observe(at.AddSeconds(5), board, [sighting]).Count == 0, "Persistent preview must not count twice.");
    Assert(ledger.Observe(at.AddSeconds(6), board with { IsCardSelectionOverlay = true }, [sighting]).Count == 0, "A selection overlay must not create evidence.");
    Assert(ledger.Observe(at.AddSeconds(7), board, [sighting with { Source = CardSightSource.History }]).Count == 0, "History must not duplicate known action evidence.");
    Assert(ledger.Observe(at.AddSeconds(8), board, [sighting with { Side = PlayerSide.Opponent }]).Count == 1, "Opponent's same card must remain independent.");
    ledger.Observe(at.AddSeconds(10), board, []);
    ledger.Observe(at.AddSeconds(13), board, []);
    Assert(ledger.Observe(at.AddSeconds(14), board, [sighting]).Count == 1, "A fresh preview after a clear interval should be retained.");
    ledger.Reset();
    var boardSighting = sighting with { Source = CardSightSource.Board };
    Assert(ledger.Observe(at, board, [boardSighting]).Count == 0, "Board evidence requires another independent frame.");
    var evidence = ledger.Observe(at.AddSeconds(1), board, [boardSighting]);
    Assert(evidence.Count == 1 && evidence[0].Sighting.Source == CardSightSource.Board, "Board presence must stay distinct from a play.");
    var tracker = new LiveDeckTracker(PlayerSide.User);
    tracker.ConsiderDirectPlay(card, .9, at, "Board only", CardProvenance.Unknown);
    Assert(tracker.DeckBuildingObservations.Count == 0, "Unknown board origin must not consume provisions.");
    tracker.ConsiderDirectPlay(card, .8, at.AddSeconds(1), "Actual preview", CardProvenance.ProbableStartingDeck);
    Assert(tracker.DeckBuildingObservations.Count == 1, "Actual play evidence should promote a board-only observation even at a lower match score.");
    tracker.ConsiderDirectPlay(card, .99, at.AddSeconds(2), "Board again", CardProvenance.Unknown);
    Assert(tracker.DeckBuildingObservations.Count == 1, "Board updates must not erase play evidence.");
}

static void TestPublicCardCatalog(string root)
{
    var cards = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
    Assert(cards.Count > 1000, "Full public catalog was not parsed.");
    Assert(cards.Single(card => card.Id == "201701").Faction == "Monsters", "Public Monster cards must use the tracker faction name.");
    Assert(cards.All(card => card.Faction is not "Monster" and not "Scoiatael"), "Public factions must be normalized consistently.");
    Assert(cards.Count(card => card.Kind == CardKind.Leader) > 20, "Public Ability entries must be leader references, never unknown playable cards.");
    Assert(!cards.Single(card => card.Id == "202568").CanBeInStartingDeck, "Crow is a generated token, not a starting-deck card.");
    Assert(cards.Single(card => card.Id == "201749").Name == "Golden Froth", "Generated special reference was not resolved.");
}

static void TestStartingDeckRules()
{
    var units = Enumerable.Range(1, 25)
        .Select(index => new DeckCard(Card($"unit-{index}", $"Unit {index}", CardKind.Unit, 6)))
        .ToArray();
    var renfriDeck = new DeckDefinition("renfri", "Renfri", "Neutral", "Imposter", 15, units);
    var assessment = StartingDeckRules.EvaluateExactDeck(renfriDeck);
    Assert(assessment.Renfri.State == ConstraintState.Confirmed, "25 units should satisfy Renfri.");
    Assert(assessment.Shupe.State == ConstraintState.Confirmed, "Unique card IDs should satisfy Shupe.");

    var tactics = Enumerable.Range(1, 12)
        .Select(index => new DeckCard(Card(
            $"tactic-{index}",
            $"Tactic {index}",
            CardKind.Special,
            4,
            "Nilfgaard",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tactic" })))
        .Concat(Enumerable.Range(1, 13)
            .Select(index => new DeckCard(Card($"soldier-{index}", $"Soldier {index}", CardKind.Unit, 5, "Nilfgaard"))))
        .ToArray();
    var enslaveDeck = new DeckDefinition("enslave", "Enslave 6", "Nilfgaard", "Enslave", 15, tactics);
    var enslave = StartingDeckRules.EvaluateExactDeck(enslaveDeck);
    Assert(enslave.EnslaveValue == 6, "Twelve Tactics should produce Enslave 6.");

    var goldenNekker = Card("gn", "Golden Nekker", CardKind.Special, 10);
    var ciriNova = Card("nova", "Ciri: Nova", CardKind.Unit, 10);
    var gnDeck = new DeckDefinition(
        "gn",
        "GN",
        "Neutral",
        "Imposter",
        15,
        new[] { new DeckCard(goldenNekker), new DeckCard(ciriNova), new DeckCard(Card("low", "Low", CardKind.Unit, 9)) });
    Assert(
        StartingDeckRules.EvaluateExactDeck(gnDeck).GoldenNekker.State == ConstraintState.Confirmed,
        "Golden Nekker and Ciri: Nova are exceptions to the 10-provision cutoff.");

    var invalidGnDeck = gnDeck with
    {
        Cards = gnDeck.Cards.Concat(new[] { new DeckCard(Card("heatwave", "Korathi Heatwave", CardKind.Special, 10)) }).ToArray(),
    };
    Assert(
        StartingDeckRules.EvaluateExactDeck(invalidGnDeck).GoldenNekker.State == ConstraintState.RuledOut,
        "A non-exempt 10-provision card should rule out Golden Nekker.");

    var now = DateTimeOffset.UtcNow;
    var observedRenfri = StartingDeckRules.EvaluateObservedDeck(
        [new ObservedCard(BuiltInCardCatalog.Cards.Single(), CardProvenance.ProbableStartingDeck, 0.9, now)]);
    Assert(observedRenfri.Renfri.State == ConstraintState.Likely, "Seeing Renfri should activate the 25-unit inference.");
    var observedGn = StartingDeckRules.EvaluateObservedDeck(
        [new ObservedCard(goldenNekker, CardProvenance.ProbableStartingDeck, 0.9, now)]);
    Assert(observedGn.GoldenNekker.State == ConstraintState.Likely, "Seeing Golden Nekker should activate its provision constraint.");
    var shupe = Card("shupe", "Shupe's Day Off", CardKind.Special, 13);
    var observedShupe = StartingDeckRules.EvaluateObservedDeck(
        [new ObservedCard(shupe, CardProvenance.ProbableStartingDeck, 0.9, now)]);
    Assert(observedShupe.Shupe.State == ConstraintState.Likely, "Seeing Shupe should activate singleton inference.");
}

static void TestProvisionProvenance()
{
    var card = Card("oneiromancy", "Oneiromancy", CardKind.Special, 13);
    var observations = new[]
    {
        new ObservedCard(card, CardProvenance.Created, 1, DateTimeOffset.UtcNow),
        new ObservedCard(card, CardProvenance.ConfirmedStartingDeck, 1, DateTimeOffset.UtcNow),
    };
    Assert(
        StartingDeckRules.ConfirmedProvisionLowerBound(observations) == 13,
        "Created cards must not consume the inferred starting-deck provision budget.");
    Assert(
        StartingDeckRules.ProbableProvisionLowerBound(observations) == 13,
        "The probable provision floor should count a likely starting card once and exclude its created observation.");
    var partial = StartingDeckRules.EvaluateObservedDeck(observations);
    Assert(partial.GoldenNekker.State == ConstraintState.RuledOut, "A likely 13-provision starting card should rule out Golden Nekker.");
    Assert(partial.Devotion.State == ConstraintState.RuledOut, "A likely Neutral starting card should rule out Devotion.");

    var bronze = Card("bronze", "Bronze", CardKind.Unit, 5, "Nilfgaard");
    var twoCopies = new ObservedCard(
        bronze,
        CardProvenance.ProbableStartingDeck,
        0.95,
        DateTimeOffset.UtcNow,
        ObservedCopies: 2);
    Assert(StartingDeckRules.ProbableProvisionLowerBound([twoCopies]) == 10, "Two observed bronze copies must count twice against provisions.");
    Assert(StartingDeckRules.EvaluateObservedDeck([twoCopies]).Shupe.State == ConstraintState.RuledOut, "Two likely starting copies should rule out singleton decks.");
}

static void TestDeckInference()
{
    var cardA = Card("a", "Card A", CardKind.Unit, 8, "Nilfgaard");
    var cardB = Card("b", "Card B", CardKind.Unit, 9, "Nilfgaard");
    var matching = new DeckDefinition(
        "matching", "Matching", "Nilfgaard", "Enslave", 15,
        new[] { new DeckCard(cardA), new DeckCard(cardB) });
    var other = new DeckDefinition(
        "other", "Other", "Nilfgaard", "Imposter", 15,
        new[] { new DeckCard(cardB) });
    var observation = new ObservedCard(
        cardA,
        CardProvenance.ConfirmedStartingDeck,
        0.99,
        DateTimeOffset.UtcNow);

    var ranked = new DeckInferenceEngine().Rank(
        new[] { other, matching },
        new[] { observation },
        "Nilfgaard",
        "Enslave");
    Assert(ranked[0].Deck.Id == matching.Id, "Matching leader and observed card should rank first.");

    var cardC = Card("c", "Card C", CardKind.Unit, 10, "Nilfgaard");
    var cardD = Card("d", "Card D", CardKind.Unit, 4, "Nilfgaard");
    var cooccurring = new DeckDefinition(
        "cooccurring", "Co-occurring", "Nilfgaard", "Enslave", 15,
        new[] { new DeckCard(cardA), new DeckCard(cardC) });
    var unrelated = new DeckDefinition(
        "unrelated", "Unrelated", "Nilfgaard", "Imposter", 15,
        new[] { new DeckCard(cardD) });
    var likely = new DeckInferenceEngine().RankLikelyUnseenCards(
        new[] { cooccurring, unrelated },
        new[] { observation },
        "Nilfgaard",
        2);
    Assert(likely[0].Card.Id == cardC.Id, "A card co-occurring with the observation should outrank an unrelated card.");
    Assert(likely[0].PosteriorPresence > likely[1].PosteriorPresence, "Co-occurrence should increase posterior presence.");
    var leaders = new DeckInferenceEngine().RankLikelyLeaders(
        new[] { cooccurring, unrelated },
        new[] { observation },
        "Nilfgaard");
    Assert(leaders[0].Leader == "Enslave", "A leader attached to the matching corpus deck should rank first.");

    var goldenNekker = Card("gn", "Golden Nekker", CardKind.Special, 14);
    var gnObservation = new ObservedCard(
        goldenNekker,
        CardProvenance.ProbableStartingDeck,
        0.95,
        DateTimeOffset.UtcNow);
    var gnAssessment = StartingDeckRules.EvaluateObservedDeck([gnObservation]);
    var gnLegal = new DeckDefinition(
        "gn-legal", "GN legal", "Nilfgaard", "Imposter", 15,
        [new DeckCard(goldenNekker), new DeckCard(cardB)]);
    var gnIllegal = new DeckDefinition(
        "gn-illegal", "GN illegal", "Nilfgaard", "Imposter", 15,
        [new DeckCard(goldenNekker), new DeckCard(cardC)]);
    var constrained = new DeckInferenceEngine().CompatibleDecks(
        [gnIllegal, gnLegal],
        [gnObservation],
        "Nilfgaard",
        gnAssessment);
    Assert(constrained.Count == 1 && constrained[0].Id == gnLegal.Id, "GN evidence should remove cached decks with non-exempt 10+ cards.");
    var forecast = new DeckInferenceEngine().ForecastProvisions(
        constrained,
        [gnObservation],
        "Nilfgaard",
        gnAssessment);
    Assert(forecast.ExpectedUnseenCardsAtLeastTen == 0, "A constrained GN corpus should predict no non-exempt unseen 10+ card here.");
}

static void TestLeaderReplacementState()
{
    var now = DateTimeOffset.UtcNow;
    var anna = Card("202880", "Anna Henrietta", CardKind.Unit, 8, "Nilfgaard");
    var annaState = LeaderStateInference.Evaluate(
        "Enslave",
        "Nilfgaard",
        1,
        [new ObservedCard(anna, CardProvenance.ProbableStartingDeck, 0.9, now)],
        "Fruits of Ysgith",
        "Monsters");
    Assert(annaState.StartingFaction == "Nilfgaard", "Anna must not mutate the starting deck faction.");
    Assert(annaState.CurrentName == "Fruits of Ysgith" && annaState.CurrentFaction == "Monsters", "Anna should copy the opponent's base leader identity.");

    var renfri = BuiltInCardCatalog.Cards.Single();
    var renfriState = LeaderStateInference.Evaluate(
        "Imprisonment",
        "Nilfgaard",
        1,
        [new ObservedCard(renfri, CardProvenance.ProbableStartingDeck, 0.9, now)],
        null,
        null);
    Assert(renfriState.StartingFaction == "Nilfgaard", "Renfri must not mutate the starting deck faction.");
    Assert(renfriState.CurrentSource == CurrentLeaderSource.Renfri && renfriState.CurrentFaction == "Neutral", "Renfri should expose a neutral replacement ability state.");
}

static void TestPointEstimation()
{
    var estimator = new CardPointEstimator();
    var vanilla = new CardDefinition("v", "Vanilla", "Neutral", CardKind.Unit, 4, 6, AbilityText: "");
    var vanillaEstimate = estimator.Estimate(vanilla);
    Assert(vanillaEstimate.Status == PointEstimateStatus.ExactImmediate && vanillaEstimate.MaximumImmediatePoints == 6, "A vanilla unit should equal its printed body.");
    var unknown = estimator.Estimate(vanilla with { AbilityText = null });
    Assert(unknown.Status == PointEstimateStatus.Unsupported && unknown.MaximumImmediatePoints is null,
        "Missing metadata is not proof of a vanilla unit.");
    var unmodeled = estimator.Estimate(vanilla with { AbilityText = "Deploy: Boost self by 2." });
    Assert(unmodeled.Status == PointEstimateStatus.Unsupported && unmodeled.MaximumImmediatePoints is null && unmodeled.MinimumImmediatePoints is null,
        "An unrecognized ability must not silently become an exact printed-power maximum or minimum.");

    var damage = new CardDefinition(
        "d", "Direct damage", "Neutral", CardKind.Special, 7,
        AbilityText: "Damage a unit by 8.");
    var damageEstimate = estimator.Estimate(damage);
    Assert(damageEstimate.Status == PointEstimateStatus.BoundedImmediate && damageEstimate.MaximumImmediatePoints == 8, "Simple damage should expose its safe maximum.");

    var engine = new CardDefinition(
        "e", "Engine", "Skellige", CardKind.Unit, 6, 4,
        AbilityText: "At the end of your turn, boost self by 1 for each damaged unit on this row.");
    var engineEstimate = estimator.Estimate(engine);
    Assert(engineEstimate.Status == PointEstimateStatus.NeedsBoardContext && engineEstimate.MaximumImmediatePoints is null, "A board-dependent engine maximum must remain unknown.");
}

static void TestLiveDeckTracking()
{
    var user = new LiveDeckTracker(PlayerSide.User);
    var opponent = new LiveDeckTracker(PlayerSide.Opponent);
    var nilfgaard = Card("ng", "Nilfgaard card", CardKind.Unit, 5, "Nilfgaard");
    var monsters = Card("mo", "Monsters card", CardKind.Unit, 5, "Monsters");
    var neutral = Card("ne", "Neutral card", CardKind.Special, 5);
    var alternative = Card("alt", "Alternative", CardKind.Unit, 5, "Scoia'tael");
    var candidate = new MoveHistoryCardCandidate(new NormalizedRegion(0.045, 0.2, 0.097, 0.338), 0.60);

    Assert(user.Consider(candidate, Matches(nilfgaard, alternative), DateTimeOffset.UtcNow), "A very clear user card should be accepted.");
    Assert(user.Faction == "Nilfgaard", "A single-faction accepted card should lock the user's faction.");
    Assert(opponent.Observations.Count == 0, "User observations must not leak into the opponent tracker.");
    Assert(user.Consider(candidate, Matches(monsters, alternative), DateTimeOffset.UtcNow.AddSeconds(1)), "A decisive off-faction card should be retained as an exception.");
    Assert(user.Faction == "Nilfgaard" && !user.HasStableFaction, "One contradiction must make the faction provisional without immediately replacing it.");
    var secondMonster = Card("mo2", "Second Monsters card", CardKind.Unit, 6, "Monsters");
    Assert(user.Consider(candidate, Matches(secondMonster, alternative), DateTimeOffset.UtcNow.AddSeconds(2)), "Repeated contradictory faction evidence should be accepted.");
    Assert(user.Faction == "Monsters", "Two strong challenger cards should adapt the faction hypothesis.");
    Assert(user.FactionExceptionCount == 1, "The old Nilfgaard observation should remain visible as an exception after the switch.");
    Assert(user.Consider(candidate, Matches(neutral, monsters), DateTimeOffset.UtcNow.AddSeconds(3)), "A neutral card should remain legal.");
    Assert(user.SetObservedCopyLowerBound(neutral.Id, 2), "A second simultaneous bronze copy should raise the copy floor.");
    Assert(StartingDeckRules.ProbableProvisionLowerBound(user.DeckBuildingObservations) >= 10, "The provision floor should include both bronze copies.");

    var hoveredOpponentCandidate = candidate with
    {
        Region = new NormalizedRegion(0.109, 0.36, 0.161, 0.498),
        Score = 0.35,
    };
    var temerianDrummer = Card("drummer", "Temerian Drummer", CardKind.Unit, 5, "Northern Realms");
    Assert(
        opponent.Consider(
            hoveredOpponentCandidate,
            new[]
            {
                new CardArtMatch(temerianDrummer, 0.58, 0.63),
                new CardArtMatch(alternative, 0.73, 0.58),
            },
            DateTimeOffset.UtcNow,
            visuallyHovered: true),
        "A vertically aligned hover with a large visual margin should accept a moderately animated card.");
    Assert(opponent.Faction == "Northern Realms", "The hovered opponent card should establish the opponent faction independently.");

    var premiumTracker = new LiveDeckTracker(PlayerSide.Opponent);
    var endregaLarva = Card("larva", "Endrega Larva", CardKind.Unit, 5, "Monsters");
    Assert(
        premiumTracker.Consider(
            hoveredOpponentCandidate with { Score = 0.367 },
            new[]
            {
                new CardArtMatch(endregaLarva, 0.577, 0.48),
                new CardArtMatch(alternative, 0.715, 0.47),
            },
            DateTimeOffset.UtcNow),
        "A non-hovered premium thumbnail with a uniquely large margin should be accepted.");
    var ambiguousPremiumTracker = new LiveDeckTracker(PlayerSide.Opponent);
    Assert(
        !ambiguousPremiumTracker.Consider(
            hoveredOpponentCandidate with { Score = 0.437 },
            new[]
            {
                new CardArtMatch(endregaLarva, 0.625, 0.48),
                new CardArtMatch(alternative, 0.655, 0.47),
            },
            DateTimeOffset.UtcNow),
        "An ambiguous animated thumbnail must remain unresolved.");

    var priorTracker = new LiveDeckTracker(PlayerSide.User);
    priorTracker.SetFactionPrior("Skellige");
    Assert(priorTracker.Faction == "Skellige" && priorTracker.HasStableFaction, "An explicitly selected own deck should seed a stable but revisable faction prior.");
    Assert(priorTracker.Consider(candidate, Matches(monsters, alternative), DateTimeOffset.UtcNow), "Contradictory evidence should remain observable with an own-deck prior.");
    Assert(priorTracker.Faction == "Skellige" && priorTracker.FactionExceptionCount == 1, "One contradiction should not overturn a selected own-deck prior.");

    static IReadOnlyList<CardArtMatch> Matches(CardDefinition best, CardDefinition second) =>
        new[] { new CardArtMatch(best, 0.20, 0.83), new CardArtMatch(second, 0.45, 0.69) };
}

static void TestObservedDeckStore()
{
    var root = Path.Combine(Path.GetTempPath(), $"gwent-companion-store-{Guid.NewGuid():N}");
    try
    {
        var store = new ObservedDeckStore();
        foreach (var side in new[] { PlayerSide.User, PlayerSide.Opponent })
        {
            store.Save(root, new ObservedDeckRecord(
                "session",
                side,
                DateTimeOffset.UtcNow,
                "Nilfgaard",
                0,
                null,
                new[] { new StoredObservedCard("1", "Card", "Nilfgaard", 5, 0.9) }));
        }

        Assert(File.Exists(Path.Combine(root, "user", "session.json")), "The user cache file is missing.");
        Assert(File.Exists(Path.Combine(root, "opponent", "session.json")), "The opponent cache file is missing.");
        Assert(store.Load(root).Count == 2, "Both side-specific records should load.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}

static void TestWorkbookImports(string notes)
{
    var files = Directory.GetFiles(notes, "*.xlsx", SearchOption.TopDirectoryOnly);
    Assert(files.Length >= 3, "Expected the three supplied workbook files.");
    var indexes = files.Select(path => new WorkbookDeckIndexReader().Read(path)).ToArray();
    var entries = indexes.SelectMany(index => index.Entries).ToArray();
    Assert(entries.Length > 100, $"Expected more than 100 linked decks, found {entries.Length}.");
    Assert(entries.Any(entry => entry.Name.Contains("Eist", StringComparison.OrdinalIgnoreCase)), "Kerp's Eist entry was not imported.");
    Assert(entries.Any(entry => entry.Leader.Contains("Enslave", StringComparison.OrdinalIgnoreCase)), "Qcento's Enslave entry was not imported.");
    Assert(entries.Any(entry => entry.Name.Contains("Otkell", StringComparison.OrdinalIgnoreCase)), "Shinmiri's Otkell entry was not imported.");
    Assert(entries.All(entry => entry.DeckUri.Host.Contains("playgwent.com", StringComparison.OrdinalIgnoreCase)), "Unexpected non-PlayGwent deck URI.");
    Console.WriteLine($"  Imported {entries.Length:N0} deck links from {indexes.Sum(index => index.SheetNames.Count):N0} sheets.");
}

static void TestVisualCalibration(string gameRoot)
{
    var sessions = Path.Combine(gameRoot, "GwentCompanion", "sessions");
    var historyPlain = LoadFrame(Path.Combine(
        sessions,
        "20260826-104306",
        "frame-000001-104306132.jpg"));
    var historyHover = LoadFrame(Path.Combine(
        sessions,
        "20260826-104306",
        "frame-000021-104311143.jpg"));
    var ordinaryBoard = LoadFrame(Path.Combine(
        sessions,
        "20260826-103944",
        "frame-000093-104007398.jpg"));
    var questsPanel = LoadFrame(Path.Combine(
        sessions,
        "20260826-103944",
        "frame-000106-104010647.jpg"));
    var redraw = LoadFrame(Path.Combine(
        sessions,
        "20260826-115042",
        "frame-000072-115100432.jpg"));
    var createChoice = LoadFrame(Path.Combine(
        sessions,
        "20260826-221352",
        "frame-001281-221600528.jpg"));
    var playedRunestone = LoadFrame(Path.Combine(
        sessions,
        "20260826-221352",
        "frame-001303-221602728.jpg"));

    var detector = new GwentVisualStateDetector();
    var plain = detector.Analyze(historyPlain);
    var hover = detector.Analyze(historyHover);
    var board = detector.Analyze(ordinaryBoard);
    var quests = detector.Analyze(questsPanel);
    var redrawObservation = detector.Analyze(redraw);
    var choiceObservation = detector.Analyze(createChoice);
    var playedObservation = detector.Analyze(playedRunestone);

    Assert(plain.View == GwentViewKind.MoveHistory, $"History panel was missed ({plain.HistoryButtonConfidence:F3}).");
    Assert(hover.View == GwentViewKind.MoveHistory, $"Hovered history panel was missed ({hover.HistoryButtonConfidence:F3}).");
    Assert(hover.HasCardTooltip, $"History card tooltip was missed ({hover.TooltipConfidence:F3}).");
    Assert(board.View == GwentViewKind.Board, $"Ordinary board was mistaken for history ({board.HistoryButtonConfidence:F3}).");
    Assert(quests.View == GwentViewKind.Board, $"Quests panel was mistaken for history ({quests.HistoryButtonConfidence:F3}).");
    Assert(redrawObservation.IsCardSelectionOverlay, $"Redraw overlay was missed ({redrawObservation.CardSelectionConfidence:F3}).");
    Assert(choiceObservation.IsCardSelectionOverlay, $"Create choice overlay was missed ({choiceObservation.CardSelectionConfidence:F3}).");
    Assert(!playedObservation.IsCardSelectionOverlay, "The player card-play lane was mistaken for a selection overlay.");
    var playedCandidates = new EnlargedCardPlayDetector().LocateAll(playedRunestone);
    Assert(
        playedCandidates.Count == 1 && playedCandidates[0].SideHint == PlayerSide.User,
        "The recorded Runestone was not isolated in the player card-play lane.");
    Console.WriteLine(
        $"  History confidence {plain.HistoryButtonConfidence:F3}; " +
        $"hover tooltip confidence {hover.TooltipConfidence:F3}.");
}

static void TestPlayGwentDeckParser()
{
    const string json = """
        {
          "deck": {
            "hash": "test-hash",
            "modified": "2026-08-01T12:30:00-04:00",
            "faction": { "slug": "northernrealms" },
            "leader": {
              "localizedName": "Mobilization",
              "provisionsCost": 15
            },
            "cards": [
              {
                "id": 122302,
                "localizedName": "Reinforced Ballista",
                "faction": { "slug": "northernrealms" },
                "type": "unit",
                "cardGroup": "bronze",
                "power": 3,
                "provisionsCost": 5,
                "repeatCount": 1,
                "categoryName": "Machine, Siege Engine",
                "secondaryFactions": [{ "value": 2 }],
                "tooltip": [[
                  { "type": "keyword", "value": "Order" },
                  { "type": "text", "value": ": Damage an enemy unit by 1." }
                ]]
              },
              {
                "id": 122103,
                "localizedName": "John Natalis",
                "faction": { "slug": "northernrealms" },
                "type": "unit",
                "cardGroup": "gold",
                "power": 1,
                "provisionsCost": 7,
                "repeatCount": 0,
                "categoryName": "Human, Soldier"
              }
            ]
          }
        }
        """;
    var encoded = System.Net.WebUtility.HtmlEncode(json);
    var html = $"<div data-state='{encoded}'></div>";
    var source = new Uri("https://www.playgwent.com/en/decks/test-hash");
    var deck = new PlayGwentDeckPageParser().Parse(html, source, "Calibration deck", 7);

    Assert(deck.Id == "test-hash", "Deck hash was not parsed.");
    Assert(deck.Faction == "Northern Realms", "Faction was not normalized.");
    Assert(deck.Leader == "Mobilization" && deck.LeaderProvisionBonus == 15, "Leader data was not parsed.");
    Assert(deck.CardCount == 3, "Repeat count was not converted to a total copy count.");
    Assert(deck.ProvisionTotal == 17, "Provision total did not include duplicate cards.");
    Assert(deck.Cards[0].Card.HasCategory("Siege Engine"), "Card categories were not split.");
    Assert(deck.Cards[0].Card.SecondaryFactions.Contains("Nilfgaard"), "Secondary faction was not parsed.");
    Assert(deck.Cards[0].Card.AbilityText == "Order: Damage an enemy unit by 1.", "Card ability text was not flattened.");
    Assert(deck.Cards[1].Card.IsGold, "Card rarity was not parsed.");
}

static PixelFrame LoadFrame(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("A required calibration frame is missing.", path);
    }

    using var stream = File.OpenRead(path);
    var bitmap = BitmapFrame.Create(
        stream,
        BitmapCreateOptions.PreservePixelFormat,
        BitmapCacheOption.OnLoad);
    bitmap.Freeze();
    return BitmapFrameAdapter.ToPixelFrame(bitmap);
}

static bool IsLikelyHovered(MoveHistoryCardCandidate candidate, GwentVisualObservation observation)
{
    if (!observation.HasCardTooltip || observation.TooltipRegion is not { } tooltip)
    {
        return false;
    }

    var cardCenter = (candidate.Region.Top + candidate.Region.Bottom) / 2;
    var tooltipCenter = (tooltip.Top + tooltip.Bottom) / 2;
    return Math.Abs(cardCenter - tooltipCenter) <= 0.12;
}

static void TestCardArtMatcher(string gameRoot, string notes)
{
    var entries = Directory.GetFiles(notes, "*.xlsx", SearchOption.TopDirectoryOnly)
        .SelectMany(path => new WorkbookDeckIndexReader().Read(path).Entries)
        .ToArray();
    var decks = new PlayGwentDeckCacheService().LoadCached(
        entries,
        Path.Combine(gameRoot, "GwentCompanion", "cache", "decks"),
        500);
    var cards = decks
        .SelectMany(deck => deck.Cards)
        .Select(item => item.Card)
        .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();
    var portraits = Path.Combine(gameRoot, "GwentCompanion", "cache", "portraits");
    var references = cards
        .Select(card => (Card: card, Path: Path.Combine(portraits, $"{card.Id}.jpg")))
        .Where(item => File.Exists(item.Path))
        .Select(item => new CardArtReference(item.Card, VisualDescriptor.Create(LoadFrame(item.Path))))
        .ToArray();
    var history = LoadFrame(Path.Combine(
        gameRoot,
        "GwentCompanion",
        "sessions",
        "20260826-112116",
        "frame-000773-112429731.jpg"));
    var located = new MoveHistoryCardLocator().Locate(history);
    Console.WriteLine(
        "  Located: " + string.Join(", ", located.Select(item => $"{item.Region.Left:F3},{item.Region.Top:F3}:{item.Score:F3}")));
    Assert(located.Count > 0, "No card thumbnail was located in the client-area history panel.");
    var matcher = new CardArtMatcher(references);
    var matches = located
        .Select(candidate => matcher.Rank(history, candidate.Region, 10))
        .MinBy(ranked => ranked[0].Distance)!;
    Console.WriteLine("  " + string.Join(", ", matches.Take(5).Select(match => $"{match.Card.Name}:{match.Distance:F3}")));
    Assert(
        string.Equals(matches[0].Card.Name, "Fiend", StringComparison.OrdinalIgnoreCase),
        "Fiend was not the strongest full-corpus match in the live client-area capture.");

    var populatedHistory = LoadFrame(Path.Combine(
        gameRoot,
        "GwentCompanion",
        "sessions",
        "20260826-112116",
        "frame-001327-112648242.jpg"));
    var populatedCandidates = new MoveHistoryCardLocator().Locate(populatedHistory);
    Assert(populatedCandidates.Count >= 3, $"Expected at least three populated history entries; found {populatedCandidates.Count}.");
}

static CardDefinition Card(
    string id,
    string name,
    CardKind kind,
    int provision,
    string faction = "Neutral",
    IReadOnlySet<string>? categories = null) =>
    new(id, name, faction, kind, provision, CardCategories: categories);

static string ResolveNotes(string[] arguments)
{
    if (arguments.Length > 0 && Directory.Exists(arguments[0]))
    {
        return Path.GetFullPath(arguments[0]);
    }

    string? fallback = null;
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "notes");
            if (Directory.Exists(candidate))
            {
                // The project also has a documentation notes folder containing one
                // synchronized workbook. Prefer the user's complete supplied source
                // when both folders are discoverable from the test executable.
                fallback ??= candidate;
                if (Directory.GetFiles(candidate, "*.xlsx", SearchOption.TopDirectoryOnly).Length >= 3)
                {
                    return candidate;
                }
            }

            current = current.Parent;
        }
    }

    if (fallback is not null) return fallback;
    throw new DirectoryNotFoundException("Pass the GWENT notes directory as the first test argument.");
}

static string ResolveGameRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Gwent.exe")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the GWENT installation.");
}

static string ResolveProjectRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GwentCompanion.sln")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests", "recording-validation")))
                return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("Could not locate the Gwent Vision repository.");
}

static void Run(string name, Action test, ICollection<string> failures)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
