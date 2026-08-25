using ReviewMemory.Core;
using ReviewMemory.GitHub;

namespace ReviewMemory.GitHub.Tests;

public sealed class GitHubPullRequestSourceTests
{
    [Fact]
    public void Overwrites_resolved_cross_referencing_by_root_comment_id()
    {
        var threads = new List<ReviewThreadData> { Thread(111), Thread(222) };
        var resolvedByRootCommentId = new Dictionary<long, bool> { [111] = true };

        var enriched = GitHubPullRequestSource.WithResolved(threads, resolvedByRootCommentId);

        Assert.Equal(2, enriched.Count);
        Assert.True(enriched[0].Resolved);
        Assert.False(enriched[1].Resolved);
    }

    [Fact]
    public void Without_map_leaves_threads_intact()
    {
        var threads = new List<ReviewThreadData> { Thread(111, resolved: true) };

        var same = GitHubPullRequestSource.WithResolved(threads, new Dictionary<long, bool>());

        Assert.Same(threads, same);
    }

    [Fact]
    public async Task GraphQL_failure_degrades_to_empty_and_warns_once()
    {
        var reasons = new List<string>();
        var source = NewSource(
            "token",
            reason => reasons.Add(reason),
            new GitHubGraphQLClient(new HttpClient(new ThrowingHandler()), "token"));

        var first = await source.ResolvedStatesOrEmptyAsync("acme", "app", 1);
        var second = await source.ResolvedStatesOrEmptyAsync("acme", "app", 2);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Single(reasons);
    }

    [Fact]
    public async Task Missing_token_degrades_with_explicit_reason()
    {
        var reasons = new List<string>();
        var source = NewSource(null, reasons.Add, graphQl: null);

        var resolved = await source.ResolvedStatesOrEmptyAsync("acme", "app", 1);

        Assert.Empty(resolved);
        var reason = Assert.Single(reasons);
        Assert.Contains("token", reason);
    }

    private static GitHubPullRequestSource NewSource(
        string? token, Action<string> degraded, GitHubGraphQLClient? graphQl) =>
        new(token, degraded, graphQl);

    private static ReviewThreadData Thread(long id, bool resolved = false) => new(
        Id: id,
        Path: "src/A.cs",
        Line: 10,
        Resolved: resolved,
        Finding: new ReviewCommentData(id, "reviewer", "finding", DateTimeOffset.UtcNow),
        Replies: []);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
