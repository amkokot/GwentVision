using System.Text.RegularExpressions;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Small visible counter crops, throttled to 1 Hz; two matching samples are required.</summary>
public sealed class OpponentHudRecognizer
{
    private DateTimeOffset _last;
    private DateTimeOffset _fastDeckUntil;
    private int? _handVote, _deckVote, _userHandVote, _userDeckVote;
    private int? _userScoreVote, _opponentScoreVote;
    private int? _userCoinVote, _opponentCoinVote;
    private int? _hand, _deck;
    // The coin purse is immediately above the opponent value and below the user
    // value. Keep these crops off the adjacent leader-charge numerals.
    public static readonly NormalizedRegion OpponentCoinRegion = new(.020, .280, .041, .313);
    public static readonly NormalizedRegion UserCoinRegion = new(.020, .687, .041, .720);
    public async Task<GwentVisualObservation> ReadAsync(PixelFrame frame, GwentVisualObservation screen,
        DateTimeOffset at, ScreenStateRecognizer reader)
    {
        if (screen.IsCardSelectionOverlay)
        {
            _fastDeckUntil=at.AddSeconds(3);
            _userDeckVote=null;
            return screen;
        }
        if (screen.View != GwentViewKind.Board) return screen;
        if (at - _last < TimeSpan.FromSeconds(1))
        {
            // Catch a tutored source leaving the pile between ordinary HUD scans.
            // Only two tiny crops, bounded to three seconds after a choice closes.
            // Two distinct reads still required; never guess a source's deck cost.
            if (at<=_fastDeckUntil && at-_lastUserDeckAt>=TimeSpan.FromMilliseconds(180) && screen.MatchHudVisible==true)
            {
                var fastHand=await ReadHandCandidateAsync(frame,new(.925,.94,.995,1),reader).ConfigureAwait(false);
                var fastDeck=fastHand is not null ? await ReadUserDeckCandidateAsync(frame,reader).ConfigureAwait(false) : null;
                var confirmed=at-_lastUserDeckAt<=TimeSpan.FromSeconds(4) && _userDeckVote==fastDeck ? fastDeck : null;
                var confirmedHand=_userHandVote==fastHand ? fastHand : null;
                _userDeckVote=fastDeck; _userHandVote=fastHand; _lastUserDeckAt=at;
                return screen with {UserDeckCount=confirmed,UserHandCount=confirmedHand};
            }
            return screen;
        }
        if (at - _last > TimeSpan.FromSeconds(4)) { _handVote = null; _deckVote = null; _userHandVote = null; _userScoreVote = null; _opponentScoreVote = null; _userCoinVote = null; _opponentCoinVote = null; }
        _last = at;
        var hand = await ReadHandCandidateAsync(frame, new(.925, 0, .995, .065), reader).ConfigureAwait(false);
        var userHand = await ReadHandCandidateAsync(frame, new(.925, .94, .995, 1), reader).ConfigureAwait(false);
        int? deck = null;
        var userDeck = userHand is not null && screen.MatchHudVisible==true
            ? await ReadUserDeckCandidateAsync(frame, reader).ConfigureAwait(false) : null;
        // A selection overlay may hide the HUD for several seconds. A new count still
        // needs two fresh readings; never corroborate it with a stale pre-overlay vote.
        var confirmedUserDeck = at - _lastUserDeckAt <= TimeSpan.FromSeconds(4) && _userDeckVote == userDeck ? userDeck : null;
        _userDeckVote = userDeck; _lastUserDeckAt = at;
        if (hand is not null && screen.MatchHudVisible==true)
        {
            // Each literal /10 authenticates its own side. A player tooltip must
            // not erase a perfectly readable opposing hand/deck counter.
            deck = await ReadOpponentDeckCandidateAsync(frame,reader).ConfigureAwait(false);
            _deck = _deckVote == deck ? deck : null; _deckVote = deck;
        }
        else { _deckVote = null; _deck = null; }
        _hand = _handVote == hand ? hand : null; _handVote = hand;
        var confirmedUserHand = _userHandVote == userHand ? userHand : null; _userHandVote = userHand;
        var opponentScore = await ReadScoreCandidateAsync(frame, new(.90, .315, .995, .385), reader).ConfigureAwait(false);
        var userScore = await ReadScoreCandidateAsync(frame, new(.90, .625, .995, .69), reader).ConfigureAwait(false);
        var confirmedOpponentScore = _opponentScoreVote == opponentScore ? opponentScore : null; _opponentScoreVote = opponentScore;
        var confirmedUserScore = _userScoreVote == userScore ? userScore : null; _userScoreVote = userScore;
        var opponentCoin = await ReadCoinCandidateAsync(frame, OpponentCoinRegion, reader).ConfigureAwait(false);
        var userCoin = await ReadCoinCandidateAsync(frame, UserCoinRegion, reader).ConfigureAwait(false);
        var confirmedOpponentCoin = _opponentCoinVote == opponentCoin ? opponentCoin : null; _opponentCoinVote = opponentCoin;
        var confirmedUserCoin = _userCoinVote == userCoin ? userCoin : null; _userCoinVote = userCoin;
        return screen with { OpponentHandCount = _hand, OpponentDeckCount = _deck, UserHandCount = confirmedUserHand,
            UserScore = confirmedUserScore, OpponentScore = confirmedOpponentScore,
            UserCoins = confirmedUserCoin, OpponentCoins = confirmedOpponentCoin, UserDeckCount = confirmedUserDeck };
    }
    private DateTimeOffset _lastUserDeckAt;
    public static async Task<int?> ReadOpponentDeckCandidateAsync(PixelFrame frame,ScreenStateRecognizer reader)
    {
        var region=new NormalizedRegion(.70,.025,.80,.16);
        // Perspective shrinks the far-side pile digits below the player-side
        // component height. Glyph similarity/margin thresholds stay unchanged.
        var glyph=DeckCounterDigitReader.Read(frame,region,minimumHeightFraction:.009);
        if(glyph is not null) return glyph;
        var lines=await reader.ReadLinesAsync(frame,new(.714,.025,.788,.145),2,enhance:false,whiteLetterMask:true,smooth:true).ConfigureAwait(false);
        var values=lines.Select(line=>ParseDeck(line.Text)).Where(value=>value is not null).Distinct().ToArray();
        if(values.Length==1) return values[0];
        var tight=await reader.ReadLinesAsync(frame,new(.735,.050,.759,.083),2,enhance:false,whiteLetterMask:true,smooth:true).ConfigureAwait(false);
        return ParseDeck(string.Join('\n',tight.Select(line=>line.Text)));
    }
    public static async Task<int?> ReadUserDeckCandidateAsync(PixelFrame frame, ScreenStateRecognizer reader)
    {
        // The board camera tilts during targeting. Cover both deck-diamond positions,
        // but stop before the /10 hand counter, preview and graveyard pile.
        var region = new NormalizedRegion(.79, .77, .925, .955);
        var glyph = DeckCounterDigitReader.Read(frame, region);
        if (glyph is not null) return glyph;
        var lines = await reader.ReadLinesAsync(frame, region, 2,
            enhance: false, whiteLetterMask: true, smooth: true).ConfigureAwait(false);
        var values = lines.Select(line => ParseDeck(line.Text)).Where(value => value is >= 0 and <= 99).Distinct().ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    public static int? ParseHand(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(10|[0-9])\s*/\s*10$");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
    public static async Task<int?> ReadHandCandidateAsync(PixelFrame frame, NormalizedRegion region, ScreenStateRecognizer reader)
    {
        var standard = ParseHandCrop(await reader.ReadAsync(frame, region).ConfigureAwait(false));
        if (standard is not null and not 0) return standard;
        // Full hands use red digits. The high-contrast OCR pass can erase the red
        // leading 1, silently turning 10/10 into a syntactically valid 0/10.
        // Only use an independently read literal token, never assume 0 means 10.
        var left=region.PixelLeft(frame.Width); var top=region.PixelTop(frame.Height);
        var width=region.PixelRight(frame.Width)-left; var height=region.PixelBottom(frame.Height)-top;
        var pixels = new byte[width*height*4];
        for (var y=0;y<height;y++)
        for (var x=0;x<width;x++)
        {
            var i=(y*width+x)*4; var color=frame.GetPixel(left+x,top+y);
            var brightest=Math.Max(color.Red,Math.Max(color.Green,color.Blue));
            pixels[i]=pixels[i+1]=pixels[i+2]=brightest;
            pixels[i+3]=255;
        }
        var colorAware = ParseHandCrop(string.Join('\n', (await reader.ReadLinesAsync(new(width,height,pixels),new(0,0,1,1),
            3,enhance:true,smooth:true).ConfigureAwait(false)).Select(line=>line.Text)));
        return colorAware == 10 ? 10 : standard is null ? colorAware : colorAware is null or 0 ? standard : null;
    }
    // Controller hints (SELECT / CANCEL) can share the counter crop during a hover.
    // Accept one literal x/10 token in that fixed HUD crop, not the surrounding words.
    public static int? ParseHandCrop(string text)
    {
        var matches = Regex.Matches(text, @"(?<![\p{L}\p{N}/])(10|[0-9])\s*/\s*10(?![\p{L}\p{N}/])");
        return matches.Count == 1 ? ParseHand(matches[0].Value) : null;
    }
    public static int? ParseDeck(string text) => Regex.IsMatch(text.Trim(), @"^[0-9]{1,2}$") ? int.Parse(text.Trim()) : null;
    public static int? ParseScore(string text) => Regex.IsMatch(text.Trim(), @"^[0-9]{1,4}$") ? int.Parse(text.Trim()) : null;
    public static int? ParseCoin(string text) => Regex.IsMatch(text.Trim(), @"^[0-9]$") ? int.Parse(text.Trim()) : null;
    /// <summary>Single-frame candidate. Live state requires two matching one-second samples.</summary>
    public static async Task<int?> ReadCoinCandidateAsync(PixelFrame frame, NormalizedRegion region, ScreenStateRecognizer reader)
    {
        var glyph = HudDigitReader.ReadCoin(frame, region) ?? HudDigitReader.ReadDefault(frame, region);
        if (glyph is >= 0 and <= 9) return glyph;
        var text = await reader.ReadAsync(frame, region).ConfigureAwait(false);
        return ParseCoin(text);
    }
    /// <summary>Unconfirmed single-frame candidate, exposed for pixel audits. Live state still requires ReadAsync's two-sample gate.</summary>
    public static async Task<int?> ReadScoreCandidateAsync(PixelFrame frame, NormalizedRegion region, ScreenStateRecognizer reader)
    {
        var glyph = HudDigitReader.ReadDefault(frame, new(.94, region.Top, .99, region.Bottom));
        if (glyph is not null) return glyph;
        // Exclude the round medallion and rope. Accept literal digits only, not OCR letter repairs.
        var tight = ParseScore(await reader.ReadAsync(frame, new(.93, region.Top, .995, region.Bottom)).ConfigureAwait(false));
        if (tight is not null) return tight;
        var lines = await reader.ReadLinesAsync(frame, region, 2, enhance: false, whiteLetterMask: true, smooth: true).ConfigureAwait(false);
        // The leading total is gold. Its luminance shape survives the ordinary OCR crop better
        // than a white-letter mask; never repair ambiguous letters into guessed digits.
        return ParseScore(string.Join('\n', lines.Select(line => line.Text))) ??
            ParseScore(await reader.ReadAsync(frame, region).ConfigureAwait(false));
    }
    public void Reset() { _last = default; _fastDeckUntil = default; _lastUserDeckAt = default; _userDeckVote = null; _handVote = null; _deckVote = null; _userHandVote = null; _hand = null; _deck = null; _userScoreVote = null; _opponentScoreVote = null; _userCoinVote = null; _opponentCoinVote = null; }
}
