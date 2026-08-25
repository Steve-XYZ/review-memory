using System.Runtime.CompilerServices;
using Octokit;
using ReviewMemory.Core;

namespace ReviewMemory.GitHub;

/// <summary>
/// History source based on the GitHub REST API. Rebuilds review threads
/// by grouping comments along their in_reply_to chain and overwrites their
/// resolved state with a complementary GraphQL query.
///
/// Degradation lives here (not in the CLI): on missing token or GraphQL
/// failure the source continues with Resolved=false so the PR stream is
/// never aborted, and reports the reason once through the
/// <c>graphQlDegraded</c> callback; the CLI decides how to present it (stderr).
/// After the first failure it does not retry for the rest of the run.
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
    /// Resolved state via GraphQL; empty dictionary when there is no token or the
    /// query fails (reason reported once through the callback).
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
            ReportDegradation("no GitHub token (--token or GITHUB_TOKEN)");
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
