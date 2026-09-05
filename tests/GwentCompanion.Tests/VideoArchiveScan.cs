using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;
using OpenCvSharp;

internal static class VideoArchiveScan
{
    public static async Task<int> ProbePreviewPowerAsync(string[] args)
    {
        var inputIndex = Array.IndexOf(args, "--preview-power-probe");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length) throw new ArgumentException("Supply --preview-power-probe <frame>.");
        var input = Path.GetFullPath(args[inputIndex + 1]);
        using var stream = File.OpenRead(input);
        var frame = BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0]);
        var reader = new GwentCompanion.Platform.Windows.Vision.BoardPowerReader();
        foreach (var (side, region) in new[]
        {
            ("Opponent", new NormalizedRegion(.800, .134, .916, .399)),
            ("User", new NormalizedRegion(.815, .413, .936, .665)),
        }) Console.WriteLine($"{side} preview power={reader.ReadCandidate(frame, region)?.ToString() ?? "-"}");
        using var textReader = new ScreenStateRecognizer();
        foreach (var (name, region) in new[]
        {
            ("current-opponent", new NormalizedRegion(.90, .315, .995, .385)),
            ("legacy-opponent", new NormalizedRegion(.82, .315, .91, .385)),
            ("current-user", new NormalizedRegion(.90, .625, .995, .69)),
            ("legacy-user-high", new NormalizedRegion(.82, .54, .91, .62)),
            ("legacy-user-mid", new NormalizedRegion(.82, .58, .91, .65)),
            ("legacy-user-low", new NormalizedRegion(.82, .62, .91, .69)),
        }) Console.WriteLine($"{name} score={await OpponentHudRecognizer.ReadScoreCandidateAsync(frame, region, textReader) ?? -1}");
        return 0;
    }

    public static async Task<int> AuditCoins(string[] args, string root)
    {
        var inputIndex = Array.IndexOf(args, "--coin-hud-audit");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length) throw new ArgumentException("Supply --coin-hud-audit <frame-or-directory>.");
        var input = Path.GetFullPath(args[inputIndex + 1]);
        var frames = File.Exists(input) ? [input] : Directory.GetFiles(input, "frame-*.jpg").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var export = args.Contains("--export-crops");
        var labelIndex = Array.IndexOf(args, "--train-opponent-label");
        var trainingLabel = labelIndex >= 0 ? args[labelIndex + 1] : null;
        if (args.Contains("--trace-glyphs")) HudDigitReader.Trace = Console.WriteLine;
        using var reader = new ScreenStateRecognizer();
        foreach (var path in frames)
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
            if (export)
            {
                var directory = Path.Combine(Path.GetDirectoryName(path)!, "coin-crops"); Directory.CreateDirectory(directory);
                foreach (var (side, region) in new[] { ("opponent", OpponentHudRecognizer.OpponentCoinRegion), ("user", OpponentHudRecognizer.UserCoinRegion) })
                {
                    var left = region.PixelLeft(frame.Width); var top = region.PixelTop(frame.Height);
                    var crop = new CroppedBitmap(decoder.Frames[0], new(left, top,
                        region.PixelRight(frame.Width) - left, region.PixelBottom(frame.Height) - top));
                    using var output = File.Create(Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "-" + side + ".png"));
                    var enlarged = new TransformedBitmap(crop, new ScaleTransform(8, 8));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(enlarged)); encoder.Save(output);
                }
            }
            var opponent = await OpponentHudRecognizer.ReadCoinCandidateAsync(frame, OpponentHudRecognizer.OpponentCoinRegion, reader);
            var user = await OpponentHudRecognizer.ReadCoinCandidateAsync(frame, OpponentHudRecognizer.UserCoinRegion, reader);
            Console.WriteLine($"{Path.GetFileName(path)} opponent={opponent?.ToString() ?? "-"} user={user?.ToString() ?? "-"}");
            if (trainingLabel is not null)
                Console.WriteLine(JsonSerializer.Serialize(HudDigitReader.Train(frame, OpponentHudRecognizer.OpponentCoinRegion, trainingLabel)));
        }
        return 0;
    }

    public static int AuditInteractionGaps(string[] args, string root)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) :
            Path.Combine(root, "GwentCompanion", "cache", "video-scan", "shinmiri", "interaction-catalog-gaps.json");
        var catalogPath = Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json");
        var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(catalogPath));
        var covered = DeckInteractionCatalog.All.Select(item => item.CardId).ToHashSet(StringComparer.Ordinal);
        var patterns = new (string Name, string Pattern, int Weight)[]
        {
            ("setup", @"(?:start|beginning) of the game|starts? in your (?:hand|graveyard)", 5),
            ("deck-summon", @"summon[^.\n]{0,100}(?:from )?(?:your |opponent'?s )?deck", 5),
            ("self-summon", @"summon self", 5),
            ("graveyard-return", @"(?:summon|play|move|return)[^.\n]{0,100}(?:from )?(?:your |opponent'?s )?graveyard", 5),
            ("zone-addition", @"(?:spawn|add|put|move|shuffle)[^.\n]{0,120}(?:deck|hand|graveyard)", 4),
            ("reveal", @"reveal", 4),
            ("self-banish", @"banish self", 4),
            ("resilience", @"resilience", 3),
            ("transform", @"transform", 3),
            ("create", @"create", 1),
        };
        var gaps = cards
            .Where(card => card.CanBeInStartingDeck && card.Kind is CardKind.Unit or CardKind.Special or CardKind.Artifact)
            .Where(card => !covered.Contains(card.Id) && !string.IsNullOrWhiteSpace(card.AbilityText))
            .Select(card =>
            {
                var matches = patterns.Where(pattern => Regex.IsMatch(card.AbilityText!, pattern.Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
                return new { Card = card, Matches = matches };
            })
            .Where(item => item.Matches.Length > 0)
            .Select(item => new
            {
                item.Card.Id, item.Card.Name, item.Card.Faction, Kind = item.Card.Kind.ToString(), item.Card.Provision,
                Priority = item.Matches.Max(match => match.Weight) >= 5 ? "high" : item.Matches.Max(match => match.Weight) >= 3 ? "medium" : "low",
                Signals = item.Matches.Select(match => match.Name).ToArray(),
                AbilityText = item.Card.AbilityText,
                Source = "https://gwent.one/en/card/" + item.Card.Id,
            })
            .OrderBy(item => item.Priority == "high" ? 0 : item.Priority == "medium" ? 1 : 2)
            .ThenBy(item => item.Faction).ThenBy(item => item.Name).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            CoveredCatalogCount = covered.Count,
            GapCandidateCount = gaps.Length,
            HighPriorityCount = gaps.Count(item => item.Priority == "high"),
            MediumPriorityCount = gaps.Count(item => item.Priority == "medium"),
            LowPriorityCount = gaps.Count(item => item.Priority == "low"),
            Note = "Mechanical ability-text audit. Candidates require review; broad Create/Transform signals are intentionally not assumed to be deck-provenance interactions.",
            Candidates = gaps,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE interaction-gap-audit candidates={gaps.Length} high={gaps.Count(item => item.Priority == "high")} output={output}");
        return 0;
    }

    public static int ExportInteractionWatch(string[] args, string root)
    {
        var scanRoot = Path.Combine(root, "GwentCompanion", "cache", "video-scan", "shinmiri");
        var seedPath = Path.Combine(scanRoot, "interesting-interactions.json");
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(scanRoot, "interaction-watch.json");
        var seeds = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(seedPath))
        {
            using var seedDocument = JsonDocument.Parse(File.ReadAllText(seedPath));
            foreach (var target in seedDocument.RootElement.GetProperty("targets").EnumerateArray())
                seeds[target.GetProperty("name").GetString()!] = target.Clone();
        }
        var targets = new List<object>();
        var catalogNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var interaction in DeckInteractionCatalog.All)
        {
            catalogNames.Add(interaction.Name);
            seeds.TryGetValue(interaction.Name, out var seed);
            targets.Add(new
            {
                cardId = interaction.CardId,
                name = interaction.Name,
                kind = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("kind", out var kind) ? kind.GetString() : interaction.Trigger,
                trigger = interaction.Trigger,
                caution = interaction.Caution,
                abilityFragment = interaction.AbilityFragment,
                interaction.StartsOutsideDrawPile,
                source = interaction.Source,
                requiredSides = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("requiredSides", out var sides)
                    ? sides.EnumerateArray().Select(item => item.GetString()).ToArray() : ["Opponent"],
                status = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("status", out var status) ? status.GetString() : "searching",
                evidence = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("evidence", out var evidence)
                    ? evidence.Clone() : default(JsonElement?),
            });
        }
        var gapPath = Path.Combine(scanRoot, "interaction-catalog-gaps.json");
        if (File.Exists(gapPath))
        {
            using var gapDocument = JsonDocument.Parse(File.ReadAllText(gapPath));
            foreach (var candidate in gapDocument.RootElement.GetProperty("Candidates").EnumerateArray()
                         .Where(candidate => candidate.GetProperty("Priority").GetString() == "high"))
            {
                var name = candidate.GetProperty("Name").GetString()!;
                if (!catalogNames.Add(name)) continue;
                seeds.TryGetValue(name, out var seed);
                targets.Add(new
                {
                    cardId = candidate.GetProperty("Id").GetString(),
                    name,
                    kind = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("kind", out var kind)
                        ? kind.GetString() : string.Join(", ", candidate.GetProperty("Signals").EnumerateArray().Select(item => item.GetString())),
                    trigger = candidate.GetProperty("AbilityText").GetString(),
                    caution = "High-priority ability-text audit candidate; the exact interaction still requires temporal review.",
                    abilityFragment = candidate.GetProperty("AbilityText").GetString(),
                    StartsOutsideDrawPile = candidate.GetProperty("Signals").EnumerateArray().Any(item => item.GetString() == "setup"),
                    source = candidate.GetProperty("Source").GetString(),
                    requiredSides = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("requiredSides", out var sides)
                        ? sides.EnumerateArray().Select(item => item.GetString()).ToArray() : ["User", "Opponent"],
                    status = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("status", out var status) ? status.GetString() : "searching",
                    evidence = seed.ValueKind != JsonValueKind.Undefined && seed.TryGetProperty("evidence", out var evidence)
                        ? evidence.Clone() : default(JsonElement?),
                });
            }
        }
        foreach (var seed in seeds.Where(item => !catalogNames.Contains(item.Key)).Select(item => item.Value))
            targets.Add(new
            {
                cardId = seed.TryGetProperty("cardId", out var cardId) ? cardId.GetString() : null,
                name = seed.GetProperty("name").GetString(),
                kind = seed.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                trigger = (string?)null,
                caution = "Manually queued interaction not yet represented in DeckInteractionCatalog.",
                abilityFragment = (string?)null,
                StartsOutsideDrawPile = false,
                source = (string?)null,
                requiredSides = seed.TryGetProperty("requiredSides", out var sides) ? sides.EnumerateArray().Select(item => item.GetString()).ToArray() : ["Opponent"],
                status = seed.TryGetProperty("status", out var status) ? status.GetString() : "searching",
                evidence = seed.TryGetProperty("evidence", out var evidence) ? evidence.Clone() : default(JsonElement?),
            });
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceCatalog = "DeckInteractionCatalog plus manually queued temporal/carryover interactions",
            TargetCount = targets.Count,
            Targets = targets,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE interaction-watch targets={targets.Count} output={output}");
        return 0;
    }

    public static int AuditHandMotion(string[] args)
    {
        var inputIndex = Array.IndexOf(args, "--hand-motion-audit");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --hand-motion-audit <window-directory>.");
        var input = Path.GetFullPath(args[inputIndex + 1]);
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException(input);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(input, "hand-motion.json");
        var files = Directory.GetFiles(input, "frame-*.jpg").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var rows = new List<object>();
        Mat? prior = null;
        foreach (var file in files)
        {
            using var image = Cv2.ImRead(file, ImreadModes.Grayscale);
            if (image.Empty()) continue;
            if (prior is not null)
            {
                using var difference = new Mat();
                Cv2.Absdiff(prior, image, difference);
                var opponent = Mean(difference, .20, 0, .91, .16);
                var user = Mean(difference, .16, .78, .82, 1);
                var board = Mean(difference, .18, .18, .82, .76);
                var stamp = Path.GetFileNameWithoutExtension(file).Replace("frame-", "", StringComparison.Ordinal);
                rows.Add(new
                {
                    Seconds = long.Parse(stamp, CultureInfo.InvariantCulture) / 1000.0,
                    OpponentMotion = opponent,
                    UserMotion = user,
                    BoardMotion = board,
                    OpponentLocalized = opponent / Math.Max(2, board),
                    UserLocalized = user / Math.Max(2, board),
                    Frame = Path.GetFileName(file),
                });
            }
            prior?.Dispose();
            prior = image.Clone();
        }
        prior?.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Input = Path.GetFileName(input), Samples = rows,
            Note = "Frame-to-frame grayscale motion by opponent hand, user hand, and central board. Localized ratios help separate hand animation from whole-board animation; they are candidates, not card identity labels.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE hand-motion frames={files.Length} samples={rows.Count} output={output}");
        return 0;

        static double Mean(Mat difference, double left, double top, double right, double bottom)
        {
            var x = Math.Clamp((int)Math.Round(left * difference.Width), 0, difference.Width - 1);
            var y = Math.Clamp((int)Math.Round(top * difference.Height), 0, difference.Height - 1);
            var width = Math.Clamp((int)Math.Round((right - left) * difference.Width), 1, difference.Width - x);
            var height = Math.Clamp((int)Math.Round((bottom - top) * difference.Height), 1, difference.Height - y);
            using var region = new Mat(difference, new OpenCvSharp.Rect(x, y, width, height));
            return Cv2.Mean(region).Val0;
        }
    }

    public static int ExtractWindow(string[] args, string root)
    {
        var inputIndex = Array.IndexOf(args, "--video-window-extract");
        var startIndex = Array.IndexOf(args, "--start-seconds");
        var durationIndex = Array.IndexOf(args, "--duration-seconds");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length || startIndex < 0 || startIndex + 1 >= args.Length ||
            durationIndex < 0 || durationIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --video-window-extract <video> --start-seconds <n> --duration-seconds <n>.");
        var inputArgument = args[inputIndex + 1];
        var isRemote = Uri.TryCreate(inputArgument, UriKind.Absolute, out var inputUri) &&
            inputUri.Scheme is "http" or "https";
        var input = isRemote ? inputArgument : Path.GetFullPath(inputArgument);
        if (!isRemote && !File.Exists(input)) throw new FileNotFoundException("Video input not found.", input);
        var start = double.Parse(args[startIndex + 1], CultureInfo.InvariantCulture);
        var duration = double.Parse(args[durationIndex + 1], CultureInfo.InvariantCulture);
        if (!double.IsFinite(start) || start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (!double.IsFinite(duration) || duration < 2 || duration > 120) throw new ArgumentOutOfRangeException(nameof(duration));
        var sampleIndex = Array.IndexOf(args, "--sample-fps");
        var sampleFps = sampleIndex >= 0 ? double.Parse(args[sampleIndex + 1], CultureInfo.InvariantCulture) : 2;
        if (!double.IsFinite(sampleFps) || sampleFps < 1 || sampleFps > 10) throw new ArgumentOutOfRangeException(nameof(sampleFps));
        var maximumIndex = Array.IndexOf(args, "--maximum-frames");
        var maximumFrames = maximumIndex >= 0 ? int.Parse(args[maximumIndex + 1], CultureInfo.InvariantCulture) : 120;
        if (maximumFrames is < 1 or > 1200 || Math.Ceiling(duration * sampleFps) > maximumFrames)
            throw new ArgumentException("Window exceeds the explicit frame budget (default 120).");
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) :
            Path.Combine(root, "GwentCompanion", "cache", "video-scan", "windows",
                isRemote ? "remote-window" : Path.GetFileNameWithoutExtension(input));
        Directory.CreateDirectory(output);

        using var capture = new VideoCapture(input);
        if (!capture.IsOpened()) throw new InvalidOperationException("OpenCV could not open the video.");
        var sourceFps = capture.Fps;
        if (!double.IsFinite(sourceFps) || sourceFps <= 0) sourceFps = 30;
        var stride = Math.Max(1, (int)Math.Round(sourceFps / sampleFps));
        capture.PosMsec = (int)Math.Round(start * 1000);
        using var source = new Mat();
        using var resized = new Mat();
        var sampled = 0;
        var decoded = 0;
        var end = start + duration;
        // Two reusable Mats only. Also bound decoding if a remote stream ignores seek/timestamps.
        var maximumDecoded = (long)Math.Ceiling(sourceFps * (duration + 2));
        while (sampled < maximumFrames && decoded < maximumDecoded && capture.Read(source) && !source.Empty())
        {
            var seconds = capture.PosMsec / 1000.0;
            if (seconds > end) break;
            if (decoded++ % stride != 0 || seconds < start - 0.1) continue;
            var selected = source;
            if (source.Width > 960)
            {
                Cv2.Resize(source, resized, new OpenCvSharp.Size(960,
                    (int)Math.Round(source.Height * 960.0 / source.Width)), 0, 0, InterpolationFlags.Area);
                selected = resized;
            }
            var stamp = (long)Math.Round(seconds * 1000);
            Cv2.ImWrite(Path.Combine(output, $"frame-{stamp:000000000}.jpg"), selected,
                [new ImageEncodingParam(ImwriteFlags.JpegQuality, 88)]);
            sampled++;
        }
        File.WriteAllText(Path.Combine(output, "window.json"), JsonSerializer.Serialize(new
        {
            Source = isRemote ? inputUri!.Host + inputUri.AbsolutePath : Path.GetFileName(input),
            RequestedSampleFps = sampleFps, ActualSampleFps = sourceFps / stride, Frames = sampled,
            MaximumFrames = maximumFrames, DecodedFrames = decoded, StartSeconds = start, DurationSeconds = duration,
            Purpose = "Small temporal review window retained after the one-at-a-time source video is deleted.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE video-window frames={sampled} start={start:F1} duration={duration:F1} output={output}");
        if (sampled == 0) throw new InvalidOperationException("No frames in requested window; remote seek may be unsupported.");
        return 0;
    }

    public static async Task<int> RecognizeAsync(string[] args, string root)
    {
        var inputIndex = Array.IndexOf(args, "--video-review-recognize");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --video-review-recognize <review-directory>.");
        var input = Path.GetFullPath(args[inputIndex + 1]);
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException(input);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(input, "recognition.json");
        var cache = Path.Combine(root, "GwentCompanion", "cache");
        var cards = BuiltInCardCatalog.Merge(GwentOneCardCatalog.Load(Path.Combine(cache, "gwent-one-cards.json")));
        var references = VisionReferenceLibrary.Load(cards, cache);
        Console.WriteLine($"Preparing {references.Count} cached artwork references for selected keyframes…");
        using var pipeline = new CardVisionPipeline(references, cards, Path.Combine(cache, "recognition-features"));
        var frames = Directory.GetFiles(input, "during.jpg", SearchOption.AllDirectories)
            .GroupBy(path => Path.GetFileName(Path.GetDirectoryName(path))!.Split('-')[0], StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var temporalWindow = frames.Length == 0 && File.Exists(Path.Combine(input, "window.json"));
        if (temporalWindow)
            frames = Directory.GetFiles(input, "frame-*.jpg", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal).Where((_, index) => index % 2 == 0).ToArray();
        var frameFilterIndex = Array.IndexOf(args, "--filter-frame");
        if (frameFilterIndex >= 0 && frameFilterIndex + 1 < args.Length)
            frames = frames.Where(path => Path.GetFileName(path).Contains(args[frameFilterIndex + 1], StringComparison.OrdinalIgnoreCase)).ToArray();
        var findings = new List<object>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (path, index) in frames.Select((path, index) => (path, index)))
        {
            var stamp = temporalWindow ? Path.GetFileNameWithoutExtension(path).Replace("frame-", "", StringComparison.Ordinal) :
                Path.GetFileName(Path.GetDirectoryName(path))!.Split('-')[0];
            var at = temporalWindow ? DateTimeOffset.UnixEpoch.AddMilliseconds(long.Parse(stamp, System.Globalization.CultureInfo.InvariantCulture)) :
                DateTimeOffset.UnixEpoch.Add(ParseTimestamp(stamp));
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var result = await pipeline.AnalyzeAsync(BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]), at, includeBoard: temporalWindow);
            var sightings = result.Sightings.Select(item => new
            {
                item.Card.Id, item.Card.Name, Side = item.Side.ToString(), Source = item.Source.ToString(),
                item.Distance, item.Margin, item.Evidence, item.NeedsTemporalConfirmation, item.Region,
            }).ToArray();
            var events = result.Events.Select(item => new
            {
                item.Sighting.Card.Id, item.Sighting.Card.Name, Side = item.Sighting.Side.ToString(), Source = item.Sighting.Source.ToString(),
            }).ToArray();
            if (sightings.Length > 0 || events.Length > 0 || result.HoveredCard is not null || result.CardMeasurements?.Count > 0 ||
                result.Screen.IsCardSelectionOverlay || result.Screen.ScreenHeader is not null)
                findings.Add(new { Seconds = at.TimeOfDay.TotalSeconds, Frame = Path.GetRelativePath(input, path), result.Screen.View,
                    result.Screen.MatchHudVisible, result.Screen.IsCardSelectionOverlay, result.Screen.ScreenHeader,
                    result.Screen.UserScore, result.Screen.OpponentScore, result.Screen.UserCoins, result.Screen.OpponentCoins,
                    Hovered = result.HoveredCard?.Name, result.HoverInPlayerHand,
                    Measurements = result.CardMeasurements?.Select(measurement => new { measurement.CardId, measurement.Side,
                        Power = measurement.Power?.Value, Armor = measurement.Armor?.Value, Charges = measurement.Charges?.Value }).ToArray(),
                    Sightings = sightings, Events = events });
            foreach (var sighting in result.Sightings)
                if (distinct.Add($"{sighting.Side}:{sighting.Card.Id}:{sighting.Source}"))
                    Console.WriteLine($"FOUND {at:HH:mm:ss.f} {sighting.Side} {sighting.Source}: {sighting.Card.Name} distance={sighting.Distance:F3}");
            if (index % 50 == 49) Console.WriteLine($"PROGRESS recognized={index + 1}/{frames.Length} findings={findings.Count}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Input = Path.GetFileName(input), SelectedFrames = frames.Length, Findings = findings,
            DistinctSightings = distinct.Count, ElapsedSeconds = watch.Elapsed.TotalSeconds,
            pipeline.CachedReferenceImages, pipeline.ComputedReferenceImages, pipeline.OcrCalls,
            Note = temporalWindow ? "Sequential recognition of a bounded temporal window, including board evidence; unlabelled results require replay review before training use." :
                "Compact recognition of motion-selected during frames only; unlabelled results require replay review before training use.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE video-recognition frames={frames.Length} findings={findings.Count} distinct={distinct.Count} output={output}");
        return 0;
    }

    public static int Promote(string[] args, string root)
    {
        var inputIndex = Array.IndexOf(args, "--video-training-promote");
        var recognitionIndex = Array.IndexOf(args, "--recognition");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length || recognitionIndex < 0 || recognitionIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --video-training-promote <review-directory> --recognition <json>.");
        var review = Path.GetFullPath(args[inputIndex + 1]);
        var recognition = Path.GetFullPath(args[recognitionIndex + 1]);
        if (!Directory.Exists(review)) throw new DirectoryNotFoundException(review);
        if (!File.Exists(recognition)) throw new FileNotFoundException("Recognition input not found.", recognition);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 ? Path.GetFullPath(args[outputIndex + 1]) : Path.ChangeExtension(recognition, ".training.json");
        var cache = Path.Combine(root, "GwentCompanion", "cache", "observed-art");
        var videoId = Path.GetFileName(review).Replace("-review", "", StringComparison.OrdinalIgnoreCase);
        var promoted = new List<object>();
        var promotedCards = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(File.ReadAllText(recognition));
        var findings = document.RootElement.GetProperty("Findings").EnumerateArray().ToArray();
        var titleAuthenticatedCards = findings
            .SelectMany(finding => finding.GetProperty("Sightings").EnumerateArray())
            .Where(sighting =>
                sighting.TryGetProperty("Evidence", out var evidence) &&
                evidence.GetString()?.StartsWith("Exact visible preview title:", StringComparison.Ordinal) == true)
            .Select(sighting => sighting.GetProperty("Id").GetString()!)
            .Where(cardId => cardId.All(char.IsAsciiDigit))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var cardId in titleAuthenticatedCards)
        {
            // The exact-title frame can be a hover tooltip and therefore is not safe
            // card-art training data. Use it only to authenticate an independent,
            // high-confidence SIFT match from a play-preview frame in the same video.
            var candidate = findings
                .SelectMany(finding => finding.GetProperty("Sightings").EnumerateArray()
                    .Select(sighting => (Finding: finding, Sighting: sighting)))
                .Where(item =>
                    item.Sighting.GetProperty("Id").GetString() == cardId &&
                    item.Sighting.GetProperty("Source").GetString() == nameof(CardSightSource.PlayPreview) &&
                    item.Sighting.TryGetProperty("Evidence", out var evidence) &&
                    evidence.GetString()?.Contains("spatially consistent SIFT features", StringComparison.Ordinal) == true &&
                    item.Sighting.GetProperty("Distance").GetDouble() <= .16 &&
                    item.Sighting.GetProperty("Margin").GetDouble() >= .05)
                .OrderBy(item => item.Sighting.GetProperty("Distance").GetDouble())
                .FirstOrDefault();
            if (candidate.Sighting.ValueKind == JsonValueKind.Undefined || !promotedCards.Add(cardId)) continue;
            var finding = candidate.Finding;
            var sighting = candidate.Sighting;
            var frame = finding.GetProperty("Frame").GetString()!;
            var source = Path.GetFullPath(Path.Combine(review, frame));
            if (!source.StartsWith(review + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) continue;
            var evidence = sighting.GetProperty("Evidence").GetString();
                var targetDirectory = Path.Combine(cache, cardId);
                Directory.CreateDirectory(targetDirectory);
                if (Directory.GetFiles(targetDirectory, "youtube-*.jpg").Length >= 4) continue;
                var side = sighting.GetProperty("Side").GetString();
                var region = sighting.TryGetProperty("Region", out var storedRegion)
                    ? new
                    {
                        Left = storedRegion.GetProperty("Left").GetDouble(),
                        Top = storedRegion.GetProperty("Top").GetDouble(),
                        Right = storedRegion.GetProperty("Right").GetDouble(),
                        Bottom = storedRegion.GetProperty("Bottom").GetDouble(),
                    }
                    : side == nameof(PlayerSide.User)
                        ? new { Left = .815, Top = .413, Right = .936, Bottom = .665 }
                        : new { Left = .800, Top = .134, Right = .916, Bottom = .399 };
                using var stream = File.OpenRead(source);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = decoder.Frames[0];
                var left = Math.Clamp((int)Math.Round(region.Left * bitmap.PixelWidth), 0, bitmap.PixelWidth - 1);
                var top = Math.Clamp((int)Math.Round(region.Top * bitmap.PixelHeight), 0, bitmap.PixelHeight - 1);
                var right = Math.Clamp((int)Math.Round(region.Right * bitmap.PixelWidth), left + 1, bitmap.PixelWidth);
                var bottom = Math.Clamp((int)Math.Round(region.Bottom * bitmap.PixelHeight), top + 1, bitmap.PixelHeight);
                var stamp = Path.GetFileName(Path.GetDirectoryName(source))!.Split('-')[0];
                var name = $"youtube-{videoId}-{stamp}-{sighting.GetProperty("Side").GetString()}";
                var imagePath = Path.Combine(targetDirectory, name + ".jpg");
                var metadataPath = Path.Combine(targetDirectory, name + ".json");
                if (File.Exists(imagePath)) continue;
                var crop = new CroppedBitmap(bitmap, new System.Windows.Int32Rect(left, top, right - left, bottom - top));
                using (var file = File.Create(imagePath))
                {
                    var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                    encoder.Frames.Add(BitmapFrame.Create(crop));
                    encoder.Save(file);
                }
                var row = new
                {
                    VideoId = videoId, CardId = cardId, CardName = sighting.GetProperty("Name").GetString(),
                    Seconds = finding.GetProperty("Seconds").GetDouble(), Frame = frame, Evidence = evidence,
                    Region = region,
                    LabelSource = "Independent exact-title evidence authenticated this high-confidence SIFT play-preview crop in the same video.",
                };
                File.WriteAllText(metadataPath, JsonSerializer.Serialize(row, new JsonSerializerOptions { WriteIndented = true }));
                promoted.Add(row);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            VideoId = videoId, Promoted = promoted,
            RejectedSelfLabels = "SIFT-only, caption-only, and hover-tooltip crops are never promoted automatically.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE video-training-promote promoted={promoted.Count} output={output}");
        return 0;
    }

    public static int Run(string[] args, string root)
    {
        var inputIndex = Array.IndexOf(args, "--video-review-scan");
        if (inputIndex < 0 || inputIndex + 1 >= args.Length)
            throw new ArgumentException("Supply --video-review-scan <video-file>.");

        var input = Path.GetFullPath(args[inputIndex + 1]);
        if (!File.Exists(input)) throw new FileNotFoundException("Video input not found.", input);
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0
            ? Path.GetFullPath(args[outputIndex + 1])
            : Path.Combine(root, "GwentCompanion", "diagnostics", "video-review-" + Path.GetFileNameWithoutExtension(input));
        var sampleIndex = Array.IndexOf(args, "--sample-fps");
        var sampleFps = sampleIndex >= 0
            ? double.Parse(args[sampleIndex + 1], CultureInfo.InvariantCulture)
            : 5;
        if (!double.IsFinite(sampleFps) || sampleFps < 1 || sampleFps > 10)
            throw new ArgumentOutOfRangeException(nameof(sampleFps), "Sample rate must be between 1 and 10 FPS.");

        Directory.CreateDirectory(output);
        using var capture = new VideoCapture(input);
        if (!capture.IsOpened()) throw new InvalidOperationException("OpenCV could not open the video.");
        var sourceFps = capture.Fps;
        if (!double.IsFinite(sourceFps) || sourceFps <= 0) sourceFps = 30;
        var stride = Math.Max(1, (int)Math.Round(sourceFps / sampleFps));
        var motion = new PreviewMotionSampler();
        var state = new GwentVisualStateDetector();
        using var source = new Mat();
        using var resized = new Mat();
        using var bgra = new Mat();
        long decoded = 0, sampled = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var duration = capture.FrameCount > 0 ? TimeSpan.FromSeconds(capture.FrameCount / sourceFps) : TimeSpan.Zero;
        var segmentCount = Math.Max(1, (int)Math.Ceiling(Math.Max(1, duration.TotalSeconds) / 600));
        var clipsPerSegment = (int)Math.Ceiling(400.0 / segmentCount);
        var bytesPerSegment = 100L * 1024 * 1024 / segmentCount;
        ReviewKeyFrameWriter? writer = null;
        var activeSegment = -1;
        var savedClips = 0;
        var skippedClips = 0;
        long savedBytes = 0;
        long suppressedOverlays = 0, suppressedNonMatches = 0;

        while (capture.Read(source))
        {
            var frameNumber = decoded++;
            if (frameNumber % stride != 0 || source.Empty()) continue;
            var selected = source;
            if (source.Width > 1280)
            {
                Cv2.Resize(source, resized, new OpenCvSharp.Size(1280,
                    (int)Math.Round(source.Height * 1280.0 / source.Width)), 0, 0, InterpolationFlags.Area);
                selected = resized;
            }
            Cv2.CvtColor(selected, bgra, ColorConversionCodes.BGR2BGRA);
            var pixels = new byte[checked(bgra.Width * bgra.Height * 4)];
            Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
            var bitmap = BitmapSource.Create(bgra.Width, bgra.Height, 96, 96, PixelFormats.Bgra32, null, pixels, bgra.Width * 4);
            bitmap.Freeze();
            var at = DateTimeOffset.UnixEpoch.AddSeconds(frameNumber / sourceFps);
            var pixelFrame = new PixelFrame(bgra.Width, bgra.Height, pixels);
            var change = motion.Measure(pixelFrame);
            var screen = state.Analyze(pixelFrame);
            sampled++;
            if (screen.IsCardSelectionOverlay) { suppressedOverlays++; continue; }
            if (screen.MatchHudVisible != true) { suppressedNonMatches++; continue; }
            var segment = Math.Min(segmentCount - 1, (int)(at.TimeOfDay.TotalSeconds / 600));
            if (segment != activeSegment)
            {
                FinishWriter();
                activeSegment = segment;
                writer = new ReviewKeyFrameWriter(Path.Combine(output, $"segment-{segment:00}"), clipsPerSegment, bytesPerSegment);
            }
            writer!.Observe(bitmap, at, change);
            if (sampled % 1000 == 0)
                Console.WriteLine($"PROGRESS sampled={sampled} video={at:HH\\:mm\\:ss}/{duration:hh\\:mm\\:ss} clips={savedClips + writer.SavedClips} memory=bounded");
        }

        FinishWriter();
        File.WriteAllText(Path.Combine(output, "video-scan.json"), JsonSerializer.Serialize(new
        {
            Source = Path.GetFileName(input),
            SourceFramesDecoded = decoded,
            SourceFps = sourceFps,
            SampleFps = sourceFps / stride,
            SampledFrames = sampled,
            DurationSeconds = duration.TotalSeconds,
            SavedClips = savedClips,
            SkippedClips = skippedClips,
            SavedBytes = savedBytes,
            SegmentSeconds = 600,
            SegmentCount = segmentCount,
            ClipsPerSegment = clipsPerSegment,
            BytesPerSegment = bytesPerSegment,
            SuppressedOverlayFrames = suppressedOverlays,
            SuppressedNonMatchFrames = suppressedNonMatches,
            PeakRetainedFrames = 12,
            Retention = "Only bounded motion-triggered before/during/after review frames; source video may be deleted after the pass.",
            ElapsedSeconds = watch.Elapsed.TotalSeconds,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"DONE video-scan sampled={sampled} clips={savedClips} saved-bytes={savedBytes} output={output}");
        return 0;

        void FinishWriter()
        {
            if (writer is null) return;
            writer.Finish();
            savedClips += writer.SavedClips;
            skippedClips += writer.SkippedClips;
            savedBytes += writer.SavedBytes;
            writer = null;
        }
    }

    private static TimeSpan ParseTimestamp(string value)
    {
        if (value.Length != 9 || !value.All(char.IsAsciiDigit)) return TimeSpan.Zero;
        return new TimeSpan(0, int.Parse(value[..2], CultureInfo.InvariantCulture),
            int.Parse(value.Substring(2, 2), CultureInfo.InvariantCulture),
            int.Parse(value.Substring(4, 2), CultureInfo.InvariantCulture),
            int.Parse(value.Substring(6, 3), CultureInfo.InvariantCulture));
    }
}
