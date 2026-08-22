using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Decisions;
using ReviewMemory.Core.Ranking;
using ReviewMemory.Core.Reporting;

namespace ReviewMemory.Storage;

public sealed class SearchRepository(NpgsqlDataSource dataSource)
{
    private const string SelectSql = """
        SELECT
            t.id          AS thread_id,
            t.pr_repo     AS repo,
            t.pr_number   AS number,
            p.title       AS pr_title,
            t.path,
            t.line,
            t.finding,
            d.outcome     AS outcome,
            d.reason      AS reason,
            t.created_at  AS created_at,
            @w_text * CASE WHEN @has_text
                      THEN LEAST(1.0, ts_rank(t.search_vec, websearch_to_tsquery('english', @text)) * @text_scale)
                      ELSE 0 END
            + @w_file * COALESCE((
                  SELECT count(*)::float8 / GREATEST(cardinality(@paths), 1)
                  FROM unnest(@paths) AS q(path)
                  JOIN pr_files f
                    ON f.pr_repo = t.pr_repo AND f.pr_number = t.pr_number AND f.path = q.path
              ), 0)
            + @w_recency * exp(- GREATEST(EXTRACT(EPOCH FROM (now() - t.created_at)) / 86400.0, 0) / @half_life_days)
                     AS score
        FROM review_threads t
        JOIN pull_requests p ON p.repo = t.pr_repo AND p.number = t.pr_number
        LEFT JOIN decisions d ON d.thread_id = t.id
        WHERE (@repo IS NULL OR t.pr_repo = @repo)
          AND (@text_is_filter = false OR @has_text = false
               OR t.search_vec @@ websearch_to_tsquery('english', @text))
          AND (@exclude_repo IS NULL OR t.pr_repo <> @exclude_repo OR t.pr_number <> @exclude_number)
          AND (@paths_empty OR EXISTS (
                  SELECT 1 FROM pr_files f2
                  WHERE f2.pr_repo = t.pr_repo AND f2.pr_number = t.pr_number AND f2.path = ANY(@paths)))
        ORDER BY score DESC
        LIMIT @limit
        """;

    /// <summary>
    /// Busca discusiones históricas por texto y/o archivos tocados.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text) && query.Paths is not { Count: > 0 })
        {
            throw new ArgumentException("La búsqueda requiere texto o al menos una ruta de archivo.");
        }

        return await QueryAsync(query, exclude: null, textIsFilter: true, cancellationToken);
    }

    /// <summary>
    /// Encuentra discusiones en OTROS PRs relevantes para los archivos y el
    /// tema del PR indicado. Excluye las discusiones del propio PR.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> ContextForPullRequestAsync(
        string repo, int number, SearchQuery? options = null, CancellationToken cancellationToken = default)
    {
        var summary = await GetPullRequestAsync(repo, number, cancellationToken)
                      ?? throw new InvalidOperationException($"El PR {repo}#{number} no está indexado.");

        var paths = await GetFilePathsAsync(repo, number, cancellationToken);
        var text = string.IsNullOrWhiteSpace(options?.Text)
            ? $"{summary.Title}\n{Truncate(summary.Body, 400)}"
            : options!.Text;

        var limit = options?.Limit ?? 5;
        return await QueryAsync(
            new SearchQuery(text, repo, paths, limit),
            (Repo: repo, Number: number),
            textIsFilter: false,
            cancellationToken);
    }

    public async Task<PullRequestSummary?> GetPullRequestAsync(
        string repo, int number, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT title, body, author, state, created_at
            FROM pull_requests
            WHERE repo = @repo AND number = @number
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("repo", repo);
        command.Parameters.AddWithValue("number", number);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PullRequestSummary(
            Repo: repo,
            Number: number,
            Title: reader.GetString(0),
            Body: reader.GetString(1),
            Author: reader.GetString(2),
            State: reader.GetString(3),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task<IReadOnlyList<string>> GetFilePathsAsync(
        string repo, int number, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT path FROM pr_files WHERE pr_repo = @repo AND pr_number = @number";

        var paths = new List<string>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("repo", repo);
        command.Parameters.AddWithValue("number", number);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    private async Task<List<SearchHit>> QueryAsync(
        SearchQuery query, (string Repo, int Number)? exclude,
        bool textIsFilter = true, CancellationToken cancellationToken = default)
    {
        var text = (query.Text ?? "").Trim();
        var hasText = text.Length > 0;
        var paths = query.Paths ?? [];
        var limit = Math.Clamp(query.Limit, 1, 100);

        var hits = new List<SearchHit>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SelectSql, connection);
        command.Parameters.AddWithValue("has_text", hasText);
        command.Parameters.AddWithValue("text", text);
        command.Parameters.AddWithValue("paths", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text, paths);
        command.Parameters.AddWithValue("paths_empty", paths.Count == 0);
        command.Parameters.AddWithValue("text_is_filter", textIsFilter);
        command.Parameters.AddWithValue("repo", NpgsqlTypes.NpgsqlDbType.Text, query.Repo ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("exclude_repo", NpgsqlTypes.NpgsqlDbType.Text, exclude?.Repo ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("exclude_number", NpgsqlTypes.NpgsqlDbType.Integer, exclude?.Number ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("w_text", RankingWeights.TextMatch);
        command.Parameters.AddWithValue("w_file", RankingWeights.FileOverlap);
        command.Parameters.AddWithValue("w_recency", RankingWeights.Recency);
        command.Parameters.AddWithValue("text_scale", RankingWeights.TextScale);
        command.Parameters.AddWithValue("half_life_days", RankingWeights.RecencyHalfLifeDays);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                ThreadId: reader.GetInt64(0),
                Repo: reader.GetString(1),
                Number: reader.GetInt32(2),
                PrTitle: reader.GetString(3),
                Path: reader.GetString(4),
                Line: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Finding: reader.GetString(6),
                Outcome: ParseOutcome(reader.IsDBNull(7) ? null : reader.GetString(7)),
                Reason: reader.IsDBNull(8) ? null : reader.GetString(8),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
                Score: reader.GetDouble(10)));
        }

        return hits;
    }

    private static DecisionOutcome ParseOutcome(string? outcome) => outcome switch
    {
        "accepted" => DecisionOutcome.Accepted,
        "rejected" => DecisionOutcome.Rejected,
        "partially_accepted" => DecisionOutcome.PartiallyAccepted,
        _ => DecisionOutcome.Unknown,
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

public sealed record PullRequestSummary(
    string Repo,
    int Number,
    string Title,
    string Body,
    string Author,
    string State,
    DateTimeOffset CreatedAt);
