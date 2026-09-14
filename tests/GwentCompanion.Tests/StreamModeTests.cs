using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

internal static class StreamModeTests
{
    public static void Run()
    {
        SourceIdentity(); DelimitedImport(); WorkbookImport(); Segmentation(); TransitionGates(); OverlayQuality(); RoundPrelude(); ValidationPrivacy(); Storage(); LowResolutionProfile();
        Console.WriteLine("PASS Stream mode: stable IDs, sheet import, game splitting, isolated idempotent storage, low-resolution profile.");
    }

    private static void SourceIdentity()
    {
        Equal("youtube:dQw4w9WgXcQ", Source("https://youtu.be/dQw4w9WgXcQ?t=20").Key, "short YouTube URL");
        Equal("youtube:dQw4w9WgXcQ", Source("https://www.youtube.com/watch?v=dQw4w9WgXcQ&utm_source=test").Key, "watch URL");
        Equal("twitch:123456789", Source("https://twitch.tv/videos/123456789?filter=archives").Key, "Twitch VOD");
        var first = Source("https://example.com/video?id=7&utm_source=a");
        var second = Source("https://example.com/video?utm_source=b&id=7");
        Equal(first.Key, second.Key, "generic tracking normalization");
        if (!first.CanonicalUri.Query.Contains("id=7", StringComparison.Ordinal)) throw new Exception("Functional query was removed.");
    }

    private static void DelimitedImport()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "links.csv");
            File.WriteAllText(path, "name,url\nA,https://youtu.be/dQw4w9WgXcQ\nB,https://youtube.com/watch?v=dQw4w9WgXcQ\nC,https://twitch.tv/videos/123456789");
            var rows = StreamLinkImportReader.Read(path);
            Equal(2, rows.Count, "CSV source deduplication");
            Equal(2, rows[0].Row, "CSV provenance row");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WorkbookImport()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "links.xlsx");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Videos" sheetId="1" r:id="rId1"/></sheets></workbook>""");
                Write(archive, "xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>""");
                Write(archive, "xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="4"><c r="C4" t="inlineStr"><is><t>https://youtu.be/dQw4w9WgXcQ</t></is></c></row></sheetData></worksheet>""");
            }
            var row = StreamLinkImportReader.Read(path).Single();
            Equal("Videos", row.Sheet, "XLSX sheet provenance"); Equal(4, row.Row, "XLSX row provenance"); Equal("C", row.Column, "XLSX column provenance");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Segmentation()
    {
        var segmenter = new StreamGameSegmenter(absenceSeconds: 10);
        segmenter.Observe(1, StreamFrameDisposition.Reliable);
        segmenter.Observe(2, StreamFrameDisposition.Reliable);
        segmenter.Observe(3, StreamFrameDisposition.SkippedObscured);
        if (segmenter.Observe(8, StreamFrameDisposition.OutsideGame) is not null) throw new Exception("Short overlay gap split a game.");
        var first = segmenter.Observe(13, StreamFrameDisposition.OutsideGame) ?? throw new Exception("Long gap did not end a game.");
        Equal(1, first.Index, "first game index");
        segmenter.Observe(20, StreamFrameDisposition.Reduced);
        segmenter.Observe(21, StreamFrameDisposition.Reliable);
        var second = segmenter.Finish(40) ?? throw new Exception("Final game was not flushed.");
        Equal(2, second.Index, "second game index");
        if (first.Tag(Source("https://youtu.be/dQw4w9WgXcQ")) == second.Tag(Source("https://youtu.be/dQw4w9WgXcQ")))
            throw new Exception("Per-game merge tags collided.");

        var startup = new StreamGameSegmenter(absenceSeconds: 45);
        startup.Observe(0, StreamFrameDisposition.Reliable);
        startup.Observe(1, StreamFrameDisposition.Reliable);
        if (startup.Observe(21, StreamFrameDisposition.Reduced, newGameStart: true) is not null)
            throw new Exception("A match's own ROUND 1 mulligan banner split its startup state.");

        var longMulligan = new StreamGameSegmenter(absenceSeconds: 45);
        longMulligan.Observe(0, StreamFrameDisposition.Reliable);
        longMulligan.Observe(1, StreamFrameDisposition.Reliable);
        longMulligan.Observe(20, StreamFrameDisposition.OutsideGame, newGameStart: true);
        if (longMulligan.Observe(47, StreamFrameDisposition.OutsideGame) is not null)
            throw new Exception("A long opening redraw tripped the ordinary absence timeout.");
        longMulligan.Observe(55, StreamFrameDisposition.Reliable);
        var continuous = longMulligan.Finish(70) ?? throw new Exception("The post-redraw game was lost.");
        if (continuous.Index != 1 || continuous.StartSeconds != 0 || continuous.EndSeconds != 70)
            throw new Exception("Opening setup and post-redraw play did not remain one game.");

        var edited = new StreamGameSegmenter(absenceSeconds: 45);
        edited.Observe(0, StreamFrameDisposition.Reliable);
        edited.Observe(1, StreamFrameDisposition.Reliable);
        edited.Observe(119, StreamFrameDisposition.Reliable);
        if (edited.Observe(121, StreamFrameDisposition.Reduced, newGameStart: true) is not { EndSeconds: 121 })
            throw new Exception("Back-to-back edited matches were merged across a new ROUND 1 cue.");
        edited.Observe(122, StreamFrameDisposition.Reliable);
        if (edited.Finish(140) is not { StartSeconds: 121 })
            throw new Exception("The ROUND 1 observation did not seed the following edited match.");
    }

    private static void Storage()
    {
        var root = NewRoot();
        try
        {
            var source = Source("https://youtu.be/dQw4w9WgXcQ"); var boundary = new StreamGameBoundary(1, 10, 50, 4, 0, 0);
            var scanned = new DateTimeOffset(2026, 9, 13, 8, 15, 0, TimeSpan.FromHours(-4));
            var published = new DateTimeOffset(2026, 9, 12, 21, 30, 0, TimeSpan.FromHours(2));
            var record = new StreamGameRecord(2, boundary.Tag(source), source.Key, source.CanonicalUri, "Test", "Channel", 1, 10, 50,
                scanned, 1, 4, 0, 0, [], null, SourcePublishedAtUtc: published);
            var store = new StreamGameStore(root); var first = store.Save(record); var second = store.Save(record);
            Equal(first, second, "idempotent path"); Equal(1, Directory.GetFiles(root, "*.gvs.json", SearchOption.AllDirectories).Length, "one compact record");
            if (Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories).Length != 0) throw new Exception("Video entered stream game storage.");
            var upgraded = record with { DetectorVersion = "next-detector", Result = "VICTORY" };
            store.Save(upgraded);
            var reloaded = JsonSerializer.Deserialize<StreamGameRecord>(File.ReadAllText(first));
            if (reloaded?.DetectorVersion != "next-detector" || reloaded.Result != "VICTORY")
                throw new Exception("A newer detector could not replace an equally dense stale stream record.");
            if (reloaded.ScannedAtUtc.Offset != TimeSpan.Zero || reloaded.ScannedAtUtc != scanned.ToUniversalTime() ||
                reloaded.SourcePublishedAtUtc?.Offset != TimeSpan.Zero || reloaded.SourcePublishedAtUtc != published.ToUniversalTime())
                throw new Exception("Stream timestamps were not persisted in canonical UTC.");
            var secondGame = record with { StreamTag = "second", GameIndex = 2, StartSeconds = 60, EndSeconds = 100 };
            var secondPath = store.Save(secondGame);
            var secondReloaded = JsonSerializer.Deserialize<StreamGameRecord>(File.ReadAllText(secondPath));
            if (secondReloaded?.ScannedAtUtc != reloaded.ScannedAtUtc ||
                secondReloaded.SourcePublishedAtUtc != reloaded.SourcePublishedAtUtc)
                throw new Exception("Games from one stream did not retain shared source timestamps.");
            var legacy = JsonSerializer.Deserialize<StreamGameRecord>(
                """{"SchemaVersion":1,"StreamTag":"legacy","SourceKey":"youtube:old","SourceUri":"https://youtu.be/old","SourceTitle":null,"SourceChannel":null,"GameIndex":1,"StartSeconds":0,"EndSeconds":20,"ScannedAtUtc":"2026-09-13T08:15:00-04:00","Reliability":1,"AnalyzedFrames":4,"ReducedFrames":0,"SkippedObscuredFrames":0,"Cards":[],"PlayerDeck":null}""");
            if (legacy?.SourcePublishedAtUtc is not null)
                throw new Exception("A legacy stream record did not default its new source timestamp to null.");
            var auxiliary = Path.Combine(root, "auxiliary");
            var observedAt = new DateTimeOffset(2026, 9, 13, 9, 0, 0, TimeSpan.FromHours(-4));
            new ObservedDeckStore().Save(auxiliary, new ObservedDeckRecord("utc", PlayerSide.Opponent,
                observedAt, "Monsters", 0, null, []));
            var observed = new ObservedDeckStore().Load(auxiliary).Single();
            if (observed.RecordedAt.Offset != TimeSpan.Zero || observed.RecordedAt != observedAt.ToUniversalTime())
                throw new Exception("Post-game observed-deck time was not persisted in UTC.");
            var eventDirectory = new PlayEventStore().CreateEventDirectory(auxiliary, "utc-event");
            new PlayEventStore().SaveRecord(eventDirectory, new RecordedPlayEvent("utc-event", observedAt,
                null, "Unknown", CardKind.Unit, 0, null, 0, new(0, 0, 1, 1), null, "during.png", null,
                "UTC storage fixture", BoardSnapshotAt: observedAt.AddSeconds(-1)));
            using var eventJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(eventDirectory, "event.json")));
            var storedEventAt = eventJson.RootElement.GetProperty("DetectedAt").GetDateTimeOffset();
            var storedBoardAt = eventJson.RootElement.GetProperty("BoardSnapshotAt").GetDateTimeOffset();
            if (storedEventAt.Offset != TimeSpan.Zero || storedEventAt != observedAt.ToUniversalTime() ||
                storedBoardAt.Offset != TimeSpan.Zero || storedBoardAt != observedAt.AddSeconds(-1).ToUniversalTime())
                throw new Exception("Per-play event times were not persisted in UTC.");
            var pair = new StreamDetectedCard("132310", "Wild Hunt Rider", PlayerSide.User, 1, 20, 22,
                [CardSightSource.Board.ToString()], ObservedCopies: 2);
            var pairRoundTrip = JsonSerializer.Deserialize<StreamDetectedCard>(JsonSerializer.Serialize(pair));
            if (pairRoundTrip?.ObservedCopies != 2)
                throw new Exception("Stream copy-floor serialization discarded a resolved pair.");
            var legacyCard = JsonSerializer.Deserialize<StreamDetectedCard>(
                """{"CardId":"132310","Name":"Wild Hunt Rider","Side":0,"EvidenceEvents":1,"FirstSeconds":20,"LastSeconds":22,"Sources":["Board"]}""");
            if (legacyCard?.ObservedCopies != 1)
                throw new Exception("A compact record written before copy floors did not default to one observed copy.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TransitionGates()
    {
        var gate = new StreamTerminalGate();
        if (gate.Observe(1, "DRAW")) throw new Exception("One noisy result OCR ended a stream game.");
        if (!gate.Observe(2, "DRAW")) throw new Exception("A stable result screen was not confirmed.");
        gate.Reset();
        if (gate.Observe(3, "VICTORY") || gate.Observe(4, "DEFEAT"))
            throw new Exception("Conflicting result OCR votes were combined.");
        if (gate.Observe(8, "DEFEAT")) throw new Exception("Stale result OCR votes were combined.");

        if (!StreamRoundHeader.IsFirst("ROUND I") || !StreamRoundHeader.IsFirst("ROI.IND 1") ||
            StreamRoundHeader.IsFirst("ROUND 2")) throw new Exception("First-round OCR normalization is unsafe.");
        var firstRound = new StreamNewGameGate();
        if (firstRound.Observe(10, StreamRoundHeader.IsFirst("ROUND I")))
            throw new Exception("One noisy ROUND 1 OCR split a stream game.");
        if (!firstRound.Observe(11, StreamRoundHeader.IsFirst("ROUND 1")))
            throw new Exception("Corroborated ROUND 1 did not split an edited stream.");
        firstRound.Reset();
        if (firstRound.Observe(12, StreamRoundHeader.IsFirst("ROUND 2")))
            throw new Exception("A later round split a stream game.");

        var scores = new StreamTerminalScoreGate();
        if (scores.Observe(1, 87, 41) is not null || scores.Observe(2, 87, 41) is not { UserScore: 87, OpponentScore: 41 })
            throw new Exception("A repeated terminal score pair was not confirmed.");
        if (scores.Observe(3, 87, 50) is not { UserScore: 87, OpponentScore: 41 } ||
            scores.Observe(4, 85, 50) is not { UserScore: 87, OpponentScore: 41 })
            throw new Exception("One-frame cascade scores replaced the last paired terminal total.");
        if (scores.Observe(5, 70, 50) is not { UserScore: 87, OpponentScore: 41 } ||
            scores.Observe(5.5, null, 50) is not { UserScore: 87, OpponentScore: 41 } ||
            scores.Observe(6, 70, 50) is not { UserScore: 70, OpponentScore: 50 })
            throw new Exception("The settled terminal score pair did not survive one unread animation frame.");

        var deckPhase = new StreamDeckPhaseGate();
        if (deckPhase.Observe(1, true, false, false) ||
            !deckPhase.Observe(2, true, false, false) ||
            !deckPhase.Observe(20, false, false, false) ||
            !deckPhase.Observe(21, false, true, false) ||
            deckPhase.Observe(22, false, true, false))
            throw new Exception("Deck-phase hysteresis did not absorb guide animation and release on authentic gameplay.");
        if (deckPhase.Observe(23, true, false, false))
            throw new Exception("A redraw row texture immediately re-entered a released deck phase.");
        deckPhase.Reset(); // the released game completed; a later guide may now be identified
        deckPhase.Observe(30, true, false, false);
        deckPhase.Observe(31, true, false, false);
        if (!deckPhase.Observe(40, true, false, true) || deckPhase.Observe(41, true, false, true))
            throw new Exception("A corroborated ROUND 1 did not release the deck phase.");

        if (ScreenStateRecognizer.NormalizeResult("UICTORY") != "VICTORY" ||
            ScreenStateRecognizer.NormalizeResult("DEFEAT") != "DEFEAT" ||
            ScreenStateRecognizer.NormalizeResult("ranked victory 2405") is not null)
            throw new Exception("Centered result normalization became either incomplete or permissive.");
    }

    private static void OverlayQuality()
    {
        const int width = 960, height = 540;
        var pixels = new byte[width * height * 4];
        var screen = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true);
        Equal(StreamFrameDisposition.SkippedObscured, StreamFrameQuality.Classify(new(width, height, pixels), screen), "covered HUD");
        Paint(new(.955, .020, .981, .052));
        Equal(StreamFrameDisposition.Reduced, StreamFrameQuality.Classify(new(width, height, pixels), screen), "one covered HUD anchor");
        var sanitizer = new StreamFrameSanitizer();
        Equal(StreamMaskedCorner.None, sanitizer.Apply(new(width, height, pixels)).Corner, "first missing-anchor vote");
        Equal(StreamMaskedCorner.None, sanitizer.Apply(new(width, height, pixels)).Corner, "second missing-anchor vote");
        var lower = sanitizer.Apply(new(width, height, pixels));
        Equal(StreamMaskedCorner.LowerRight, lower.Corner, "lower overlay mask");
        if (lower.Frame.GetPixel((int)(width * .90), (int)(height * .80)) != new PixelColor(0, 0, 0))
            throw new Exception("Covered lower preview lane was not masked.");
        Paint(new(.955, .947, .981, .979));
        Equal(StreamFrameDisposition.Reliable, StreamFrameQuality.Classify(new(width, height, pixels), screen), "clear HUD anchors");
        Equal(StreamMaskedCorner.None, sanitizer.Apply(new(width, height, pixels)).Corner, "clear frame mask");

        void Paint(NormalizedRegion region)
        {
            for (var y = region.PixelTop(height); y < region.PixelBottom(height); y++)
            for (var x = region.PixelLeft(width); x < region.PixelRight(width); x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = byte.MaxValue; pixels[offset + 3] = byte.MaxValue;
            }
        }
    }

    private static void RoundPrelude()
    {
        var card = new CardDefinition("drawn", "Drawn card", "Monster", CardKind.Unit, 5);
        var at = DateTimeOffset.UnixEpoch;
        var preview = new VisionEvidenceEvent(at, new(card, PlayerSide.User, CardSightSource.PlayPreview,
            new(.82, .41, .92, .66), .08, 1), "fixture");
        var ordinary = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null, MatchHudVisible: true);
        var filter = new StreamRoundPreludeFilter();
        Equal(0, filter.Observe(at, ordinary, [preview]).Count, "round prelude hold");
        var round = ordinary with { IsCardSelectionOverlay = true, ScreenHeader = "RouND 2" };
        Equal(0, filter.Observe(at.AddSeconds(4), round, []).Count, "late round banner purge");
        Equal(0, filter.Finish().Count, "dealt card excluded from archive");

        filter = new StreamRoundPreludeFilter();
        filter.Observe(at, ordinary, [preview]);
        Equal(1, filter.Observe(at.AddSeconds(7), ordinary, []).Count, "ordinary play released");

        var special = card with { Id = "special", Name = "Non-permanent special", Kind = CardKind.Special };
        var impossible = new CardSighting(special, PlayerSide.Opponent, CardSightSource.Board,
            new(.4, .2, .46, .34), .3, .8);
        Equal(0, CardVisionPipeline.SuppressImpossibleBoardZones([impossible]).Count, "special board rejection");
        Equal(1, CardVisionPipeline.SuppressImpossibleBoardZones([impossible with { Source = CardSightSource.PlayPreview }]).Count,
            "special preview retained");
    }

    private static void ValidationPrivacy()
    {
        Equal(StreamValidationPrivacy.AnonymousSourceKey("youtube:private-id"),
            StreamValidationPrivacy.AnonymousSourceKey("youtube:private-id"), "stable anonymous validation source");
        if (StreamValidationPrivacy.AnonymousSourceKey("youtube:private-id").Contains("private-id", StringComparison.Ordinal))
            throw new Exception("Stream validation source identity was not anonymized.");
        using var frame = new OpenCvSharp.Mat(540, 960, OpenCvSharp.MatType.CV_8UC3,
            new OpenCvSharp.Scalar(255, 255, 255));
        StreamValidationPrivacy.Apply(frame, StreamMaskedCorner.UpperRight);
        var left = frame.At<OpenCvSharp.Vec3b>(270, 20);
        var upperRight = frame.At<OpenCvSharp.Vec3b>(20, 900);
        var lowerRight = frame.At<OpenCvSharp.Vec3b>(500, 900);
        if (left.Item0 > 20 || upperRight.Item0 > 20 || lowerRight.Item0 < 240)
            throw new Exception("Automatic stream-validation privacy masks are incomplete or overbroad.");
    }

    private static void LowResolutionProfile()
    {
        var frame = new PixelFrame(640, 360, new byte[640 * 360 * 4]);
        if (VisionFrameNormalizer.Prepare(frame).Supported) throw new Exception("Live profile accepted 360p.");
        var prepared = VisionFrameNormalizer.Prepare(frame, allowStreamResolution: true);
        if (!prepared.Supported || !prepared.Upscaled || prepared.Frame.Width != 960 || prepared.Frame.Height != 540)
            throw new Exception("Stream profile did not normalize 360p to 540p.");
    }

    private static StreamSourceIdentity Source(string uri) => StreamSourceIdentity.TryCreate(uri, out var source) && source is not null
        ? source : throw new Exception("Source URI was rejected: " + uri);
    private static string NewRoot() { var path = Path.Combine(Path.GetTempPath(), "gv-stream-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Write(ZipArchive archive, string path, string text) { var entry = archive.CreateEntry(path); using var writer = new StreamWriter(entry.Open(), Encoding.UTF8); writer.Write(text); }
    private static void Equal<T>(T expected, T actual, string label) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{label}: expected {expected}, got {actual}"); }
}
