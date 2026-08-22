using ReviewMemory.Core.Ranking;

namespace ReviewMemory.Core.Tests;

public sealed class ScoringTests
{
    [Fact]
    public void Text_match_alone_cannot_reach_high()
    {
        var score = Scoring.Combine(new SearchHitComponents(TextMatch: 1.0, FileOverlap: 0, Recency: 0));

        Assert.Equal(RankingWeights.TextMatch, score, precision: 2);
        Assert.Equal(ScoreBand.Medium, Scoring.BandOf(score));
    }

    [Fact]
    public void Strong_text_and_overlap_is_high()
    {
        var score = Scoring.Combine(new SearchHitComponents(1.0, 1.0, 0.5));

        Assert.True(score >= 0.65);
        Assert.Equal(ScoreBand.High, Scoring.BandOf(score));
    }

    [Theory]
    [InlineData(0.90, ScoreBand.High)]
    [InlineData(0.65, ScoreBand.High)]
    [InlineData(0.64, ScoreBand.Medium)]
    [InlineData(0.40, ScoreBand.Medium)]
    [InlineData(0.39, ScoreBand.Low)]
    public void Bands_follow_thresholds(double score, ScoreBand expected)
    {
        Assert.Equal(expected, Scoring.BandOf(score));
    }

    [Fact]
    public void Combine_clamps_to_unit_interval()
    {
        var score = Scoring.Combine(new SearchHitComponents(double.MaxValue, double.MaxValue, double.MaxValue));

        Assert.Equal(1.0, score);
    }
}
