using System.Text.RegularExpressions;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Result-screen-only OCR for mutually exclusive faction-MMR and standard-rank layouts.</summary>
public sealed class PostMatchMmrRecognizer
{
    private const int ResultReadIntervalMilliseconds = 100;
    private const int RankVoteIntervalMilliseconds = 250;
    private DateTimeOffset _lastRead;
    private DateTimeOffset _lastVote;
    private PostMatchMmr? _candidate;
    private int _votes;
    private DateTimeOffset _lastRankVote;
    private PostMatchRank? _rankCandidate;
    private int _rankVotes;
    private int? _rankNumberCandidate;
    private int _rankNumberVotes;
    private DateTimeOffset _lastMainMenuVote;
    private int _mainMenuVotes;

    public static bool IsResultHeader(string? header) => PostMatchMmr.IsResultHeader(header);

    public async Task<GwentVisualObservation> ReadAsync(PixelFrame frame, GwentVisualObservation screen, DateTimeOffset at, ScreenStateRecognizer reader)
    {
        if (!IsResultHeader(screen.ScreenHeader))
        {
            if (screen.MatchHudVisible != false) { ClearCandidates(); return screen; }
            // The progression animation can be skipped before its first retained
            // sample. Standard Mode exposes the same current/season-peak pair in a
            // fixed central panel and is also a reliable end-of-match exit cue.
            if (at - _lastRead < TimeSpan.FromMilliseconds(ResultReadIntervalMilliseconds)) return screen;
            _lastRead = at;
            var labelLines = await reader.ReadLinesAsync(frame, new(.35, .29, .72, .43),
                scale: 2, enhance: false, whiteLetterMask: true, smooth: true).ConfigureAwait(false);
            var label = string.Join('\n', labelLines.Select(line => line.Text));
            if (!IsStandardModeMenu(label)) { ClearCandidates(); return screen; }
            var numbers = await reader.ReadLinesAsync(frame, new(.44, .49, .67, .55),
                scale: 3, enhance: false, whiteLetterMask: true, smooth: true).ConfigureAwait(false);
            var menuCandidate = ParseMainMenu(label, numbers);
            if (menuCandidate is null)
            {
                // Some display/scaling combinations render the gold digits too dimly
                // for the white-letter mask. Retry a taller/wider lane with ordinary
                // contrast OCR before falling back to the fixed glyph reader.
                var alternateNumbers = await reader.ReadLinesAsync(frame, new(.42, .46, .70, .60),
                    scale: 3, enhance: true, whiteLetterMask: false, smooth: true).ConfigureAwait(false);
                menuCandidate = ParseMainMenu(label, alternateNumbers);
            }
            if (menuCandidate is null)
            {
                var current = HudDigitReader.ReadDefault(frame, new(.455, .495, .505, .55));
                var peak = HudDigitReader.ReadDefault(frame, new(.515, .495, .565, .55));
                if (current is >= 0 and <= 10000 && peak is >= 0 and <= 10000 && current <= peak)
                    menuCandidate = new(current, null, true, "Standard Mode summary (current / season peak)", peak);
            }
            var exitCue = ConfirmMainMenu(at);
            var menuReading = Confirm(menuCandidate, at, ResultReadIntervalMilliseconds);
            _rankCandidate = null; _rankVotes = 0; _rankNumberCandidate = null; _rankNumberVotes = 0;
            return screen with { IsCardSelectionOverlay = true, CardSelectionConfidence = 1,
                ScreenHeader = "STANDARD MODE", PostMatchMmr = menuReading, PostMatchRank = null,
                PostMatchExitCue = exitCue, PostMatchMmrCandidate = menuCandidate is null ? null :
                    menuCandidate with { Confirmed = false, ReadCount = _votes } };
        }
        _mainMenuVotes = 0; _lastMainMenuVote = default;
        screen = screen with { IsCardSelectionOverlay = true, CardSelectionConfidence = 1 };
        // Result/progression panels are sometimes skipped in under a second. The
        // post-game pipeline is numeric-only. Two independently captured reads at
        // capture cadence confirm the fixed result layout before it can be skipped.
        if (at - _lastRead < TimeSpan.FromMilliseconds(ResultReadIntervalMilliseconds)) return screen;
        _lastRead = at;
        // User/central result panel only. Never read the right-hand opponent profile as ours.
        var lines = await reader.ReadLinesAsync(frame, new(.02, .12, .75, .94), scale: 2).ConfigureAwait(false);
        var candidate = Parse(screen.ScreenHeader, lines);
        PostMatchRank? rankCandidate = null;
        if (candidate is null)
        {
            var label = await reader.ReadAsync(frame, new(.43, .44, .58, .50)).ConfigureAwait(false);
            if (label.Trim().Equals("RANKED", StringComparison.OrdinalIgnoreCase))
            {
                var numbers = await reader.ReadLinesAsync(frame, new(.46, .25, .54, .35),
                    scale: 2, enhance: false, smooth: true).ConfigureAwait(false);
                candidate = ParseRankedPanel(screen.ScreenHeader, label, numbers);
                if (candidate is null)
                {
                    var rankNumbers = await reader.ReadLinesAsync(frame, new(.46, .34, .54, .44),
                        scale: 3, enhance: false, whiteLetterMask: true, smooth: true).ConfigureAwait(false);
                    rankCandidate = ParseRankPanel(screen.ScreenHeader, label, rankNumbers);
                    var glyph = HudDigitReader.ReadRank(frame, new(.485, .37, .52, .435));
                    var glyphCandidate = glyph is >= 0 and <= 30 ? new PostMatchRank(glyph.Value, "Ranked ladder shield glyph") : null;
                    rankCandidate = rankCandidate is not null && glyphCandidate is not null && rankCandidate.Rank != glyphCandidate.Rank
                        ? null : rankCandidate ?? glyphCandidate;
                    // RANKED labels both layouts. A missing diamond reading does
                    // not prove this is a ladder shield: fabricating a rank result
                    // here caused auto-stop before the faction rating was acquired.
                }
            }
        }
        var reading = Confirm(candidate, at, ResultReadIntervalMilliseconds);
        var rankReading = ConfirmRank(rankCandidate, at);
        return screen with { PostMatchMmr = reading, PostMatchRank = rankReading,
            PostMatchMmrCandidate = candidate is null ? null : candidate with { Confirmed = false, ReadCount = _votes } };
    }

    public static PostMatchRank? ParseRankPanel(string? header, string label, IReadOnlyList<VisibleTextLine> numbers)
    {
        if (!IsResultHeader(header) || !label.Trim().Equals("RANKED", StringComparison.OrdinalIgnoreCase)) return null;
        var candidates = numbers.Where(line => line.Region.Left >= .46 && line.Region.Right <= .54 &&
            line.Region.Top >= .34 && line.Region.Bottom <= .44 && Regex.IsMatch(line.Text.Trim(), @"^[0-9]{1,2}$"))
            .Select(line => int.Parse(line.Text.Trim())).Where(rank => rank is >= 0 and <= 30).Distinct().ToArray();
        return candidates.Length == 1 ? new(candidates[0], "Ranked ladder shield") : null;
    }

    public static PostMatchMmr? ParseRankedPanel(string? header, string label, IReadOnlyList<VisibleTextLine> numbers)
    {
        if (!IsResultHeader(header) || !label.Trim().Equals("RANKED", StringComparison.OrdinalIgnoreCase) || numbers.Count != 2) return null;
        var ordered = numbers.OrderBy(line => line.Region.Top).ToArray();
        if (ordered.Any(line => line.Region.Left < .46 || line.Region.Right > .54 ||
            line.Region.Top < .25 || line.Region.Bottom > .35 || !Regex.IsMatch(line.Text.Trim(), @"^[0-9]{4}$")) ||
            ordered[0].Region.Bottom > ordered[1].Region.Top || ordered[0].Region.Top >= .31 || ordered[1].Region.Top < .31) return null;
        // User-verified UI semantics: the large upper rating is the season PEAK;
        // the smaller lower rating is CURRENT. These are not a before/after pair.
        var peak = int.Parse(ordered[0].Text.Trim()); var current = int.Parse(ordered[1].Text.Trim());
        if (current > peak) return null;
        return new(current, null, true, "Ranked faction diamond (lower current / upper season peak)", peak);
    }

    public static bool IsStandardModeMenu(string text)
    {
        var compact = string.Concat(text.Where(char.IsLetter)).ToUpperInvariant();
        return compact.Contains("STANDARDMODE", StringComparison.Ordinal) &&
            compact.Contains("SEASON", StringComparison.Ordinal);
    }

    public static PostMatchMmr? ParseMainMenu(string label, IReadOnlyList<VisibleTextLine> numbers)
    {
        if (!IsStandardModeMenu(label)) return null;
        var values = numbers.OrderBy(line => line.Region.Left).SelectMany(line =>
                Regex.Matches(line.Text, @"(?<![0-9])[0-9]{3,4}(?![0-9])").Cast<Match>()
                    .Select(match => (Value: int.Parse(match.Value), line.Region)))
            .Where(item => item.Value is >= 0 and <= 10000).ToArray();
        if (values.Length < 2)
        {
            // The left lane is always current faction MMR. Retain it when peak OCR
            // fails; two agreeing frames are still required before confirmation.
            var currentOnly = values.Where(item => item.Region.Left >= .43 && item.Region.Right <= .53)
                .Select(item => item.Value).Distinct().ToArray();
            return currentOnly.Length == 1
                ? new(currentOnly[0], null, true, "Standard Mode summary (current; season peak unread)")
                : null;
        }
        // The panel can show current MMR, season peak, and a rightmost leaderboard
        // position. With all three present the first pair is unambiguous. When OCR
        // sees only two, reject a right-lane second value instead of caching rank
        // position as season peak (2393 / 3209 in the retained September panel).
        var peak = values[1];
        if (values.Length == 2 && peak.Region.Right > .59) return null;
        if (values[0].Value > peak.Value) return null;
        return new(values[0].Value, null, true, "Standard Mode summary (current / season peak)", peak.Value);
    }

    public PostMatchMmr? Confirm(PostMatchMmr? candidate, DateTimeOffset at, int intervalMs = 600)
    {
        // Match the OCR cadence; a longer vote interval silently discards every
        // second read and can miss a short progression panel entirely.
        if (at <= _lastVote || at - _lastVote < TimeSpan.FromMilliseconds(intervalMs)) return null;
        if (at - _lastVote > TimeSpan.FromSeconds(4)) { _candidate = null; _votes = 0; }
        _lastVote = at;
        var compatible = candidate is not null && _candidate is not null &&
            candidate.RatingAfter == _candidate.RatingAfter && candidate.Change == _candidate.Change &&
            candidate.IsFactionRating == _candidate.IsFactionRating &&
            (candidate.SeasonPeak == _candidate.SeasonPeak || candidate.SeasonPeak is null || _candidate.SeasonPeak is null);
        _votes = compatible ? _votes + 1 : candidate is null ? 0 : 1;
        _candidate = compatible && candidate!.SeasonPeak is null && _candidate!.SeasonPeak is not null
            ? _candidate : candidate;
        return _votes >= 2 ? _candidate : null;
    }

    public PostMatchRank? ConfirmRank(PostMatchRank? candidate, DateTimeOffset at)
    {
        if (at <= _lastRankVote || at - _lastRankVote < TimeSpan.FromMilliseconds(RankVoteIntervalMilliseconds)) return null;
        if (at - _lastRankVote > TimeSpan.FromSeconds(4))
        { _rankCandidate = null; _rankVotes = 0; _rankNumberCandidate = null; _rankNumberVotes = 0; }
        _lastRankVote = at;
        _rankVotes = candidate is not null ? _rankVotes + 1 : 0;
        if (candidate?.Rank is { } number)
        {
            _rankNumberVotes = _rankNumberCandidate == number ? _rankNumberVotes + 1 : 1;
            _rankNumberCandidate = number;
        }
        else if (candidate is null) { _rankNumberCandidate = null; _rankNumberVotes = 0; }
        _rankCandidate = candidate;
        // The standard-rank progression panel can be visible for only about
        // 1.4 seconds. Two capture-separated reads fit that animation while the
        // exact VICTORY/DEFEAT header plus fixed RANKED label keep the screen
        // gate substantially stronger than an ordinary OCR number match.
        return _rankVotes >= 2 && candidate?.Rank is not null && _rankNumberVotes >= 2
            ? candidate with { Rank = _rankNumberCandidate }
            : null;
    }

    private bool ConfirmMainMenu(DateTimeOffset at)
    {
        if (at <= _lastMainMenuVote || at - _lastMainMenuVote < TimeSpan.FromMilliseconds(ResultReadIntervalMilliseconds)) return false;
        _mainMenuVotes = at - _lastMainMenuVote <= TimeSpan.FromSeconds(4) ? _mainMenuVotes + 1 : 1;
        _lastMainMenuVote = at;
        return _mainMenuVotes >= 2;
    }

    public static PostMatchMmr? Parse(string? header, IReadOnlyList<VisibleTextLine> lines)
    {
        if (!IsResultHeader(header)) return null;
        var results = new List<PostMatchMmr>();
        foreach (var line in lines)
        {
            if ((line.Region.Left + line.Region.Right) / 2 > .65) continue;
            var text = line.Text.Trim().Replace('−', '-').Replace('–', '-');
            var match = Regex.Match(text,
                @"^(?:(?:CURRENT|YOUR)\s+)?(?<label>FACTION\s+MMR|FMMR|MMR)\s*:?\s*(?<value>[0-9]{3,4}|10000)?\s*(?:\(?(?<delta>[+-]\s*[0-9]{1,3})\)?)?\s*$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var values = new HashSet<int>(); var deltas = new HashSet<int>();
            if (match.Groups["value"].Success) values.Add(int.Parse(match.Groups["value"].Value));
            if (match.Groups["delta"].Success) deltas.Add(int.Parse(match.Groups["delta"].Value.Replace(" ", "")));
            foreach (var nearby in lines.Where(other => !ReferenceEquals(other, line)))
            {
                var center = (nearby.Region.Left + nearby.Region.Right) / 2;
                var dy = nearby.Region.Top - line.Region.Bottom;
                var sameRow = Math.Abs((nearby.Region.Top + nearby.Region.Bottom - line.Region.Top - line.Region.Bottom) / 2) < .018 &&
                    nearby.Region.Left >= line.Region.Right && nearby.Region.Left - line.Region.Right < .06;
                var below = dy is >= -.005 and <= .075 && center >= line.Region.Left - .03 && center <= line.Region.Right + .06;
                if (center > .65 || !(sameRow || below)) continue;
                var number = nearby.Text.Trim().Replace('−', '-').Replace('–', '-');
                if (Regex.IsMatch(number, @"^(?:[0-9]{3,4}|10000)$")) values.Add(int.Parse(number));
                if (Regex.IsMatch(number, @"^\(?[+-]\s*[0-9]{1,3}\)?$")) deltas.Add(int.Parse(number.Replace("(", "").Replace(")", "").Replace(" ", "")));
            }
            if (values.Count > 1 || deltas.Count > 1 || values.Count + deltas.Count == 0) continue;
            int? rating = values.Count == 1 ? values.Single() : null;
            int? change = deltas.Count == 1 ? deltas.Single() : null;
            if (change is < -200 or > 200 || rating - change is < 0 or > 10000) continue;
            var label = match.Groups["label"].Value.ToUpperInvariant();
            results.Add(new(rating, change, label != "MMR", label));
        }
        // Multiple rating panels/scopes are ambiguous. Do not select whichever OCR saw first.
        var distinct = results.Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private void ClearCandidates()
    {
        _candidate = null; _votes = 0; _rankCandidate = null; _rankVotes = 0; _rankNumberCandidate = null; _rankNumberVotes = 0;
        _mainMenuVotes = 0; _lastMainMenuVote = default;
    }

    public void Reset()
    {
        _lastRead = default; _lastVote = default; _lastRankVote = default; ClearCandidates();
    }
}
