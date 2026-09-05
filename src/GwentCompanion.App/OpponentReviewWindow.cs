using System.Windows;
using System.Windows.Media;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public sealed record OpponentReviewOptions(LearnedOpponentDeck Record, Action<DeckEditorDraft, bool> Save);

/// <summary>The actual full deck builder, with a separate save-to-opponent-memory action.</summary>
public sealed class OpponentReviewWindow : DeckBuilderWindow
{
    public OpponentReviewWindow(LearnedOpponentDeck record, IReadOnlyList<CardDefinition> catalog,
        DeckLibrary library, string libraryPath, Action changed, Action<DeckEditorDraft, bool> save,
        Func<CardDefinition, ImageSource?>? art = null, Action<DeckEditorDraft>? saveDraft = null)
        : base(library, libraryPath, catalog, OpponentReviewDraft.Template(record, catalog), changed, art,
            OpponentReviewDraft.SourceKey(record), saveDraft, new(record, save))
    {
        Title = "Review opponent · " + record.Name;
        WindowState = WindowState.Maximized;
        Topmost = false;
        Foreground = Brushes.WhiteSmoke;
        FontFamily = new FontFamily("Segoe UI");
    }
}
