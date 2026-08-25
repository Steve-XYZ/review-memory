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

    [Fact]
    public async Task Reindex_same_pr_keeps_counts_and_stable_hash()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        var threadId = NewThreadId();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);

        var firstRun = await index.UpsertAsync(SamplePullRequest(repo, 1, threadId));
        var first = await ReadPrStateAsync(dataSource, repo, 1);

        var secondRun = await index.UpsertAsync(SamplePullRequest(repo, 1, threadId));
        var second = await ReadPrStateAsync(dataSource, repo, 1);

        Assert.Equal((Threads: 1, Decisions: 1), firstRun);
        Assert.Equal(firstRun, secondRun);
        Assert.Equal(
            (first.Threads, first.Comments, first.Decisions, first.Finding, first.ContentHash),
            (second.Threads, second.Comments, second.Decisions, second.Finding, second.ContentHash));
        Assert.Equal(1, second.Threads);
        Assert.Equal(2, second.Comments);
        Assert.Equal(1, second.Decisions);
        Assert.NotNull(second.ContentHash);
    }

    [Fact]
    public async Task Manual_decision_survives_reindex_without_changes()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        var threadId = NewThreadId();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);

        await index.UpsertAsync(SamplePullRequest(repo, 2, threadId));
        await MarkDecisionManualAsync(dataSource, threadId);
        var before = await ReadDecisionAsync(dataSource, threadId);
        var stateBefore = await ReadPrStateAsync(dataSource, repo, 2);

        await index.UpsertAsync(SamplePullRequest(repo, 2, threadId));
        var after = await ReadDecisionAsync(dataSource, threadId);
        var stateAfter = await ReadPrStateAsync(dataSource, repo, 2);

        Assert.Equal(("rejected", "manual"), (after.Outcome, after.Confidence));
        Assert.Equal(before, after);
        Assert.Equal(
            (stateBefore.Threads, stateBefore.Comments, stateBefore.Decisions,
             stateBefore.Finding, stateBefore.ContentHash),
            (stateAfter.Threads, stateAfter.Comments, stateAfter.Decisions,
             stateAfter.Finding, stateAfter.ContentHash));
    }

    [Fact]
    public async Task Changed_thread_is_updated_and_decision_reinferred()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        var threadId = NewThreadId();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);

        await index.UpsertAsync(SamplePullRequest(repo, 3, threadId));
        var before = await ReadPrStateAsync(dataSource, repo, 3);

        await index.UpsertAsync(SamplePullRequest(
            repo, 3, threadId,
            findingBody: "This retry can process the same provider transaction twice under load.",
            replyBody: "Not an issue: providerRequestId already enforced by a unique constraint."));
        var after = await ReadPrStateAsync(dataSource, repo, 3);
        var decision = await ReadDecisionAsync(dataSource, threadId);

        Assert.NotEqual(before.ContentHash, after.ContentHash);
        Assert.NotEqual(before.Finding, after.Finding);
        Assert.Contains("twice under load", after.Finding);
        Assert.Equal("rejected", decision.Outcome);
        Assert.Equal("inferred", decision.Confidence);
    }

    [Fact]
    public async Task Migration_002_applies_over_db_with_001_data()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        var threadId = NewThreadId();
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);
        await index.UpsertAsync(SamplePullRequest(repo, 4, threadId));

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var drop = new NpgsqlCommand(
                         "ALTER TABLE review_threads DROP COLUMN IF EXISTS content_hash", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var forget = new NpgsqlCommand(
                         "DELETE FROM schema_migrations WHERE name = '002_thread_content_hash.sql'", connection))
        {
            await forget.ExecuteNonQueryAsync();
        }

        var applied = await DbMigrations.ApplyAsync(dataSource);

        Assert.Equal(["002_thread_content_hash.sql"], applied);

        var stateAfterMigration = await ReadPrStateAsync(dataSource, repo, 4);
        Assert.Null(stateAfterMigration.ContentHash);
        Assert.Equal(threadId, stateAfterMigration.ThreadIds.Single());

        await index.UpsertAsync(SamplePullRequest(repo, 4, threadId));
        var stateAfterReindex = await ReadPrStateAsync(dataSource, repo, 4);
        Assert.NotNull(stateAfterReindex.ContentHash);
    }

    [Fact]
    public async Task Upsert_persists_resolved_flag_per_thread()
    {
        if (ConnectionString is null)
        {
            return;
        }

        var repo = $"smoke/{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await DbMigrations.ApplyAsync(dataSource);
        var index = new IndexRepository(dataSource);

        var resolvedId = NewThreadId();
        var openId = NewThreadId();
        await index.UpsertAsync(PullRequestWithResolvedThreads(repo, 9, resolvedId, openId));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, resolved FROM review_threads WHERE pr_repo = @repo ORDER BY id", connection);
        command.Parameters.AddWithValue("repo", repo);

        var stored = new Dictionary<long, bool>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            stored[reader.GetInt64(0)] = reader.GetBoolean(1);
        }

        Assert.Equal(2, stored.Count);
        Assert.True(stored[resolvedId]);
        Assert.False(stored[openId]);
    }

    private static long NewThreadId() =>
        Random.Shared.NextInt64(1_000_000_000, long.MaxValue / 4);

    private static async Task MarkDecisionManualAsync(NpgsqlDataSource dataSource, long threadId)
    {
        const string sql = """
            UPDATE decisions
            SET outcome = 'rejected',
                reason = 'human correction: lexical false positive',
                confidence = 'manual'
            WHERE thread_id = @thread_id
            """;

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("thread_id", threadId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(
        int Threads, int Comments, int Decisions, string? Finding,
        string? ContentHash, IReadOnlyList<long> ThreadIds)> ReadPrStateAsync(
        NpgsqlDataSource dataSource, string repo, int number)
    {
        const string sql = """
            SELECT t.id, t.finding, t.content_hash
            FROM review_threads t
            WHERE t.pr_repo = @repo AND t.pr_number = @number
            ORDER BY t.id
            """;

        var threadIds = new List<long>();
        string? finding = null;
        string? contentHash = null;
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("repo", repo);
            command.Parameters.AddWithValue("number", number);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                threadIds.Add(reader.GetInt64(0));
                finding = reader.GetString(1);
                contentHash = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        const string countsSql = """
            SELECT
                (SELECT COUNT(*) FROM review_comments c
                 JOIN review_threads t ON t.id = c.thread_id
                 WHERE t.pr_repo = @repo AND t.pr_number = @number),
                (SELECT COUNT(*) FROM decisions d
                 JOIN review_threads t ON t.id = d.thread_id
                 WHERE t.pr_repo = @repo AND t.pr_number = @number)
            """;

        int comments;
        int decisions;
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(countsSql, connection))
        {
            command.Parameters.AddWithValue("repo", repo);
            command.Parameters.AddWithValue("number", number);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            comments = (int)(long)reader.GetInt64(0);
            decisions = (int)(long)reader.GetInt64(1);
        }

        return (threadIds.Count, comments, decisions, finding, contentHash, threadIds);
    }

    private static async Task<(string Outcome, string Confidence, string? Reason, DateTimeOffset DecidedAt)>
        ReadDecisionAsync(NpgsqlDataSource dataSource, long threadId)
    {
        const string sql = """
            SELECT outcome, confidence, reason, decided_at
            FROM decisions
            WHERE thread_id = @thread_id
            """;

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("thread_id", threadId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static async Task<int> CountAppliedMigrationsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM schema_migrations", connection);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static PullRequestData PullRequestWithResolvedThreads(
        string repo, int number, long resolvedId, long openId) => new(
            Repo: repo,
            Number: number,
            Title: "Persist resolved flag",
            Body: "Two threads with different resolved states.",
            Author: "javier",
            State: PrState.Open,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-5),
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-4),
            MergedAt: null,
            Files:
            [
                new PullRequestFileData("src/Service.cs", 4, 1, "@@ -1,2 +1,4 @@\n a\n+b\n+c\n d"),
            ],
            Threads:
            [
                new ReviewThreadData(
                    Id: resolvedId,
                    Path: "src/Service.cs",
                    Line: 2,
                    Resolved: true,
                    Finding: new ReviewCommentData(resolvedId, "reviewer", "Null check missing here.", DateTimeOffset.UtcNow.AddDays(-5)),
                    Replies:
                    [
                        new ReviewCommentData(resolvedId + 1, "dev", "Fixed in the next commit.", DateTimeOffset.UtcNow.AddDays(-5).AddHours(3)),
                    ]),
                new ReviewThreadData(
                    Id: openId,
                    Path: "src/Service.cs",
                    Line: 5,
                    Resolved: false,
                    Finding: new ReviewCommentData(openId, "reviewer", "Should this be configurable?", DateTimeOffset.UtcNow.AddDays(-4)),
                    Replies: []),
            ]);

    private static PullRequestData SamplePullRequest(
        string repo, int number, long? threadId = null, int line = 42,
        string? findingBody = null, string? replyBody = null)
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
                        findingBody ?? "This retry can process the same provider transaction twice.",
                        DateTimeOffset.UtcNow.AddDays(-30)),
                    Replies:
                    [
                        new ReviewCommentData(id + 1, "dev",
                            replyBody ?? "Fixed by adding an idempotency check.",
                            DateTimeOffset.UtcNow.AddDays(-30).AddHours(2)),
                    ]),
            ]);
    }
}
