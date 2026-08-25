namespace ReviewMemory.Core;

/// <summary>
/// Search over the indexed memory. Text applies full-text search;
/// file paths filter PRs that touched those files.
/// At least one of the two signals must be present.
/// </summary>
public sealed record SearchQuery(
    string Text,
    string? Repo = null,
    IReadOnlyList<string>? Paths = null,
    int Limit = 10);
