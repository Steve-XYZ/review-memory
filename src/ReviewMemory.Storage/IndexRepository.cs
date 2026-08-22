using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;

namespace ReviewMemory.Storage;

public sealed class IndexRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Indexa o re-indexa un PR completo de forma transaccional.
    /// Devuelve cuántas discusiones y decisiones se almacenaron.
    /// </summary>
    public async Task<(int Threads, int Decisions)> UpsertAsync(
        PullRequestData pullRequest, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var decisions = 0;

        await UpsertPullRequestAsync(connection, transaction, pullRequest, cancellationToken);
        await ReplaceFilesAsync(connection, transaction, pullRequest, cancellationToken);
        await DeleteThreadsAsync(connection, transaction, pullRequest, cancellationToken);

        foreach (var thread in pullRequest.Threads)
        {
            await InsertThreadAsync(connection, transaction, pullRequest, thread, cancellationToken);
            await InsertCommentsAsync(connection, transaction, thread, cancellationToken);

            var decision = DecisionInferrer.Infer(thread);
            await UpsertDecisionAsync(connection, transaction, thread.Id, decision, cancellationToken);
            if (decision.Outcome is not DecisionOutcome.Unknown)
            {
                decisions++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (pullRequest.Threads.Count, decisions);
    }

    private static async Task UpsertPullRequestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO pull_requests (repo, number, title, body, author, state, created_at, updated_at, merged_at)
            VALUES (@repo, @number, @title, @body, @author, @state, @created_at, @updated_at, @merged_at)
            ON CONFLICT (repo, number) DO UPDATE SET
                title      = EXCLUDED.title,
                body       = EXCLUDED.body,
                author     = EXCLUDED.author,
                state      = EXCLUDED.state,
                created_at = EXCLUDED.created_at,
                updated_at = EXCLUDED.updated_at,
                merged_at  = EXCLUDED.merged_at,
                indexed_at = now()
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("repo", pr.Repo);
        command.Parameters.AddWithValue("number", pr.Number);
        command.Parameters.AddWithValue("title", pr.Title);
        command.Parameters.AddWithValue("body", pr.Body ?? "");
        command.Parameters.AddWithValue("author", pr.Author);
        command.Parameters.AddWithValue("state", ToDb(pr.State));
        command.Parameters.AddWithValue("created_at", pr.CreatedAt);
        command.Parameters.AddWithValue("updated_at", pr.UpdatedAt);
        command.Parameters.AddWithValue("merged_at", pr.MergedAt ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceFilesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, CancellationToken cancellationToken)
    {
        const string deleteSql = "DELETE FROM pr_files WHERE pr_repo = @repo AND pr_number = @number";
        await using (var delete = new NpgsqlCommand(deleteSql, connection, transaction))
        {
            delete.Parameters.AddWithValue("repo", pr.Repo);
            delete.Parameters.AddWithValue("number", pr.Number);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertFileSql = """
            INSERT INTO pr_files (pr_repo, pr_number, path, additions, deletions, patch)
            VALUES (@repo, @number, @path, @additions, @deletions, @patch)
            RETURNING id
            """;

        const string insertHunkSql = """
            INSERT INTO code_hunks (file_id, old_start, old_lines, new_start, new_lines, body)
            VALUES (@file_id, @old_start, @old_lines, @new_start, @new_lines, @body)
            """;

        foreach (var file in pr.Files)
        {
            long fileId;
            await using (var insert = new NpgsqlCommand(insertFileSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("repo", pr.Repo);
                insert.Parameters.AddWithValue("number", pr.Number);
                insert.Parameters.AddWithValue("path", file.Path);
                insert.Parameters.AddWithValue("additions", file.Additions);
                insert.Parameters.AddWithValue("deletions", file.Deletions);
                insert.Parameters.AddWithValue("patch", file.Patch ?? (object)DBNull.Value);
                fileId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            foreach (var hunk in file.Hunks)
            {
                await using (var insert = new NpgsqlCommand(insertHunkSql, connection, transaction))
                {
                    insert.Parameters.AddWithValue("file_id", fileId);
                    insert.Parameters.AddWithValue("old_start", hunk.OldStart);
                    insert.Parameters.AddWithValue("old_lines", hunk.OldLines);
                    insert.Parameters.AddWithValue("new_start", hunk.NewStart);
                    insert.Parameters.AddWithValue("new_lines", hunk.NewLines);
                    insert.Parameters.AddWithValue("body", hunk.Text);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
    }

    private static async Task DeleteThreadsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM review_threads WHERE pr_repo = @repo AND pr_number = @number";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("repo", pr.Repo);
        command.Parameters.AddWithValue("number", pr.Number);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertThreadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, ReviewThreadData thread, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO review_threads (id, pr_repo, pr_number, path, line, resolved, author, finding, created_at)
            VALUES (@id, @repo, @number, @path, @line, @resolved, @author, @finding, @created_at)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", thread.Id);
        command.Parameters.AddWithValue("repo", pr.Repo);
        command.Parameters.AddWithValue("number", pr.Number);
        command.Parameters.AddWithValue("path", thread.Path);
        command.Parameters.AddWithValue("line", thread.Line ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("resolved", thread.Resolved);
        command.Parameters.AddWithValue("author", thread.Finding.Author);
        command.Parameters.AddWithValue("finding", thread.Finding.Body);
        command.Parameters.AddWithValue("created_at", thread.Finding.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCommentsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        ReviewThreadData thread, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO review_comments (id, thread_id, author, body, created_at)
            VALUES (@id, @thread_id, @author, @body, @created_at)
            ON CONFLICT (id) DO NOTHING
            """;

        foreach (var comment in thread.Replies.Prepend(thread.Finding))
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", comment.Id);
            command.Parameters.AddWithValue("thread_id", thread.Id);
            command.Parameters.AddWithValue("author", comment.Author);
            command.Parameters.AddWithValue("body", comment.Body);
            command.Parameters.AddWithValue("created_at", comment.CreatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertDecisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long threadId, Decision decision, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO decisions (thread_id, outcome, reason, confidence)
            VALUES (@thread_id, @outcome, @reason, @confidence)
            ON CONFLICT (thread_id) DO UPDATE SET
                outcome    = EXCLUDED.outcome,
                reason     = EXCLUDED.reason,
                confidence = EXCLUDED.confidence,
                decided_at = now()
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("thread_id", threadId);
        command.Parameters.AddWithValue("outcome", ToDb(decision.Outcome));
        command.Parameters.AddWithValue("reason", decision.Reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("confidence", decision.Confidence is DecisionConfidence.Manual ? "manual" : "inferred");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToDb(PrState state) => state switch
    {
        PrState.Merged => "merged",
        PrState.Closed => "closed",
        _ => "open",
    };

    private static string ToDb(DecisionOutcome outcome) => outcome switch
    {
        DecisionOutcome.Accepted => "accepted",
        DecisionOutcome.Rejected => "rejected",
        DecisionOutcome.PartiallyAccepted => "partially_accepted",
        _ => "unknown",
    };
}
