using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ReviewMemory.Mcp;

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "reviewmemory", Version = "1.0.0" },
    ServerInstructions = """
        Servidor MCP de ReviewMemory: memoria institucional de code review.
        Antes de revisar un PR, consulta 'context' de ese PR y usa 'search'
        para recuperar decisiones previas del equipo por texto o archivos.
        Ambos tools son de solo lectura; la memoria se alimenta con el comando
        index del CLI, nunca desde este servidor.
        """,
    Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability(),
    },
    ToolCollection = MemoryTools.CreateCollection(new MemoryService()),
};

await McpServer.Create(new StdioServerTransport("reviewmemory"), options).RunAsync();
