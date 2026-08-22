using System.Text.Json;
using ReviewMemory.Core.Decisions;
using ReviewMemory.Core.Reporting;

namespace ReviewMemory.Core.Tests;

public sealed class SearchRendererTests
{
    private static readonly SearchHit Hit = new(
        ThreadId: 991,
        Repo: "Shirka-Corporation/player-manager",
        Number: 1943,
        PrTitle: "AdJoe payout flow",
        Path: "src/AdJoePayoutHandler.cs",
        Line: 120,
        Finding: "Provider callbacks could be processed twice.",
        Outcome: DecisionOutcome.Accepted,
        Reason: "respuesta de dev: \"fixed with idempotency check\"",
        Score: 0.91,
        CreatedAt: new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Console_marks_high_scores()
    {
        var output = SearchRenderer.RenderConsole([Hit]);

        Assert.Contains("HIGH", output);
        Assert.Contains("Similitud: 0.91", output);
        Assert.Contains("PR #1943 (Shirka-Corporation/player-manager)", output);
        Assert.Contains("src/AdJoePayoutHandler.cs:120", output);
        Assert.Contains("#discussion_r991", output);
        Assert.Contains("Finding aceptado", output);
    }

    [Fact]
    public void Console_without_hits_is_explicit()
    {
        var output = SearchRenderer.RenderConsole([]);

        Assert.StartsWith("0 discusiones", output);
    }

    [Fact]
    public void Json_uses_camel_case_and_string_enums()
    {
        var json = SearchRenderer.RenderJson([Hit]);
        using var document = JsonDocument.Parse(json);

        var first = document.RootElement[0];
        Assert.Equal(991, first.GetProperty("threadId").GetInt64());
        Assert.Equal("accepted", first.GetProperty("outcome").GetString());
        Assert.Equal(0.91, first.GetProperty("score").GetDouble(), precision: 2);
    }

    [Fact]
    public void Outcome_description_includes_reason()
    {
        var text = SearchRenderer.DescribeOutcome(DecisionOutcome.Rejected, "invariante ya cubierta por la BD");

        Assert.Contains("rechazado", text);
        Assert.Contains("invariante ya cubierta por la BD", text);
    }
}
