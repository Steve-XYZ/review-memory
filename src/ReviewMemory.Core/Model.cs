using ReviewMemory.Core.Decisions;
using ReviewMemory.Core.Diff;

namespace ReviewMemory.Core;

public enum PrState
{
    Open,
    Closed,
    Merged,
}

public sealed record CodeHunk(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    string Text);

public sealed record PullRequestFileData(
    string Path,
    int Additions,
    int Deletions,
    string? Patch)
{
    public IReadOnlyList<CodeHunk> Hunks => PatchHunks.Parse(Patch);
}

public sealed record ReviewCommentData(
    long Id,
    string Author,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record ReviewThreadData(
    long Id,
    string Path,
    int? Line,
    bool Resolved,
    ReviewCommentData Finding,
    IReadOnlyList<ReviewCommentData> Replies);

public sealed record PullRequestData(
    string Repo,
    int Number,
    string Title,
    string Body,
    string Author,
    PrState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? MergedAt,
    IReadOnlyList<PullRequestFileData> Files,
    IReadOnlyList<ReviewThreadData> Threads);

/// <summary>
/// Source of PR history. The production implementation talks to the GitHub
/// API; tests can feed in-memory repositories.
/// </summary>
public interface IPullRequestSource
{
    IAsyncEnumerable<PullRequestData> GetRecentPullRequestsAsync(
        string owner, string name, int last, CancellationToken cancellationToken = default);
}
