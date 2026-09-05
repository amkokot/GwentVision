using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using System.Text.RegularExpressions;

namespace GwentCompanion.Core.Inference;

/// <summary>Independent hand-cost corroboration, never a new play event or guessed card identity.</summary>
public sealed class HandCommitTracker
{
    private readonly Dictionary<PlayerSide,(int Count,DateTimeOffset At)> _hands=[];
    private readonly Dictionary<PlayerSide,Pending> _pending=[];
    private readonly Dictionary<PlayerSide,DateTimeOffset> _ambiguous=[];
    private readonly Dictionary<PlayerSide,(int Count,DateTimeOffset At)> _jumpCandidates=[];
    private readonly Dictionary<PlayerSide,RefillingHandPlays> _refilling=[];
    private static readonly Regex RefillingHandAbility = new(
        @"\bPlay up to (?<count>[1-9]\d*) (?<filter>bronze|gold|unit|special|artifact) cards? from your hand,\s*then draw as many cards\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public void Reset() { _hands.Clear(); _pending.Clear(); _ambiguous.Clear(); _jumpCandidates.Clear(); _refilling.Clear(); }
    public IReadOnlyList<VisionEvidenceEvent> Observe(DateTimeOffset at,GwentVisualObservation screen,IReadOnlyList<VisionEvidenceEvent> events)
    {
        var confirmed=new List<VisionEvidenceEvent>();
        foreach(var side in Enum.GetValues<PlayerSide>())
        {
            int? count=side==PlayerSide.User ? screen.UserHandCount : screen.OpponentHandCount;
            var prior=_hands.TryGetValue(side,out var h) && at-h.At<=TimeSpan.FromSeconds(20) ? h : ((int Count,DateTimeOffset At)?)null;
            if (_pending.TryGetValue(side,out var expired) && at-expired.Event.ObservedAt>TimeSpan.FromSeconds(12)) _pending.Remove(side);
            if (_refilling.TryGetValue(side,out var expiredRefill) && at-expiredRefill.SourceAt>TimeSpan.FromSeconds(20)) _refilling.Remove(side);
            if (count is {} fresh)
            {
                var jump=prior is {} old && (fresh>old.Count || fresh<old.Count-1);
                if(jump)
                {
                    // A single misread (notably 3↔6) must not poison the next
                    // genuine play for twelve seconds. Real round draws/tutor
                    // jumps persist and are accepted on a second matching read.
                    if(_jumpCandidates.TryGetValue(side,out var candidate) && candidate.Count==fresh &&
                        at>candidate.At && at-candidate.At<=TimeSpan.FromSeconds(6))
                    {
                        _pending.Remove(side); _ambiguous[side]=at.AddSeconds(12);
                        _hands[side]=(fresh,at); _jumpCandidates.Remove(side);
                    }
                    else
                    {
                        _jumpCandidates[side]=(fresh,at);
                        count=null;
                    }
                }
                else
                {
                    _jumpCandidates.Remove(side);
                    _hands[side]=(fresh,at);
                }
            }
            foreach(var source in events.Where(e=>e.Sighting.Side==side && e.Sighting.Source==CardSightSource.PlayPreview))
            {
                var match=RefillingHandAbility.Match(source.Sighting.Card.AbilityText??"");
                if(match.Success && int.TryParse(match.Groups["count"].Value,out var plays) && plays is >0 and <=5)
                    _refilling[side]=new(source.Sighting.Card.Id,source.ObservedAt,plays,match.Groups["filter"].Value);
            }
            foreach(var action in events.Where(e=>e.Sighting.Side==side && e.Sighting.Source==CardSightSource.PlayPreview))
            {
                // Some cards play a bounded number of cards from hand and immediately
                // draw the same number. The stable post-resolution hand count hides the
                // normal one-card decrement, but the printed source text still proves
                // that its next matching previews are independent hand plays. The exact
                // count, side, card class and short time window keep this conservative.
                if(_refilling.TryGetValue(side,out var refill) && action.ObservedAt>refill.SourceAt &&
                    action.Sighting.Card.Id!=refill.SourceId && MatchesRefillFilter(action.Sighting.Card,refill.Filter))
                {
                    confirmed.Add(action with { Description=action.Description+
                        $" Printed {refill.Filter} hand-play/refill sequence ({refill.Remaining} bounded slot(s) before this event)." });
                    if(refill.Remaining==1) _refilling.Remove(side);
                    else _refilling[side]=refill with { Remaining=refill.Remaining-1 };
                    _pending.Remove(side);
                    continue;
                }
                // Multiple different previews can be a tutor/create chain. A
                // shared decrement cannot identify which child came from hand.
                if(_pending.TryGetValue(side,out var previous) && previous.Event.Sighting.Card.Id!=action.Sighting.Card.Id)
                {
                    // If a stable unchanged HUD read occurred after the earlier
                    // preview, that action cannot explain a decrement first seen
                    // with this newer preview. Attribute the cost to the new play.
                    // This is important when a generated Order/Deploy payoff is
                    // followed several seconds later by a genuine hand play.
                    if(prior is {} stable && stable.At>previous.Event.ObservedAt && count==stable.Count-1)
                        _pending[side]=new(action,stable.Count);
                    else
                    {
                        _pending.Remove(side); _ambiguous[side]=at.AddSeconds(12);
                        continue;
                    }
                }
                if(!_pending.ContainsKey(side) && prior is {} before && (!_ambiguous.TryGetValue(side,out var until) || at>until))
                    _pending[side]=new(action,before.Count);
            }
            if(_pending.TryGetValue(side,out var pending) && count==pending.HandBefore-1 && at>=pending.Event.ObservedAt &&
                !screen.IsCardSelectionOverlay && screen.MatchHudVisible==true)
            {
                confirmed.Add(pending.Event); _pending.Remove(side);
            }
        }
        return confirmed;
    }
    private sealed record Pending(VisionEvidenceEvent Event,int HandBefore);
    private sealed record RefillingHandPlays(string SourceId,DateTimeOffset SourceAt,int Remaining,string Filter);
    private static bool MatchesRefillFilter(CardDefinition card,string filter) => filter.ToLowerInvariant() switch
    {
        "bronze" => !card.IsGold,
        "gold" => card.IsGold,
        "unit" => card.Kind==CardKind.Unit,
        "special" => card.Kind==CardKind.Special,
        "artifact" => card.Kind==CardKind.Artifact,
        _ => false,
    };

    public static bool Apply(IReadOnlyList<VisionEvidenceEvent> confirmed,PlayProvenanceResolver origins,DeckMutationLedger mutations,
        ThinningCopyTracker copies,LiveDeckTracker user,LiveDeckTracker opponent,DeckDefinition? reference,
        Func<CardSighting,bool>? zoneRisk=null)
    {
        var changed=false;
        foreach(var e in confirmed)
        {
            var s=e.Sighting; var tracker=s.Side==PlayerSide.User?user:opponent;
            if (!s.Card.CanBeInStartingDeck || s.Side==PlayerSide.User && reference is not null && reference.CountOf(s.Card.Id)==0 ||
                zoneRisk?.Invoke(s)==true || origins.HasNonOrderCopyRisk(s,e.ObservedAt) || mutations.HasRecentReplay(s.Side,e.ObservedAt) ||
                mutations.OriginRisk(s,e.ObservedAt,tracker.Observations,additionalCopy:true,independentHandPlay:true) is not null) continue;
            changed |= tracker.ConsiderDirectPlay(s.Card,1-s.Distance,e.ObservedAt,
                "Recognized play independently corroborated by a one-card hand decrement or a bounded printed hand-play/refill sequence; outside-hand generation does not explain that cost.",
                CardProvenance.ProbableStartingDeck);
            if (s.Side!=PlayerSide.User || reference is null || reference.CountOf(s.Card.Id)>1)
                changed |= copies.ObserveEvent(e,CardProvenance.ProbableStartingDeck,tracker);
        }
        return changed;
    }
}
