using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReviewMemory.Mcp;

namespace ReviewMemory.Mcp.Tests;

/// <summary>
/// Contrato del servidor MCP según docs/specs/06-roadmap.md §3: dos tools de
/// solo lectura (search y context), error estructurado sin crash cuando no hay BD.
/// </summary>
public sealed class MemoryToolContractTests
{
    private static readonly string Unreachable =
        "Host=localhost;Port=5999;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory";

    private static MemoryService Service() => new(() => Unreachable);

    [Fact]
    public void Registra_exactamente_search_y_context_en_solo_lectura()
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
    public void Search_exige_query_y_context_exige_repo_y_pr_en_el_schema()
    {
        var tools = MemoryTools.CreateCollection(Service()).ToDictionary(t => t.ProtocolTool.Name);

        Assert.Equal(["query"], RequiredParams(tools["search"].ProtocolTool.InputSchema));
        Assert.Equal(["pr", "repo"], RequiredParams(tools["context"].ProtocolTool.InputSchema).OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task Search_sin_texto_ni_archivos_devuelve_error_estructurado()
    {
        var result = await Service().SearchAsync("   ");

        Assert.True(result.IsError);
        Assert.Equal("invalid_search", ErrorCode(result));
    }

    [Fact]
    public async Task Context_con_repo_malformado_devuelve_error_estructurado()
    {
        var result = await Service().ContextAsync("no-tiene-formato-owner-name", 1);

        Assert.True(result.IsError);
        Assert.Equal("invalid_repo", ErrorCode(result));
    }

    [Fact]
    public async Task Base_datos_inalcanzable_en_search_devuelve_error_estructurado()
    {
        var result = await Service().SearchAsync("provider transaction twice");

        Assert.True(result.IsError);
        Assert.Equal("database_unreachable", ErrorCode(result));
    }

    [Fact]
    public async Task Base_datos_inalcanzable_en_context_devuelve_error_estructurado()
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
