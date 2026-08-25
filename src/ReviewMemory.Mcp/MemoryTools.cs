using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace ReviewMemory.Mcp;

/// <summary>
/// Registration of the server's two read tools. Read-only by design:
/// the memory is fed with the CLI's index command, never from the agent.
/// </summary>
public static class MemoryTools
{
    public static McpServerPrimitiveCollection<McpServerTool> CreateCollection(MemoryService service) =>
    [
        McpServerTool.Create(service.SearchAsync, new McpServerToolCreateOptions
        {
            Name = "search",
            Title = "Historical search",
            Description = """
                Searches historical code review discussions by free text and/or touched files.
                Parameters: query (free text), repo (optional owner/name filter), files (optional
                paths, accepts a list or csv) and limit (default 10). Returns exactly the same
                JSON as 'reviewmemory search --format json': an array of hits with threadId, repo,
                number, prTitle, path, line, finding, outcome, reason, score, createdAt and url.
                """,
            ReadOnly = true,
            Idempotent = true,
            Destructive = false,
        }),
        McpServerTool.Create(service.ContextAsync, new McpServerToolCreateOptions
        {
            Name = "context",
            Title = "Historical context for a PR",
            Description = """
                Retrieves the historical discussions relevant for a specific PR, excluding those
                of the PR itself. Parameters: repo (owner/name, required), pr (number, required) and
                limit (default 10). The PR must be indexed beforehand with 'reviewmemory index'.
                Returns exactly the same JSON as 'reviewmemory context --format json'.
                """,
            ReadOnly = true,
            Idempotent = true,
            Destructive = false,
        }),
    ];
}
