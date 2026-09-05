using System.Collections.Immutable;
using System.Diagnostics;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.GameState;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Vision;

internal static class PriorityReachTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        var clock = new TestClock();
        var priority = new GameplayWorkPriority(clock);
        using var cancelled = new CancellationTokenSource();
        using (priority.EnterRecognition())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = Task.Run(() => { started.SetResult(); priority.WaitForReach(cancelled.Token); });
            await started.Task;
            await Task.Delay(40);
            Check(!waiter.IsCompleted, "Reach ran while card recognition owned the budget.");
            cancelled.Cancel();
            try { await waiter.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("Paused Reach ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        Check(priority.PauseReason is null, "Recognition lease leaked after completion.");
        priority.SignalAction();
        Check(priority.PauseReason is not null, "Sudden action did not reserve recognition time.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(priority.PauseReason is null, "A settled turn never resumed Reach.");
        priority.ObservePressure(4, TimeSpan.Zero);
        Check(priority.PauseReason is not null, "Backlogged recognition did not pause Reach.");
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(priority.PauseReason is null, "Expired pressure permanently starved Reach.");

        var schedule = new VisionScanSchedule();
        var at = DateTimeOffset.UnixEpoch;
        Check(schedule.NextIncludesBoard(at, 0, 0, 10, opponentHand: 10), "Initial board scan missing.");
        Check(!schedule.NextIncludesBoard(at.AddMilliseconds(500), 0, 0, 10, opponentHand: 10), "Heavy scan guard missing.");
        Check(schedule.NextIncludesBoard(at.AddSeconds(1), 0, 0, 10, opponentHand: 9),
            "Scoreless opponent play did not request a board scan.");
        Check(!schedule.NextIncludesBoard(at.AddMilliseconds(1500), 0, 0, 10, opponentHand: 9), "Opponent signal caused endless scans.");
        Check(!schedule.NextIncludesBoard(at.AddSeconds(2), 0, 0, 10, opponentHand: 9) &&
            !schedule.NextIncludesBoard(at.AddMilliseconds(2500), 0, 0, 10, opponentHand: 9) &&
            schedule.NextIncludesBoard(at.AddSeconds(4), 0, 0, 10, opponentHand: 9), "Unsignaled periodic board coverage disappeared.");

        var body = new CardDefinition("priority-body", "Body", "Monsters", CardKind.Unit, 4, 5, AbilityText: "", PrintedArmor: 0);
        var reply = body with { Id = "priority-reply", Name = "Reply", Power = 7 };
        var second = body with { Id = "priority-second", Name = "Second", Power = 9 };
        CardDefinition[] catalog = [body, reply, second];
        var position = GamePosition.EmptyKnown() with { Round = 1, ActivePlayer = PlayerSide.User };
        var input = ThreatPositionBuilder.Build(position, catalog, body, null, [], []);
        ThreatCandidate[] candidates = [new(reply, .8, "Hypothesis", 1, .4), new(second, .6, "Hypothesis", 1, .3)];
        var analyzer = new ThreatAnalyzer(catalog);
        var progress = new List<ThreatProgress>();
        var first = analyzer.Analyze(input, 0, candidates, progress: progress.Add);
        Check(first.Complete && progress.Count >= 2 && progress.All(p => !p.Report.Complete && p.Report.Reach is null),
            "Partial work was missing or incorrectly classified opponent reach as complete.");
        Check(progress[0].Completed == 1 && progress[0].Report.Replies.Count == 0 &&
            progress.Select(p => p.Completed).SequenceEqual(progress.Select(p => p.Completed).Order()), "Progress did not start with the player then accumulate replies.");
        Check(ReferenceEquals(first, analyzer.Analyze(input, 0, candidates)), "Unchanged complete report was recalculated.");
        using (var alreadyCancelled = new CancellationTokenSource())
        {
            alreadyCancelled.Cancel();
            try { analyzer.Analyze(input, 0, candidates, alreadyCancelled.Token); throw new InvalidOperationException("Cancelled request returned a cached result."); }
            catch (OperationCanceledException) { }
        }
        var before = analyzer.CandidateCacheHits;
        var rescored = analyzer.Analyze(input, 2, candidates.Select(c => c with { ModelShare = .2 }).ToArray());
        Check(analyzer.CandidateCacheHits >= before + 3 && rescored.GapAfterPlay == first.GapAfterPlay + 2,
            "Changed score/model weights recomputed card transitions or reused a stale report.");
        Check(ThreatAnalyzer.RequestKey(input, 0, candidates) != ThreatAnalyzer.RequestKey(input, 0,
            candidates.Select(c => c with { CopiesRemaining = 2, ConditionalHandChance = .9 })), "Hand availability missing from cache identity.");
        Check(ThreatAnalyzer.RequestKey(input, 0, candidates) != ThreatAnalyzer.RequestKey(input with { Assumptions = ["stale board"] }, 0, candidates),
            "Freshness assumptions missing from cache identity.");

        var resumed = new ThreatAnalyzer(catalog);
        using (var interrupt = new CancellationTokenSource())
        {
            try
            {
                resumed.Analyze(input, 0, candidates, interrupt.Token, p => { if (p.Completed == 2) interrupt.Cancel(); });
                throw new InvalidOperationException("Progress cancellation ignored.");
            }
            catch (OperationCanceledException) { }
        }
        Check(resumed.CachedCandidates >= 2, "Cancellation discarded completed player/opponent transitions.");
        var resumedReport = resumed.Analyze(input, 0, candidates);
        Check(resumed.CandidateCacheHits >= 2 && resumedReport.GapAfterPlay == first.GapAfterPlay &&
            resumedReport.Replies.Select(r => (r.Candidate.Card.Id, r.Points)).SequenceEqual(first.Replies.Select(r => (r.Candidate.Card.Id, r.Points))),
            "Resumed evaluation repeated completed work or changed the answer.");

        var create = new CardDefinition("priority-create", "Create fixture", "Neutral", CardKind.Special, 5,
            AbilityText: "Create a bronze unit.");
        var attempts = 0;
        var retryProfiles = new ThreatAnalyzer(catalog.Append(create), () => ++attempts == 1
            ? throw new OperationCanceledException("Cold profile build interrupted") : new CreatePointProfiles("fixture", CreatePointProfiles.Version, []));
        try { retryProfiles.Analyze(input, 0, [new(create, .5, "Hypothesis")]); throw new InvalidOperationException("Expected profile interruption."); }
        catch (OperationCanceledException) { }
        Check(retryProfiles.Analyze(input, 0, [new(create, .5, "Hypothesis")]).Complete && attempts == 2,
            "A cancelled profile build poisoned later Reach requests.");

        var cached = new CandidatePointEvaluator(catalog, cacheCapacity: 4);
        var original = cached.Evaluate(position, body, PlayerSide.User);
        Check(ReferenceEquals(original, cached.Evaluate(position, body, PlayerSide.User)), "Candidate transition not reused.");
        var changedStates = new[] {
            position with { Round = 2 }, position with { ActivePlayer = PlayerSide.Opponent },
            position with { Opponent = position.Opponent with { Coins = 5 } },
            position with { User = position.User with { Passed = true } },
            position with { RowEffects = [new(PlayerSide.User, BoardRow.Melee, "Frost", 2)] },
            position with { CardValues = [new(PlayerSide.User, body.Id, "raid-damage", 2, 2)] },
            position with { Zones = position.Zones.Select(z => z.Side == PlayerSide.Opponent && z.Zone == CardZone.Hand ? z with { TotalCount = 5 } : z).ToImmutableArray() }
        };
        foreach (var changed in changedStates)
            Check(!ReferenceEquals(original, cached.Evaluate(changed, body, PlayerSide.User)), "Changed game state reused a stale transition.");
        Check(cached.CachedEvaluations == 4, "Candidate cache grew beyond its bound.");
        using (var stop = new CancellationTokenSource())
        using (new ReachWorkScope(priority, stop.Token))
        {
            stop.Cancel();
            try { new TacticalPlayEngine(catalog).ImmediateMaximum(input.Position, input.HoverInstanceId, PlayerSide.User); throw new InvalidOperationException("Nested engine bypassed scope cancellation."); }
            catch (OperationCanceledException) { }
        }
        // The thread-local scope must not affect ordinary offline calls after disposal.
        Check(new TacticalPlayEngine(catalog).ImmediateMaximum(input.Position, input.HoverInstanceId, PlayerSide.User).MaximumPoints == 5,
            "Disposed Reach scope leaked into unrelated calculations.");
        Console.WriteLine("PASS priority Reach: recognition preemption, cancellation/resume, partial progress, opponent scoreless-play scans, quiet coverage, semantic cache invalidation and bounded retention.");
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
