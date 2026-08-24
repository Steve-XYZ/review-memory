using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;
using ReviewMemory.Mcp;
using ReviewMemory.Storage;

namespace ReviewMemory.Mcp.Tests;

/// <summary>
/// Paridad de contrato docs/specs/03-recuperacion.md: la salida JSON de los tools
/// MCP debe ser idéntica a la de los comandos homónimos del CLI con --format json.
/// El único componente no determinista es la recencia (depende de now() en cada
/// consulta), así que los score se comparan con tolerancia y el resto byte a byte.
/// </summary>
public sealed class McpCliParityTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("REVIEWMEMORY_TEST_CONNECTIONSTRING");

    [Fact]
    public async Task Search_json_del_tool_iguala_al_cli()
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
    public async Task Search_por_archivos_json_del_tool_iguala_al_cli()
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
    public async Task Context_json_del_tool_iguala_al_cli()
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
                $"score del hit {i} difiere más allá del ruido de recencia");
        }

        return toolHits;
    }

    private static string MaskScores(string json) =>
        Regex.Replace(json, @"""score"":\s*-?\d+(\.\d+)?([eE][+-]?\d+)?", @"""score"": <ruido-recencia>");

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
