using System.Text.Json.Serialization;

namespace GwentCompanion.Core.Vision;

/// <summary>A visibly labelled result, never an inferred opponent rating.</summary>
public sealed record PostMatchMmr(int? RatingAfter, int? Change, bool IsFactionRating, string Label, int? SeasonPeak = null)
{
    public static bool IsResultHeader(string? header) => header is not null && System.Text.RegularExpressions.Regex.IsMatch(header.Trim(),
        @"^(VICTORY|DEFEAT|DRAW|MATCH RESULTS|MATCH REWARDS|PROGRESSION|PROGRESS)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    [JsonIgnore] public int? RatingBefore => IsFactionRating && RatingAfter is { } after && Change is { } delta ? after - delta : null;
    // If only the final faction rating is readable it is an approximate match context.
    // An unqualified MMR label might be total/peak MMR: retain it, but do not use it as fMMR.
    [JsonIgnore] public int? MatchContext => IsFactionRating ? RatingBefore ?? RatingAfter : null;
    [JsonIgnore] public string Summary => $"Observed {(IsFactionRating ? "faction MMR" : "MMR (scope unconfirmed)")}" +
        (RatingAfter is { } rating ? $" {rating}" : "") + (Change is { } change ? $" ({change:+0;-0;0})" : " · change unread") +
        (RatingBefore is { } before ? $" · before match {before}" : "") +
        (SeasonPeak is { } peak ? $" · season peak {peak}" : "");
}
