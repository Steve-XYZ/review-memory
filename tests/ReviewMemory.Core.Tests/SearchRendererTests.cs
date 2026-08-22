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

    [Theory]
    [InlineData(DecisionOutcome.Accepted, "Finding aceptado — se corrigió la implementación")]
    [InlineData(DecisionOutcome.Rejected, "Finding rechazado")]
    [InlineData(DecisionOutcome.PartiallyAccepted, "Finding parcialmente aceptado")]
    [InlineData(DecisionOutcome.Unknown, "Desenlace desconocido")]
    public void Outcome_labels_match_the_spec_exactly(DecisionOutcome outcome, string expected)
    {
        Assert.Equal(expected, SearchRenderer.DescribeOutcome(outcome, reason: null));
    }

    [Fact]
    public void Outcome_description_appends_reason_after_label()
    {
        var text = SearchRenderer.DescribeOutcome(
            DecisionOutcome.Accepted, "respuesta de dev: \"fixed\"");

        Assert.Equal("Finding aceptado — se corrigió la implementación. respuesta de dev: \"fixed\"", text);
    }

    [Fact]
    public void Zero_results_message_matches_the_spec_exactly()
    {
        Assert.Equal("0 discusiones relevantes encontradas", SearchRenderer.RenderConsole([]));
    }

    [Fact]
    public void Console_shows_resolution_for_unknown_outcome_with_reason()
    {
        var hit = Hit with { Outcome = DecisionOutcome.Unknown, Reason = "señales contradictorias" };

        var output = SearchRenderer.RenderConsole([hit]);

        Assert.Contains("Resolución: Desenlace desconocido. señales contradictorias", output);
    }

    [Fact]
    public void Console_omits_resolution_for_unknown_outcome_without_reason()
    {
        var hit = Hit with { Outcome = DecisionOutcome.Unknown, Reason = null };

        var output = SearchRenderer.RenderConsole([hit]);

        Assert.DoesNotContain("Resolución:", output);
    }

    [Fact]
    public void Console_truncates_first_line_of_finding_at_72_chars_with_ellipsis()
    {
        var finding = new string('a', 100);
        var hit = Hit with { Finding = finding };

        var output = SearchRenderer.RenderConsole([hit]);
        var header = output.Split('\n').Single(line => line.StartsWith("1. HIGH"));

        Assert.EndsWith("…", header);
        Assert.Contains(new string('a', 71), header);
        Assert.DoesNotContain(new string('a', 72), header);
    }

    [Fact]
    public void Console_flattens_and_truncates_finding_body_at_240_chars_with_ellipsis()
    {
        var finding = string.Concat(Enumerable.Repeat("palabra ", 60));
        var hit = Hit with { Finding = "primera línea\n" + finding };
        const int maxLength = 240;

        var output = SearchRenderer.RenderConsole([hit]);
        var content = output.Split('\n').First(line => line.TrimStart().StartsWith("primera")).TrimEnd('\r');

        Assert.Equal(maxLength, content.TrimStart().Length);
        Assert.EndsWith("…", content);
    }

    [Theory]
    [InlineData(0.50, "MEDIUM")]
    [InlineData(0.10, "LOW")]
    public void Console_labels_bands_per_thresholds(double score, string band)
    {
        var hit = Hit with { Score = score };

        Assert.Contains(band, SearchRenderer.RenderConsole([hit]));
    }

    [Fact]
    public void Json_exposes_exactly_the_contracted_field_names()
    {
        var json = SearchRenderer.RenderJson([Hit]);
        using var document = JsonDocument.Parse(json);

        var names = document.RootElement[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var expected = new[]
        {
            "threadId", "repo", "number", "prTitle", "path", "line",
            "finding", "outcome", "reason", "score", "createdAt", "url",
        }.OrderBy(n => n).ToArray();

        Assert.Equal(expected, names);
    }

    [Fact]
    public void Json_serializes_enums_as_camel_case_strings()
    {
        var hits = new[]
        {
            Hit with { ThreadId = 1, Outcome = DecisionOutcome.PartiallyAccepted },
            Hit with { ThreadId = 2, Outcome = DecisionOutcome.Rejected },
            Hit with { ThreadId = 3, Outcome = DecisionOutcome.Unknown },
        };

        var json = SearchRenderer.RenderJson(hits);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("partiallyAccepted", document.RootElement[0].GetProperty("outcome").GetString());
        Assert.Equal("rejected", document.RootElement[1].GetProperty("outcome").GetString());
        Assert.Equal("unknown", document.RootElement[2].GetProperty("outcome").GetString());
    }

    [Fact]
    public void Json_omits_null_line_and_reason()
    {
        var hit = Hit with { Line = null, Reason = null };

        var json = SearchRenderer.RenderJson([hit]);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement[0].TryGetProperty("line", out _));
        Assert.False(document.RootElement[0].TryGetProperty("reason", out _));
    }

    [Fact]
    public void Json_url_follows_the_github_discussion_pattern()
    {
        var json = SearchRenderer.RenderJson([Hit]);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            "https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r991",
            document.RootElement[0].GetProperty("url").GetString());
    }

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
