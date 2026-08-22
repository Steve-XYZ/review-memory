using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;
using ReviewMemory.Storage;

namespace ReviewMemory.Storage.Tests;

public sealed class StorageIntegrationTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("REVIEWMEMORY_TEST_CONNECTIONSTRING");

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        if (ConnectionString is null)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

        await DbMigrations.ApplyAsync(dataSource);
        var second = await DbMigrations.ApplyAsync(dataSource);

        Assert.Empty(second);

        var applied = await CountAppliedMigrationsAsync(dataSource);
        Assert.True(applied > 0);
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_search_finds_thread()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);
        var search = new SearchRepository(dataSource);

        var (_, firstDecisions) = await index.UpsertAsync(SamplePullRequest(repo, 1));
        await index.UpsertAsync(SamplePullRequest(repo, 1));

        var hits = await search.SearchAsync(new SearchQuery("provider transaction twice", repo));

        var hit = Assert.Single(hits);
        Assert.Equal("src/LottoPendingTransactionProcessor.cs", hit.Path);
        Assert.Equal(DecisionOutcome.Accepted, hit.Outcome);
        Assert.Contains($"#discussion_r{hit.ThreadId}", hit.Url);
        Assert.True(hit.Score > 0);
        Assert.Equal(1, firstDecisions);
    }

    [Fact]
    public async Task Context_excludes_threads_of_same_pr()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);
        var search = new SearchRepository(dataSource);

        var otherThreadId = NewThreadId();
        await index.UpsertAsync(SamplePullRequest(repo, 7));
        await index.UpsertAsync(SamplePullRequest(repo, 8, otherThreadId, line: 55));

        var context = await search.ContextForPullRequestAsync(repo, 7);

        Assert.Single(context);
        Assert.Equal(8, context[0].Number);
        Assert.Equal(otherThreadId, context[0].ThreadId);
    }

    private static long NewThreadId() =>
        Random.Shared.NextInt64(1_000_000_000, long.MaxValue / 4);

    private static async Task<int> CountAppliedMigrationsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM schema_migrations", connection);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static PullRequestData SamplePullRequest(
        string repo, int number, long? threadId = null, int line = 42)
    {
        var id = threadId ?? Random.Shared.NextInt64(1_000_000_000, long.MaxValue / 4);
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
                    Line: line,
                    Resolved: true,
                    Finding: new ReviewCommentData(id, "reviewer",
                        "This retry can process the same provider transaction twice.", DateTimeOffset.UtcNow.AddDays(-30)),
                    Replies:
                    [
                        new ReviewCommentData(id + 1, "dev",
                            "Fixed by adding an idempotency check.", DateTimeOffset.UtcNow.AddDays(-30).AddHours(2)),
                    ]),
            ]);
    }
}
