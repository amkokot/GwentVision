using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private bool _expandedWorkspace, _wideWorkspace, _fullScreen, _applyingWorkspace;
    private double _workspaceZoom = 1;
    private Rect? _compactBounds, _windowedBounds;
    private WindowState _compactWindowState, _windowedState;

    private void WorkspaceToggle_OnClick(object sender, RoutedEventArgs e)
    {
        try { SetExpandedWorkspace(!_expandedWorkspace); }
        catch (Exception error) { FooterStatusText.Text = "Display change failed: " + error.Message; }
    }

    private void SetExpandedWorkspace(bool expanded)
    {
        if (_expandedWorkspace == expanded) return;
        if (expanded)
        {
            _compactWindowState = WindowState;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _compactBounds = DesktopDisplays.Bounds(this);
            _expandedWorkspace = true;
            var display = DesktopDisplays.Nearest(_compactBounds.Value, DesktopDisplays.All());
            DesktopDisplays.Place(this, display.WorkArea);
        }
        else
        {
            if (_fullScreen) SetFullScreen(false);
            _expandedWorkspace = false;
            WindowState = WindowState.Normal;
            if (_compactBounds is { } bounds)
            {
                var display = DesktopDisplays.Nearest(bounds, DesktopDisplays.All());
                DesktopDisplays.Place(this, DesktopDisplays.Fit(bounds, display.WorkArea));
            }
            WindowState = _compactWindowState;
        }
        UpdateWorkspaceLayout();
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        if (fullScreen)
        {
            if (!_expandedWorkspace) SetExpandedWorkspace(true);
            _windowedState = WindowState;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _windowedBounds = DesktopDisplays.Bounds(this);
            _fullScreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            var display = DesktopDisplays.Nearest(_windowedBounds.Value, DesktopDisplays.All());
            DesktopDisplays.Place(this, display.Bounds);
        }
        else
        {
            _fullScreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (_windowedBounds is { } bounds)
                DesktopDisplays.Place(this, DesktopDisplays.Fit(bounds, DesktopDisplays.Nearest(bounds, DesktopDisplays.All()).WorkArea));
            WindowState = _windowedState;
        }
        UpdateWorkspaceLayout();
    }

    private void Workspace_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _page is UiPage.Library or UiPage.Candidates)
        {
            var query = _page == UiPage.Candidates ? CandidateCardSearch : LibrarySearch.Query;
            query.Focus(); query.SelectAll(); e.Handled = true; return;
        }
        if (e.Key != Key.F11 && !(e.Key == Key.Escape && _fullScreen)) return;
        e.Handled = true;
        try { SetFullScreen(e.Key == Key.F11 && !_fullScreen); }
        catch (Exception error) { FooterStatusText.Text = "Display change failed: " + error.Message; }
    }

    private void WorkspaceDisplay_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target) return;
        var menu = CreateWorkspaceDisplayMenu();
        menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    private ContextMenu CreateWorkspaceDisplayMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, Action action, bool check = false)
        {
            var item = new MenuItem { Header = label, IsChecked = check };
            item.Click += (_, _) => { try { action(); } catch (Exception error) { FooterStatusText.Text = "Display change failed: " + error.Message; } };
            menu.Items.Add(item);
        }
        Add(_fullScreen ? "Exit full screen · F11 / Esc" : "Enter full screen · F11", () => SetFullScreen(!_fullScreen));
        menu.Items.Add(new Separator());
        var displays = DesktopDisplays.All();
        var current = DesktopDisplays.Nearest(DesktopDisplays.Bounds(this), displays);
        foreach (var (display, index) in displays.Select((d, i) => (d, i)))
            Add($"Display {index + 1}{(display.Primary ? " · primary" : "")} · {display.Bounds.Width:0} × {display.Bounds.Height:0}", () => MoveWorkspaceToDisplay(display.Id), display.Id == current.Id);
        menu.Items.Add(new Separator());
        foreach (var zoom in new[] { 1d, 1.15, 1.3 })
            Add($"Content size · {zoom:P0}", () => { _workspaceZoom = zoom; UpdateWorkspaceLayout(); }, Math.Abs(_workspaceZoom - zoom) < .01);
        Add("Return to compact companion", () => SetExpandedWorkspace(false));
        return menu;
    }

    private void MoveWorkspaceToDisplay(string id)
    {
        // Resolve again at click time: a monitor may have been unplugged while the menu was open.
        var target = DesktopDisplays.All().FirstOrDefault(d => d.Id == id);
        if (target is null) { FooterStatusText.Text = "That display is no longer connected. Open Display to choose again."; return; }
        if (!_expandedWorkspace) SetExpandedWorkspace(true);
        WindowState = WindowState.Normal;
        DesktopDisplays.Place(this, _fullScreen ? target.Bounds : target.WorkArea);
        if (_fullScreen) { _windowedBounds = target.WorkArea; _windowedState = WindowState.Normal; }
        UpdateWorkspaceLayout();
    }

    private void UpdateWorkspaceLayout(double? width = null)
    {
        if (BrandTitle is null || Pages is null || _applyingWorkspace) return;
        _applyingWorkspace = true;
        try
        {
            _wideWorkspace = _expandedWorkspace && (width ?? ActualWidth) / _workspaceZoom >= 1100;
            WorkspaceBody.LayoutTransform = _expandedWorkspace ? new ScaleTransform(_workspaceZoom, _workspaceZoom) : Transform.Identity;
            HoverThreatViewport.MaxHeight = _wideWorkspace ? 220 : double.PositiveInfinity;
            BrandTitle.FontSize = _wideWorkspace ? 20 : 16;
            LibraryNavigation.Visibility = Visibility.Visible;
            WorkspaceDisplayButton.Visibility = _expandedWorkspace ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceToggleIcon.Data = Geometry.Parse(_expandedWorkspace
                ? "M 5,1 L 15,1 L 15,11 M 1,5 L 11,5 L 11,15 L 1,15 Z"
                : "M 6,1 L 1,1 L 1,6 M 10,1 L 15,1 L 15,6 M 1,10 L 1,15 L 6,15 M 10,15 L 15,15 L 15,10");
            WorkspaceToggle.ToolTip = _expandedWorkspace ? "Return to compact companion" : "Expanded workspace · F11 for full screen";
            AutomationProperties.SetName(WorkspaceToggle, _expandedWorkspace ? "Return to compact companion" : "Expand workspace");
            WindowLayout.Margin = new Thickness(_wideWorkspace ? 20 : 12);
            Grid.SetColumn(LibraryNavigation, 0);
            Grid.SetColumnSpan(LibraryNavigation, _wideWorkspace ? 2 : 3);
            LibraryNavigation.Margin = new Thickness(0, 0, _wideWorkspace ? 8 : 0, 8);
            Grid.SetRow(PlayerDeckBanner, _wideWorkspace ? 2 : 3);
            Grid.SetColumn(PlayerDeckBanner, _wideWorkspace ? 2 : 0);
            Grid.SetColumnSpan(PlayerDeckBanner, _wideWorkspace ? 1 : 3);
            PlayerDeckBanner.Margin = new Thickness(0, 0, 0, _wideWorkspace ? 8 : 7);
            var isLive = _page is UiPage.Plays or UiPage.Deck or UiPage.Candidates or UiPage.Pinned;
            var live = _wideWorkspace && isLive;
            for (var i = 0; i < 3; i++) Pages.ColumnDefinitions[i].Width = (live || i == 0)
                ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            foreach (var page in Pages.Children.OfType<FrameworkElement>().Where(c => c != WorkspaceLaneHeadings))
            {
                Grid.SetRow(page, 1); Grid.SetColumn(page, 0); Grid.SetColumnSpan(page, 3);
                page.Margin = new Thickness(0);
                page.Visibility = page.Name == _page + "Page" ? Visibility.Visible : Visibility.Collapsed;
            }
            WorkspaceLaneHeadings.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceLaneHeadings.Columns = 3;
            FirstLaneTitle.Text = "OPPONENT CARDS";
            SecondLaneTitle.Text = "CANDIDATE CARDS";
            ThirdLaneTitle.Text = "SNAPSHOTS";
            ThirdLaneTitle.Visibility = Visibility.Visible;
            if (live)
            {
                var lanes = new[] { DeckPage, CandidatesPage, PinnedPage };
                for (var i = 0; i < lanes.Length; i++)
                {
                    lanes[i].Visibility = Visibility.Visible; Grid.SetColumn(lanes[i], i); Grid.SetColumnSpan(lanes[i], 1);
                    lanes[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, i == 2 ? 0 : 8, 0);
                }
            }
            LiveModeBar.Visibility = isLive && !_wideWorkspace ? Visibility.Visible : Visibility.Collapsed;
            PlayerDeckBanner.Visibility = isLive || _wideWorkspace ? Visibility.Visible : Visibility.Collapsed;
            ApplyBrowserColumns(LibraryLayout, _wideWorkspace);
            ArrangeLibraryColumns(_wideWorkspace);
            OpponentDeckCards.RowHeight = _wideWorkspace ? 42 : 29; OpponentDeckCards.ShowDetails = _wideWorkspace;
            LibraryDeckCards.RowHeight = _wideWorkspace ? 46 : 38;
            LibraryManagementScroll.MaxHeight = _wideWorkspace ? 180 : _compactPanel ? 80 : 130;
            // Keep the single review controls beside their faction synergy meters.
            if (SynergyHistory.Parent != SynergyHistoryHost)
            {
                if (SynergyHistory.Parent is Panel oldParent) oldParent.Children.Remove(SynergyHistory);
                SynergyHistoryHost.Children.Add(SynergyHistory); SynergyHistory.Visibility = Visibility.Visible;
            }
        }
        finally { _changingNavigation = false; _applyingWorkspace = false; }
    }

    private static void ApplyBrowserColumns(Grid grid, bool wide)
    {
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(wide ? 24 : 0);
        grid.ColumnDefinitions[2].Width = wide ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);
    }
    private static void Cell(FrameworkElement control, int row, int column, int rows = 1, int columns = 1)
    {
        Grid.SetRow(control, row); Grid.SetColumn(control, column); Grid.SetRowSpan(control, rows); Grid.SetColumnSpan(control, columns);
    }
    private void ArrangeLibraryColumns(bool wide)
    {
        LibraryLayout.RowDefinitions[1].Height = new GridLength(wide ? 1 : 2, GridUnitType.Star);
        LibraryLayout.RowDefinitions[2].Height = GridLength.Auto;
        LibraryLayout.RowDefinitions[3].Height = wide ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        LibraryLayout.RowDefinitions[3].MinHeight = wide ? 0 : 70;
        Cell(LibraryToolbar, 0, 0); Cell(DeckList, 1, 0, wide ? 3 : 1);
        Cell(LibraryPreviewHeading, wide ? 0 : 2, wide ? 2 : 0);
        Cell(LibraryDeckCards, wide ? 1 : 3, wide ? 2 : 0, wide ? 3 : 1);
    }
}
