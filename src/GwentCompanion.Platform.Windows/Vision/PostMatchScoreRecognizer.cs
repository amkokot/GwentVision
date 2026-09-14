using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Read the settled NEXT result table, never the moving intro or the MMR/XP panel.</summary>
public sealed class PostMatchScoreRecognizer
{
    private static readonly Lazy<HudDigitReader> Digits = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", "result-score-digits.json");
        return new(File.Exists(path) ? JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(path)) ?? [] : []);
    });
    private DateTimeOffset _lastRead, _lastVote;
    private PostMatchRoundScore[]? _candidate;
    private PostMatchRoundScore[]? _confirmed;
    private int _votes;

    public async Task<GwentVisualObservation> ReadAsync(PixelFrame frame, GwentVisualObservation screen,
        DateTimeOffset at, ScreenStateRecognizer reader)
    {
        if (!screen.FrameGeometrySupported || screen.ScreenHeader?.Trim().ToUpperInvariant() is not ("VICTORY" or "DEFEAT" or "DRAW"))
        { _candidate = null; _confirmed = null; _votes = 0; return screen; }
        // Each distinct capture may vote. A time throttle here can discard the
        // second readable frame when the player immediately clicks NEXT.
        if (at <= _lastRead) return screen;
        _lastRead = at;
        if (_confirmed is not null) return screen with { PostMatchRoundScores = _confirmed.ToArray() };
        var next = await reader.ReadAsync(frame, new(.465, .925, .535, .975)).ConfigureAwait(false);
        if (!next.Trim().Equals("NEXT", StringComparison.OrdinalIgnoreCase))
        { Confirm(null, at); return screen; }

        var scores = new List<PostMatchRoundScore>();
        var tops = new[] { .347, .514, .680 };
        for (var round = 0; round < 3; round++)
        {
            var user = await Number(new(.405, tops[round], .462, tops[round] + .069));
            var opponent = await Number(new(.535, tops[round], .594, tops[round] + .069));
            if (user is null || opponent is null) { Confirm(null, at); return screen; }
            scores.Add(new(round + 1, user.Value, opponent.Value));
        }
        _confirmed = Confirm(scores.ToArray(), at);
        return screen with { PostMatchRoundScores = _confirmed?.ToArray() };

        async Task<int?> Number(NormalizedRegion region)
        {
            // Exclude the diamond border. Result digits use a different font from
            // the live scoreboard; the shared glyph reader rejects ambiguous shapes.
            var glyph = Digits.Value.Read(frame, new(region.Left + .009, region.Top, region.Left + .048, region.Bottom));
            if (glyph is not null) return glyph;
            var lines = await reader.ReadLinesAsync(frame, region, scale: 3, enhance: false, smooth: true).ConfigureAwait(false);
            var numbers = lines.Where(l => Regex.IsMatch(l.Text.Trim(), @"^[0-9]{1,4}$"))
                .Select(l => int.Parse(l.Text.Trim())).Distinct().ToArray();
            return numbers.Length == 1 ? numbers[0] : null;
        }
    }
    public PostMatchRoundScore[]? Confirm(PostMatchRoundScore[]? candidate, DateTimeOffset at)
    {
        if (at <= _lastVote) return null;
        if (at - _lastVote > TimeSpan.FromSeconds(3)) { _candidate = null; _votes = 0; }
        _lastVote = at;
        if (candidate is not { Length: 3 } || candidate.Where((s, i) => s.Round != i + 1 ||
            s.UserScore is < 0 or > 9999 || s.OpponentScore is < 0 or > 9999).Any())
        { _candidate = null; _votes = 0; return null; }
        _votes = _candidate is not null && _candidate.SequenceEqual(candidate) ? _votes + 1 : 1;
        _candidate = candidate.ToArray();
        if (_votes < 2) return null;
        // Two trailing empty rows can also follow an early forfeit. Do not invent
        // a played second round when the table alone cannot establish it.
        if (candidate[1] is { UserScore: 0, OpponentScore: 0 } &&
            candidate[2] is { UserScore: 0, OpponentScore: 0 }) return null;
        // Tied rounds award both players a round. A game already decided in the
        // first two rows has an unused 0–0 placeholder in the third row.
        var endedInTwo = candidate.Take(2).All(s => s.UserScore >= s.OpponentScore) ||
            candidate.Take(2).All(s => s.OpponentScore >= s.UserScore);
        if (endedInTwo)
            return candidate[2] is { UserScore: 0, OpponentScore: 0 } ? candidate[..2] : null;
        return candidate.ToArray();
    }

    public void Reset() { _lastRead = default; _lastVote = default; _candidate = null; _confirmed = null; _votes = 0; }
}
