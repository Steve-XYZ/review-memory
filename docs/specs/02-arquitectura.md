# Arquitectura

## Stack

| Capa | Tecnología |
|---|---|
| Runtime | .NET 10 (`net10.0`) |
| CLI | System.CommandLine `3.0.0-preview.7.26381.103` |
| Cliente GitHub | Octokit `14.0.0` (API REST v3) · cliente GraphQL v4 propio (`HttpClient` + System.Text.Json, sin dependencias nuevas) |
| Persistencia | Npgsql `10.0.3` sobre PostgreSQL 17 (full-text search nativo) |
| Servidor MCP | SDK oficial de MCP en C# (`ModelContextProtocol` `2.2.0`), transporte stdio |
| Tests | xUnit `2.9.3` + Microsoft.NET.Test.Sdk `17.14.1`, coverlet.collector `6.0.4` |
| Frontend | ninguno: CLI con salida `console` y `json` para agentes |

Postgres local vía docker-compose (`postgres:17` en `localhost:5433`, BD
`reviewmemory`). CI levanta el mismo Postgres 17 como service container y corre
`dotnet build` + `dotnet test --no-build`.

## Estructura de la solución

```
review-memory/
├── ReviewMemory.slnx
├── src/
│   ├── ReviewMemory.Core/            # librería pura: modelo, diff, decisiones, ranking, reporting
│   │   ├── Model.cs                  # PullRequestData, ReviewThreadData, CodeHunk, IPullRequestSource
│   │   ├── SearchQuery.cs
│   │   ├── Diff/PatchHunks.cs        # patch unificado → CodeHunk[]
│   │   ├── Decisions/DecisionInferrer.cs
│   │   ├── Ranking/Scoring.cs        # pesos de ranking y bandas HIGH / MEDIUM / LOW
│   │   └── Reporting/SearchRenderer.cs
│   ├── ReviewMemory.GitHub/          # GitHubPullRequestSource: REST → PullRequestData
│   ├── ReviewMemory.Storage/         # IndexRepository, SearchRepository, DbMigrations
│   │   └── Migrations/*.sql          # recursos embebidos del ensamblado Storage (001_init, 002_thread_content_hash)
│   └── ReviewMemory.Cli/             # host System.CommandLine: index · search · context
│   └── ReviewMemory.Mcp/             # servidor MCP stdio: tools search · context
├── tests/
│   ├── ReviewMemory.Core.Tests/
│   └── ReviewMemory.Storage.Tests/   # integración contra Postgres real (se omiten sin conexión)
└── docs/specs/
```

Dirección de dependencias:

```
              ┌────────────────────────────┐
              │      ReviewMemory.Cli      │
              └───┬─────────┬────────┬─────┘
                  ▼         ▼        ▼
               GitHub    Storage    Core
                  └────▶ Core ◀──────┘
```

- **Cli → { Core, GitHub, Storage }**: compone los tres; parsea args y mapea a exit codes (0 éxito; 1 error de parsing o runtime; 2 uso inválido detectado por el programa — detalle en 04-cli §Exit codes).
- **Mcp → { Core, Storage }**: servidor MCP stdio de solo lectura; reutiliza `SearchRepository` y `SearchRenderer` para devolver el mismo JSON que el CLI (03-recuperacion §Contrato para agentes).
- **GitHub → Core** y **Storage → Core**: ambos hablan el idioma del dominio.
- **Core → nada**: cero `PackageReference` y cero `ProjectReference`. El contrato `IPullRequestSource` vive en Core, así que las pruebas pueden alimentar fuentes en memoria y una futura fuente GraphQL solo añadiría un proyecto que referencia Core.

## Modelo de dominio

```csharp
public enum PrState { Open, Closed, Merged }

public sealed record PullRequestData(
    string Repo, int Number, string Title, string Body, string Author,
    PrState State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? MergedAt,
    IReadOnlyList<PullRequestFileData> Files,
    IReadOnlyList<ReviewThreadData> Threads);

public sealed record PullRequestFileData(string Path, int Additions, int Deletions, string? Patch);
public sealed record CodeHunk(int OldStart, int OldLines, int NewStart, int NewLines, string Text);

public sealed record ReviewThreadData(
    long Id, string Path, int? Line, bool Resolved,
    ReviewCommentData Finding, IReadOnlyList<ReviewCommentData> Replies);

public sealed record ReviewCommentData(long Id, string Author, string Body, DateTimeOffset CreatedAt);

public sealed record SearchQuery(string Text, string? Repo = null,
    IReadOnlyList<string>? Paths = null, int Limit = 10);
```

Todo es record inmutable. `PatchHunks.Parse` deriva los hunks del patch unificado
que trae la API (propiedad `Hunks` de `PullRequestFileData`); los archivos
binarios o sin patch llegan sin hunks.

## Modelo de dominio vs tablas

Esquema completo en `src/ReviewMemory.Storage/Migrations/001_init.sql`.

| Dominio (Core) | Tabla | Clave |
|---|---|---|
| `PullRequestData` | `pull_requests` | PK natural `(repo, number)` — espejo de cómo GitHub identifica un PR |
| `PullRequestFileData` | `pr_files` | surrogate `id`; FK `(pr_repo, pr_number)` ON DELETE CASCADE; `UNIQUE (pr_repo, pr_number, path)` |
| `CodeHunk` | `code_hunks` | surrogate `id`; FK `file_id` ON DELETE CASCADE |
| `ReviewThreadData` | `review_threads` | PK = id del comentario raíz de GitHub (globalmente único) |
| `ReviewCommentData` | `review_comments` | PK = id del comentario en GitHub; FK `thread_id` |
| `Decision` (inferida) | `decisions` | PK `thread_id`; FK ON DELETE CASCADE |

Columnas que definen el comportamiento:

| Tabla | Columnas clave |
|---|---|
| `pull_requests` | `state` restringido a `open/closed/merged`; `indexed_at` se refresca en cada upsert |
| `code_hunks` | `old_start/old_lines/new_start/new_lines` del hunk; `body` conserva las líneas |
| `review_threads` | `resolved boolean NOT NULL DEFAULT false` (REST no lo expone, ver Limitaciones); `search_vec tsvector GENERATED ALWAYS AS (to_tsvector('english', finding \|\| ' ' \|\| replace(path, '/', ' '))) STORED` con índice GIN |
| `decisions` | `outcome CHECK IN ('accepted','rejected','partially_accepted','unknown')`; `confidence CHECK IN ('inferred','manual')` |

Dos decisiones de modelado:

- **PKs naturales donde el dominio ya tiene identidad.** `(repo, number)` para PRs; el id del comentario de GitHub como PK de hilos y comentarios. Esto da trazabilidad directa: cada hit de búsqueda reconstruye su URL `https://github.com/{repo}/pull/{n}#discussion_r{id}`.
- **El índice de búsqueda es columna generada, no trabajo de ingesta.** `search_vec` se calcula en Postgres a partir de `finding` + ruta; indexar no necesita saber nada de full-text search.

## Flujo de ingesta

```
reviewmemory index owner/name --last N
   ↓ GitHubPullRequestSource.GetRecentPullRequestsAsync
     GET /pulls paginado (State=all, sort=updated desc, PageSize=100) hasta N PRs
   ↓ por cada PR:
     GET /pulls/{n}/files     → PullRequestFileData[] (+ patch → hunks)
     GET /pulls/{n}/comments  → PullRequestReviewComment[]
     BuildThreads()           → cadenas in_reply_to → ReviewThreadData[]
     GraphQL reviewThreads    → isResolved por hilo (ver Reconstrucción de hilos)
   ↓ PullRequestData                       (record de Core)
    ↓ IndexRepository.UpsertAsync           UNA transacción por PR:
        UPSERT pull_requests                ON CONFLICT (repo, number)
        DELETE pr_files → INSERT pr_files + code_hunks
        conciliación de hilos por id + content_hash (ver abajo):
          nuevos → INSERT · modificados → UPDATE (+comments) · sin cambio → intactos
          ausentes en GitHub → DELETE            cascada: comments y decisions
        DecisionInferrer.Infer solo en nuevos/modificados → UPSERT decisions
      COMMIT
```

La transacción por PR es el límite de consistencia: si la ingesta de un PR
falla a mitad (red, rate limit), queda o la versión previa o ninguna; nunca un
PR con archivos sin hilos. La salida reporta por PR cuántas discusiones y
decisiones con desenlace (`outcome ≠ unknown`) quedaron almacenadas.

### Re-indexado incremental

Cada hilo lleva un `content_hash` (SHA256 en hex) de su contenido visible:
path, línea, estado `resolved`, autor y cuerpo del finding, y respuestas
ordenadas por fecha (empate: id) con su autor y cuerpo — sin ids ni
timestamps. El formato exacto está documentado en el código
(`IndexRepository.ContentHash`). Al indexar un PR:

- los hilos nuevos se insertan;
- los hilos cuyo hash cambió se actualizan y sus comentarios se reescriben
  (delete por `thread_id` + insert);
- los hilos sin cambio no se tocan: sus filas —y sus decisiones— quedan
  intactas;
- los hilos que ya no existen en GitHub se borran (la cascada elimina sus
  comentarios y decisiones).

La re-inferencia de decisión corre solo para hilos nuevos o modificados, y el
indexador la salta si el hilo ya tiene una decisión `confidence = 'manual'`:
una corrección humana sobrevive a cualquier re-indexado. `pr_files` sigue con
replace-all porque es íntegramente derivable de la API.

La recuperación (`search`, `context`) usa `SearchRepository`: una consulta SQL
que puntúa cada hilo con tres señales — FTS sobre `search_vec`
(`websearch_to_tsquery`), solapamiento de rutas contra `pr_files`, y recencia
exponencial con constante temporal de 120 días (mitad del factor ≈83 días; ver
03-recuperacion §Recency), con pesos 0.55 / 0.30 / 0.15 definidos en
Core. `context` excluye las discusiones del propio PR. Las señales se detallan
en `03-recuperacion.md`.

## Reconstrucción de hilos

La API REST devuelve los comentarios de review planos; el agrupamiento en
discusiones lo hace `BuildThreads`:

1. Índice por `Id` de todos los comentarios del PR.
2. Para cada comentario, se sube la cadena `InReplyToId` hasta llegar a uno sin padre (con guardia de ciclos).
3. Se agrupa por comentario raíz, se ordena por `CreatedAt`: el primero es el `Finding`, el resto son `Replies`.
4. El id del hilo es el id del comentario raíz; `Path` y `Line` salen del finding (`Position ?? OriginalPosition`).

Un comentario cuya raíz no aparece en la respuesta (cadena rota) se descarta en
lugar de inventar un hilo nuevo.

### Estado resolved vía GraphQL

El flag `isResolved` solo existe en GraphQL v4. Tras cargar un PR por REST, la
fuente consulta `pullRequest.reviewThreads(first: 100)` con paginación por
`pageInfo` y sobrescribe `Resolved` antes de persistir:

```graphql
query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      reviewThreads(first: 100, after: $cursor) {
        pageInfo { hasNextPage endCursor }
        nodes { isResolved comments(first: 1) { nodes { databaseId } } }
      }
    }
  }
}
```

- **Cruce de ids.** El nodo GraphQL trae un id opaco (`PRRT_…`) que no coincide
  con el id numérico de `ReviewThreadData.Id`. Cada hilo pide su primer
  comentario —la conexión llega en orden cronológico y el primero es el que
  abrió el hilo— y se cruza por su `databaseId`, el mismo id numérico que REST
  v3 usa para ese comentario. Hilos sin cruce conservan `false`.
- **Token requerido.** El cliente GraphQL solo funciona con token; sin él no
  se consulta.
- **Degradación.** Ante token ausente, fallo HTTP o rate limit, la ingesta
  continúa con `Resolved = false` y exit 0. La responsabilidad vive en
  `GitHubPullRequestSource` —es quien puede mantener vivo el stream de PRs—,
  que reporta el motivo una sola vez por corrida (tras el primer fallo no se
  reintenta); el CLI lo presenta como
  `aviso: no se pudo obtener resolved vía GraphQL: <motivo>` en stderr.
  El cliente (`GitHubGraphQLClient`) lanza excepciones tipadas; nunca traga
  fallos por su cuenta.
- **La inferencia no cambia.** `resolved` se persiste pero sigue sin
  participar en `DecisionInferrer` (ver 05-decisiones §Limitaciones).

## Migraciones embebidas

Sin herramientas externas ni scripts sueltos:

1. `Migrations/*.sql` viajan como `EmbeddedResource` dentro del ensamblado Storage.
2. `DbMigrations.ApplyAsync` crea `schema_migrations (name text PRIMARY KEY, applied_at timestamptz DEFAULT now())`.
3. Lee los recursos `.sql` embebidos, los ordena por nombre (orden ordinal: `001_init.sql`, `002_…`), y aplica cada uno en su propia transacción junto al `INSERT INTO schema_migrations`.
4. Es idempotente: los nombres ya registrados se saltan. El CLI llama `ApplyAsync` al abrir la BD, así que cualquier comando deja el esquema al día.

Añadir un cambio de esquema = crear `002_lo_que_sea.sql` en `Migrations/`. Nada más.

## Limitaciones conocidas

| Limitación | Detalle |
|---|---|
| ~~`resolved` siempre `false`~~ resuelta | Desde este PR `resolved` se llena vía GraphQL (`pullRequest.reviewThreads`); requiere token de GitHub y degrada a `false` con un aviso en stderr si la consulta no está disponible (ver Reconstrucción de hilos §Estado resolved). |
| ~~Re-index borra y recrea hilos del PR~~ — resuelto | Conciliación incremental por id + hash de contenido: los hilos sin cambio no se reescriben y una decisión `confidence = 'manual'` sobrevive a cualquier re-indexado. Mecanismo en «Re-indexado incremental» (Flujo de ingesta). |
| Cadenas huérfanas descartadas | Si la raíz de un hilo no viene en la respuesta REST, toda la discusión se pierde silenciosamente. |
| Sub-recursos sin paginación explícita | `files` y `comments` se piden sin `ApiOptions`; un PR con más de una página puede quedar incompleto. |
| Coordenadas de línea aproximadas | `line` guarda `Position ?? OriginalPosition` (posición en el diff). En comentarios obsoletos queda la posición original, que puede no coincidir con el archivo actual. |
| Rate limit REST | Sin token: 60 req/h; la ingesta consume ~2 llamadas extra por PR (`files` + `comments`). |

## Criterios de aceptación de la siguiente iteración

**GraphQL para `resolved`**

- `review_threads.resolved` se llena desde `isResolved` de GraphQL
  (`pullRequest.reviewThreads`), manteniendo el resto de la ingesta REST.
- Un test de integración indexa un PR con hilos resueltos y abiertos y verifica
  que la columna distingue ambos estados.
- Si GraphQL falla, la ingesta degrada a `false` documentado en vez de abortar.

**Indexado incremental**

- Re-indexar un PR sin cambios en GitHub no reescribe filas de
  `review_threads`, `review_comments` ni `decisions` (verificable en test).
- Filas con `decisions.confidence = 'manual'` sobreviven cualquier re-index del mismo PR.
- Los PRs sin cambios consumen menos llamadas REST (condicional por `updated_at` o ETag).

**Completitud de ingesta**

- `files` y `comments` se leen con paginación explícita; un test cubre el caso
  de más de una página sin pérdida de comentarios.
