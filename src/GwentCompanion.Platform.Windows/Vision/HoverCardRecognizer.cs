using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record HoverReading(CardDefinition? Card, CreatedCardDescription? Description, RuntimeCardValue? RuntimeValue,
    CultistInfusionReading? CultistInfusion = null, NormalizedRegion? TitleRegion = null);

/// <summary>Cheap text-only hover path, independent of the expensive artwork worker.</summary>
public sealed class HoverCardRecognizer(IEnumerable<CardDefinition> catalog)
{
    private readonly CardDefinition[] _catalog = catalog.ToArray();
    private readonly HoverTitleIndex _titles = new(catalog);
    private DateTimeOffset _lastRead, _candidateAt;
    private string? _candidate;
    private CardDefinition? _confirmed;
    private NormalizedRegion? _region;
    private NormalizedRegion? _identityRegion;
    private NormalizedRegion? _titleRegion;
    private byte[]? _identityPixels;
    private RuntimeCardValue? _runtime;
    private CultistInfusionReading? _cultist;
    public bool ReadCultistInfusions { get; set; }
    public Action<string>? Trace { get; set; }
    public void Reset() { _lastRead = _candidateAt = default; _candidate = null; _confirmed = null; _region = null; _runtime = null; _cultist = null; _identityRegion = null; _titleRegion = null; _identityPixels = null; }
    public async Task<HoverReading> ReadAsync(PixelFrame frame, GwentVisualObservation screen, DateTimeOffset at, ScreenStateRecognizer reader)
    {
        if (!screen.HasCardTooltip || screen.TooltipRegion is not { } region) { Trace?.Invoke("no tooltip"); Reset(); return new(null, null, null); }
        var moved = _region != region;
        if (moved) { _runtime = null; _cultist = null; } // A fresh title read below still verifies identity after movement.
        _region = region;
        // Confirm a new title on the next retained frame, rather than waiting two 650ms periods.
        // A pointer can move between a board card and a hand card while the coarse
        // beige component stays in the same cell. Time/geometry alone cannot reuse
        // its name. Reuse only byte-identical pixels of the actual title line.
        if (!moved && IdentityUnchanged(frame) && at - _lastRead < TimeSpan.FromMilliseconds(_confirmed is null ? 180 : 350)) return new(_confirmed, null, null, TitleRegion: _titleRegion);
        _lastRead = at;
        var crop = new NormalizedRegion(Math.Max(0, region.Left - .02), Math.Max(0, region.Top - .075),
            Math.Min(1, region.Right + .02), Math.Min(1, region.Bottom + .02));
        // The black title strip needs different preprocessing from beige ability prose.
        // Contrast enhancement erased "False" in False Ciri on the reviewed recording.
        var header = new NormalizedRegion(crop.Left, Math.Max(0, region.Top - .10), crop.Right, Math.Min(1, region.Top + .015));
        var titleLines = await reader.ReadLinesAsync(frame, header, scale: 2, enhance: false, smooth: true).ConfigureAwait(false);
        var text = string.Join('\n', titleLines.Select(line => line.Text));
        var card = _titles.Read(text);
        var exactHeader = card is not null;
        string? body = null;
        if (card is null)
        {
            // Beige components can merge with nearby card art, so their top edge is not
            // always the tooltip title edge. Search the located component for an exact
            // catalog-title line before applying prose-oriented enhancement.
            titleLines = await reader.ReadLinesAsync(frame, crop, scale: 2, enhance: false, smooth: true).ConfigureAwait(false);
            var visible = string.Join('\n', titleLines.Select(line => line.Text));
            card = _titles.Read(visible);
            exactHeader = card is not null;
            if (!string.IsNullOrWhiteSpace(visible)) text += "\n" + visible;
        }
        if (card is null)
        {
            // Reuse Gwent bright-title preprocessing for tooltips. Exact complete
            // names only; no fuzzy substitution or names from ability prose.
            var styled = await reader.ReadTitleLinesAsync(frame, header).ConfigureAwait(false);
            var candidates = styled.Where(line => TitleTextStyle.IsBrightTitleLine(frame,line.Region) &&
                    TitleTextStyle.CoversBrightText(frame,line.Region,header))
                .Select(line => _titles.Read(line.Text)).Where(value => value is not null).DistinctBy(value=>value!.Id).ToArray();
            if (candidates.Length == 1) { card=candidates[0]; exactHeader=true; titleLines=styled; }
            else if (candidates.Length == 0)
            {
                var equivalent=styled.Where(line=>TitleTextStyle.IsBrightTitleLine(frame,line.Region) &&
                        TitleTextStyle.CoversBrightText(frame,line.Region,header))
                    .Select(line=>_titles.ReadGlyphEquivalent(line.Text)).Where(value=>value is not null).DistinctBy(value=>value!.Id).ToArray();
                if (equivalent.Length==1) { card=equivalent[0]; exactHeader=false; titleLines=styled; }
            }
            Trace?.Invoke("hover style="+string.Join('|',styled.Select(line=>line.Text)));
        }
        if (card is null)
        {
            var bodyLines = await reader.ReadLinesAsync(frame, crop).ConfigureAwait(false);
            body = string.Join('\n', bodyLines.Select(line => line.Text));
            card = _titles.Read(body);
            if (card is not null) titleLines = bodyLines;
        }
        Trace?.Invoke($"region={region} moved={moved} text={text.Replace('\n', '|')} match={card?.Name ?? "-"} prior={_candidate ?? "-"}");
        // A changed/failed title immediately retires the old identity. No old card during a new hover.
        // Header matching is exact against the normalized catalog title. It is safe to
        // publish in one frame and important for video/rolling-buffer samples where a
        // tooltip may be unobscured only briefly. A title recovered from the noisy beige
        // ability body still has to recur before it can identify a card.
        _confirmed = exactHeader
            ? card
            : card is not null && card.Id == _candidate && at > _candidateAt && at - _candidateAt <= TimeSpan.FromSeconds(1.5)
                ? card
                : null;
        _candidate = card?.Id; _candidateAt = at;
        var lineRegion = card is null ? null : titleLines.FirstOrDefault(line => _titles.Read(line.Text)?.Id == card.Id)?.Region;
        _titleRegion = lineRegion;
        Trace?.Invoke($"hover title region={lineRegion?.ToString() ?? "-"}");
        RememberIdentity(frame, lineRegion is { } line ? new(Math.Max(0,line.Left-.005),Math.Max(0,line.Top-.005),Math.Min(1,line.Right+.005),Math.Min(1,line.Bottom+.005)) : crop);
        if (card?.Id == CultistSynergyTracker.ScenarioId || card?.HasCategory("Cultist") == true) ReadCultistInfusions = true;
        // Only these cards currently expose implemented dynamic text. Most hovers need
        // one small header OCR, not a full ability paragraph or a scan of all card names.
        if (card?.Id is "203102" or "202360" or "203275" or "203198" or "200022" or "202192" or "202219" or "203159")
            body ??= await reader.ReadAsync(frame, crop).ConfigureAwait(false);
        // Any unit can be infused. Enable the larger text read only after Cultist
        // context appears, leaving the ordinary one-header hover path unchanged.
        if (ReadCultistInfusions && card?.Kind == CardKind.Unit)
            body ??= await reader.ReadAsync(frame, crop).ConfigureAwait(false);
        var value = RuntimeCardValueReader.Read(_confirmed, body ?? text);
        var measured = value is not null && value == _runtime ? value : null;
        _runtime = value;
        var cultist = CultistInfusionReader.Read(_confirmed, body);
        var measuredCultist = cultist is not null && cultist.SameAs(_cultist) ? cultist : null;
        _cultist = cultist;
        return new(_confirmed, body is null ? null : CreatedCardDescriptionReader.Parse(body, _catalog), measured, measuredCultist, _titleRegion);
    }

    private bool IdentityUnchanged(PixelFrame frame)
    {
        if (_identityRegion is not { } r || _identityPixels is null) return false;
        var left=r.PixelLeft(frame.Width); var top=r.PixelTop(frame.Height);
        var width=r.PixelRight(frame.Width)-left; var height=r.PixelBottom(frame.Height)-top;
        if (_identityPixels.Length != width*height*4) return false;
        for(var y=0;y<height;y++)
            if(!frame.BgraPixels.AsSpan(((top+y)*frame.Width+left)*4,width*4).SequenceEqual(_identityPixels.AsSpan(y*width*4,width*4))) return false;
        return true;
    }
    private void RememberIdentity(PixelFrame frame, NormalizedRegion r)
    {
        _identityRegion=r;
        var left=r.PixelLeft(frame.Width); var top=r.PixelTop(frame.Height);
        var width=r.PixelRight(frame.Width)-left; var height=r.PixelBottom(frame.Height)-top;
        _identityPixels=new byte[width*height*4];
        for(var y=0;y<height;y++) frame.BgraPixels.AsSpan(((top+y)*frame.Width+left)*4,width*4).CopyTo(_identityPixels.AsSpan(y*width*4,width*4));
    }
}
