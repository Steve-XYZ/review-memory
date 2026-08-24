using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace ReviewMemory.Mcp;

/// <summary>
/// Registro de los dos tools de lectura del servidor. Solo lectura por diseño:
/// la memoria se alimenta con el comando index del CLI, nunca desde el agente.
/// </summary>
public static class MemoryTools
{
    public static McpServerPrimitiveCollection<McpServerTool> CreateCollection(MemoryService service) =>
    [
        McpServerTool.Create(service.SearchAsync, new McpServerToolCreateOptions
        {
            Name = "search",
            Title = "Búsqueda histórica",
            Description = """
                Busca discusiones históricas de code review por texto libre y/o archivos tocados.
                Parámetros: query (texto libre), repo (filtro opcional owner/name), files (rutas
                opcionales, admite lista o csv) y limit (default 10). Devuelve exactamente el mismo
                JSON que 'reviewmemory search --format json': un array de hits con threadId, repo,
                number, prTitle, path, line, finding, outcome, reason, score, createdAt y url.
                """,
            ReadOnly = true,
            Idempotent = true,
            Destructive = false,
        }),
        McpServerTool.Create(service.ContextAsync, new McpServerToolCreateOptions
        {
            Name = "context",
            Title = "Contexto histórico de un PR",
            Description = """
                Recupera las discusiones históricas relevantes para un PR concreto, excluyendo las
                del propio PR. Parámetros: repo (owner/name, requerido), pr (número, requerido) y
                limit (default 10). El PR debe estar indexado previamente con 'reviewmemory index'.
                Devuelve exactamente el mismo JSON que 'reviewmemory context --format json'.
                """,
            ReadOnly = true,
            Idempotent = true,
            Destructive = false,
        }),
    ];
}
