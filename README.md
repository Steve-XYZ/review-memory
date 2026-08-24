# ReviewMemory

Memoria institucional para code review. No es otro "AI reviewer": es la memoria que cualquier reviewer —humano, Codex, Claude, Copilot— consulta antes de revisar un PR.

```
reviewmemory context Shirka-Corporation/player-manager --pr 2268
```

```
3 discusión(es) históricamente relevante(s)

1. HIGH — Provider callbacks could be processed twice.
   Similitud: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120
   ...
```

El agente que revisa tu PR sabe qué preocupaciones son históricamente relevantes y cuáles ya fueron descartadas por el equipo. Eso vale más que meter veinte reglas más en `AGENTS.md`.

**La memoria es el producto; el LLM reviewer no lo es.**

## Stack

.NET 10 · System.CommandLine · Octokit · Npgsql (PostgreSQL full-text search) · ModelContextProtocol (servidor MCP stdio) · xUnit

## Comandos

```bash
# indexar los últimos N PRs de un repositorio
reviewmemory index Shirka-Corporation/player-manager --last 200

# buscar discusiones históricas por texto y/o archivos tocados
reviewmemory search "duplicate lotto transaction" --repo Shirka-Corporation/player-manager
reviewmemory search "retry idempotency" --files src/LottoPendingTransactionProcessor.cs

# contexto histórico relevante para un PR concreto (excluye sus propias discusiones)
reviewmemory context Shirka-Corporation/player-manager --pr 2268 --limit 5
```

`--format json` devuelve el mismo resultado serializado para consumo de agentes.

## Servidor MCP

La misma memoria, expuesta como tools MCP de solo lectura sobre stdio: los agentes (Claude Code, OpenCode, Codex, Copilot, Cursor…) consultan el historial sin invocar el CLI ni saber nada de Postgres.

| Tool | Parámetros | Equivale a |
|---|---|---|
| `search` | `query`, `repo` (opcional), `files` (opcional), `limit` (opcional) | `reviewmemory search --format json` |
| `context` | `repo`, `pr`, `limit` (opcional) | `reviewmemory context --format json` |

Cada tool devuelve exactamente el mismo JSON que el comando homónimo del CLI con `--format json`. No hay tools de escritura: la memoria se alimenta con `index`, nunca desde el agente consumidor. Si la BD no está accesible, la respuesta es un error estructurado (`isError: true` con código y mensaje) y el proceso sigue vivo.

### Ejecutar

```bash
docker compose up -d db                       # la BD debe estar levantada
dotnet run --project src/ReviewMemory.Mcp     # servidor MCP sobre stdio
```

Como herramienta .NET autocontenida: publica una vez y apunta cada cliente al binario.

```bash
dotnet publish src/ReviewMemory.Mcp -c Release -o publish/mcp
./publish/mcp/ReviewMemory.Mcp                # binario autocontenido del server
```

La conexión se resuelve igual que en el CLI: variable `REVIEWMEMORY_CONNECTIONSTRING` o, por defecto, la BD local de docker-compose (`localhost:5433`).

### Registro en Claude Code

En el `.mcp.json` del proyecto (o en la configuración global):

```json
{
  "mcpServers": {
    "reviewmemory": {
      "type": "stdio",
      "command": "/ruta/absoluta/a/review-memory/publish/mcp/ReviewMemory.Mcp",
      "env": {
        "REVIEWMEMORY_CONNECTIONSTRING": "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory"
      }
    }
  }
}
```

### Registro en OpenCode

En `opencode.json`:

```json
{
  "mcp": {
    "reviewmemory": {
      "type": "local",
      "command": ["/ruta/absoluta/a/review-memory/publish/mcp/ReviewMemory.Mcp"],
      "environment": {
        "REVIEWMEMORY_CONNECTIONSTRING": "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory"
      }
    }
  }
}
```

## Configuración

| Variable | Uso |
|---|---|
| `GITHUB_TOKEN` | token de GitHub para `index` (sin token: 60 req/h) |
| `REVIEWMEMORY_CONNECTIONSTRING` | conexión Postgres; por defecto la BD local de docker-compose |

```bash
docker compose up -d          # Postgres 17 en localhost:5433 (evita el 5432 típico de otras BD locales)
dotnet run --project src/ReviewMemory.Cli -- index owner/name --last 50
```

| Exit code | Significado |
|---|---|
| 0 | éxito |
| 1 | error en tiempo de ejecución (GitHub, red, base de datos) |
| 2 | uso inválido o entidad no encontrada |

## Qué hace el ranking (etapa 1)

Sin IA. Tres señales combinadas sobre las discusiones de review indexadas:

- **Full-text search**: `tsvector` generado sobre el comentario del reviewer + la ruta del archivo (`websearch_to_tsquery`, índice GIN).
- **Solapamiento de archivos**: proporción de rutas consultadas que el PR candidato tocó.
- **Recencia**: decaimiento exponencial con constante temporal de 120 días (mitad del factor ≈83 días; detalle en `docs/specs/03-recuperacion.md`).

Cada hilo lleva una **decisión inferida** (`accepted` / `rejected` / `partially_accepted` / `unknown`) a partir de señales léxicas deterministas en las respuestas; señales contradictorias quedan como `unknown` en lugar de adivinar.

## Estado: bases de la etapa 1

- [x] Solución `ReviewMemory.slnx`: Cli · Core · GitHub · Storage (+ tests)
- [x] Esquema PostgreSQL con migraciones embebidas (`schema_migrations`)
- [x] Ingesta REST de GitHub: PRs, archivos, hunks, comentarios agrupados en hilos
- [x] Búsqueda FTS + solapamiento + recencia; comando `context` que excluye el propio PR
- [x] CI (build + tests contra Postgres real)
- [ ] Specs en `docs/specs/`
- [ ] Estado "resolved" de hilos vía GraphQL (REST no lo expone)
- [ ] Re-indexado incremental (hoy re-indexar borra y recrea los hilos del PR)
- [x] MCP server stdio con tools `search` y `context`, paridad JSON con el CLI
- [ ] embeddings/pgvector · aprendizaje post-review

## Documentación

Las specs de la siguiente etapa se escriben en [docs/specs](docs/specs/README.md).
