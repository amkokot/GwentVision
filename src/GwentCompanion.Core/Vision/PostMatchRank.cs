using System.Text.Json.Serialization;

namespace GwentCompanion.Core.Vision;

/// <summary>A confirmed standard-ladder result screen. The shield number may remain unread.</summary>
public sealed record PostMatchRank(int? Rank, string Label)
{
    [JsonIgnore] public bool IsProRank => Rank == 0;
    [JsonIgnore] public string Summary => Rank is null ? "Observed ranked-ladder result (rank unread)" :
        IsProRank ? "Observed Pro Rank (rank 0)" : $"Observed rank {Rank}";
}
