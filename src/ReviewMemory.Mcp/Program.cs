using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReviewMemory.Mcp;

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "reviewmemory", Version = "1.0.0" },
    ServerInstructions = """
        ReviewMemory MCP server: institutional memory for code review.
        Before reviewing a PR, query its 'context' and use 'search'
        to retrieve the team's prior decisions by text or files.
        Both tools are read-only; the memory is fed with the CLI's
        index command, never from this server.
        """,
    Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability(),
    },
    ToolCollection = MemoryTools.CreateCollection(new MemoryService()),
};

await McpServer.Create(new StdioServerTransport("reviewmemory"), options).RunAsync();
