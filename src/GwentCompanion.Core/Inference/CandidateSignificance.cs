namespace GwentCompanion.Core.Inference;

/// <summary>A bounded recommendation-strength index, not a p-value or calibrated probability.</summary>
public sealed record CandidateSignificance(double Value, double InclusionSupport, double Specificity,
    double Reliability, double EffectiveSupportingFamilies, double Freshness, double EvidenceFit)
{
    public static CandidateSignificance Calculate(double inclusion, double baseline, double supportingFamilies,
        double freshness, double evidenceFit, bool hasContext)
    {
        static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        var p = Unit(inclusion); var b = Unit(baseline); var age = Unit(freshness); var fit = Unit(evidenceFit);
        var n = double.IsFinite(supportingFamilies) ? Math.Max(0, supportingFamilies) : 0;
        // Relative enrichment is bounded: an enormous lift for a tiny base rate
        // cannot outweigh low inclusion support or a one-list coincidence.
        var specificity = hasContext && p > b ? (p - b) / p : 0;
        var reliability = Math.Sqrt(n / (n + 3)) * (.65 + .35 * age);
        var value = (.78 * p + .22 * Math.Sqrt(p) * specificity) * reliability * (.4 + .6 * fit);
        return new(Unit(value), p, specificity, reliability, n, age, fit);
    }

    public string Label => EffectiveSupportingFamilies == 0 ? "No supporting lists" :
        EffectiveSupportingFamilies < 3 ? "Limited support" :
        EvidenceFit < .5 ? "Weak deck fit" : Specificity >= .2 ? "Context-linked" : "General meta fit";
}
