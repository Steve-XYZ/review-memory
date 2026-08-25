using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;
using ReviewMemory.Mcp;
using ReviewMemory.Storage;

namespace ReviewMemory.Mcp.Tests;

/// <summary>
/// Contract parity with docs/specs/03-ranking.md: the JSON output of the MCP tools
/// must be identical to the CLI's homonymous commands with --format json.
/// The only non-deterministic component is recency (depends on now() on each
/// query), so scores are compared with tolerance and the rest byte by byte.
/// </summary>
public sealed class McpCliParityTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("REVIEWMEMORY_TEST_CONNECTIONSTRING");

    [Fact]
    public async Task Tool_search_json_matches_cli()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = await SeedIndexedPullRequestAsync();
        var service = new MemoryService(() => ConnectionString);

        var cli = CliProcess.Run(
            "search", "provider transaction twice", "--repo", repo, "--limit", "10",
            "--format", "json", "--connection-string", ConnectionString);
        var tool = await service.SearchAsync("provider transaction twice", repo);

        AssertParity(cli, tool, expectedHits: 1);
    }

    [Fact]
    public async Task Search_by_files_tool_json_matches_cli()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = await SeedIndexedPullRequestAsync();
        var service = new MemoryService(() => ConnectionString);
        const string path = "src/LottoPendingTransactionProcessor.cs";

        var cli = CliProcess.Run(
            "search", "", "--files", path, "--repo", repo, "--limit", "10",
            "--format", "json", "--connection-string", ConnectionString);
        var tool = await service.SearchAsync("", repo, [path]);

        AssertParity(cli, tool, expectedHits: 1);
    }

    [Fact]
    public async Task Tool_context_json_matches_cli()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);
        await index.UpsertAsync(SamplePullRequest(repo, 7, DecisionOutcome.Accepted));
        await index.UpsertAsync(SamplePullRequest(repo, 8, DecisionOutcome.Rejected));
        var service = new MemoryService(() => ConnectionString);

        var cli = CliProcess.Run(
            "context", repo, "--pr", "7", "--limit", "10",
            "--format", "json", "--connection-string", ConnectionString);
        var tool = await service.ContextAsync(repo, 7);

        var parity = AssertParity(cli, tool, expectedHits: 1);
        Assert.Equal(8, (int)parity[0]!.GetProperty("number").GetInt32());
    }

    private static JsonElement[] AssertParity(CliProcess.Result cli, ModelContextProtocol.Protocol.CallToolResult tool, int expectedHits)
    {
        Assert.Equal(0, cli.ExitCode);
        Assert.Equal(string.Empty, cli.StdErr);
        Assert.NotEqual(true, tool.IsError);

        var block = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(tool.Content));
        var cliDoc = JsonDocument.Parse(cli.StdOut.TrimEnd());
        var toolDoc = JsonDocument.Parse(block.Text);

        Assert.Equal(JsonValueKind.Array, toolDoc.RootElement.ValueKind);
        var cliHits = cliDoc.RootElement.EnumerateArray().ToArray();
        var toolHits = toolDoc.RootElement.EnumerateArray().ToArray();
        Assert.Equal(expectedHits, toolHits.Length);
        Assert.Equal(cliHits.Length, toolHits.Length);

        Assert.Equal(MaskScores(cli.StdOut.TrimEnd()), MaskScores(block.Text));
        for (var i = 0; i < cliHits.Length; i++)
        {
            Assert.True(
                Math.Abs(cliHits[i].GetProperty("score").GetDouble() - toolHits[i].GetProperty("score").GetDouble()) < 1e-5,
                $"hit {i} score differs beyond recency noise");
        }

        return toolHits;
    }

    private static string MaskScores(string json) =>
        Regex.Replace(json, @"""score"":\s*-?\d+(\.\d+)?([eE][+-]?\d+)?", @"""score"": <recency-noise>");

    private static async Task<string> SeedIndexedPullRequestAsync()
    {
        var repo = $"smoke/{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString!);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);
        await index.UpsertAsync(SamplePullRequest(repo, 1, DecisionOutcome.Accepted));
        return repo;
    }

    private static PullRequestData SamplePullRequest(string repo, int number, DecisionOutcome outcome)
    {
        var id = Random.Shared.NextInt64(1_000_000_000, long.MaxValue / 4);
        var replyBody = outcome switch
        {
            DecisionOutcome.Accepted => "Fixed by adding an idempotency check.",
            _ => "Not an issue: providerRequestId already enforced.",
        };
        return new PullRequestData(
            Repo: repo,
            Number: number,
            Title: "Fix duplicate payout processing",
            Body: "Adds an idempotency guard to the payout handler.",
            Author: "javier",
            State: PrState.Merged,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-30),
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-29),
            MergedAt: DateTimeOffset.UtcNow.AddDays(-29),
            Files:
            [
                new PullRequestFileData(
                    "src/LottoPendingTransactionProcessor.cs",
                    Additions: 12,
                    Deletions: 3,
                    Patch: "@@ -40,3 +40,5 @@\n existing\n+if (!IsProcessed(tx))\n+    Process(tx)\n Save();"),
            ],
            Threads:
            [
                new ReviewThreadData(
                    Id: id,
                    Path: "src/LottoPendingTransactionProcessor.cs",
                    Line: 42,
                    Resolved: true,
                    Finding: new ReviewCommentData(id, "reviewer",
                        "This retry can process the same provider transaction twice.",
                        DateTimeOffset.UtcNow.AddDays(-30)),
                    Replies:
                    [
                        new ReviewCommentData(id + 1, "dev", replyBody,
                            DateTimeOffset.UtcNow.AddDays(-30).AddHours(2)),
                    ]),
            ]);
    }
}
