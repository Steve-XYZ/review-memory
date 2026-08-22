# CLI

La superficie CLI vive en `src/ReviewMemory.Cli/Program.cs` sobre System.CommandLine. El binario es `ReviewMemory.Cli`; en desarrollo se invoca con `dotnet run --project src/ReviewMemory.Cli -- <comando>`. Los comandos `search` y `context` solo hablan con PostgreSQL; solo `index` habla con la API de GitHub.

## Comandos

### index

```
reviewmemory index <repo> [options]
```

Descarga y indexa los PRs recientes de un repositorio. Pagina la API REST (state `all`, ordenados por `updated` descendente, páginas de 100) hasta cubrir `--last` PRs o agotar el historial; por cada PR descarga archivos y comentarios de review, agrupa los comentarios en hilos por su cadena de `in_reply_to`, infiere la decisión de cada hilo y hace upsert. Re-indexar borra y recrea los hilos del PR.

| Argumento/Opción | Tipo | Default | Descripción |
|---|---|---|---|
| `repo` | `owner/name` | — | Repositorio a indexar; otro formato es error de uso (exit 2) |
| `--last` | int | `200` | Cantidad de PRs más recientemente actualizados a indexar |
| `--token` | string? | `$GITHUB_TOKEN` | Token de GitHub; vacío o ausente ⇒ cliente anónimo (60 req/h) |
| `--connection-string` | string? | ver [Configuración](#configuración) | Conexión Postgres |

### search

```
reviewmemory search <query> [options]
```

Busca discusiones históricas de review por texto (full-text search) y/o por archivos tocados (solapamiento). Al menos una de las dos señales debe estar presente.

| Argumento/Opción | Tipo | Default | Descripción |
|---|---|---|---|
| `query` | string | — | Texto libre a buscar en el historial de reviews |
| `--repo` | string? | — | Filtra por repositorio `owner/name` |
| `--files` | string? (csv) | — | Rutas separadas por coma para búsqueda por solapamiento |
| `--limit` | int | `10` | Máximo de resultados |
| `--format` | `console` \| `json` | `console` | Formato del reporte |
| `--connection-string` | string? | ver [Configuración](#configuración) | Conexión Postgres |

### context

```
reviewmemory context <repo> [options]
```

Recupera el contexto histórico relevante para un PR concreto, **excluyendo las discusiones del propio PR**. El PR debe estar indexado previamente.

| Argumento/Opción | Tipo | Default | Descripción |
|---|---|---|---|
| `repo` | `owner/name` | — | Repositorio del PR; otro formato es error de uso (exit 2) |
| `--pr` | int | — (**requerido**) | Número del PR a contextualizar |
| `--limit` | int | `10` | Máximo de resultados |
| `--format` | `console` \| `json` | `console` | Formato del reporte |
| `--connection-string` | string? | ver [Configuración](#configuración) | Conexión Postgres |

Nota: `--limit` comparte definición entre `search` y `context`; su default es 10 en ambos. Los ejemplos del README pasan `--limit 5` explícito.

## Configuración

Ambas resoluciones ocurren al arrancar cada comando; las migraciones de esquema (`schema_migrations`) se aplican antes de cualquier lectura o escritura.

**Connection string**, en este orden de precedencia:

1. Flag `--connection-string`
2. Variable de entorno `REVIEWMEMORY_CONNECTIONSTRING`
3. Default: `Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory` — la BD local de `docker-compose.yml` (Postgres 17 publicado en `localhost:5433` para no chocar con Postgres locales en 5432)

**Token de GitHub** (solo lo usa `index`):

1. Flag `--token`
2. Variable de entorno `GITHUB_TOKEN`

Sin token, Octokit opera anónimo: 60 req/h por IP. Autenticado: 5.000 req/h. Cada PR indexado cuesta ~3 requests (listado paginado + archivos + comentarios de review); indexar 200 PRs son ~600 requests.

## Exit codes

| Código | Condición |
|---|---|
| `0` | Éxito, incluida una búsqueda sin resultados (`0 discusiones relevantes encontradas`) |
| `1` | Error de parsing de System.CommandLine (falta comando, falta `--pr` requerido, opción desconocida): imprime mensaje + ayuda. Error en tiempo de ejecución capturado (`Octokit.ApiException`, `HttpRequestException`, `NpgsqlException`): imprime `error: <mensaje>` en stderr |
| `2` | Uso inválido detectado por el programa, con mensaje en stderr (ver tabla siguiente) |

Validaciones que producen exit 2, con su mensaje literal:

| Comando | Condición | Mensaje (stderr) |
|---|---|---|
| `index`, `context` | `repo` no es `owner/name` (dos segmentos no vacíos) | `error: el repositorio debe tener el formato owner/name` |
| `search`, `context` | `--format` distinto de `console`/`json` | `error: --format desconocido '<valor>' (console|json)` |
| `search` | `query` vacío Y sin `--files` útil | `error: la búsqueda requiere texto o --files` |
| `context` | El PR no está en la memoria | `error: el PR <owner>/<name>#<n> no está indexado; ejecuta 'reviewmemory index' primero` |

Orden de validación en `search`: primero `--format`, luego texto/archivos. En `context`: primero `--format`, luego formato de `repo`, luego la consulta a BD. Un fallo de conexión a Postgres (puerto caído) es exit 1: p. ej. `error: Failed to connect to 127.0.0.1:5999`.

## Salida de index

Una línea por PR a medida que se procesa (visibilidad durante corridas largas), seguida de un resumen final:

```
#11 feat(search): content search on webhook body → 1 discusión(es)
Indexados 1 PRs · 1 discusiones · 0 decisiones con desenlace
```

El resumen cuenta PRs procesados, hilos totales e hilos con decisión inferida distinta de `unknown`. Todo va a stdout; los errores van a stderr.

## Salida console (search / context)

`context` antepone un header con el estado del PR (solo en formato console; `json` no lo emite) seguido de una línea en blanco:

```
MERGED PR #1 «feat(ui): Next.js UI — endpoints, webhook feed, detail viewer, replay» (Steve-XYZ)
```

Después, ambos comandos renderizan igual (`SearchRenderer`):

```
N discusión(es) históricamente relevante(s)

1. HIGH — Provider callbacks could be processed twice.
   Similitud: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120

   Preocupación previa del reviewer:
   <finding en una línea, truncado a 240 caracteres>

   Resolución: Finding aceptado — se corrigió la implementación. <razón>
   https://github.com/<owner>/<name>/pull/<n>#discussion_r<id>
```

Sin resultados: `0 discusiones relevantes encontradas`.

Detalles:

- La banda sale del score combinado (texto 0.55 + solapamiento 0.30 + recencia 0.15): `HIGH` ≥ 0.65, `MEDIUM` ≥ 0.40, si no `LOW`.
- `Similitud` es el score con dos decimales.
- La primera línea del finding de cada hit trunca a 72 caracteres con elipsis `…`; el título del PR no aparece en la salida console (solo en JSON como `prTitle`).
- `File:` omite `:<línea>` cuando el hilo no tiene posición.
- `Resolución:` solo aparece si el desenlace no es `unknown` o hay razón. Etiquetas: `Finding aceptado — se corrigió la implementación` / `Finding rechazado` / `Finding parcialmente aceptado` / `Desenlace desconocido`.

## Salida json

Serialización de la lista de hits con `System.Text.Json`: indentada, propiedades en camelCase, enums como strings camelCase, campos nulos omitidos (`reason` y `line` desaparecen cuando son null). Campos por hit:

| Campo | Tipo | Descripción |
|---|---|---|
| `threadId` | long | Id del comentario raíz del hilo |
| `repo` | string | `owner/name` |
| `number` | int | Número del PR |
| `prTitle` | string | Título del PR |
| `path` | string | Archivo del finding |
| `line` | int? | Posición en el diff; ausente si es null |
| `finding` | string | Cuerpo completo del comentario del reviewer |
| `outcome` | string | `accepted` \| `rejected` \| `partiallyAccepted` \| `unknown` |
| `reason` | string? | Razón inferida; ausente si es null |
| `score` | double | Score combinado sin redondear |
| `createdAt` | datetime | ISO 8601 con offset (`2026-08-22T02:37:24+00:00`) |
| `url` | string | `https://github.com/{repo}/pull/{number}#discussion_r{threadId}` |

```json
[
  {
    "threadId": 3834863303,
    "repo": "Steve-XYZ/webhook-replay",
    "number": 1,
    "prTitle": "feat(ui): Next.js UI — endpoints, webhook feed, detail viewer, replay",
    "path": "ui/components/WebhookFeed.tsx",
    "line": 57,
    "finding": "_🎯 Functional Correctness_ | …",
    "outcome": "unknown",
    "score": 0.3497674137550216,
    "createdAt": "2026-08-22T02:37:24+00:00",
    "url": "https://github.com/Steve-XYZ/webhook-replay/pull/1#discussion_r3834863303"
  }
]
```

Este schema es el contrato para agentes y para el futuro MCP server: los cambios rompientes requieren actualizar esta spec en el mismo PR.

## Requisitos previos

```bash
docker compose up -d db   # postgres:17 en localhost:5433, container review-memory-db
```

El compose define healthcheck `pg_isready -U reviewmemory -d reviewmemory` (cada 5 s, 10 reintentos). Credenciales por defecto: BD `reviewmemory`, usuario/password `reviewmemory`. Sin la BD levantada, los tres comandos fallan con exit 1 y `error: <mensaje de Npgsql>` en stderr.

`GITHUB_TOKEN` solo es necesario para `index`. `search` y `context` funcionan sin token ni red.

## Ejemplos

Verificados contra el binario (`dotnet run --project src/ReviewMemory.Cli -- …`):

```bash
# ayuda global y por comando (-?, -h, --help y --version los aporta System.CommandLine)
dotnet run --project src/ReviewMemory.Cli -- --help
dotnet run --project src/ReviewMemory.Cli -- context --help

# previo: levantar la BD local
docker compose up -d db

# indexar los últimos 200 PRs (default)
export GITHUB_TOKEN=ghp_xxx   # recomendado: sin token, 60 req/h
dotnet run --project src/ReviewMemory.Cli -- index Shirka-Corporation/player-manager

# indexar solo los últimos 50, con token explícito
dotnet run --project src/ReviewMemory.Cli -- index Steve-XYZ/webhook-replay --last=50 --token ghp_xxx

# buscar por texto libre, acotado a un repo
dotnet run --project src/ReviewMemory.Cli -- search "duplicate lotto transaction" --repo Shirka-Corporation/player-manager

# buscar por archivos tocados (csv para varias rutas)
dotnet run --project src/ReviewMemory.Cli -- search "retry idempotency" --files src/LottoPendingTransactionProcessor.cs

# salida serializada para agentes
dotnet run --project src/ReviewMemory.Cli -- search "signature digest" --format json

# contexto histórico de un PR, top 3, excluyendo sus propias discusiones
dotnet run --project src/ReviewMemory.Cli -- context Steve-XYZ/webhook-replay --pr 1 --limit=3

# apuntar a otra BD sin tocar el entorno
dotnet run --project src/ReviewMemory.Cli -- search "x" \
  --connection-string "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory"
```

Rutas de error verificadas (todas exit 2 salvo la última, exit 1):

```bash
dotnet run --project src/ReviewMemory.Cli -- index owner           # error: el repositorio debe tener el formato owner/name
dotnet run --project src/ReviewMemory.Cli -- search ""             # error: la búsqueda requiere texto o --files
dotnet run --project src/ReviewMemory.Cli -- search x --format xml # error: --format desconocido 'xml' (console|json)
dotnet run --project src/ReviewMemory.Cli -- context a/b --pr 999  # error: el PR a/b#999 no está indexado; ejecuta 'reviewmemory index' primero
dotnet run --project src/ReviewMemory.Cli -- context a/b           # Option '--pr' is required. + ayuda (exit 1)
```

## Criterios de aceptación (siguiente iteración CLI)

1. **Spec como contrato**: toda opción, default y mensaje de esta spec coincide con el binario; un smoke test que ejecute `--help` de cada comando y las cinco rutas de error anteriores debe seguir pasando tras cualquier cambio en `Program.cs`. Cambiar un mensaje obliga a actualizar esta spec en el mismo PR. Cubierto por `tests/ReviewMemory.Cli.Tests`, que ejecuta el binario real como subproceso.
2. **Exit codes estables**: 0 éxito, 1 parsing/tiempo de ejecución, 2 uso inválido. CI verifica los tres vía `dotnet test` (`tests/ReviewMemory.Cli.Tests`).
3. **JSON estable**: mismos campos camelCase, enums como strings camelCase, nulos omitidos. Es el schema que consumirán agentes y MCP server; cambios rompientes requieren nota explícita.
4. **`context` preserva su semántica**: excluye las discusiones del propio PR, exige `--pr`, y el header solo aparece en formato console.
5. **Degradación sin token**: `index` funciona anónimo (60 req/h) y agota rate limit como exit 1 con `error: <mensaje>` en stderr, nunca un crash sin capturar.
6. **Ejemplos copiables**: los bloques de ejemplos de esta spec y del README siguen ejecutándose tal cual en un checkout limpio con `docker compose up -d db`.

## Decisiones

- **Los defaults son los de `Program.cs`, no los de los ejemplos**: `--limit` vale 10 también en `context`; el README usa `--limit 5` explícito en su ejemplo, no como default.
- **Errores de parsing (System.CommandLine) son exit 1, no 2**: el exit 2 queda reservado a las validaciones de dominio con mensajes propios; el texto bruto de la librería no se traduce.
- **Salida de `index` sin `--format`**: el progreso línea a línea ya es consumible; un modo json de index se decide cuando exista un consumidor real.
