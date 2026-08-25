using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;

namespace ReviewMemory.Storage;

public sealed class IndexRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Indexes or re-indexes a whole PR transactionally, reconciling
    /// incoming threads against existing ones by GitHub id and content
    /// hash: inserts new ones, updates changed ones (with their comments),
    /// leaves unchanged ones intact and deletes those no longer on GitHub.
    /// Decision inference only runs on new or changed threads and never
    /// overwrites a manual decision. Returns how many discussions were
    /// stored and how many decisions with outcome the PR has.
    /// </summary>
    public async Task<(int Threads, int Decisions)> UpsertAsync(
        PullRequestData pullRequest, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertPullRequestAsync(connection, transaction, pullRequest, cancellationToken);
        await ReplaceFilesAsync(connection, transaction, pullRequest, cancellationToken);

        var existingHashes = await LoadThreadHashesAsync(
            connection, transaction, pullRequest, cancellationToken);
        await DeleteThreadsNotInAsync(
            connection, transaction, pullRequest,
            pullRequest.Threads.Select(t => t.Id).ToArray(), cancellationToken);

        var added = new List<(ReviewThreadData Thread, string Hash)>();
        var changed = new List<(ReviewThreadData Thread, string Hash)>();

        foreach (var thread in pullRequest.Threads)
        {
            var hash = ContentHash(thread);
            if (existingHashes.TryGetValue(thread.Id, out var currentHash))
            {
                if (currentHash != hash)
                {
                    changed.Add((thread, hash));
                }
            }
            else
            {
                added.Add((thread, hash));
            }
        }

        foreach (var (thread, hash) in added)
        {
            await InsertThreadAsync(connection, transaction, pullRequest, thread, hash, cancellationToken);
        }

        foreach (var (thread, hash) in changed)
        {
            await UpdateThreadAsync(connection, transaction, thread, hash, cancellationToken);
        }

        foreach (var (thread, _) in changed)
        {
            await DeleteCommentsAsync(connection, transaction, thread.Id, cancellationToken);
        }

        foreach (var (thread, _) in added.Concat(changed))
        {
            await InsertCommentsAsync(connection, transaction, thread, cancellationToken);

            if (!await IsManualDecisionAsync(connection, transaction, thread.Id, cancellationToken))
            {
                var decision = DecisionInferrer.Infer(thread);
                await UpsertDecisionAsync(connection, transaction, thread.Id, decision, cancellationToken);
            }
        }

        var decisions = await CountDecisionsWithOutcomeAsync(
            connection, transaction, pullRequest, cancellationToken);

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

    private static async Task<Dictionary<long, string?>> LoadThreadHashesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, content_hash
            FROM review_threads
            WHERE pr_repo = @repo AND pr_number = @number
            """;

        var hashes = new Dictionary<long, string?>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("repo", pr.Repo);
        command.Parameters.AddWithValue("number", pr.Number);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hashes.Add(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        return hashes;
    }

    /// <summary>
    /// Deletes the PR's threads absent from the incoming list (the cascade removes
    /// their comments and decisions); with an empty list it deletes all of them.
    /// </summary>
    private static async Task DeleteThreadsNotInAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, long[] keepIds, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM review_threads
            WHERE pr_repo = @repo AND pr_number = @number AND id <> ALL(@ids)
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("repo", pr.Repo);
        command.Parameters.AddWithValue("number", pr.Number);
        command.Parameters.AddWithValue("ids", keepIds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertThreadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, ReviewThreadData thread, string contentHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO review_threads (id, pr_repo, pr_number, path, line, resolved, author, finding, created_at, content_hash)
            VALUES (@id, @repo, @number, @path, @line, @resolved, @author, @finding, @created_at, @content_hash)
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
        command.Parameters.AddWithValue("content_hash", contentHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateThreadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        ReviewThreadData thread, string contentHash, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE review_threads
            SET path = @path, line = @line, resolved = @resolved,
                author = @author, finding = @finding, created_at = @created_at,
                content_hash = @content_hash
            WHERE id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", thread.Id);
        command.Parameters.AddWithValue("path", thread.Path);
        command.Parameters.AddWithValue("line", thread.Line ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("resolved", thread.Resolved);
        command.Parameters.AddWithValue("author", thread.Finding.Author);
        command.Parameters.AddWithValue("finding", thread.Finding.Body);
        command.Parameters.AddWithValue("created_at", thread.Finding.CreatedAt);
        command.Parameters.AddWithValue("content_hash", contentHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteCommentsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long threadId, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM review_comments WHERE thread_id = @thread_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("thread_id", threadId);
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

    /// <summary>Whether the thread already has a human-corrected decision.</summary>
    private static async Task<bool> IsManualDecisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long threadId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT confidence FROM decisions WHERE thread_id = @thread_id";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("thread_id", threadId);
        var confidence = await command.ExecuteScalarAsync(cancellationToken);
        return confidence is "manual";
    }

    private static async Task<int> CountDecisionsWithOutcomeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        PullRequestData pr, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM decisions d
            JOIN review_threads t ON t.id = d.thread_id
            WHERE t.pr_repo = @repo AND t.pr_number = @number AND d.outcome <> 'unknown'
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("repo", pr.Repo);
        command.Parameters.AddWithValue("number", pr.Number);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
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

    /// <summary>
    /// Lowercase hex SHA256 of the thread's visible content; detects changes
    /// between indexations without ids or timestamps. Deterministic format
    /// "reviewmemory-thread-v1": fields separated by '\n' with '\' escaped as
    /// '\\', '\r' as '\r' and '\n' as '\n'. Fields in order: version, path,
    /// line (empty when null), resolved ("true"/"false"), author and body of the
    /// finding, then author and body of each reply ordered by CreatedAt
    /// (tie-break: Id).
    /// </summary>
    private static string ContentHash(ReviewThreadData thread)
    {
        IEnumerable<string> Fields()
        {
            yield return "reviewmemory-thread-v1";
            yield return Escape(thread.Path);
            yield return thread.Line?.ToString(CultureInfo.InvariantCulture) ?? "";
            yield return thread.Resolved ? "true" : "false";
            yield return Escape(thread.Finding.Author);
            yield return Escape(thread.Finding.Body);
            foreach (var reply in thread.Replies.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id))
            {
                yield return Escape(reply.Author);
                yield return Escape(reply.Body);
            }
        }

        var canonical = string.Join('\n', Fields());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

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
