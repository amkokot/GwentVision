using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Core.Inference;

public sealed record SummonCandidateEvidence(string CardId, double Weight, int Rounds, int Opportunities, string Reason);

/// <summary>Soft negative evidence only. Repeated golds in one round are correlated; Roach can remain in hand.</summary>
public sealed class SummonAbsenceTracker
{
    private sealed class Window(DateTimeOffset at, int round)
    { public DateTimeOffset At = at; public int Round = round; public DateTimeOffset LastScan; public int Scans; public bool Counted; }
    private readonly List<Window> _windows = [];
    private readonly Dictionary<int,int> _misses = [];
    private bool _seen;
    private bool _kingSeen;
    private int _kingUnrefundedTributes;
    private DateTimeOffset _lastAt;
    public void Reset() { _windows.Clear(); _misses.Clear(); _seen=false; _kingSeen=false; _kingUnrefundedTributes=0; _lastAt=default; }
    public void ObserveEvent(VisionEvidenceEvent e, int? round)
    {
        if(e.Sighting.Side!=PlayerSide.Opponent) return;
        if(e.Sighting.Card.Id=="203100") { _kingSeen=true; _kingUnrefundedTributes=0; }
        if(e.Sighting.Card.Id=="112210") { _seen=true; _misses.Clear(); _windows.Clear(); return; }
        if(_seen || e.Sighting.Source!=CardSightSource.PlayPreview || !e.Sighting.Card.IsGold || round is not >=1 or >3) return;
        // One window for a tutor chain, not one penalty for every fetched gold.
        if(_windows.Any(w=>Math.Abs((e.ObservedAt-w.At).TotalSeconds)<15)) return;
        _windows.Add(new(e.ObservedAt,round.Value));
        if(_windows.Count>24) _windows.RemoveAt(0);
    }
    public bool ObserveFrame(DateTimeOffset at,GwentVisualObservation screen,IReadOnlyList<CardSighting> sightings,bool scanned)
    {
        if(at<=_lastAt) return false; _lastAt=at;
        if(sightings.Any(s=>s.Side==PlayerSide.Opponent && s.Card.Id=="112210" && s.Distance<=.3))
        { var changed=_misses.Count>0; _seen=true; _misses.Clear(); _windows.Clear(); return changed; }
        if(_seen || !scanned || screen.View!=GwentViewKind.Board || screen.IsCardSelectionOverlay || screen.MatchHudVisible==false ||
            screen.HasCardTooltip && (screen.TooltipRegion is not {} tip || tip.Top<.47) ||
            sightings.Any(s=>s.Source==CardSightSource.PlayPreview) ||
            !sightings.Any(s=>s.Side==PlayerSide.Opponent && s.Source==CardSightSource.Board && s.Distance<=.3)) return false;
        var updated=false;
        foreach(var window in _windows.Where(w=>!w.Counted && at-w.At>=TimeSpan.FromSeconds(3) && at-w.At<=TimeSpan.FromSeconds(22)))
        {
            if(window.LastScan!=default && at-window.LastScan<TimeSpan.FromMilliseconds(600)) continue;
            window.LastScan=at;
            if(++window.Scans<2) continue;
            window.Counted=true; _misses[window.Round]=Math.Min(2,_misses.GetValueOrDefault(window.Round)+1); updated=true;
        }
        return updated;
    }
    public bool ObserveTributeResolution(bool refunded)
    {
        if (refunded)
        {
            var changed = !_kingSeen || _kingUnrefundedTributes > 0;
            _kingSeen = true; _kingUnrefundedTributes = 0; return changed;
        }
        if (_kingSeen) return false;
        var before = _kingUnrefundedTributes;
        _kingUnrefundedTributes = Math.Min(3, _kingUnrefundedTributes + 1);
        return before != _kingUnrefundedTributes;
    }
    public IReadOnlyList<SummonCandidateEvidence> Evidence
    {
        get
        {
            var evidence = new List<SummonCandidateEvidence>();
            if (!_seen && _misses.Count > 0)
                evidence.Add(new("112210", Math.Max(.05,_misses.Aggregate(1d,(weight,p)=>weight*Math.Pow(p.Key switch {1=>.60,2=>.45,_=>.30},1+.3*(p.Value-1)))),
                    _misses.Count,_misses.Values.Sum(),$"Roach not detected after covered gold-play windows in {_misses.Count} round(s). Progressively downweighted; may be in hand or missed by vision, never ruled out. Repeated same-round triggers are capped."));
            if (!_kingSeen && _kingUnrefundedTributes > 0)
                evidence.Add(new("203100", Math.Max(.12, .35 * Math.Pow(.75, _kingUnrefundedTributes - 1)), 0, _kingUnrefundedTributes,
                    $"{_kingUnrefundedTributes} payable Tribute{(_kingUnrefundedTributes == 1 ? " was" : "s were")} paid without a refund. King of Beggars was not in deck at those moments; it may be absent or bricked in hand, so it is downweighted rather than excluded."));
            return evidence;
        }
    }
}
