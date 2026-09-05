using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GwentCompanion.Core.GameState;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private void CheckCompactTooltips(string folder)
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        foreach (var navigation in new FrameworkElement[] { LibraryNavigation, GameplayNavigation,
            AnalysisNavigation, ReferenceNavigation, LiveModeBar, ReferenceModeBar })
            Check(navigation.ToolTip is null, "Redundant navigation tooltip: " + navigation.Name);

        var spent = new TextBlock(); var remaining = new TextBlock();
        var usage = new ProvisionUsage(42, 165, 123, "Full accounting explanation retained for accessibility.", 6, 19, 123d / 19, true);
        foreach (var (name, value, knownDeck, unknownLeader) in new[]
        {
            ("opponent-unknown", usage, false, true),
            ("opponent-known", usage, false, false),
            ("player-known", usage with { AssumedSize = false }, true, false),
            ("larger-deck", usage with { UnaccountedCards = 24 }, false, true),
            ("unknown-budget", usage with { Total = null, RemainingCeiling = null }, false, true),
            ("conflicting-budget", usage with { RemainingCeiling = null }, false, false),
            ("accounted", usage with { CommittedCards = 25, UnaccountedCards = 0, AssumedSize = false }, true, false)
        })
        {
            RenderProvisionHints(spent, remaining, value, knownDeck, unknownLeader);
            Check(AutomationProperties.GetHelpText(spent) == value.Detail && AutomationProperties.GetHelpText(remaining) == value.Detail,
                "Full provision explanation lost: " + name);
            Check(spent.ToolTip.ToString()!.Contains(knownDeck ? "selected deck cost" : value.Total is null ? "unknown" :
                unknownLeader ? "leader unknown" : "leader allowance"), "Wrong provision total basis: " + name);
            Check(remaining.ToolTip.ToString()!.Contains("Assumes") == value.AssumedSize, "Starting-size caveat incorrect: " + name);
            if (name == "larger-deck") Check(remaining.ToolTip.ToString()!.Contains("30 starting cards"), "Assumed size hardcoded to 25.");
            foreach (var (label, text) in new[] { ("spent", spent.ToolTip.ToString()!), ("left", remaining.ToolTip.ToString()!) })
            {
                Check(text.Length <= 180 && text.Split('\n').Length <= 3, "Provision tooltip became verbose: " + name);
                var tip = new ToolTip { Content = text };
                RenderStandalone(tip, Path.Combine(folder, $"tooltip-{name}-{label}.png"), 360);
                Check(tip.ActualHeight is > 20 and <= 100, "Provision tooltip exceeded compact height: " + name);
            }
        }
        RenderLiveValues();
        foreach (var label in new[] { UserSpentText, UserRemainingText, OpponentSpentText, OpponentRemainingText })
            Check(label.ToolTip is string text && text.Length <= 180, "Live render restored verbose tooltip: " + label.Name);
    }
}
