using System.Runtime.CompilerServices;
using Octokit;
using ReviewMemory.Core;

namespace ReviewMemory.GitHub;

/// <summary>
/// Fuente de historial basada en la API REST de GitHub. Reconstruye los
/// hilos de review agrupando comentarios por su cadena de in_reply_to y
/// sobrescribe su estado resolved con una consulta GraphQL complementaria.
///
/// La degradación vive aquí (y no en el CLI): ante token ausente o fallo de
/// GraphQL la fuente continúa con Resolved=false para que el stream de PRs
/// nunca se aborte, e informa el motivo una sola vez a través del callback
/// <c>graphQlDegraded</c>; el CLI decide cómo presentarlo (stderr).
/// Tras el primer fallo no se reintenta en el resto de la corrida.
/// </summary>
public sealed class GitHubPullRequestSource : IPullRequestSource
{
    private static readonly IReadOnlyDictionary<long, bool> NoResolvedStates =
        new Dictionary<long, bool>();

    private readonly GitHubClient _client;
    private readonly GitHubGraphQLClient? _graphQl;
    private readonly Action<string>? _graphQlDegraded;
    private bool _degradationReported;

    public GitHubPullRequestSource(string? token, Action<string>? graphQlDegraded = null)
        : this(token, graphQlDegraded, CreateGraphQlClient(token))
    {
    }

    internal GitHubPullRequestSource(
        string? token, Action<string>? graphQlDegraded, GitHubGraphQLClient? graphQl)
    {
        _client = new GitHubClient(new ProductHeaderValue("review-memory"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            _client.Credentials = new Credentials(token);
        }

        _graphQlDegraded = graphQlDegraded;
        _graphQl = graphQl;
    }

    private static GitHubGraphQLClient? CreateGraphQlClient(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : new GitHubGraphQLClient(token);

    public async IAsyncEnumerable<PullRequestData> GetRecentPullRequestsAsync(
        string owner,
        string name,
        int last,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var collected = 0;

        for (var page = 1; collected < last; page++)
        {
            var batch = await _client.PullRequest.GetAllForRepository(
                owner,
                name,
                new PullRequestRequest
                {
                    State = ItemStateFilter.All,
                    SortProperty = PullRequestSort.Updated,
                    SortDirection = SortDirection.Descending,
                },
                new ApiOptions { PageSize = 100, StartPage = page, PageCount = 1 });

            if (batch.Count == 0)
            {
                yield break;
            }

            foreach (var pullRequest in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await LoadAsync(owner, name, pullRequest, cancellationToken);
                collected++;
                if (collected >= last)
                {
                    break;
                }
            }
        }
    }

    private async Task<PullRequestData> LoadAsync(
        string owner, string name, Octokit.PullRequest pullRequest, CancellationToken cancellationToken)
    {
        var files = await _client.PullRequest.Files(owner, name, pullRequest.Number);
        var comments = await _client.PullRequest.ReviewComment.GetAll(owner, name, pullRequest.Number);
        var resolvedStates = await ResolvedStatesOrEmptyAsync(owner, name, pullRequest.Number, cancellationToken);

        return new PullRequestData(
            Repo: $"{owner}/{name}",
            Number: pullRequest.Number,
            Title: pullRequest.Title ?? "",
            Body: pullRequest.Body ?? "",
            Author: pullRequest.User?.Login ?? "unknown",
            State: MapState(pullRequest),
            CreatedAt: pullRequest.CreatedAt,
            UpdatedAt: pullRequest.UpdatedAt,
            MergedAt: pullRequest.MergedAt,
            Files: [.. files.Select(MapFile)],
            Threads: WithResolved(BuildThreads(comments), resolvedStates));
    }

    /// <summary>
    /// Estado resolved vía GraphQL; diccionario vacío si no hay token o la
    /// consulta falla (motivo reportado una sola vez por el callback).
    /// </summary>
    internal async Task<IReadOnlyDictionary<long, bool>> ResolvedStatesOrEmptyAsync(
        string owner, string name, int number, CancellationToken cancellationToken = default)
    {
        if (_degradationReported)
        {
            return NoResolvedStates;
        }

        if (_graphQl is null)
        {
            ReportDegradation("no hay token de GitHub (--token o GITHUB_TOKEN)");
            return NoResolvedStates;
        }

        try
        {
            return await _graphQl.GetResolvedByRootCommentAsync(owner, name, number, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is HttpRequestException or TaskCanceledException or GitHubGraphQlException)
        {
            ReportDegradation(ex.Message);
            return NoResolvedStates;
        }
    }

    private void ReportDegradation(string reason)
    {
        _degradationReported = true;
        _graphQlDegraded?.Invoke(reason);
    }

    internal static IReadOnlyList<ReviewThreadData> WithResolved(
        IReadOnlyList<ReviewThreadData> threads, IReadOnlyDictionary<long, bool> resolvedByRootCommentId) =>
        resolvedByRootCommentId.Count == 0
            ? threads
            :
            [
                .. threads.Select(thread => resolvedByRootCommentId.TryGetValue(thread.Id, out var resolved)
                    ? thread with { Resolved = resolved }
                    : thread),
            ];

    private static PrState MapState(Octokit.PullRequest pullRequest) =>
        pullRequest.Merged ? PrState.Merged
        : pullRequest.ClosedAt is not null ? PrState.Closed
        : PrState.Open;

    private static PullRequestFileData MapFile(Octokit.PullRequestFile file) => new(
        Path: file.FileName,
        Additions: file.Additions,
        Deletions: file.Deletions,
        Patch: file.Patch);

    internal static IReadOnlyList<ReviewThreadData> BuildThreads(
        IReadOnlyList<PullRequestReviewComment> comments)
    {
        var byId = comments.ToDictionary(c => c.Id);

        long? RootOf(PullRequestReviewComment comment)
        {
            var current = comment;
            var visited = new HashSet<long>();
            while (current.InReplyToId is { } parent && byId.TryGetValue(parent, out var next))
            {
                if (!visited.Add(current.Id))
                {
                    return null;
                }
                current = next;
            }
            return current.InReplyToId is null ? current.Id : null;
        }

        return comments
            .Select(comment => (Comment: comment, Root: RootOf(comment)))
            .Where(pair => pair.Root is not null)
            .GroupBy(pair => pair.Root!.Value)
            .Select(group =>
            {
                var ordered = group.Select(p => p.Comment).OrderBy(c => c.CreatedAt).ToList();
                var finding = ordered[0];
                return new ReviewThreadData(
                    Id: finding.Id,
                    Path: finding.Path ?? "",
                    Line: finding.Position ?? finding.OriginalPosition,
                    Resolved: false,
                    Finding: new ReviewCommentData(finding.Id, finding.User?.Login ?? "unknown", finding.Body ?? "", finding.CreatedAt),
                    Replies:
                    [
                        .. ordered.Skip(1).Select(c =>
                            new ReviewCommentData(c.Id, c.User?.Login ?? "unknown", c.Body ?? "", c.CreatedAt)),
                    ]);
            })
            .ToList();
    }
}
