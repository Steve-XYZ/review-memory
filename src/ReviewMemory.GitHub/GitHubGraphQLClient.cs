using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReviewMemory.GitHub;

/// <summary>
/// GraphQL-level error (payload with <c>errors</c> and no usable data, or
/// non-success HTTP response, including rate limit). The caller treats it
/// as a degradation reason.
/// </summary>
public sealed class GitHubGraphQlException(string message) : Exception(message);

/// <summary>
/// Minimal client for GitHub's GraphQL v4 API over its own HttpClient and
/// System.Text.Json. Only exposes what ingestion needs: the <c>isResolved</c>
/// state of review threads, which the REST v3 API does not publish.
///
/// Id cross-referencing: the GraphQL node carries an opaque id (<c>PRRT_…</c>) that does
/// not match the numeric id ReviewMemory uses as thread identity (the REST v3
/// id of the root comment). So each thread requests its first comment —replies
/// arrive in chronological order and the first is the one that opened the
/// thread— and is cross-referenced by its <c>databaseId</c>, the same numeric id
/// the REST API uses for that comment.
/// </summary>
public sealed class GitHubGraphQLClient
{
    private const string Endpoint = "https://api.github.com/graphql";

    private const string Query = """
        query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              reviewThreads(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  isResolved
                  comments(first: 1) { nodes { databaseId } }
                }
              }
            }
          }
        }
        """;

    private readonly HttpClient _http;

    public GitHubGraphQLClient(string token)
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, token)
    {
    }

    internal GitHubGraphQLClient(HttpClient httpClient, string token)
    {
        _http = httpClient;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("review-memory");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Returns each thread's resolved state of the PR, indexed by the root
    /// comment id (the same value as <see cref="Core.ReviewThreadData.Id"/>).
    /// Throws <see cref="HttpRequestException"/> on HTTP failure and
    /// <see cref="GitHubGraphQlException"/> on GraphQL errors or rate limit.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, bool>> GetResolvedByRootCommentAsync(
        string owner, string name, int number, CancellationToken cancellationToken = default)
    {
        var resolved = new Dictionary<long, bool>();
        string? cursor = null;
        bool hasNextPage;

        do
        {
            hasNextPage = false;
            var request = new
            {
                query = Query,
                variables = new { owner, name, number, cursor },
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(Endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubGraphQlException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd());
            }

            using var payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var root = payload.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                throw new GitHubGraphQlException(FirstError(root) ?? "response without data");
            }

            var threads = Navigate(data, "repository", "pullRequest", "reviewThreads");
            if (threads is null || threads.Value.ValueKind == JsonValueKind.Null)
            {
                break;
            }

            foreach (var node in threads.Value.GetProperty("nodes").EnumerateArray())
            {
                var comments = node.GetProperty("comments").GetProperty("nodes");
                if (comments.GetArrayLength() == 0)
                {
                    continue;
                }

                resolved[comments[0].GetProperty("databaseId").GetInt64()] =
                    node.GetProperty("isResolved").GetBoolean();
            }

            var pageInfo = threads.Value.GetProperty("pageInfo");
            hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
            if (hasNextPage)
            {
                cursor = pageInfo.GetProperty("endCursor").GetString();
            }
        }
        while (hasNextPage);

        return resolved;
    }

    private static string? FirstError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            return errors[0].TryGetProperty("message", out var message)
                ? message.GetString()
                : errors[0].GetRawText();
        }

        return null;
    }

    private static JsonElement? Navigate(JsonElement element, params string[] path)
    {
        foreach (var property in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out element))
            {
                return null;
            }
        }

        return element;
    }
}
