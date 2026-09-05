using System.Windows;
using GwentCompanion.Core.Simulation;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private sealed record StatisticsRow(string Heading, string Detail, PointDistribution Distribution, int? Threshold, Visibility ChartVisibility);
    private static string StatisticsText(CardStatEstimate estimate, int? gap = null)
    {
        var d = estimate.Distribution;
        if (d.Resolved < .00001) return "";
        return $"Range {d.Quantile(.1)}–{d.Quantile(.9)} · mean {d.Mean:0.0}";
    }
    private void ClearThreatStatistics()
    { PlayerStatisticsPanel.Visibility = Visibility.Collapsed; ThreatStatisticsExpander.Visibility = Visibility.Collapsed; ThreatStatisticsList.ItemsSource = null; PlayerHistogram.Distribution = null; }
    private void RenderThreatStatistics(ThreatReport report)
    {
        ClearThreatStatistics();
        if (report.PlayerStatistics is { } player)
        {
            PlayerStatisticsPanel.Visibility = Visibility.Visible;
            PlayerStatisticsText.Text = StatisticsText(player);
            PlayerStatisticsText.ToolTip = "Estimated point range.";
            System.Windows.Automation.AutomationProperties.SetHelpText(PlayerStatisticsText, player.Distribution.Basis);
            PlayerHistogram.Distribution = player.Distribution;
            PlayerHistogram.Visibility = player.Distribution.Resolved >= .15 ? Visibility.Visible : Visibility.Collapsed;
        }
        var rows = (report.ReplyStatistics ?? []).OrderBy(estimate => estimate.Card.Provision).Take(5).Select(estimate => new StatisticsRow(
            estimate.Card.Kind == CardKind.Leader ? "Leader · " + estimate.Card.Name : $"{estimate.Card.Provision}p · {estimate.Card.Name}", StatisticsText(estimate, report.GapAfterPlay), estimate.Distribution,
            estimate.WholePlay ? report.GapAfterPlay : null, estimate.Distribution.Resolved >= .15 ? Visibility.Visible : Visibility.Collapsed)).ToArray();
        ThreatStatisticsExpander.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ThreatStatisticsList.ItemsSource = rows;
    }
}
