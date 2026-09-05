namespace GwentCompanion.Core.Simulation;

public sealed record PointMass(int Points, double Probability);

/// <summary>Probability mass, never counts of maximum-search branches. Missing mass stays unresolved.</summary>
public sealed record PointDistribution(IReadOnlyList<PointMass> Bins, double Unresolved, bool Approximate, string Basis, bool IsOptionFloor = false)
{
    public double Resolved => Bins.Sum(bin => bin.Probability);
    public double? Mean => Resolved > 0 ? Bins.Sum(bin => bin.Points * bin.Probability) / Resolved : null;
    public int? Quantile(double q)
    {
        if (Resolved <= 0) return null;
        var threshold = Math.Clamp(q, 0, 1) * Resolved; var total = 0d;
        foreach (var bin in Bins.OrderBy(bin => bin.Points)) { total += bin.Probability; if (total >= threshold) return bin.Points; }
        return Bins.Max(bin => bin.Points);
    }
    public (double Low, double High) Ahead(int gap)
    { var low = Bins.Where(bin => bin.Points > gap).Sum(bin => bin.Probability); return (low, IsOptionFloor ? 1 : Math.Min(1, low + Unresolved)); }
    public PointDistribution Shift(int points) => this with { Bins = Bins.Select(bin => bin with { Points = bin.Points + points }).ToArray() };
    public static PointDistribution Of(IEnumerable<PointMass> masses, double unresolved, bool approximate, string basis)
    {
        var bins = masses.GroupBy(bin => bin.Points).Select(group => new PointMass(group.Key, group.Sum(bin => bin.Probability)))
            .Where(bin => bin.Probability > 1e-15).OrderBy(bin => bin.Points).ToArray();
        if (!double.IsFinite(unresolved) || unresolved < 0 || bins.Any(bin => !double.IsFinite(bin.Probability) || bin.Probability < 0) ||
            Math.Abs(bins.Sum(bin => bin.Probability) + unresolved - 1) > 1e-7) throw new ArgumentException("Invalid probability mass");
        return new(bins, unresolved, approximate, basis);
    }
    /// <summary>Uniform distinct offers, best choice. An offer containing ANY unknown option is unresolved, not renormalized away.</summary>
    public static PointDistribution BestOffer(IReadOnlyList<int?> scores, int options, string basis)
    {
        if (scores.Count == 0 || options < 1) return Of([], 1, true, basis);
        var k = Math.Min(options, scores.Count); var denominator = Choose(scores.Count, k);
        var bins = new List<PointMass>(); var cumulative = 0; var previous = 0d;
        foreach (var group in scores.Where(score => score.HasValue).Select(score => score!.Value).GroupBy(score => score).OrderBy(group => group.Key))
        {
            cumulative += group.Count(); var cdf = Choose(cumulative, k) / denominator;
            bins.Add(new(group.Key, cdf - previous)); previous = cdf;
        }
        return Of(bins, Math.Max(0, 1 - previous), true, basis);
    }
    private static double Choose(int n, int k)
    {
        if (n < k) return 0; var value = 1d;
        for (var i = 1; i <= k; i++) value *= (n - k + i) / (double)i;
        return value;
    }
    /// <summary>Best KNOWN option in an offer. Unknown alternatives may be better, even when a known option is offered.</summary>
    public static PointDistribution BestKnownOffer(IReadOnlyList<int?> scores, int options, string basis)
    {
        if (scores.Count == 0 || options < 1) return Of([], 1, true, basis);
        var k = Math.Min(options, scores.Count); var denominator = Choose(scores.Count, k);
        var unknown = scores.Count(score => score is null); var noKnown = Choose(unknown, k) / denominator;
        var bins = new List<PointMass>(); var cumulative = unknown; var previous = noKnown;
        foreach (var group in scores.Where(score => score.HasValue).Select(score => score!.Value).GroupBy(score => score).OrderBy(group => group.Key))
        {
            cumulative += group.Count(); var cdf = Choose(cumulative, k) / denominator;
            bins.Add(new(group.Key, cdf - previous)); previous = cdf;
        }
        return Of(bins, noKnown, true, basis) with { IsOptionFloor = unknown > 0 };
    }
}
