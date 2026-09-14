using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow : Window
{
    private readonly string _directory, _dataRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private MatchAnalysisEntry[] _all = [], _filtered = [];
    private IReadOnlyList<CardDefinition> _catalog = [];
    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CardDefinition> _cards = new(StringComparer.Ordinal);
    private bool _ready, _fullscreen;
    private int _page, _loadVersion;
    private WindowState _previousState;
    private const int PageSize = 30;
    private int _unreadable, _duplicates;
    private string? _catalogWarning;

    public MatchAnalysisWindow(string directory, string dataRoot)
    {
        _directory = directory; _dataRoot = dataRoot;
        InitializeComponent();
        DisplayChoice.ItemsSource = new[] { "Match history", "Factions & leaders", "Statistics" }; DisplayChoice.SelectedIndex = 0;
        SideChoice.ItemsSource = new[] { "Opponent", "Player" }; SideChoice.SelectedIndex = 0;
        GroupChoice.ItemsSource = new[] { "Factions", "Leader abilities" }; GroupChoice.SelectedIndex = 0;
        MetricChoice.ItemsSource = new[] { "Encounter share", "Win rate" }; MetricChoice.SelectedIndex = 0;
        _ready = true;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _loadVersion++; };
    }

    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        RefreshButton.IsEnabled = false; StatusText.Text = "Reading local match records…";
        try
        {
            var loaded = await Task.Run(() =>
            {
                var data = MatchAnalysis.Load(_directory, _lifetime.Token);
                IReadOnlyList<CardDefinition> catalog = []; string? warning = null;
                try { catalog = GwentOneCardCatalog.Load(Path.Combine(_dataRoot, "cache", "gwent-one-cards.json")); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { warning = "Card names unavailable; original IDs are shown."; }
                return (data, catalog, warning);
            }, _lifetime.Token);
            if (version != _loadVersion || _lifetime.IsCancellationRequested) return;
            var selectedPatch = (PatchChoice.SelectedItem as PatchOption)?.Value;
            var wasInitialized = PatchChoice.Items.Count > 0;
            _all = loaded.data.Entries; _unreadable = loaded.data.UnreadableFiles; _duplicates = loaded.data.DuplicateFiles;
            _catalog = loaded.catalog; _catalogWarning = loaded.warning;
            _cards = _catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id, StringComparer.Ordinal);
            _ready = false;
            var patches = MatchAnalysis.Patches(_all);
            var options = new[] { new PatchOption(null, "All patches") }.Concat(patches.Select(p =>
                new PatchOption(p, p))).ToArray();
            PatchChoice.ItemsSource = options;
            PatchChoice.SelectedItem = wasInitialized
                ? options.FirstOrDefault(p => p.Value == selectedPatch) ?? options[0]
                : options.FirstOrDefault(p => p.Value is not null && p.Value != "Unknown patch") ?? options[0];
            _ready = true; _page = 0; Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != _loadVersion) return;
            StatusText.Text = "Could not read match data: " + error.Message;
            EmptyText.Text = "The data could not be read. Check the message below, then try Refresh data. Existing files have not been changed.";
            EmptyPanel.Visibility = Visibility.Visible;
        }
        finally { if (version == _loadVersion) RefreshButton.IsEnabled = true; }
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready) { _page = 0; Render(); } }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Render()
    {
        _filtered = MatchAnalysis.Filter(_all, (PatchChoice.SelectedItem as PatchOption)?.Value);
        var totals = MatchAnalysis.Totals(_filtered);
        Kpis.ItemsSource = new[]
        {
            new MetricVm("RECORDED GAMES", totals.Games.ToString("N0"), $"{totals.ActiveDays:N0} active days"),
            new MetricVm("WIN RATE", Percent(totals.WinRate), $"{totals.Wins + totals.Losses:N0} wins + losses; draws excluded"),
            new MetricVm("WINS / LOSSES / DRAWS", $"{totals.Wins} / {totals.Losses} / {totals.Draws}", "Only explicitly recorded outcomes"),
            new MetricVm("WIN / LOSS BALANCE", $"{totals.Wins - totals.Losses:+0;-0;0}", "Wins minus losses in the selected patch")
        };
        var breakdown = DisplayChoice.SelectedIndex == 1;
        HistoryView.Visibility = DisplayChoice.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        BreakdownView.Visibility = breakdown ? Visibility.Visible : Visibility.Collapsed;
        StatisticsView.Visibility = DisplayChoice.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SideControls.Visibility = GroupControls.Visibility = MetricControls.Visibility = breakdown ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = _filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _all.Length == 0 ? "Run live recognition in Gwent Vision, then return here and refresh. Saved games will appear automatically."
            : "No games match this patch. Choose another patch or All patches.";
        StatusText.Text = $"{_filtered.Length:N0} of {_all.Length:N0} local observations · unknown outcomes are never counted as losses." +
            (_unreadable == 0 ? "" : $"  {_unreadable:N0} unreadable files skipped; originals preserved.") +
            (_duplicates == 0 ? "" : $"  {_duplicates:N0} duplicate files excluded.") + (_catalogWarning is null ? "" : "  " + _catalogWarning);
        if (HistoryView.Visibility == Visibility.Visible) RenderHistory();
        if (breakdown) RenderBreakdown();
        if (StatisticsView.Visibility == Visibility.Visible) RenderStatistics(totals);
    }

    private void RenderHistory()
    {
        _page = Math.Clamp(_page, 0, Math.Max(0, (_filtered.Length - 1) / PageSize));
        HistoryList.ItemsSource = _filtered.Skip(_page * PageSize).Take(PageSize).Select(m => new HistoryVm(m,
            Identity(m.UserFaction, m.UserLeader), Identity(m.OpponentFaction, m.OpponentLeader))).ToArray();
        PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = (_page + 1) * PageSize < _filtered.Length;
        PageLabel.Text = _filtered.Length == 0 ? "No matches" : $"{_page * PageSize + 1:N0}–{Math.Min((_page + 1) * PageSize, _filtered.Length):N0} of {_filtered.Length:N0} games";
    }
    private void PreviousClicked(object sender, RoutedEventArgs e) { _page--; RenderHistory(); }
    private void NextClicked(object sender, RoutedEventArgs e) { _page++; RenderHistory(); }

    private void RenderBreakdown()
    {
        var leaders = GroupChoice.SelectedIndex == 1; var winRate = MetricChoice.SelectedIndex == 1;
        var player = SideChoice.SelectedIndex == 1;
        var groups = leaders
            ? MatchAnalysis.Breakdown(_filtered, m => LeaderKey(player ? m.UserFaction : m.OpponentFaction, player ? m.UserLeader : m.OpponentLeader),
                GwentOneCardCatalog.StartingLeaders(_catalog).Select(c => LeaderKey(c.Faction, c.Name)))
            : MatchAnalysis.Breakdown(_filtered, m => player ? m.UserFaction : m.OpponentFaction, MatchAnalysis.Factions);
        BreakdownTitle.Text = (player ? "Your " : "Opponent ") + (leaders ? "leader abilities" : "factions");
        BreakdownExplanation.Text = winRate
            ? "Win rate = wins ÷ (wins + losses). Draws and unknown results are excluded. Small samples remain visible; — means no decisive results."
            : player ? "Play share = games using this faction or leader ÷ all games in the selected patch."
            : "Encounter share = games against this opponent group ÷ all games in the selected patch. Unknown opponents are included in the total.";
        BreakdownList.ItemsSource = groups.Select(g =>
        {
            IdentityVm identity;
            if (leaders)
            {
                var split = g.Key.Split('\u001f');
                identity = Identity(split[0], split.Length > 1 ? split[1] : "Unknown leader");
            }
            else identity = Identity(g.Key, null);
            return new BreakdownVm(identity, winRate ? g.WinRate ?? 0 : g.Share,
                winRate ? Percent(g.WinRate) : $"{g.Share:0.#}%", $"{g.Games:N0} games",
                $"{g.Wins} W  /  {g.Losses} L  /  {g.Draws} D", $"{g.Unknown} unknown · {g.Wins + g.Losses} decisive");
        }).ToArray();
    }

    private string LeaderKey(string faction, string leader)
    {
        var card = ResolveLeader(faction, leader);
        return (card?.Faction ?? faction) + "\u001f" + (card?.Name ?? leader);
    }

    private void RenderStatistics(MatchAnalysisTotals t)
    {
        var latest = _filtered.Length == 0 ? DateOnly.FromDateTime(DateTime.UtcNow) : _filtered.Max(m => m.Date);
        var recent = MatchAnalysis.Totals(_filtered.Where(m => m.Date > latest.AddDays(-7)).ToArray());
        var previous = MatchAnalysis.Totals(_filtered.Where(m => m.Date <= latest.AddDays(-7) && m.Date > latest.AddDays(-14)).ToArray());
        var eligible = MatchAnalysis.Breakdown(_filtered, m => m.OpponentFaction)
            .Where(g => g.Wins + g.Losses >= 5 && g.Key != "Unknown faction").ToArray();
        var toughest = eligible.OrderBy(g => g.WinRate).ThenByDescending(g => g.Games).FirstOrDefault();
        var strongest = eligible.OrderByDescending(g => g.WinRate).ThenByDescending(g => g.Games).FirstOrDefault();
        StatisticsList.ItemsSource = new[]
        {
            new MetricVm("Recent win rate", Percent(recent.WinRate), $"7 days ending {latest:dd MMM}: {recent.Wins} wins / {recent.Losses} losses. Draws excluded."),
            new MetricVm("Change from the previous week", recent.WinRate is { } now && previous.WinRate is { } before ? $"{now - before:+0.#;-0.#;0} pp" : "—", $"Previous 7 days: {Percent(previous.WinRate)} across {previous.Wins + previous.Losses} decisive games. Opponents and decks may differ."),
            new MetricVm("Matchup to review", toughest?.Key ?? "—", toughest is null ? "Needs at least 5 wins + losses against one faction." : $"{Percent(toughest.WinRate)} · {toughest.Wins} W / {toughest.Losses} L. Review these losses in match history; this combines your decks."),
            new MetricVm("Strongest matchup so far", strongest?.Key ?? "—", strongest is null ? "Needs at least 5 wins + losses against one faction." : $"{Percent(strongest.WinRate)} · {strongest.Wins} W / {strongest.Losses} L. Small samples can change quickly; this combines your decks."),
            new MetricVm("Draw rate", t.Wins + t.Losses + t.Draws == 0 ? "—" : $"{100d * t.Draws / (t.Wins + t.Losses + t.Draws):0.#}%", $"{t.Draws} draws among {t.Wins + t.Losses + t.Draws} completed results.")
        };
        RatingCharts.ItemsSource = MatchAnalysis.Factions.Select(f => MatchAnalysis.RatingProgress(_filtered, f))
            .OrderByDescending(s => s.Points.Length).ToArray();
    }

    private async void MatchExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: HistoryVm row } expander || e.OriginalSource != sender) return;
        var pending = new TextBlock { Text = "Loading both decks…", Margin = new Thickness(0, 12, 0, 0) };
        expander.Content = pending;
        try
        {
            var match = await Task.Run(() => LocalMatchStore.Read(row.Entry.Path), _lifetime.Token);
            if (!expander.IsExpanded || !ReferenceEquals(expander.Content, pending) || _lifetime.IsCancellationRequested) return;
            var scores = match.Rounds.Length == 0 ? "Round scores not recorded."
                : string.Join(" · ", match.Rounds.Select(r => $"R{r.Number}: {r.UserScore?.ToString() ?? "?"}–{r.OpponentScore?.ToString() ?? "?"}{(r.FinalConfirmed ? " (final)" : " (last observed)")}"));
            var info = $"Patch {match.Patch}  |  {scores}\n" +
                "Your deck is the saved selected list. Faded opponent cards are guesses. Card provisions use the current catalog.";
            expander.Content = new ContentControl
            {
                ContentTemplate = (DataTemplate)FindResource("MatchDetails"),
                Content = new DetailsVm(info, Deck("YOUR DECK", match.User, true), Deck("OPPONENT DECK", match.Opponent, false))
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (ReferenceEquals(expander.Content, pending)) pending.Text = "This match could not be opened: " + error.Message; }
    }
    private void MatchCollapsed(object sender, RoutedEventArgs e)
    { if (sender is Expander expander && e.OriginalSource == sender) expander.Content = null; }

    private DeckVm Deck(string label, MatchPlayer player, bool mine) => new(label, Identity(player.Faction, player.Leader),
        mine && player.Reference.Length > 0
        ? [Group("Saved player deck", "#B4C9E2", $"{player.Reference.Sum(c => c.Copies)} cards · exact copies of the selected list, preserved with this match.", player.Reference)]
        : [Group("Deck composition", "#9AD1B3", (mine ? "No trusted reference deck is available for this match. " : "") +
                "SEEN = observed · % ? = tentative estimate · PICK ? = your choice. Guesses are faded. Tokens and generated cards are excluded from this starting-deck view.",
                StartingDeckCards(player))]);

    private MatchCard[] StartingDeckCards(MatchPlayer player) => player.Observations
        .Where(c => StartingDeckRules.CountsAgainstStartingDeck(c.Origin)).Concat(player.Hypothesis)
        .Where(c => _cards.TryGetValue(c.CardId, out var card) && StartingDeckRules.IsLegalStartingCard(card, player.Faction))
        .ToArray();
    private CardGroupVm Group(string title, string color, string note, MatchCard[] cards) =>
        new(title + $" · {cards.Length}", FactionPalette.Brush(color), cards.Length == 0 ? "None recorded. " + note : note,
            cards.OrderBy(c => _cards.GetValueOrDefault(c.CardId) ?? new(c.CardId, c.CardId, "Unknown", CardKind.Unknown, 0), DeckBuilderOrder.Comparer).Select((c, i) =>
            {
                var card = _cards.GetValueOrDefault(c.CardId);
                var kind = c.Evidence switch { MatchCardEvidence.Inferred => "Inferred", MatchCardEvidence.ManualHypothesis => "Manual hypothesis", MatchCardEvidence.SelectedReference => "Reference", _ => Origin(c.Origin) };
                var confidence = c.Confidence == 255 ? "score unknown" : $"{100d * c.Confidence / 254:0}% {(c.Evidence == MatchCardEvidence.Observed ? "recognition" : "model score")}";
                var guessed = c.Evidence is MatchCardEvidence.Inferred or MatchCardEvidence.ManualHypothesis;
                var badge = c.Evidence switch { MatchCardEvidence.SelectedReference => "SAVED", MatchCardEvidence.Observed => "SEEN",
                    MatchCardEvidence.ManualHypothesis => "PICK ?", _ => c.Confidence == 255 ? "?" : $"{100d * c.Confidence / 254:0}% ?" };
                var stateColor = c.Evidence switch { MatchCardEvidence.Observed => "#7DD9AE", MatchCardEvidence.Inferred => "#89BFFF",
                    MatchCardEvidence.ManualHypothesis => "#E5C77E", _ => color };
                return new CardVm(card?.Name ?? "Card " + c.CardId, (c.CopyCountIsEstimate ? "~" : "") + c.Copies + "×",
                    kind + (c.Evidence == MatchCardEvidence.SelectedReference ? " · selected deck" : " · " + confidence), CardArt(c.CardId),
                    (i + 1).ToString(), card?.Provision.ToString() ?? "?", badge,
                    FactionPalette.Brush(stateColor), FactionPalette.Brush(card?.IsGold == true ? "#9E844D" : "#59615C"), guessed ? 0.76 : 1);
            }).ToArray());

    private static string Origin(CardProvenance origin) => origin switch
    {
        CardProvenance.ConfirmedStartingDeck => "Confirmed starting origin", CardProvenance.ProbableStartingDeck => "Probable starting origin",
        CardProvenance.Unknown => "Origin unknown", _ => origin.ToString()
    };
    private CardDefinition? ResolveLeader(string? faction, string? leader) => _catalog.FirstOrDefault(c => c.Kind == CardKind.Leader &&
        (c.Id == leader || c.Name.Equals(leader, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(faction) || faction == "Unknown faction" || c.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)));

    private IdentityVm Identity(string? faction, string? leader)
    {
        var card = ResolveLeader(faction, leader);
        var name = MatchAnalysisEntry.Clean(faction);
        var palette = FactionPalette.For(name);
        var icon = card is null ? null : LoadImage(Path.Combine(_dataRoot, "cache", "leader-portraits", card.Id + ".jpg"), 80)
            ?? LoadImage(Path.Combine(_dataRoot, "assets", "vision", "leaders", card.Id + ".jpg"), 80)
            ?? LoadImage(Path.Combine(AppContext.BaseDirectory, "vision-assets", "leaders", card.Id + ".jpg"), 80);
        var monogram = name switch { "Monsters" => "MO", "Nilfgaard" => "NG", "Northern Realms" => "NR", "Scoia'tael" => "ST", "Skellige" => "SK", "Syndicate" => "SY", _ => "?" };
        return new(name, leader is null ? "Faction" : card?.Name ?? MatchAnalysisEntry.Clean(leader, "Unknown leader"), icon,
            monogram, FactionPalette.Brush(palette.Accent), FactionPalette.Brush(palette.Surface));
    }
    private ImageSource? CardArt(string id) => LoadImage(Path.Combine(_dataRoot, "cache", "portraits", id + ".jpg"), 150);
    private ImageSource? LoadImage(string path, int width)
    {
        if (_images.TryGetValue(path, out var existing)) return existing;
        ImageSource? source = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width; bitmap.UriSource = new Uri(Path.GetFullPath(path)); bitmap.EndInit(); bitmap.Freeze(); source = bitmap;
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException) { }
        _images[path] = source; return source;
    }
    private static string Percent(double? value) => value is { } number ? $"{number:0.#}%" : "—";

    private void FullscreenClicked(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { SetFullscreen(!_fullscreen); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullscreen) { SetFullscreen(false); e.Handled = true; }
    }
    private void SetFullscreen(bool full)
    {
        if (full == _fullscreen) return;
        if (full)
        {
            _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = _previousState; }
        _fullscreen = full; FullscreenButton.Content = full ? "Exit full screen · Esc" : "Full screen · F11";
    }

    private sealed record PatchOption(string? Value, string Label);
    private sealed record MetricVm(string Label, string Value, string Note);
    private sealed record IdentityVm(string Title, string Subtitle, ImageSource? Icon, string Monogram, Brush Accent, Brush Surface);
    private sealed record BreakdownVm(IdentityVm Identity, double Value, string ValueLabel, string SampleLabel, string RecordLabel, string UnknownLabel);
    private sealed record CardVm(string Name, string ValueBadge, string Detail, ImageSource? Artwork,
        string Position, string Provision, string Badge, Brush StateColor, Brush FrameColor, double RowOpacity)
    {
        public string AccessibleName => $"{Name}, {ValueBadge}, {Badge}. {Detail}";
        public string Tooltip => AccessibleName;
    }
    private sealed record CardGroupVm(string Title, Brush Accent, string Note, CardVm[] Cards);
    private sealed record DeckVm(string Label, IdentityVm Identity, CardGroupVm[] Groups);
    private sealed record DetailsVm(string Context, DeckVm Mine, DeckVm Other);
    private sealed record HistoryVm(MatchAnalysisEntry Entry, IdentityVm Mine, IdentityVm Other)
    {
        public string DateLabel => Entry.Date.ToString("dd MMM yyyy");
        public string PatchLabel => "Patch " + Entry.Patch;
        public string ResultLabel => Entry.Outcome switch { MatchOutcome.Win => "WIN", MatchOutcome.Loss => "LOSS", MatchOutcome.Draw => "DRAW", _ => "UNKNOWN" };
        public Brush OutcomeBrush => FactionPalette.Brush(Entry.Outcome switch { MatchOutcome.Win => "#8FD7AD", MatchOutcome.Loss => "#F09289", MatchOutcome.Draw => "#E3C789", _ => "#B4C0BB" });
        public string RatingLabel => Entry.RatingLabel;
        public string AccessibleName => $"{DateLabel}, {ResultLabel}, {Mine.Title} {Mine.Subtitle} against {Other.Title} {Other.Subtitle}. Expand decks.";
    }
}
