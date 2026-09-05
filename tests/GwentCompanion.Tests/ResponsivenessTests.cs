using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class ResponsivenessTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static Task RunAsync(string root)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyAsync(root); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3));
    }

    private static async Task VerifyAsync(string root)
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var applied = new List<int>();
        var errors = new List<Exception>();
        using var queue = new LatestWorkQueue<int, int, int>(input =>
        {
            Check(Environment.CurrentManagedThreadId != uiThread, "Calculation blocked the owning UI thread.");
            if (input == 1) { started.SetResult(); Check(release.Wait(TimeSpan.FromSeconds(10)), "UI never released background work."); }
            return input;
        }, value =>
        {
            Check(Environment.CurrentManagedThreadId == uiThread, "Result applied off the UI thread.");
            applied.Add(value);
        }, errors.Add);
        queue.Request(1, 1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100; i++) queue.Request(1, 1);
        Check(queue.Calculations == 1, "Identical pending work was not coalesced.");
        for (var i = 2; i <= 100; i++) queue.Request(i, i);
        release.Set();
        await queue.Idle;
        Check(errors.Count == 0 && applied.SequenceEqual([100]) && queue.Calculations == 2 && queue.DiscardedResults == 1,
            "Stale result applied or pending changes accumulated instead of latest-only work.");
        queue.Request(100, 100); await queue.Idle;
        Check(queue.Calculations == 2, "Completed identical work was repeated.");
        queue.Request(1, 101); await queue.Idle;
        Check(applied.SequenceEqual([100, 101]), "Returning to an older key failed to refresh.");

        var attempts = 0; var recovered = false;
        using var retry = new LatestWorkQueue<int, int, int>(input => ++attempts == 1 ? throw new InvalidDataException("fixture") : input,
            _ => recovered = true, errors.Add);
        retry.Request(1, 1); await retry.Idle;
        retry.Request(1, 1); await retry.Idle;
        Check(attempts == 2 && recovered && errors.Count == 1, "A failed key could not be retried.");

        started = new(TaskCreationOptions.RunContinuationsAsynchronously); release.Reset();
        var afterClose = false;
        using var closing = new LatestWorkQueue<int, int, int>(input =>
        { started.SetResult(); Check(release.Wait(TimeSpan.FromSeconds(10)), "Close fixture timed out."); return input; },
            _ => afterClose = true, _ => afterClose = true);
        closing.Request(1, 1); await started.Task;
        closing.Dispose(); release.Set(); await closing.Idle;
        Check(!afterClose, "Closed window still received pending results.");

        var card = new CardDefinition("fixture", "fixture", "Monsters", CardKind.Unit, 4);
        var edits = new DeckProjectionEdits(); edits.Include(card, 2);
        var snapshot = edits.Snapshot(); edits.Exclude(card, 2);
        Check(snapshot.Included.Count == 2 && snapshot.Excluded.Count == 0 && edits.Included.Count == 1,
            "Background inputs share mutable manual choices.");

        // Real expanded corpus: scheduling must not change the model or copy ordering.
        var decks = await Task.Run(() => DeckLibrary.Load(Path.Combine(root, "GwentCompanion", "cache", "deck-library.json")).Decks);
        var importClock = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            var batch = new DeckLibrary();
            var unknown = decks.Select(d => d with { Stratagem = null }).ToArray();
            batch.Merge(unknown);
            Check(batch.Decks.Length == unknown.Select(DeckLibrary.Fingerprint).Distinct().Count(),
                "Indexed import failed to merge identical unknown-stratagem compositions.");
            batch.Merge(decks);
            var fingerprints = batch.Records.Select(r => r.Fingerprint).ToHashSet();
            Check(decks.All(d => fingerprints.Contains(DeckLibrary.Fingerprint(d))),
                "Indexed enrichment lost a known stratagem variant.");
        });
        Console.WriteLine($"PASS indexed import: {decks.Length} missing-stratagem payloads plus metadata enrichment in {importClock.Elapsed.TotalMilliseconds:F1}ms; no user files changed.");
        var selected = decks.First(d => d.Faction == "Nilfgaard" && d.CardCount >= 25);
        var evidence = selected.Cards.Take(4).Select(c => new ObservedCard(c.Card, CardProvenance.ProbableStartingDeck,
            .99, DateTimeOffset.Now, "Responsiveness fixture", c.Count)).ToArray();
        var clock = Stopwatch.StartNew();
        var expected = await Task.Run(() => new OpponentDeckProjector().Build(decks, evidence, selected.Faction,
            startingLeader: selected.Leader, startingStratagemId: selected.Stratagem?.Id));
        var baselineMs = clock.Elapsed.TotalMilliseconds;
        OpponentDeckProjection? actual = null;
        using var real = new LatestWorkQueue<int, ObservedCard[], OpponentDeckProjection>(input =>
            new OpponentDeckProjector().Build(decks, input, selected.Faction,
                startingLeader: selected.Leader, startingStratagemId: selected.Stratagem?.Id), value => actual = value, errors.Add);
        clock.Restart(); real.Request(1, evidence);
        for (var i = 0; i < 1000; i++) real.Request(1, evidence);
        var requestMs = clock.Elapsed.TotalMilliseconds;
        await real.Idle;
        Check(real.Calculations == 1 && actual is not null && errors.Count == 1, "Real-corpus refresh duplicated work or failed.");
        Check(expected.Slots.Select(s => (s.Card?.Id, s.Copy, s.State)).SequenceEqual(actual!.Slots.Select(s => (s.Card?.Id, s.Copy, s.State))),
            "Background projection differs from synchronous prediction.");
        Check(expected.Meta.Cards.Zip(actual.Meta.Cards).All(p => p.First.Card.Id == p.Second.Card.Id &&
            Math.Abs(p.First.ConditionalPresence - p.Second.ConditionalPresence) < 1e-10), "Background inference scores changed.");
        Console.WriteLine($"PASS responsiveness: {decks.Length} cached decks; model={baselineMs:F1}ms; 1001 UI requests={requestMs:F2}ms; calculations=1.");
        Console.WriteLine("PASS owning-thread callbacks, latest-only pending work, stale-result rejection, unchanged-key reuse, retry, closing, immutable edits, equivalent deck predictions.");
    }
}
