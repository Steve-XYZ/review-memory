namespace ReviewMemory.Core.Ranking;

public static class RankingWeights
{
    public const double TextMatch = 0.55;
    public const double FileOverlap = 0.30;
    public const double Recency = 0.15;
    public const int TextScale = 6;
    public const double RecencyHalfLifeDays = 120.0;
}

public sealed record SearchHitComponents(double TextMatch, double FileOverlap, double Recency)
{
    public static readonly SearchHitComponents Empty = new(0, 0, 0);
}

public enum ScoreBand
{
    High,
    Medium,
    Low,
}

public static class Scoring
{
    public static double Combine(SearchHitComponents components)
    {
        var score =
            RankingWeights.TextMatch * Math.Min(1.0, components.TextMatch * RankingWeights.TextScale) +
            RankingWeights.FileOverlap * Math.Min(1.0, components.FileOverlap) +
            RankingWeights.Recency * components.Recency;

        return Math.Clamp(score, 0.0, 1.0);
    }

    public static ScoreBand BandOf(double score) => score switch
    {
        >= 0.65 => ScoreBand.High,
        >= 0.40 => ScoreBand.Medium,
        _ => ScoreBand.Low,
    };

    public static string Label(ScoreBand band) => band switch
    {
        ScoreBand.High => "HIGH",
        ScoreBand.Medium => "MEDIUM",
        _ => "LOW",
    };
}
