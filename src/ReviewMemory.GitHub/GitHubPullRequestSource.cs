using System.Runtime.CompilerServices;
using Octokit;
using ReviewMemory.Core;

namespace ReviewMemory.GitHub;

/// <summary>
/// Fuente de historial basada en la API REST de GitHub. Reconstruye los
/// hilos de review agrupando comentarios por su cadena de in_reply_to;
/// el estado "resolved" del hilo no está disponible vía REST y llega false.
/// </summary>
public sealed class GitHubPullRequestSource : IPullRequestSource
{
    private readonly GitHubClient _client;

    public GitHubPullRequestSource(string? token)
    {
        _client = new GitHubClient(new ProductHeaderValue("review-memory"));
        if (!string.IsNullOrWhiteSpace(token))
        {
            _client.Credentials = new Credentials(token);
        }
    }

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
            Threads: BuildThreads(comments));
    }

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
