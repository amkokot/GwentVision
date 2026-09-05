using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

internal static partial class DeckBuilderSmoke
{
    private static async Task CheckFillFallback(string project, string output, IReadOnlyList<CardDefinition> catalog)
    {
        var seed = catalog.First(c => c.Faction == "Monsters" && c.Provision == 4 && c.Kind == CardKind.Unit && c.CanBeInStartingDeck);
        var token = catalog.Single(c => c.Id == "202706");
        var leader = catalog.Single(c => c.Name == "Force of Nature");
        var source = new DeckDefinition("token-fixture", "Partial starting list", "Monsters", leader.Name, leader.Provision,
            [new(seed), new(token with { CanBeInStartingDeck = true })]); // Old metadata must not override current token classification.
        var library = new DeckLibrary();
        var editor = new DeckBuilderWindow(library, Path.Combine(output, "fallback-library.json"), catalog, source, () => { });
        try
        {
            await Settled(editor);
            Check(Result(editor).Cards.Count == 1 && Result(editor).Cards[0].Card.Id == seed.Id, "Old token metadata entered the fixed deck.");
            var toggle = (IToggleProvider)new CheckBoxAutomationPeer(editor.AutoFill).GetPattern(PatternInterface.Toggle);
            var timer = Stopwatch.StartNew(); toggle.Toggle(); await Settled(editor);
            Check(timer.Elapsed < TimeSpan.FromSeconds(2), "Auto-fill click took too long: " + timer.Elapsed);
            Check(Result(editor).Cards.Sum(c => c.Count) == 25 && editor.SaveDeckButton.IsEnabled, "Auto-fill click did not complete a legal deck without a library match.");
            Check(Result(editor).Cards.All(c => StartingDeckRules.IsStartingCard(c.Card)), "Catalogue fallback added a token.");
            var filledRows = ((System.Collections.IEnumerable)editor.DraftCards.Rows!).Cast<object>().ToArray();
            double Opacity(object row) => (double)row.GetType().GetProperty("RowOpacity")!.GetValue(row)!;
            string Badge(object row) => (string)row.GetType().GetProperty("Badge")!.GetValue(row)!;
            Check(filledRows.Length == 25 && filledRows.Where(row => Badge(row) == "AUTO").All(row => Opacity(row) < 1) &&
                filledRows.Where(row => Badge(row) == "FIXED").All(row => Opacity(row) == 1),
                "AUTO rows were not visually separated from FIXED rows.");
            File.WriteAllText(Path.Combine(output, "autofill-timing.txt"), $"Empty-library checkbox click to legal 25-card result: {timer.ElapsedMilliseconds}ms.");
            Render(editor, Path.Combine(output, "autofill-fallback.png"), 1320, 900);
            toggle.Toggle(); await Settled(editor);
            Check(Result(editor).Cards.Sum(c => c.Count) == 1, "Turning auto-fill off retained AUTO cards.");
            var partialRows = ((System.Collections.IEnumerable)editor.DraftCards.Rows!).Cast<object>().ToArray();
            Check(partialRows.Length == 25 && partialRows.Count(row => row.GetType().Name == "EmptyDeckSlot") == 24,
                "Incomplete drafts did not retain visible placeholders for all 25 deck slots.");
            Render(editor, Path.Combine(output, "manual-empty-slots.png"), 1320, 900);
            toggle.Toggle(); toggle.Toggle(); await Settled(editor); await Task.Delay(180);
            Check(editor.AutoFill.IsChecked == false && Result(editor).Cards.Sum(c => c.Count) == 1, "Cancelled fill overwrote the newer manual draft.");
            toggle.Toggle(); await Settled(editor);
            var saved = (DeckDefinition)Call(editor, "SaveLocal")!;
            Check(saved.CardCount == 25 && saved.Cards.All(c => c.Card.Id != token.Id), "Saved deck contains generated Woodland Spirit.");
        }
        finally
        {
            typeof(DeckBuilderWindow).GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, false);
            editor.Close();
        }
    }
}
