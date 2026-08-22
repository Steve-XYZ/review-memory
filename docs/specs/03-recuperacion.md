# Recuperación y ranking

Cómo la memoria convierte una consulta en discusiones históricas ordenadas por relevancia. Todo lo documentado aquí existe ya en etapa 1: cada peso, fórmula y campo sale del código (`src/ReviewMemory.Storage/SearchRepository.cs`, `src/ReviewMemory.Core/Ranking/Scoring.cs`, `src/ReviewMemory.Core/Reporting/SearchRenderer.cs`, `src/ReviewMemory.Storage/Migrations/001_init.sql`).

## Pipeline

```
consulta (texto y/o rutas)
  → SQL único sobre Postgres: filtra, puntúa, ordena y corta (LIMIT)
  → lista de SearchHit (score ya calculado en la BD)
  → render console | json
```

Cada comando aplica migraciones antes de consultar (`DbMigrations.ApplyAsync`), así la primera invocación crea el esquema.

Base léxica: `review_threads.search_vec` es un `tsvector` generado y almacenado:

```sql
to_tsvector('english', finding || ' ' || replace(path, '/', ' '))
```

Participan tanto el comentario del reviewer como la ruta del archivo. Sobre esa columna hay índice GIN (`idx_review_threads_fts`) e índice btree sobre `pr_files.path`.

## Señales de ranking

Pesos fijos en código (`RankingWeights`):

| Señal | Peso | Fuente del componente |
|---|---|---|
| TextMatch | **0.55** | `ts_rank` × escala 6, capado a 1.0 |
| FileOverlap | **0.30** | rutas consultadas presentes en el PR candidato / total de rutas consultadas |
| Recency | **0.15** | decaimiento exponencial con constante 120 días |

Fórmula completa tal como la evalúa el SQL:

```
score =
    0.55 * CASE WHEN hay_texto
           THEN LEAST(1.0, ts_rank(search_vec, websearch_to_tsquery('english', @text)) * 6)
           ELSE 0 END
  + 0.30 * COALESCE((
        SELECT count(*)::float8 / GREATEST(cardinality(@paths), 1)
        FROM unnest(@paths) AS q(path)
        JOIN pr_files f ON f.pr_repo = t.pr_repo AND f.pr_number = t.pr_number AND f.path = q.path
    ), 0)
  + 0.15 * exp(- GREATEST(EXTRACT(EPOCH FROM (now() - t.created_at)) / 86400.0, 0) / 120.0)
ORDER BY score DESC LIMIT @limit
```

### TextMatch

- Consulta con `websearch_to_tsquery('english', @text)`: acepta frases entrecomilladas, `OR` y `-exclusiones`; entrada malformada no lanza error, se sanea.
- El `ts_rank` crudo devuelve valores pequeños (típicamente centésimas). La escala ×6 los lleva a un rango útil y `LEAST(1.0, …)` garantiza que la contribución textual nunca supere su peso máximo de 0.55.
- Sin texto en la consulta, la señal aporta 0 (no error).

Ejemplo: `ts_rank` de 0.20 → `min(1.0, 0.20 × 6) = 1.00` → aporta `0.55`. Con `ts_rank` de 0.05 → capado en `0.30` → aporta `0.165`.

### FileOverlap

Proporción de las rutas consultadas que el PR candidato tocó:

- Numerador: rutas consultadas (`unnest(@paths)`) que aparecen en `pr_files` del candidato. La restricción `UNIQUE (pr_repo, pr_number, path)` hace que cada ruta cuente como mucho una vez, así que el cociente queda acotado en [0, 1] sin cap explícito.
- Denominador: total de rutas consultadas (`cardinality(@paths)`), con `GREATEST(…, 1)` para evitar división por cero cuando la lista viene vacía.

En `search` las rutas vienen de `--files` (separadas por coma). En `context` son todas las rutas del PR contextualizado.

### Recency

```
exp(-edad_en_días / 120)
```

con `edad_en_días = max(segundos_desde_created_at / 86400, 0)` (el `GREATEST` protege contra timestamps futuros).

| Edad del hilo | Factor |
|---|---|
| 0 días | 1.000 |
| 83 días | ≈ 0.500 |
| 120 días | ≈ 0.368 |
| 240 días | ≈ 0.135 |

Nota: la constante se llama `RecencyHalfLifeDays` y vale 120.0, pero matemáticamente es el divisor del exponente (constante temporal), no la vida media: el score realmente se reduce a la mitad a los `120·ln 2 ≈ 83` días. El README dice "vida media de 120 días"; el código hace lo de arriba.

### Ejemplo completo

Consulta `"duplicate lotto transaction"` con rutas `{A, B}`; hilo creado hace 60 días en un PR que tocó solo A:

| Componente | Cálculo | Aporte |
|---|---|---|
| TextMatch | `min(1.0, 0.20 × 6) = 1.00` | `0.55 × 1.00 = 0.550` |
| FileOverlap | `1/2 = 0.50` | `0.30 × 0.50 = 0.150` |
| Recency | `e^(-60/120) ≈ 0.607` | `0.15 × 0.607 ≈ 0.091` |
| **Total** | | **≈ 0.79 → HIGH** |

La puntuación ocurre íntegramente en SQL. Existe `Scoring.Combine` en C# como espejo de la fórmula (capa adicional con `Math.Min` sobre solapamiento y clamp final a [0, 1]); hoy solo lo ejercitan los tests unitarios, no la búsqueda.

## Semántica de filtrado: `search` vs `context`

Ambos comandos comparten un único SQL; cambia qué parámetros activan filtro y exclusión.

| Aspecto | `search` | `context` |
|---|---|---|
| Requisito mínimo | texto o `--files` (si no: exit 2) | PR indexado (si no: exit 2) |
| Filtro de repositorio | opcional vía `--repo owner/name` | implícito: solo hilos del mismo repositorio |
| Texto | **filtra** (`search_vec @@ websearch_to_tsquery`) **y** puntúa | **solo puntúa**, jamás filtra (`text_is_filter = false`) |
| Rutas | si se pasan, filtran por existencia: el PR debe tener ≥ 1 de ellas | siempre activas: candidatos = hilos de OTROS PRs que comparten ≥ 1 ruta con las rutas del PR |
| Texto por defecto | el argumento posicional | `"{title}\n{cuerpo truncado a 400 caracteres}"` del PR |
| Hilos excluidos | ninguno | los del propio `repo#number` |
| Límite por defecto | 10 (`--limit`) | 5 en la API (`options?.Limit ?? 5`); el CLI siempre pasa `--limit`, cuyo default también es 10 |

El `LIMIT` efectivo se recorta al rango [1, 100] (`Math.Clamp`). Con ambos filtros presentes en `search` se aplican en conjunción: el hilo debe matchear el texto Y tocar alguna ruta consultada.

Caso borde de `context`: si el PR contextualizado tiene 0 rutas indexadas, la condición de rutas desaparece (`@paths_empty = true`) y los candidatos pasan a ser todos los hilos de otros PRs del mismo repositorio, rankeados solo por texto y recencia.

Los desenlaces llegan por `LEFT JOIN decisions`: un hilo sin decisión se devuelve con `outcome: "unknown"` y `reason: null` (nunca se descarta por falta de decisión).

## Bandas de similitud

Sobre el score final ([0, 1] por construcción: la suma de pesos es 1.0):

| Banda | Umbral | Etiqueta en consola |
|---|---|---|
| High | score ≥ **0.65** | `HIGH` |
| Medium | score ≥ **0.40** | `MEDIUM` |
| Low | resto | `LOW` |

Límites verificados por tests (`ScoringTests`): 0.65 → High, 0.64 → Medium, 0.40 → Medium, 0.39 → Low. Un hit con solo texto perfecto (1.0) alcanza 0.55 y queda en Medium: ningún hit llega a HIGH sin coincidencia de archivos o sin recencia reciente.

La banda se calcula en el cliente y **solo aparece en la salida console**; el JSON transporta el score crudo.

## Formatos de salida

### Console (`--format console`, default)

Con resultados:

```
{N} discusión(es) históricamente relevante(s)

{i}. {BANDA} — {primera línea del finding, truncada a 72}
   Similitud: {score con 2 decimales}
   PR #{number} ({repo}) · {created_at:yyyy-MM-dd}
   File: {path}[:{line}]

   Preocupación previa del reviewer:
   {finding aplanado a una línea, truncado a 240}

   Resolución: {desenlace[. reason]}
   {url}
```

Ejemplo real (fixture de tests):

```
1. HIGH — Provider callbacks could be processed twice.
   Similitud: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120

   Preocupación previa del reviewer:
   Provider callbacks could be processed twice.

   Resolución: Finding aceptado — se corrigió la implementación. respuesta de dev: "fixed with idempotency check"
   https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r991
```

Reglas del renderer:

- Sin resultados: `0 discusiones relevantes encontradas`.
- Los truncados terminan en `…` (la longitud máxima incluye el carácter).
- El bloque `Resolución:` aparece si hay desenlace conocido o razón; etiquetas: `Finding aceptado — se corrigió la implementación` / `Finding rechazado` / `Finding parcialmente aceptado` / `Desenlace desconocido`, seguidas de `. {reason}` si existe.
- La URL cierra siempre cada bloque.

### JSON (`--format json`)

Array de records `SearchHit` serializado con indentación de 2 espacios, `camelCase` y enums como strings `camelCase`; propiedades nulas se omiten (`line` y `reason` desaparecen cuando son null).

| Campo | Tipo | Contenido |
|---|---|---|
| `threadId` | number | id del comentario raíz del hilo en GitHub |
| `repo` | string | `owner/name` |
| `number` | number | número del PR |
| `prTitle` | string | título del PR |
| `path` | string | archivo discutido |
| `line` | number \| ausente | línea, si la hay |
| `finding` | string | preocupación del reviewer |
| `outcome` | string | `accepted` \| `rejected` \| `partiallyAccepted` \| `unknown` |
| `reason` | string \| ausente | evidencia del desenlace |
| `score` | number | score [0, 1] calculado en SQL |
| `createdAt` | string ISO 8601 | fecha del hilo |
| `url` | string | `https://github.com/{repo}/pull/{number}#discussion_r{threadId}` |

```json
[
  {
    "threadId": 991,
    "repo": "Shirka-Corporation/player-manager",
    "number": 1943,
    "prTitle": "AdJoe payout flow",
    "path": "src/AdJoePayoutHandler.cs",
    "line": 120,
    "finding": "Provider callbacks could be processed twice.",
    "outcome": "accepted",
    "reason": "respuesta de dev: \"fixed with idempotency check\"",
    "score": 0.91,
    "createdAt": "2026-03-10T00:00:00+00:00",
    "url": "https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r991"
  }
]
```

## Contrato para agentes

Invocaciones:

```bash
# búsqueda libre, salida para consumo programático
reviewmemory search "duplicate lotto transaction" \
  --repo Shirka-Corporation/player-manager \
  --limit 10 --format json

# búsqueda por archivos tocados
reviewmemory search "retry idempotency" \
  --files src/LottoPendingTransactionProcessor.cs,src/LottoGateway.cs \
  --format json

# contexto histórico de un PR (excluye sus propios hilos)
reviewmemory context Shirka-Corporation/player-manager --pr 2268 --limit 5 --format json
```

| Exit code | Significado | Ejemplos |
|---|---|---|
| **0** | éxito (incluye 0 resultados) | búsqueda ejecutada |
| **1** | error en tiempo de ejecución | Postgres inalcanzable (`NpgsqlException`); en `index`: error HTTP o de la API de GitHub |
| **2** | uso inválido o entidad no encontrada | `search` sin texto ni `--files`; `--format` distinto de `console\|json`; repositorio fuera del formato `owner/name`; `context` sobre PR no indexado |

Errores van a stderr con prefijo `error: `; stdout solo lleva resultados. La conexión se resuelve así: `--connection-string` → variable `REVIEWMEMORY_CONNECTIONSTRING` → BD local de docker-compose (`localhost:5433`). Un agente puede asumir: exit 0 ⇒ parsear stdout como JSON; exit ≠ 0 ⇒ leer stderr.

## Límites actuales

- **Sin embeddings**: recuperación puramente léxica con configuración `'english'`; sinónimos o paráfrasis sin tokens compartidos no se recuperan, y findings en español se indexan con stemming inglés.
- **Ranking lineal**: suma ponderada con pesos fijos compilados en código; sin aprendizaje ni ajuste por repositorio.
- **`cardinality(@paths)` como normalizador**: cuantas más rutas se consultan, menos aporta cada solapamiento individual; un PR de contexto con muchas rutas diluye la señal hacia 0.
- **Score opaco**: la salida solo expone el total; ni los tres componentes ni la banda llegan al JSON.
- **Fórmula duplicada**: vive en el SQL (autoridad) y en `Scoring.Combine` (C#, solo tests); pueden divergir sin que la búsqueda lo detecte.
- **`ts_rank` sin normalización** (default 0 de Postgres): la longitud del texto influye en la puntuación.
- **Sin desempate**: `ORDER BY score DESC` sin criterio secundario; hits empatados en orden indefinido.

## Criterios de aceptación — siguiente iteración

Para la iteración de embeddings/pgvector (y cualquier cambio de recuperación):

1. Nueva migración `002_*` que añada la columna de embedding y su índice aproximado (HNSW o IVFFlat); `DbMigrations.ApplyAsync` debe aplicar limpio tanto sobre BD vacía como sobre una BD con datos de `001`. Verificable en CI (tests corren contra Postgres real).
2. La señal semántica entra en la fórmula con peso propio documentado en esta spec; la suma de pesos sigue siendo ≤ 1.0 y el score queda en [0, 1], cubierto por tests de `Scoring`.
3. Test de integración que pruebe valor semántico: una consulta sin tokens léxicos compartidos con el corpus recupera hilos relevantes (p. ej. buscar «transacciones duplicadas» encuentra un finding que dice "double processing").
4. El JSON expone el desglose de componentes (texto / archivos / recencia / semántica) y la banda; un test del renderer afirma la presencia y tipos de esos campos.
5. Contrato estable: exit codes 0/1/2 y los campos existentes de la tabla JSON no cambian de nombre ni tipo; solo se permiten adiciones.
6. Cualquier cambio de pesos, umbrales de banda o fórmula exige actualizar esta spec en el mismo PR.
