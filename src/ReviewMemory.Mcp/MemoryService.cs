using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Npgsql;
using ReviewMemory.Core;
using ReviewMemory.Core.Reporting;
using ReviewMemory.Storage;

namespace ReviewMemory.Mcp;

/// <summary>
/// Executes the two read operations over the memory (search and context)
/// reusing Core/Storage and returning the same JSON as the CLI with
/// --format json. Failures are reported as structured tool errors;
/// they never propagate an exception that would take the process down.
/// </summary>
public sealed class MemoryService(Func<string?>? connectionStringResolver = null)
{
    private const string LocalDockerConnectionString =
        "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory";

    private static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<string?> _connectionStringResolver =
        connectionStringResolver ?? (() => Environment.GetEnvironmentVariable("REVIEWMEMORY_CONNECTIONSTRING"));

    public async Task<CallToolResult> SearchAsync(
        string query, string? repo = null, string[]? files = null, int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var paths = ParseFiles(files);
        if (string.IsNullOrWhiteSpace(query) && paths.Length == 0)
        {
            return Error("invalid_search", "search requires text or --files");
        }

        return await RunAsync(async dataSource =>
        {
            var search = new SearchRepository(dataSource);
            var hits = await search.SearchAsync(new SearchQuery(query, repo, paths, limit), cancellationToken);
            return Success(SearchRenderer.RenderJson(hits));
        }, cancellationToken);
    }

    public async Task<CallToolResult> ContextAsync(
        string repo, int pr, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (ParseRepo(repo) is not { } parsed)
        {
            return Error("invalid_repo", "repository must be in owner/name format");
        }

        var canonical = $"{parsed.Owner}/{parsed.Name}";
        return await RunAsync(async dataSource =>
        {
            var search = new SearchRepository(dataSource);
            var summary = await search.GetPullRequestAsync(canonical, pr, cancellationToken);
            if (summary is null)
            {
                return Error(
                    "pr_not_indexed",
                    $"PR {canonical}#{pr} is not indexed; run 'reviewmemory index' first");
            }

            var hits = await search.ContextForPullRequestAsync(
                canonical, pr, new SearchQuery("", Limit: limit), cancellationToken);
            return Success(SearchRenderer.RenderJson(hits));
        }, cancellationToken);
    }

    private async Task<CallToolResult> RunAsync(
        Func<NpgsqlDataSource, Task<CallToolResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(_connectionStringResolver() ?? LocalDockerConnectionString);
            await DbMigrations.ApplyAsync(dataSource, cancellationToken);
            return await action(dataSource);
        }
        catch (Exception ex) when (ex is NpgsqlException or HttpRequestException)
        {
            return Error("database_unreachable", ex.Message);
        }
        catch (Exception ex)
        {
            return Error("internal_error", ex.Message);
        }
    }

    private static CallToolResult Success(string json) => new()
    {
        Content = [new TextContentBlock { Text = json }],
    };

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new ToolErrorResponse(new ToolError(code, message)), ErrorJsonOptions),
            },
        ],
    };

    private static (string Owner, string Name)? ParseRepo(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? (parts[0], parts[1])
            : null;
    }

    private static string[] ParseFiles(string[]? files) =>
        files?
            .SelectMany(f => f.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray() ?? [];

    private sealed record ToolError(string Code, string Message);

    private sealed record ToolErrorResponse(ToolError Error);
}
