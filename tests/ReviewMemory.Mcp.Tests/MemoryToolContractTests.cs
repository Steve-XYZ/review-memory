using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReviewMemory.Mcp;

namespace ReviewMemory.Mcp.Tests;

/// <summary>
/// MCP server contract per docs/specs/06-roadmap.md §3: two read-only tools
/// (search and context), structured error without crash when there is no DB.
/// </summary>
public sealed class MemoryToolContractTests
{
    private static readonly string Unreachable =
        "Host=localhost;Port=5999;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory";

    private static MemoryService Service() => new(() => Unreachable);

    [Fact]
    public void Registers_exactly_search_and_context_as_read_only()
    {
        var tools = MemoryTools.CreateCollection(Service());

        var byName = tools.ToDictionary(t => t.ProtocolTool.Name);
        Assert.Equal(["context", "search"], byName.Keys.OrderBy(n => n).ToArray());

        foreach (var tool in byName.Values)
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.NotEqual(true, tool.ProtocolTool.Annotations?.DestructiveHint);
        }
    }

    [Fact]
    public void Search_requires_query_and_context_requires_repo_and_pr_in_schema()
    {
        var tools = MemoryTools.CreateCollection(Service()).ToDictionary(t => t.ProtocolTool.Name);

        Assert.Equal(["query"], RequiredParams(tools["search"].ProtocolTool.InputSchema));
        Assert.Equal(["pr", "repo"], RequiredParams(tools["context"].ProtocolTool.InputSchema).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task Search_without_text_or_files_returns_structured_error()
    {
        var result = await Service().SearchAsync("   ");

        Assert.True(result.IsError);
        Assert.Equal("invalid_search", ErrorCode(result));
    }

    [Fact]
    public async Task Context_with_malformed_repo_returns_structured_error()
    {
        var result = await Service().ContextAsync("no-tiene-formato-owner-name", 1);

        Assert.True(result.IsError);
        Assert.Equal("invalid_repo", ErrorCode(result));
    }

    [Fact]
    public async Task Unreachable_database_on_search_returns_structured_error()
    {
        var result = await Service().SearchAsync("provider transaction twice");

        Assert.True(result.IsError);
        Assert.Equal("database_unreachable", ErrorCode(result));
    }

    [Fact]
    public async Task Unreachable_database_on_context_returns_structured_error()
    {
        var result = await Service().ContextAsync("owner/name", 1);

        Assert.True(result.IsError);
        Assert.Equal("database_unreachable", ErrorCode(result));
    }

    private static string[] RequiredParams(JsonElement inputSchema) =>
        inputSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

    private static string ErrorCode(CallToolResult result)
    {
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var doc = JsonDocument.Parse(block.Text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
