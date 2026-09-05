using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class DeckBuilderReplay
{
    public static PixelFrame Restore(string imagePath)
    {
        using var image = File.OpenRead(imagePath);
        var decoder = BitmapDecoder.Create(image, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var crop = BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(imagePath, ".json")));
        var width = meta.RootElement.GetProperty("SourceWidth").GetInt32();
        var height = meta.RootElement.GetProperty("SourceHeight").GetInt32();
        var region = meta.RootElement.GetProperty("Region").Deserialize<NormalizedRegion>();
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < crop.Height; y++)
            Buffer.BlockCopy(crop.BgraPixels, y * crop.Width * 4, pixels,
                ((region.PixelTop(height) + y) * width + region.PixelLeft(width)) * 4, crop.Width * 4);
        return new PixelFrame(width, height, pixels);
    }

    public static async Task<int> RunAsync(string root, string directory, bool probe)
    {
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion", "cache", "gwent-one-cards.json"));
        using var scanner = new DeckBuilderScanner(catalog);
        using var text = new ScreenStateRecognizer();
        var drafts = new DeckScanDraft();
        var results = new List<object>();
        foreach (var path in Directory.GetFiles(directory, "page-*.jpg").Order())
        {
            var frame = Restore(path);
            if (probe)
            {
                if (Path.GetFileName(path) is not ("page-001.jpg" or "page-011.jpg")) continue;
                foreach (var enhanced in new[] { false, true })
                {
                    var region = new NormalizedRegion(.062, .18, .192, .887);
                    var lines = await text.ReadLinesAsync(frame, region, 3, enhance: enhanced, smooth: true);
                    Console.WriteLine(Path.GetFileName(path) + " name-band enhance=" + enhanced + " " + JsonSerializer.Serialize(lines));
                    if (!enhanced)
                    {
                        foreach (var line in lines.Where(line => line.Text == "Gotyat"))
                        foreach (var mask in new[] { false, true })
                        {
                            var focus = new NormalizedRegion(line.Region.Left - .003, line.Region.Top - .005, line.Region.Right + .005, line.Region.Bottom + .005);
                            var retry = await text.ReadLinesAsync(frame, focus, 4, enhance: false, whiteLetterMask: mask, smooth: true);
                            Console.WriteLine("RETRY mask=" + mask + " " + JsonSerializer.Serialize(retry));
                        }
                        foreach (var line in lines.Where(line => DeckBuilderScanner.MatchLines([line], catalog).Any(item => !item.Card.IsGold)))
                        {
                            var center = (line.Region.Top + line.Region.Bottom) / 2;
                            var badge = new NormalizedRegion(.192, center - .019, .216, center + .019);
                            var marks = await text.ReadLinesAsync(frame, badge, 4, enhance: false, smooth: true);
                            Console.WriteLine("BADGE " + line.Text + " " + JsonSerializer.Serialize(marks));
                        }
                    }
                }
                continue;
            }
            var page = await scanner.ReadAsync(frame, DeckBuilderScanner.LeftPanel);
            drafts.Observe(page.Cards);
            drafts.ObserveHeader(page.Leader, page.Stratagem);
            results.Add(new { Page = Path.GetFileName(path), Cards = page.Cards.Select(item => new { item.Card.Id, item.Card.Name, item.Count }), page.Lines, page.Quantities, page.NameCorrections,
                Leader = page.Leader?.Name, Stratagem = page.Stratagem?.Name });
            Console.WriteLine(Path.GetFileName(path) + ": " + string.Join(", ", page.Cards.Select(item => item.Card.Name + " x" + item.Count)));
        }
        if (probe) return 0;
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "confirmed-deck.json")));
        var truth = expected.RootElement.GetProperty("Cards").EnumerateArray()
            .ToDictionary(item => item.GetProperty("Id").GetString()!, item => item.GetProperty("Count").GetInt32());
        var got = drafts.Cards.ToDictionary(item => item.Card.Id, item => item.Count);
        var missing = truth.Where(item => got.GetValueOrDefault(item.Key) < item.Value)
            .Select(item => $"{catalog.First(card => card.Id == item.Key).Name}: {got.GetValueOrDefault(item.Key)}/{item.Value}").ToArray();
        var excess = got.Where(item => item.Value > truth.GetValueOrDefault(item.Key)).ToArray();
        var report = new { Source = directory, Scope = "One observation per saved keyframe; saved frames are not a complete live time sequence",
            ExpectedCopies = truth.Values.Sum(), ReplayedCopies = drafts.CardCount, Leader = drafts.Leader?.Name, Stratagem = drafts.Stratagem?.Name, Missing = missing, Excess = excess,
            Cards = drafts.Cards.Select(item => new { item.Card.Id, item.Card.Name, item.Count }), Pages = results };
        var output = Path.Combine(root, "GwentCompanion", "diagnostics", "v0.1.6-builder-replay.json");
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"REPLAY {drafts.CardCount}/{truth.Values.Sum()} copies; leader={drafts.Leader?.Name}; stratagem={drafts.Stratagem?.Name}; missing={string.Join(", ", missing)}; excess={excess.Length}; {output}");
        var expectedLeader = expected.RootElement.GetProperty("Leader").GetString();
        var expectedStratagem = expected.RootElement.TryGetProperty("Stratagem", out var stratagem) && stratagem.ValueKind == JsonValueKind.Object
            ? stratagem.GetProperty("Name").GetString() : null;
        return missing.Length == 0 && excess.Length == 0 && drafts.Leader?.Name == expectedLeader &&
            (expectedStratagem is null || drafts.Stratagem?.Name == expectedStratagem) ? 0 : 1;
    }
}
