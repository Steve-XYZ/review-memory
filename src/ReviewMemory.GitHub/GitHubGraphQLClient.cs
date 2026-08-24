using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReviewMemory.GitHub;

/// <summary>
/// Error de nivel GraphQL (payload con <c>errors</c> sin datos utilizables o
/// respuesta HTTP no satisfactoria, incluido el rate limit). El llamador lo
/// trata como motivo de degradación.
/// </summary>
public sealed class GitHubGraphQlException(string message) : Exception(message);

/// <summary>
/// Cliente mínimo de la API GraphQL v4 de GitHub sobre HttpClient propio y
/// System.Text.Json. Solo expone lo que la ingesta necesita: el estado
/// <c>isResolved</c> de los hilos de review, que la API REST v3 no publica.
///
/// Cruce de ids: el nodo GraphQL trae un id opaco (<c>PRRT_…</c>) que no
/// coincide con el id numérico que ReviewMemory usa como identidad del hilo
/// (el id REST v3 del comentario raíz). Por eso cada hilo pide su primer
/// comentario —la conexión llega en orden cronológico y el primero es el que
/// abrió el hilo— y se cruza por su <c>databaseId</c>, el mismo id numérico
/// que la API REST usa para ese comentario.
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
    /// Devuelve el estado resolved de cada hilo del PR, indexado por el id del
    /// comentario raíz (el mismo valor que <see cref="Core.ReviewThreadData.Id"/>).
    /// Lanza <see cref="HttpRequestException"/> ante fallo HTTP y
    /// <see cref="GitHubGraphQlException"/> ante errores o rate limit de GraphQL.
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
                throw new GitHubGraphQlException(FirstError(root) ?? "respuesta sin datos");
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
