using System.Net;

namespace GwentCompanion.Core.Data;

/// <summary>Authored notes, not inferred card facts or part of deck-composition identity.</summary>
public sealed record DeckDetails(string Overview = "", string GamePlan = "", string Mulligans = "", string Matchups = "", string GuideTitle = "")
{
    public bool HasContent => new[] { Overview, GamePlan, Mulligans, Matchups }.Any(s => !string.IsNullOrWhiteSpace(s));
    public string ToGuideHtml()
    {
        return string.Join("", new[] { ("Overview", Overview), ("Game plan", GamePlan), ("Mulligans", Mulligans), ("Matchups", Matchups) }
            .Where(section => !string.IsNullOrWhiteSpace(section.Item2)).Select(section =>
                "<h2>" + section.Item1 + "</h2><p>" + WebUtility.HtmlEncode(section.Item2.Trim())
                    .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>") + "</p>"));
    }
}

public sealed record DeckExportReceipt(string? WebsiteDeckId, string? DeckHash, DateTimeOffset AttemptedAt,
    bool GuideCreated = false, bool OutcomeUnknown = false, bool GameImported = false, bool CardsVerified = false);
