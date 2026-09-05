using System.Windows;
using System.Windows.Controls;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class DeckBuilderWindow
{
    private bool _repairRunning;
    private async void RepairDeckClicked(object sender, RoutedEventArgs e)
    {
        if (_rebuilding || _repairRunning || _result is not { Leader: { } leader } draft) return;
        var source = new DeckDefinition("repair-draft", DeckName.Text, leader.Faction, leader.Name, leader.Provision, draft.Cards, Stratagem: draft.Stratagem);
        var keep = VisibleDraftRows.Where(r => _draftSelection.Contains(CopyKey(r))).GroupBy(r => r.Card.Id)
            .Select(g => new DeckCard(g.First().Card, g.Count())).ToArray();
        var corpus = _libraryDecks; var excluded = _excluded.ToHashSet();
        _repairRunning = true; ((UIElement)Content).IsEnabled = false; BuilderStatus.Text = "Finding minimal recommended substitutions…";
        try
        {
            var token = _fill?.Token ?? CancellationToken.None;
            var result = await Task.Run(() => DeckRepair.Suggest(source, corpus, _catalog, keep, excluded, token), token);
            if (_closed) return;
            if (result.Options.Count == 0) { BuilderStatus.Text = result.Message; return; }
            var dialog = CreateRepairDialog(source, result);
            if (dialog.ShowDialog() != true || dialog.Tag is not DeckRepairOption chosen) return;
            ApplyRepair(chosen);
        }
        catch (OperationCanceledException) { if (!_closed) BuilderStatus.Text = "Repair cancelled. The draft is unchanged."; }
        catch (Exception error) { if (!_closed) BuilderStatus.Text = "Repair unavailable: " + error.Message; }
        finally { _repairRunning = false; if (!_closed) ((UIElement)Content).IsEnabled = true; }
    }
    internal Window CreateRepairDialog(DeckDefinition source, DeckRepairResult result)
    {
            var dialog = new Window { Owner = IsVisible ? this : null, Title = "Update deck · recommended repairs", Width = 610, Height = 520,
                MinWidth = 420, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var layout = new DockPanel { Margin = new Thickness(16) };
            var heading = new TextBlock { Text = $"{source.ProvisionTotal} / {150 + source.LeaderProvisionBonus}p → current-patch repair",
                FontSize = 18, FontFamily = new System.Windows.Media.FontFamily("Georgia"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) };
            DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
            var note = new TextBlock { Text = result.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
            DockPanel.SetDock(note, Dock.Top); layout.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var apply = new Button { Content = "Apply to draft", IsDefault = true }; var cancel = new Button { Content = "Cancel", IsCancel = true };
            buttons.Children.Add(apply); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
            var choices = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Controls.FactionPalette.Brush("#11181C") };
            foreach (var option in result.Options)
            {
                string Difference(DeckDefinition a, DeckDefinition b) => string.Join(", ", a.Cards.Where(c => c.Count > b.CountOf(c.Card.Id)).Select(c => c.Card.Name + " ×" + (c.Count - b.CountOf(c.Card.Id))));
                choices.Items.Add(new ListBoxItem { Tag = option, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = $"{option.Swaps} substitution(s) · {option.Deck.ProvisionTotal}/{150 + option.Deck.LeaderProvisionBonus}p\nRemove: {Difference(source, option.Deck)}\nAdd: {Difference(option.Deck, source)}\n{option.Reason}", Margin = new Thickness(6) } });
            }
            choices.SelectedIndex = 0; layout.Children.Add(choices); dialog.Content = layout;
            apply.Click += (_, _) => { if (choices.SelectedItem is ListBoxItem { Tag: DeckRepairOption chosen }) { dialog.Tag = chosen; dialog.DialogResult = true; } };
            return dialog;
    }
    private void ApplyRepair(DeckRepairOption option)
    {
        Checkpoint(); _rendering = true;
        _fixed.Clear(); foreach (var card in option.Deck.Cards) _fixed[card.Card.Id] = card;
        _explicitLeader = _catalog.First(c => c.Kind == CardKind.Leader && c.Faction == option.Deck.Faction && c.Name == option.Deck.Leader);
        _explicitStratagem = option.Deck.Stratagem;
        AutoFill.IsChecked = false; _rendering = false; ClearDraftSelection();
        _dirty = true; Rebuild(); BuilderStatus.Text = $"Applied {option.Swaps} recommended substitution(s). Undo restores the original draft; Save preserves this variation.";
    }
}
