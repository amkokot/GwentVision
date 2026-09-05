using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class DeckExportWindow
{
    private sealed record PendingExport(PlayGwentCreateRequest Payload, string? GuideBody);
    private PendingExport? _pendingExport;
    private bool _loginRequested;
    private DateTimeOffset _queuedAt;
    private readonly DispatcherTimer _loginTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    // Offline acceptance seam: the ordinary UI/controller is exercised without account writes.
    internal Func<string, string?, Task<PlayGwentReply>>? RequestForSmoke { get; set; }

    private async Task ContinueQueuedExportAsync()
    {
        if (_closed || _busy || !_ready || _pendingExport is not { } pending) return;
        if (DateTimeOffset.UtcNow - _queuedAt > TimeSpan.FromMinutes(10))
        { CancelQueuedClicked(this, new()); ExportStatus.Text = "Queued export expired. Click Export again when ready; no deck was sent."; return; }
        _busy = true; RefreshButtons();
        try
        {
            var loginReply = await Request(PlayGwentExport.LoginCheckPath, null);
            var auth = PlayGwentExport.Authentication(loginReply);
            if (auth == PlayGwentAuthentication.Unknown)
                throw new InvalidOperationException(PlayGwentExport.AuthenticationFailureMessage(loginReply));
            if (auth == PlayGwentAuthentication.SignedOut)
            {
                ExportStatus.Text = $"Sign in on the right. All {_deck.CardCount} cards are queued and will transfer automatically after sign-in. Do not use New deck.";
                ShowWebsite(); _loginTimer.Start();
                if (!_loginRequested) { _loginRequested = true; await OpenSignInAsync(); }
                return;
            }

            // Only a confirmed sign-in can consume the queued operation. Never retry a write on a timer.
            _pendingExport = null; _loginTimer.Stop();
            if (_receipt?.DeckHash is null)
            {
                StoreReceipt(new(null, null, DateTimeOffset.Now, OutcomeUnknown: true));
                ExportStatus.Text = $"Transferring {_deck.CardCount} cards, leader and stratagem…";
                var reply = await Request(PlayGwentExport.CreatePath, JsonSerializer.Serialize(pending.Payload, PlayGwentExport.Json));
                if (reply.Status is >= 400 and < 500) StoreReceipt(_receipt! with { OutcomeUnknown = false });
                RequireSuccess(reply);
                StoreReceipt(PlayGwentExport.Created(reply, _receipt!.AttemptedAt));
            }
            if (_receipt?.WebsiteDeckId is not { } id) throw new InvalidOperationException("The saved export has no website ID to verify. Review its link before retrying.");
            ExportStatus.Text = "Checking the saved deck against every selected card copy…";
            PlayGwentExport.VerifyDeck(await Request(PlayGwentExport.ReadDeckPath(id), null), pending.Payload, id);
            StoreReceipt(_receipt with { CardsVerified = true });
            ExportStatus.Text = $"Transferred and verified all {_deck.CardCount} cards, leader and stratagem. The exact link is saved; Import into GWENT is now available.";
            if (pending.GuideBody is not null && !_receipt.GuideCreated) await CreateGuide(pending.GuideBody);
            OpenCreatedDeck();
        }
        catch (Exception exception)
        {
            _pendingExport = null; _loginTimer.Stop();
            ExportStatus.Text = "Export stopped: " + exception.Message;
            if (_receipt?.CardsVerified == true) OpenCreatedDeck();
        }
        finally { _busy = false; RefreshButtons(); }
    }

    private async Task OpenSignInAsync()
    {
        if (Website.CoreWebView2 is null) return;
        // The official builder uses this same login form. Never read its inputs, credentials,
        // cookies or tokens; the user signs in directly on PlayGWENT/GOG.
        await Website.CoreWebView2.ExecuteScriptAsync("""
            (() => {
                if (location.origin !== 'https://www.playgwent.com') return false;
                const form = document.querySelector('#loginForm');
                if (!form) return false;
                const target = new URL(form.action, location.href);
                if (target.protocol !== 'https:' || target.username || target.password || target.port ||
                    !['www.playgwent.com','playgwent.com','login.gog.com','auth.gog.com','www.gog.com','gog.com'].includes(target.hostname)) return false;
                form.requestSubmit(); return true;
            })();
            """);
    }
    private void OpenCreatedDeck()
    {
        if (_receipt?.DeckHash is not { } hash || Website.CoreWebView2 is null) return;
        ShowWebsite();
        Website.CoreWebView2.Navigate(PlayGwentExport.Origin + "/en/decks/" + hash);
    }
    private void ShowWebsite() { ExportPreviewPanel.Visibility = Visibility.Collapsed; Website.Visibility = Visibility.Visible; }
    private void CancelQueuedClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _pendingExport = null; _loginTimer.Stop(); _loginRequested = false;
        ExportStatus.Text = "Queued export cancelled. No deck was sent."; RefreshButtons();
    }
}
