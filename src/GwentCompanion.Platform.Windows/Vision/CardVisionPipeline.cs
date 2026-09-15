using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using GwentCompanion.Platform.Windows.Capture;
using OpenCvSharp;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record CardVisionResult(DateTimeOffset SampledAt, GwentVisualObservation Screen,
    IReadOnlyList<CardSighting> Sightings, IReadOnlyList<VisionEvidenceEvent> Events, bool BoardWasScanned,
    bool ArtworkWasScanned = true, CreatedCardDescription? Description = null, VisibleZoneInspection? GraveyardInspection = null,
    DevotionVisualCue? DevotionCue = null, CardDefinition? HoveredCard = null, bool HoverInPlayerHand = false,
    IReadOnlyList<CardStateMeasurement>? CardMeasurements = null, RuntimeCardValue? RuntimeValue = null,
    LeaderAbilityReading? OpponentLeader = null, CultistInfusionReading? CultistInfusion = null,
    DeckPlayChoiceReading? DeckPlayChoices = null, bool? PointerInPlayerHand = null);

public sealed record PreparedVisionFrame(PixelFrame Frame, DateTimeOffset SampledAt, GwentVisualObservation Screen,
    IReadOnlyList<CardSighting> Titles, bool NeedsArtwork = false, CreatedCardDescription? Description = null, VisibleZoneInspection? GraveyardInspection = null,
    DevotionVisualCue? DevotionCue = null, CardDefinition? HoveredCard = null, RuntimeCardValue? RuntimeValue = null,
    bool HoverInPlayerHand = false)
{
    public LeaderAbilityReading? OpponentLeader { get; init; }
    public CultistInfusionReading? CultistInfusion { get; init; }
    public DeckPlayChoiceReading? DeckPlayChoices { get; init; }
    public bool? PointerInPlayerHand { get; init; }
    public CardVisionResult TextResult => new(SampledAt, Screen, Titles, [], false, false, Description, GraveyardInspection, DevotionCue, HoveredCard, HoverInPlayerHand, RuntimeValue: RuntimeValue, OpponentLeader: OpponentLeader, CultistInfusion: CultistInfusion, DeckPlayChoices: DeckPlayChoices, PointerInPlayerHand: PointerInPlayerHand);
}

public interface ICardVisionPipeline
{
    Task<PreparedVisionFrame> PrepareAsync(PixelFrame frame, DateTimeOffset sampledAt);
    Task<PreparedVisionFrame> PrepareResultsAsync(PixelFrame frame, DateTimeOffset sampledAt) =>
        PrepareAsync(frame, sampledAt);
    CardVisionResult RecognizePrepared(PreparedVisionFrame prepared, bool includeBoard);
    CardVisionResult Commit(CardVisionResult result);
}

/// <summary>The same pixel-to-evidence pipeline is used in the app and session replay.</summary>
public sealed class CardVisionPipeline(IEnumerable<(CardDefinition Card, string Path)> references,
    IEnumerable<CardDefinition>? catalog = null, string? featureCacheDirectory = null,
    VisionReferenceScope referenceScope = VisionReferenceScope.FullCatalog, ScreenStateRecognizer? screen = null,
    bool allowStreamResolution = false) : IDisposable, ICardVisionPipeline
{
    private readonly ScreenStateRecognizer _screen = screen ?? new();
    private readonly FeatureCardRecognizer _cards = new(references, featureCacheDirectory, scope: referenceScope);
    private CardFrameRecognizer? _fallback;
    private int _fallbackGeneration = -1;
    private readonly MatchVisionLedger _ledger = new();
    private readonly OpponentHudRecognizer _hud = new();
    private readonly PostMatchMmrRecognizer _postMatchMmr = new();
    private readonly PostMatchScoreRecognizer _postMatchScores = new();
    private readonly OakcrittersEffectRecognizer _oakcritters = new();
    private readonly BoardPowerReader _boardPower = new();
    private DateTimeOffset _lastInspectionAt;
    private readonly HoverCardRecognizer _hover = new(catalog ?? references.Select(item => item.Card));
    private readonly CardDefinition[] _catalog = (catalog ?? references.Select(item => item.Card)).DistinctBy(card => card.Id).ToArray();
    private LeaderAbilityRecognizer? _leaders;
    private DeckPlayChoiceRecognizer? _deckChoices;
    private DeckPlayResolutionTracker? _deckResolutions;
    private readonly PreviewTitleRecognizer _titles = new(catalog ?? references.Select(item => item.Card));
    private readonly CardAppearanceFamilies _families = new(catalog ?? references.Select(item => item.Card));
    private string[] _knownPlayerDeck = [];
    public Action<string>? Trace { set => _cards.Trace = value; }
    public Action<string>? HoverTrace { set => _hover.Trace = value; }
    public int CachedReferenceImages => _cards.CachedImages;
    public int ComputedReferenceImages => _cards.ComputedImages;
    public double ReferenceLoadMilliseconds => _cards.ReferenceLoadMilliseconds;
    public double MatcherTrainMilliseconds => _cards.MatcherTrainMilliseconds;
    public bool UseTextCache { set => _screen.UseTextCache = value; }
    public int OcrCalls => _screen.OcrCalls;
    public int OcrCacheHits => _screen.OcrCacheHits;
    public int CachedTextBytes => _screen.CachedTextBytes;
    public VisionStageTimings Timings { get; } = new();
    public IReadOnlyDictionary<string, VisionStageTimings.StageTiming> FeatureTimings => _cards.Timings.Snapshot();

    public async Task<CardVisionResult> AnalyzeAsync(PixelFrame frame, DateTimeOffset sampledAt, bool includeBoard = true)
        => Commit(RecognizePrepared(await PrepareAsync(frame, sampledAt).ConfigureAwait(false), includeBoard));

    /// <summary>
    /// Result panels need the screen, round-score and rank/MMR readers only. This
    /// path deliberately skips HUD, leader, title, hover, choice and artwork work
    /// so several independent numeric votes can catch up before a short panel ends.
    /// </summary>
    public async Task<PreparedVisionFrame> PrepareResultsAsync(PixelFrame frame, DateTimeOffset sampledAt)
    {
        var preparedFrame = VisionFrameNormalizer.Prepare(frame, allowStreamResolution);
        if (!preparedFrame.Supported)
        {
            var unsupported = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
                MatchHudVisible: false, FrameGeometrySupported: false, FrameGeometryWarning: preparedFrame.Warning);
            return new PreparedVisionFrame(frame, sampledAt, unsupported, []);
        }
        frame = preparedFrame.Frame;
        var clock = System.Diagnostics.Stopwatch.GetTimestamp();
        var screen = await _screen.AnalyzeAsync(frame).ConfigureAwait(false);
        clock = Timings.Record("Screen", clock);
        screen = await _postMatchScores.ReadAsync(frame, screen, sampledAt, _screen).ConfigureAwait(false);
        screen = await _postMatchMmr.ReadAsync(frame, screen, sampledAt, _screen).ConfigureAwait(false);
        Timings.Record("LeaderAndResults", clock);
        return new PreparedVisionFrame(frame, sampledAt, screen, []);
    }

    // Only the fast worker calls this; OCR engine instances are never shared concurrently.
    public async Task<PreparedVisionFrame> PrepareAsync(PixelFrame frame, DateTimeOffset sampledAt)
    {
        var preparedFrame = VisionFrameNormalizer.Prepare(frame, allowStreamResolution);
        if (!preparedFrame.Supported)
        {
            var unsupported = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
                MatchHudVisible: false, FrameGeometrySupported: false, FrameGeometryWarning: preparedFrame.Warning);
            return new PreparedVisionFrame(frame, sampledAt, unsupported, []);
        }
        frame = preparedFrame.Frame;
        // Text uses the retained capture resolution. Shrinking first can erase thin
        // capitals. Only captures above the bounded 1080p analysis ceiling are
        // reduced; this prevents 4K/8K SIFT allocation spikes during gameplay.
        var clock = System.Diagnostics.Stopwatch.GetTimestamp();
        var screen = await _screen.AnalyzeAsync(frame).ConfigureAwait(false);
        clock = Timings.Record("Screen", clock);
        screen = await _hud.ReadAsync(frame, screen, sampledAt, _screen).ConfigureAwait(false);
        clock = Timings.Record("Hud", clock);
        screen = await _postMatchScores.ReadAsync(frame, screen, sampledAt, _screen).ConfigureAwait(false);
        screen = await _postMatchMmr.ReadAsync(frame, screen, sampledAt, _screen).ConfigureAwait(false);
        _leaders ??= new LeaderAbilityRecognizer(_catalog);
        var opponentLeader = _leaders.Observe(frame, screen, sampledAt);
        clock = Timings.Record("LeaderAndResults", clock);
        var titles = (await _titles.RecognizeAsync(frame, screen, _screen).ConfigureAwait(false)).Select(_families.Normalize).ToArray();
        clock = Timings.Record("PreviewTitles", clock);
        if (titles.Any(sight => sight.Card.Id == CultistSynergyTracker.ScenarioId || sight.Card.HasCategory("Cultist")))
            _hover.ReadCultistInfusions = true;
        var hover = await _hover.ReadAsync(frame, screen, sampledAt, _screen).ConfigureAwait(false);
        clock = Timings.Record("Hover", clock);
        VisibleZoneInspection? inspection = null;
        if (screen.IsCardSelectionOverlay && screen.ScreenHeader?.Contains("GRAVEYARD", StringComparison.OrdinalIgnoreCase) == true &&
            sampledAt - _lastInspectionAt >= TimeSpan.FromSeconds(2))
        {
            _lastInspectionAt = sampledAt;
            var lines = await _screen.ReadLinesAsync(frame, new NormalizedRegion(.04, .12, .96, .96), scale: 1).ConfigureAwait(false);
            var text = string.Join('\n', lines.Select(line => line.Text));
            inspection = VisibleZoneInspectionReader.Parse(text, _catalog);
        }
        var devotion = _oakcritters.Observe(frame, screen, titles, sampledAt);
        // Player-hand tooltips reach the bottom HUD/hand band. Keep this conservative:
        // it is used for bookkeeping (for example Hen Gaidth), never to claim a play.
        var hoverInPlayerHand = IsGeometricPlayerHandHover(hover.Card, screen, hover.TitleRegion);
        clock = Timings.Record("Inspection", clock);
        var choices = await (_deckChoices ??= new(references, _catalog)).ReadAsync(frame, screen, _screen).ConfigureAwait(false);
        Timings.Record("Choices", clock);
        return new PreparedVisionFrame(frame, sampledAt, screen, titles, _titles.HasUnresolvedHeader, hover.Description, inspection, devotion,
            hover.Card, hover.RuntimeValue, hoverInPlayerHand) { OpponentLeader = opponentLeader, CultistInfusion = hover.CultistInfusion,
                DeckPlayChoices = choices };
    }

    // Only the artwork worker calls this. No ledger state is changed until chronological commit.
    public CardVisionResult RecognizePrepared(PreparedVisionFrame prepared, bool includeBoard)
    {
        var frame = prepared.Frame; var sampledAt = prepared.SampledAt; var screen = prepared.Screen;
        var titles = prepared.Titles; var description = prepared.Description;
        if (!screen.FrameGeometrySupported || screen.IsCardSelectionOverlay) return prepared.TextResult;
        var scanBoard = ShouldScanBoard(screen, includeBoard);
        // Recording resolution is independent of the real-time analysis budget.
        if (!scanBoard && frame.Width > 960)
        {
            using var original = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
            using var resized = new Mat();
            Cv2.Resize(original, resized, new Size(960, (int)Math.Round(frame.Height * 960.0 / frame.Width)), 0, 0, InterpolationFlags.Area);
            var pixels = new byte[resized.Width * resized.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
            frame = new PixelFrame(resized.Width, resized.Height, pixels);
        }
        // Recognizing one lane must not suppress an artwork-only preview in the other.
        if (!scanBoard && titles.Where(item => !item.IsSupplementalTitle).Select(item => item.Side).Distinct().Count() == 2)
            return prepared.TextResult;
        var clock = System.Diagnostics.Stopwatch.GetTimestamp();
        var features = _cards.Recognize(frame, screen, scanBoard);
        clock = Timings.Record(scanBoard ? "BoardFeatures" : "PreviewFeatures", clock);
        // Reuse the exact appearance descriptors already computed by the SIFT loader.
        // This avoids a second image decode/read for every reference at startup.
        var artReferences = _cards.ArtReferences;
        RefreshFallback(artReferences, sampledAt);
        _fallback?.Expire(sampledAt);
        var fallback = _fallback?.Recognize(frame, screen, scanBoard, features)
            .Where(sighting => _cards.CanUseFallback(sighting.Card.Id, sighting.Side)) ?? [];
        var artwork = features.Concat(fallback).ToArray();
        Timings.Record(scanBoard ? "BoardFallback" : "PreviewFallback", clock);
        var sightings = SuppressImpossibleBoardZones(
            SuppressBoardTooltipPreviews(MergePreviewEvidence(artwork, titles), screen, prepared.HoveredCard));
        screen = BoardOcclusionVerifier.Refine(screen, sightings, prepared.HoveredCard);
        return new CardVisionResult(sampledAt, screen, sightings, [], scanBoard, Description: description,
            GraveyardInspection: prepared.GraveyardInspection, DevotionCue: prepared.DevotionCue, HoveredCard: prepared.HoveredCard, HoverInPlayerHand: prepared.HoverInPlayerHand,
            CardMeasurements: scanBoard ? _boardPower.Observe(frame, sampledAt, sightings, screen) : null, RuntimeValue: prepared.RuntimeValue,
            OpponentLeader: prepared.OpponentLeader, CultistInfusion: prepared.CultistInfusion, DeckPlayChoices: prepared.DeckPlayChoices,
            PointerInPlayerHand: prepared.PointerInPlayerHand);
    }

    public static bool ShouldScanBoard(GwentVisualObservation screen, bool requested) =>
        requested && screen.FrameGeometrySupported && screen.MatchHudVisible == true && !screen.IsCardSelectionOverlay;

    // A few matching features identify artwork, not necessarily an action. During
    // dealing, moving hand cards briefly cross exactly the player's preview lane.
    // Clear written titles / strong art remain immediate; weak artwork must recur.
    public static CardSighting RequirePreviewCorroboration(CardSighting sight) =>
        sight.Source == CardSightSource.PlayPreview && sight.Distance > .30
            ? sight with { NeedsTemporalConfirmation = true } : sight;

    public static IReadOnlyList<CardSighting> MergePreviewEvidence(IReadOnlyList<CardSighting> artwork, IReadOnlyList<CardSighting> titles)
    {
        // Legacy exact titles retain their priority. The optional styled fallback
        // must not replace an already accepted, immediate artwork preview (matching
        // or conflicting) with a weaker observation that delays the existing path.
        var effectiveTitles = titles.Where(title => !title.IsSupplementalTitle || !artwork.Any(art =>
            art.Source == CardSightSource.PlayPreview && art.Side == title.Side && !RequirePreviewCorroboration(art).NeedsTemporalConfirmation)).ToArray();
        return artwork.Where(item => item.Source != CardSightSource.PlayPreview || !effectiveTitles.Any(title => title.Side == item.Side))
            .Concat(effectiveTitles).Select(RequirePreviewCorroboration).ToArray();
    }

    /// <summary>
    /// The expanded tooltip for a selected board card can occupy the same fixed
    /// lower/right lane as a user play preview. An exact title is still identity
    /// evidence, but it is not a second action when matching artwork is already on
    /// that side of the board and the detected tooltip overlaps the preview lane.
    /// </summary>
    public static IReadOnlyList<CardSighting> SuppressBoardTooltipPreviews(IReadOnlyList<CardSighting> sightings,
        GwentVisualObservation screen, CardDefinition? hoveredCard)
    {
        if (hoveredCard is null || !screen.HasCardTooltip || screen.TooltipRegion is not { } tooltip) return sightings;
        static bool Overlaps(NormalizedRegion left,NormalizedRegion right) =>
            left.Left<right.Right && left.Right>right.Left && left.Top<right.Bottom && left.Bottom>right.Top;
        return sightings.Where(sighting => sighting.Source!=CardSightSource.PlayPreview || sighting.Card.Id!=hoveredCard.Id ||
            !Overlaps(sighting.Region,tooltip) || !sightings.Any(board=>board.Source==CardSightSource.Board &&
                board.Side==sighting.Side && board.Card.Id==sighting.Card.Id && board.Distance<=.45)).ToArray();
    }

    /// <summary>
    /// Artwork matching establishes identity, but it cannot make a non-permanent
    /// special into a settled board object. Low-resolution compression can make a
    /// special in the hand or transition lane align weakly with a board crop; keep
    /// genuine preview evidence while rejecting the impossible zone assignment.
    /// </summary>
    public static IReadOnlyList<CardSighting> SuppressImpossibleBoardZones(IReadOnlyList<CardSighting> sightings) =>
        sightings.Where(sighting => sighting.Source != CardSightSource.Board || sighting.Card.Kind != CardKind.Special).ToArray();

    public CardVisionResult Commit(CardVisionResult result)
    {
        _cards.ObserveRoundBoundary(result.Screen);
        // Preserve a short-lived exact settled seed so a later scheduled board
        // scan can run the known-player self-thinning pair search even if the
        // ordinary feature pass no longer sees the small animated card.
        _fallback?.ObserveKnownPlayerBoardCandidates(result.SampledAt, result.Sightings);
        // An exact non-overlay hover title is safe to use as a bounded search
        // candidate even when its controller/zone is unresolved. It is not an
        // event or origin claim; subsequent artwork still has to pass every visual
        // and side-geometry gate. This helps cards first exposed by a brief summon.
        if (!result.Screen.IsCardSelectionOverlay && result.HoveredCard is { } hovered)
        {
            // Board hover geometry does not identify the controller. With a
            // pinned player deck, admitting every opponent hover into both
            // candidate indices can later relabel similar player-side art. A
            // confirmed player-hand hover is controller-specific; otherwise
            // preserve only known or independently established player identities.
            if (result.HoverInPlayerHand || result.PointerInPlayerHand == true ||
                _cards.CanSeedPlayerCandidateFromAmbiguousHover(hovered.Id))
                _cards.ObservePlayerCards([hovered.Id]);
            _cards.ObserveOpponentCards([hovered.Id]);
        }
        var events = _ledger.Observe(result.SampledAt, result.Screen, result.Sightings, result.BoardWasScanned, result.HoveredCard,
            result.ArtworkWasScanned, result.HoverInPlayerHand, result.PointerInPlayerHand);
        var resolved = (_deckResolutions ??= new(_catalog)).Observe(result.SampledAt, result.Screen, events, result.DeckPlayChoices);
        var committed = events.Concat(resolved).ToArray();
        _leaders?.ObserveEvents(committed);
        var playerCandidates = CandidateIds(committed, PlayerSide.User).Distinct(StringComparer.Ordinal).ToArray();
        _cards.ObservePlayerCards(playerCandidates);
        foreach (var source in committed.Where(item => item.Sighting.Side == PlayerSide.Opponent &&
                     item.Sighting.Source == CardSightSource.PlayPreview).Select(item => item.Sighting.Card))
        {
            var recurring = CompanionCardRules.RecurringSummonPool(source, _catalog,
                result.OpponentLeader?.Card.Faction);
            var named = CompanionCardRules.NamedDeckSummonTargets(source, _catalog);
            _cards.ObserveOpponentSummonSource(source, recurring, named);
        }
        // A repeated exact far-side tooltip can recover a persistent recurring
        // source whose play animation was missed. Board evidence does not activate
        // ordinary Deploy/Create outputs, but this source must expand the bounded
        // target-art index so subsequent printed summons remain detectable.
        foreach (var source in committed.Where(item => item.Sighting.Side == PlayerSide.Opponent &&
                     item.Sighting.Source == CardSightSource.Board &&
                     CompanionCardRules.IsRecurringDeckSummonSource(item.Sighting.Card))
                 .Select(item => item.Sighting.Card))
        {
            var recurring = CompanionCardRules.RecurringSummonPool(source, _catalog,
                result.OpponentLeader?.Card.Faction);
            var named = CompanionCardRules.NamedDeckSummonTargets(source, _catalog);
            _cards.ObserveOpponentSummonSource(source, recurring, named);
        }
        _cards.ObserveOpponentCards(CandidateIds(committed, PlayerSide.Opponent).Distinct(StringComparer.Ordinal));
        // Loading the newly measured identity can advance the reference generation.
        // Refresh the fallback before handing it the committed action.
        var artReferences = _cards.ArtReferences;
        RefreshFallback(artReferences, result.SampledAt);
        _fallback?.ObserveEvents(committed);
        return result with { Events = committed };
    }

    private void RefreshFallback(IReadOnlyList<CardArtReference> artReferences, DateTimeOffset at)
    {
        if (_fallback is not null && _fallbackGeneration == _cards.ReferenceGeneration) return;
        var previous = _fallback;
        // A fresh install may have no pinned player deck or projected opponent
        // list yet. One title-confirmed identity is still sufficient for the
        // strict recent-card fallback; its geometry and negative-frame gates do
        // not require an unrelated second reference.
        _fallback = artReferences.Count > 0 ? new(new CardArtMatcher(artReferences)) : null;
        _fallbackGeneration = _cards.ReferenceGeneration;
        if (_fallback is not null)
        {
            _fallback.SetKnownPlayerDeck(_knownPlayerDeck);
            if (previous is not null) _fallback.CopyRecentFrom(previous, at);
        }
    }

    private IEnumerable<string> CandidateIds(IEnumerable<VisionEvidenceEvent> events, PlayerSide side)
    {
        foreach (var evidence in events.Where(item => item.Sighting.Side == side &&
                     item.Sighting.Source is CardSightSource.PlayPreview or CardSightSource.DeckReveal))
        {
            yield return evidence.Sighting.Card.Id;
            // Merely revealing a card does not trigger its Deploy/Order outputs.
            // A committed play, however, should arm explicitly named Spawn,
            // Summon and Transform results on either side. Candidate-scoped live
            // recognition otherwise cannot see tokens such as Savolla's Frightener.
            if (evidence.Sighting.Source == CardSightSource.PlayPreview)
            {
                foreach (var target in CompanionCardRules.TriggeredAutomaticPairTargets(evidence.Sighting.Card)) yield return target;
                foreach (var target in NamedOutputs(evidence.Sighting.Card)) yield return target;
            }
        }
    }

    private IEnumerable<string> NamedOutputs(CardDefinition source)
    {
        var text = source.AbilityText ?? "";
        if (!Regex.IsMatch(text, @"\b(?:Spawn|Summon|Transform(?:\s+self)?\s+into)\b", RegexOptions.IgnoreCase)) yield break;
        foreach (var target in _catalog.Where(card => card.Id != source.Id && card.Name.Length >= 4 &&
                     Regex.IsMatch(text, @"\b" + Regex.Escape(card.Name) + @"\b", RegexOptions.IgnoreCase)))
            yield return target.Id;
    }

    public void Reset() { _ledger.Reset(); _deckResolutions?.Reset(); _hud.Reset(); _postMatchMmr.Reset(); _postMatchScores.Reset(); _oakcritters.Reset(); _boardPower.Reset(); _leaders?.Reset(); _lastInspectionAt = default; _hover.Reset(); _hover.ReadCultistInfusions = false; Timings.Reset(); _cards.Timings.Reset(); _cards.ResetObservedPlayerCards(); _cards.ResetObservedOpponentCards(); _cards.ResetTransientCandidates(); _fallback?.Reset(); }
    public void SetKnownPlayerDeck(IEnumerable<string> cardIds)
    {
        _knownPlayerDeck = cardIds.Distinct(StringComparer.Ordinal).ToArray();
        _cards.SetKnownPlayerDeck(_knownPlayerDeck);
        _fallback?.SetKnownPlayerDeck(_knownPlayerDeck);
    }
    public void SetLikelyOpponentCards(IEnumerable<string> cardIds) => _cards.SetLikelyOpponentCards(cardIds);
    public static bool IsGeometricPlayerHandHover(CardDefinition? card, GwentVisualObservation screen,
        NormalizedRegion? titleRegion = null) => card is not null && !screen.IsCardSelectionOverlay && screen.MatchHudVisible == true &&
        (screen.TooltipRegion is { Bottom: >= .88, Top: >= .45 } ||
         titleRegion is { Top: >= .64, Bottom: <= .82 });
    public void Dispose() { _cards.Dispose(); _screen.Dispose(); _oakcritters.Dispose(); _leaders?.Dispose(); }

    private static PixelFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
    }
}
