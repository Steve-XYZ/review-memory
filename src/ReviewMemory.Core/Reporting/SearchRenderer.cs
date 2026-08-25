using System.Text.Json;
using System.Text.Json.Serialization;
using ReviewMemory.Core.Decisions;
using ReviewMemory.Core.Ranking;

namespace ReviewMemory.Core.Reporting;

public sealed record SearchHit(
    long ThreadId,
    string Repo,
    int Number,
    string PrTitle,
    string Path,
    int? Line,
    string Finding,
    DecisionOutcome Outcome,
    string? Reason,
    double Score,
    DateTimeOffset CreatedAt)
{
    public string Url => $"https://github.com/{Repo}/pull/{Number}#discussion_r{ThreadId}";
}

public static class SearchRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string RenderConsole(IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
        {
            return "0 relevant discussions found";
        }

        var writer = new StringWriter();
        writer.WriteLine($"{hits.Count} historically relevant discussion(s)");
        writer.WriteLine();

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var band = Scoring.Label(Scoring.BandOf(hit.Score));

            writer.WriteLine($"{i + 1}. {band} — {Truncate(FirstLine(hit.Finding), 72)}");
            writer.WriteLine($"   Similarity: {hit.Score:0.00}");
            writer.WriteLine($"   PR #{hit.Number} ({hit.Repo}) · {hit.CreatedAt:yyyy-MM-dd}");
            writer.WriteLine($"   File: {hit.Path}{LineSuffix(hit.Line)}");
            writer.WriteLine();
            writer.WriteLine("   Previous reviewer concern:");
            writer.WriteLine($"   {Indent(Truncate(hit.Finding.Replace('\n', ' '), 240))}");
            if (hit.Outcome is not DecisionOutcome.Unknown || hit.Reason is not null)
            {
                writer.WriteLine();
                writer.WriteLine($"   Resolution: {DescribeOutcome(hit.Outcome, hit.Reason)}");
            }
            writer.WriteLine($"   {hit.Url}");
            if (i < hits.Count - 1)
            {
                writer.WriteLine();
            }
        }

        return writer.ToString();
    }

    public static string RenderJson(IReadOnlyList<SearchHit> hits) =>
        JsonSerializer.Serialize(hits, JsonOptions);

    public static string DescribeOutcome(DecisionOutcome outcome, string? reason)
    {
        var label = outcome switch
        {
            DecisionOutcome.Accepted => "Finding accepted — implementation was fixed",
            DecisionOutcome.Rejected => "Finding rejected",
            DecisionOutcome.PartiallyAccepted => "Finding partially accepted",
            _ => "Unknown outcome",
        };
        return reason is null ? label : $"{label}. {reason}";
    }

    private static string LineSuffix(int? line) => line is null ? "" : $":{line}";

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static string Indent(string text) => text;
}
