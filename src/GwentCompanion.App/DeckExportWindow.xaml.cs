using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using Microsoft.Web.WebView2.Core;

namespace GwentCompanion.App;

public partial class DeckExportWindow : Window
{
    private readonly DeckLibrary _library;
    private readonly string _path;
    private readonly DeckDefinition _deck;
    private readonly CardDefinition[] _catalog;
    private readonly Action _changed;
    private DeckExportReceipt? _receipt;
    private TaskCompletionSource<PlayGwentReply>? _reply;
    private string? _requestId;
    private bool _busy, _ready, _closed;

    public DeckExportWindow(DeckLibrary library, string path, DeckDefinition deck, IEnumerable<CardDefinition> catalog, Action changed,
        bool initializeBrowser = true)
    {
        _library = library; _path = path; _catalog = catalog.ToArray(); _changed = changed;
        deck = new CurrentCardValues(_catalog).Deck(deck); _deck = deck;
        InitializeComponent();
        Website.Visibility = Visibility.Hidden;
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 30); Height = Math.Min(800, SystemParameters.WorkArea.Height - 40);
        var variation = _library.VariationGroupFor(deck.Id);
        var marker = variation?.Members.Length > 1 ? " · " + DeckLibrary.Fingerprint(deck)[..8] : "";
        WebsiteName.Text = deck.Name[..Math.Min(deck.Name.Length, 50 - marker.Length)] + marker;
        DeckSummary.Text = $"{deck.Name} · {deck.Faction} · {deck.Leader} · {deck.CardCount} cards · {deck.ProvisionTotal}p · {deck.Stratagem?.Name ?? "stratagem required"}" +
            "\n" + _library.VariationLabel(deck.Id) + " · Exporting only this exact composition, not the whole group.";
        var stored = _library.Find(deck.Id);
        var details = stored?.Details ?? new(); _receipt = stored?.Export;
        Overview.Text = details.Overview; GamePlan.Text = details.GamePlan; Mulligans.Text = details.Mulligans; Matchups.Text = details.Matchups; GuideTitle.Text = details.GuideTitle;
        TransferCards.ItemsSource = DeckBuilderOrder.Sort(deck.Cards);
        TransferSummary.Text = $"{deck.CardCount} card copies · {deck.Leader} · {deck.Stratagem?.Name ?? "Choose a stratagem in the editor"}";
        _loginTimer.Tick += async (_, _) => await ContinueQueuedExportAsync();
        if (initializeBrowser) Loaded += InitializeBrowser;
        Closing += ClosingExport;
        Closed += (_, _) => { _closed = true; _pendingExport = null; _loginTimer.Stop(); Website.Dispose(); };
        RefreshButtons();
    }

    private async void InitializeBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(Path.GetDirectoryName(_path)!, "playgwent-browser"));
            if (_closed) return;
            await Website.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            ConfigureBrowser();
            Website.CoreWebView2.Navigate(PlayGwentExport.LibraryUrl);
            ExportStatus.Text = _receipt?.OutcomeUnknown == true
                ? "An earlier request has an uncertain outcome. Check your website library before allowing a retry."
                : $"Ready: export all {_deck.CardCount} cards with one button. If sign-in is needed, the transfer will resume automatically afterward.";
        }
        catch (Exception exception)
        { if (!_closed) ExportStatus.Text = "The embedded browser could not start. Microsoft Edge WebView2 Runtime is required. " + exception.Message; }
    }
    private void ConfigureBrowser()
    {
            Website.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            Website.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Website.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            Website.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            Website.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            Website.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!PlayGwentExport.AllowedNavigation(args.Uri))
                {
                    args.Cancel = true; _pendingExport = null; _loginTimer.Stop();
                    ExportStatus.Text = "This sign-in destination is not supported by the exporter. Navigation and the queued export were stopped.";
                    RefreshButtons(); return;
                }
                _ready = false; BrowserAddress.Text = new Uri(args.Uri).Host; RefreshButtons();
            };
            Website.CoreWebView2.NavigationCompleted += async (_, args) =>
            { _ready = args.IsSuccess && PlayGwentExport.IsWebsite(Website.Source?.AbsoluteUri); RefreshButtons(); await ContinueQueuedExportAsync(); };
            Website.CoreWebView2.NewWindowRequested += (_, args) =>
            { args.Handled = true; if (PlayGwentExport.AllowedNavigation(args.Uri)) Website.CoreWebView2.Navigate(args.Uri); };
            Website.CoreWebView2.WebMessageReceived += Received;
    }

    private DeckDetails Details() => new(Overview.Text, GamePlan.Text, Mulligans.Text, Matchups.Text, GuideTitle.Text);
    private string TitleForGuide => string.IsNullOrWhiteSpace(GuideTitle.Text) ? WebsiteName.Text : GuideTitle.Text;
    private void SaveDetails()
    { _library.SetDetails(_deck.Id, Details()); _library.Save(_path); _changed(); }
    private void SaveDetailsClicked(object sender, RoutedEventArgs e)
    { try { SaveDetails(); ExportStatus.Text = "Optional details saved locally. Nothing has been sent to PlayGWENT."; } catch (Exception exception) { ExportStatus.Text = exception.Message; } }
    private void StoreReceipt(DeckExportReceipt receipt)
    { _receipt = receipt; _library.SetExport(_deck.Id, receipt); _library.Save(_path); _changed(); RefreshButtons(); }
    private void RefreshButtons()
    {
        var usable = _ready && !_busy && _pendingExport is null && _receipt?.OutcomeUnknown != true;
        CreateButton.IsEnabled = usable && _receipt?.CardsVerified != true;
        GuideButton.IsEnabled = usable && _receipt is { WebsiteDeckId: not null, GuideCreated: false, CardsVerified: true };
        ImportButton.IsEnabled = usable && _receipt is { DeckHash: not null, GameImported: false, CardsVerified: true };
        ExportDetailsPanel.IsEnabled = !_busy && _pendingExport is null;
        SaveDetailsButton.IsEnabled = ExportDetailsPanel.IsEnabled;
        WebsiteName.IsEnabled = _receipt?.DeckHash is null;
        CancelQueuedButton.Visibility = _pendingExport is not null ? Visibility.Visible : Visibility.Collapsed;
        CancelQueuedButton.IsEnabled = !_busy;
        RetryButton.Visibility = _receipt?.OutcomeUnknown == true ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !_busy;
        DeckLink.Text = _receipt?.DeckHash is { } hash ? PlayGwentExport.Origin + "/en/decks/" + hash : "";
        CopyLinkButton.IsEnabled = DeckLink.Text.Length > 0;
        DeckLink.Visibility = CopyLinkButton.Visibility = DeckLink.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GuideButton.Visibility = _receipt?.CardsVerified == true ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = _receipt?.CardsVerified == true ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.Content = _pendingExport is not null ? "Waiting for sign-in…" : _receipt?.CardsVerified == true ? "All cards transferred and verified ✓" :
            _receipt?.DeckHash is not null ? "Verify existing website deck" : $"Export all {_deck.CardCount} cards";
        if (_receipt?.GameImported == true) ImportButton.Content = "Imported into GWENT ✓";
        if (_receipt?.GuideCreated == true) GuideButton.Content = "Guide draft saved · edit on website";
    }

    private async Task<PlayGwentReply> Request(string path, string? body)
    {
        if (RequestForSmoke is not null) return await RequestForSmoke(path, body);
        if (!_ready || !PlayGwentExport.IsWebsite(Website.Source?.AbsoluteUri)) throw new InvalidOperationException("Return to PlayGWENT and sign in before exporting.");
        _requestId = Guid.NewGuid().ToString("N");
        _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Website.CoreWebView2.ExecuteScriptAsync(PlayGwentExport.RequestScript(_requestId, path, body));
            return await _reply.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally { _reply = null; _requestId = null; }
    }
    private void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_reply is null || !PlayGwentExport.IsWebsite(e.Source) || !PlayGwentExport.IsWebsite(Website.Source?.AbsoluteUri)) return;
        try
        {
            if (e.WebMessageAsJson.Length > 600000) return;
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("requestId", out var id) || id.GetString() != _requestId) return;
            var response = JsonSerializer.Deserialize<PlayGwentReply>(e.WebMessageAsJson, PlayGwentExport.Json);
            if (response is not null) _reply.TrySetResult(response);
        }
        catch (JsonException) { }
    }

    private static void RequireSuccess(PlayGwentReply reply)
    {
        if (reply.Redirected) throw new InvalidOperationException("The website redirected the write request. Its outcome is unconfirmed; check the website before retrying.");
        if (reply.Status is 401 or 403) throw new InvalidDataException("Your sign-in expired. Sign in to PlayGWENT, then try again.");
        if (reply.Status == 0 || reply.Error is not null) throw new InvalidOperationException("No confirmed response. Check the website before retrying.");
        if (reply.Status is < 200 or >= 300) throw new InvalidDataException($"PlayGWENT rejected the request (HTTP {reply.Status}). No automatic retry was made.");
        using var json = JsonDocument.Parse(reply.Body);
        if (json.RootElement.ValueKind is JsonValueKind.False or JsonValueKind.Null) throw new InvalidDataException("The website did not confirm success; check its library before retrying.");
        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            throw new InvalidDataException("The website reported an error; inspect the website library before retrying.");
    }
    private async void CreateClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _pendingExport is not null || _receipt?.OutcomeUnknown == true || _receipt?.CardsVerified == true) return;
        try
        {
            var payload = PlayGwentExport.CreateRequest(_deck, WebsiteName.Text, _catalog);
            var guide = IncludeGuide.IsChecked == true ? PlayGwentExport.GuideBody(TitleForGuide, Details()) : null;
            SaveDetails(); _pendingExport = new(payload, guide); _loginRequested = false; _queuedAt = DateTimeOffset.UtcNow;
            ExportStatus.Text = $"All {_deck.CardCount} cards are prepared. Checking PlayGWENT sign-in…";
            RefreshButtons(); await ContinueQueuedExportAsync();
        }
        catch (Exception exception) { ExportStatus.Text = "Export stopped: " + exception.Message; }
    }
    private async Task CreateGuide(string body)
    {
        if (_receipt?.WebsiteDeckId is not { } id) throw new InvalidOperationException("Create the website deck first.");
        StoreReceipt(_receipt with { OutcomeUnknown = true });
        var reply = await Request(PlayGwentExport.GuidePath(id), body);
        if (reply.Status is >= 400 and < 500) StoreReceipt(_receipt with { OutcomeUnknown = false });
        RequireSuccess(reply);
        StoreReceipt(_receipt with { GuideCreated = true, OutcomeUnknown = false });
        ExportStatus.Text = "Website deck and optional guide draft saved. The guide has not been published.";
    }
    private async void GuideClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _pendingExport is not null) return;
        try
        {
            var body = PlayGwentExport.GuideBody(TitleForGuide, Details()); SaveDetails();
            _busy = true; RefreshButtons(); await CreateGuide(body);
            OpenCreatedDeck();
        }
        catch (Exception exception) { ExportStatus.Text = "Guide stopped: " + exception.Message; }
        finally { _busy = false; RefreshButtons(); }
    }
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _pendingExport is not null || _receipt is not { CardsVerified: true, DeckHash: { } hash }) return;
        try
        {
            _busy = true; RefreshButtons(); StoreReceipt(_receipt with { OutcomeUnknown = true });
            ExportStatus.Text = "Requesting game import using owned cards only…";
            // Deliberately NEVER send expected_cost: spending scraps must stay in the official website confirmation flow.
            var reply = await Request(PlayGwentExport.ImportPath(hash), null);
            if (reply.Status is >= 400 and < 500) StoreReceipt(_receipt with { OutcomeUnknown = false });
            if (reply.Status is 402 or 409)
            {
                using var document = JsonDocument.Parse(reply.Body);
                var cost = document.RootElement.TryGetProperty("total_crafting_cost", out var value) ? value.ToString() : "unknown";
                ExportStatus.Text = $"Missing cards; the website reports a crafting cost of {cost} scraps. Nothing was crafted by this app. Review and confirm crafting on the official website if wanted.";
                OpenCreatedDeck(); return;
            }
            RequireSuccess(reply);
            StoreReceipt(_receipt with { GameImported = true, OutcomeUnknown = false });
            ExportStatus.Text = "Imported into your GWENT account. Restart the game if the new deck is not visible yet.";
        }
        catch (Exception exception) { ExportStatus.Text = "Game import stopped: " + exception.Message; }
        finally { _busy = false; RefreshButtons(); }
    }
    private void RetryClicked(object sender, RoutedEventArgs e)
    {
        if (_receipt is null || _busy) return;
        if (MessageBox.Show(this, "Only retry if you checked the website and the previous action did NOT succeed. Repeating a completed action may create a duplicate deck or guide. Allow another attempt?",
                "Uncertain export outcome", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { StoreReceipt(_receipt with { OutcomeUnknown = false }); ExportStatus.Text = "Retry enabled after your confirmation. No request has been sent yet."; }
        catch (Exception exception) { ExportStatus.Text = exception.Message; }
    }
    private void LibraryClicked(object sender, RoutedEventArgs e)
    { if (!_busy && Website.CoreWebView2 is not null) { ShowWebsite(); Website.CoreWebView2.Navigate(PlayGwentExport.LibraryUrl); } }
    private void CopyLinkClicked(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(DeckLink.Text); } catch (Exception exception) { ExportStatus.Text = exception.Message; } }
    private void ClosingExport(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; ExportStatus.Text = "Wait for the current request to finish so its outcome can be saved."; return; }
        if (Details() == (_library.Find(_deck.Id)?.Details ?? new DeckDetails())) return;
        var answer = MessageBox.Show(this, "Save your edited deck details locally before closing?", "Deck details", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) e.Cancel = true;
        else if (answer == MessageBoxResult.Yes)
            try { SaveDetails(); } catch (Exception exception) { ExportStatus.Text = exception.Message; e.Cancel = true; }
    }
}
