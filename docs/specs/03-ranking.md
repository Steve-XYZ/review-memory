# Retrieval and ranking

How the memory turns a query into historically relevant discussions, ordered by relevance. Everything documented here already exists in stage 1: every weight, formula, and field comes from the code (`src/ReviewMemory.Storage/SearchRepository.cs`, `src/ReviewMemory.Core/Ranking/Scoring.cs`, `src/ReviewMemory.Core/Reporting/SearchRenderer.cs`, `src/ReviewMemory.Storage/Migrations/001_init.sql`).

## Pipeline

```
query (text and/or paths)
  → single SQL statement on Postgres: filters, scores, orders, cuts (LIMIT)
  → list of SearchHit (score already computed in the DB)
  → render console | json
```

Every command applies migrations before querying (`DbMigrations.ApplyAsync`), so the first invocation creates the schema.

Lexical basis: `review_threads.search_vec` is a generated, stored `tsvector`:

```sql
to_tsvector('english', finding || ' ' || replace(path, '/', ' '))
```

Both the reviewer's comment and the file path participate. On that column there is a GIN index (`idx_review_threads_fts`) and a btree index over `pr_files.path`.

## Ranking signals

Fixed weights in code (`RankingWeights`):

| Signal | Weight | Component source |
|---|---|---|
| TextMatch | **0.55** | `ts_rank` × scale 6, capped at 1.0 |
| FileOverlap | **0.30** | queried paths present in the candidate PR / total queried paths |
| Recency | **0.15** | exponential decay with a 120-day constant |

Full formula as the SQL evaluates it:

```
score =
    0.55 * CASE WHEN has_text
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

- Query with `websearch_to_tsquery('english', @text)`: accepts quoted phrases, `OR`, and `-exclusions`; malformed input does not throw, it gets sanitized.
- Raw `ts_rank` returns small values (typically hundredths). The ×6 scale brings them into a useful range and `LEAST(1.0, …)` guarantees the textual contribution never exceeds its maximum weight of 0.55.
- With no text in the query, the signal contributes 0 (no error).

Example: a `ts_rank` of 0.20 → `min(1.0, 0.20 × 6) = 1.00` → contributes `0.55`. With a `ts_rank` of 0.05 → capped at `0.30` → contributes `0.165`.

### FileOverlap

Proportion of the queried paths that the candidate PR touched:

- Numerator: queried paths (`unnest(@paths)`) present in the candidate's `pr_files`. The `UNIQUE (pr_repo, pr_number, path)` constraint makes each path count at most once, so the quotient is bounded in [0, 1] without an explicit cap.
- Denominator: total of queried paths (`cardinality(@paths)`), with `GREATEST(…, 1)` to avoid division by zero when the list is empty.

In `search` the paths come from `--files` (comma-separated). In `context` they are all paths of the contextualized PR.

### Recency

```
exp(-age_in_days / 120)
```

with `age_in_days = max(seconds_since_created_at / 86400, 0)` (the `GREATEST` protects against future timestamps).

| Thread age | Factor |
|---|---|
| 0 days | 1.000 |
| 83 days | ≈ 0.500 |
| 120 days | ≈ 0.368 |
| 240 days | ≈ 0.135 |

Note: the constant is named `RecencyHalfLifeDays` and equals 120.0, but mathematically it is the exponent's divisor (time constant), not the half-life: the score actually halves at `120·ln 2 ≈ 83` days. The name is historical; treat 120 as the time constant.

### Full example

Query `"duplicate lotto transaction"` with paths `{A, B}`; thread created 60 days ago in a PR that touched only A:

| Component | Calculation | Contribution |
|---|---|---|
| TextMatch | `min(1.0, 0.20 × 6) = 1.00` | `0.55 × 1.00 = 0.550` |
| FileOverlap | `1/2 = 0.50` | `0.30 × 0.50 = 0.150` |
| Recency | `e^(-60/120) ≈ 0.607` | `0.15 × 0.607 ≈ 0.091` |
| **Total** | | **≈ 0.79 → HIGH** |

Scoring happens entirely in SQL. `Scoring.Combine` exists in C# as a mirror of the formula (extra layer with `Math.Min` over overlap and a final clamp to [0, 1]); today only the unit tests exercise it, not search.

## Filtering semantics: `search` vs `context`

Both commands share a single SQL query; what changes is which parameters activate filtering and exclusion.

| Aspect | `search` | `context` |
|---|---|---|
| Minimum requirement | text or `--files` (otherwise: exit 2) | indexed PR (otherwise: exit 2) |
| Repository filter | optional via `--repo owner/name` | implicit: only threads from the same repository |
| Text | **filters** (`search_vec @@ websearch_to_tsquery`) **and** scores | **only scores**, never filters (`text_is_filter = false`) |
| Paths | if provided, filter by existence: the PR must have ≥ 1 of them | always active: candidates = threads of OTHER PRs sharing ≥ 1 path with the PR's paths |
| Default text | the positional argument | `"{title}\n{body truncated to 400 characters}"` of the PR |
| Excluded threads | none | those of `repo#number` itself |
| Default limit | 10 (`--limit`) | 5 at the API level (`options?.Limit ?? 5`); the CLI always passes `--limit`, whose default is also 10 |

The effective `LIMIT` is clamped to [1, 100] (`Math.Clamp`). When both filters are present in `search` they combine conjunctively: the thread must match the text AND touch some queried path.

Edge case of `context`: if the contextualized PR has 0 indexed paths, the path condition disappears (`@paths_empty = true`) and candidates become all threads of other PRs in the same repository, ranked by text and recency only.

Outcomes arrive via `LEFT JOIN decisions`: a thread without a decision is returned with `outcome: "unknown"` and `reason: null` (never discarded for lack of a decision).

## Similarity bands

Over the final score ([0, 1] by construction: weights sum to 1.0):

| Band | Threshold | Console label |
|---|---|---|
| High | score ≥ **0.65** | `HIGH` |
| Medium | score ≥ **0.40** | `MEDIUM` |
| Low | rest | `LOW` |

Boundaries verified by tests (`ScoringTests`): 0.65 → High, 0.64 → Medium, 0.40 → Medium, 0.39 → Low. A hit with perfect text alone (1.0) reaches 0.55 and lands in Medium: no hit reaches HIGH without file matches or recent recency.

The band is computed client-side and **only appears in console output**; the JSON carries the raw score.

## Output formats

### Console (`--format console`, default)

With results:

```
{N} historically relevant discussion(s)

{i}. {BAND} — {first line of the finding, truncated to 72}
   Similarity: {score with 2 decimals}
   PR #{number} ({repo}) · {created_at:yyyy-MM-dd}
   File: {path}[:{line}]

   Previous reviewer concern:
   {finding flattened to one line, truncated to 240}

   Resolution: {outcome[. reason]}
   {url}
```

Real example (test fixture):

```
1. HIGH — Provider callbacks could be processed twice.
   Similarity: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120

   Previous reviewer concern:
   Provider callbacks could be processed twice.

   Resolution: Finding accepted — implementation was fixed. response from dev: "fixed with idempotency check"
   https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r991
```

Renderer rules:

- No results: `0 relevant discussions found`.
- Truncated values end in `…` (the maximum length includes the character).
- The `Resolution:` block appears when there is a known outcome or reason; labels: `Finding accepted — implementation was fixed` / `Finding rejected` / `Finding partially accepted` / `Unknown outcome`, followed by `. {reason}` when present.
- The URL always closes each block.

### JSON (`--format json`)

Array of `SearchHit` records serialized with 2-space indentation, `camelCase`, and enums as `camelCase` strings; null properties are omitted (`line` and `reason` disappear when null).

| Field | Type | Contents |
|---|---|---|
| `threadId` | number | id of the thread's root comment on GitHub |
| `repo` | string | `owner/name` |
| `number` | number | PR number |
| `prTitle` | string | PR title |
| `path` | string | discussed file |
| `line` | number \| absent | line, when present |
| `finding` | string | reviewer concern |
| `outcome` | string | `accepted` \| `rejected` \| `partiallyAccepted` \| `unknown` |
| `reason` | string \| absent | outcome evidence |
| `score` | number | score [0, 1] computed in SQL |
| `createdAt` | string ISO 8601 | thread date |
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
    "reason": "response from dev: \"fixed with idempotency check\"",
    "score": 0.91,
    "createdAt": "2026-03-10T00:00:00+00:00",
    "url": "https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r991"
  }
]
```

## Agent contract

Invocations:

```bash
# free search, output for programmatic consumption
reviewmemory search "duplicate lotto transaction" \
  --repo Shirka-Corporation/player-manager \
  --limit 10 --format json

# search by touched files
reviewmemory search "retry idempotency" \
  --files src/LottoPendingTransactionProcessor.cs,src/LottoGateway.cs \
  --format json

# historical context for a PR (excludes its own threads)
reviewmemory context Shirka-Corporation/player-manager --pr 2268 --limit 5 --format json
```

| Exit code | Meaning | Examples |
|---|---|---|
| **0** | success (including 0 results) | search executed |
| **1** | runtime error | unreachable Postgres (`NpgsqlException`); in `index`: HTTP or GitHub API error |
| **2** | invalid usage or entity not found | `search` without text or `--files`; `--format` other than `console\|json`; repository outside `owner/name` format; `context` on a non-indexed PR |

Errors go to stderr prefixed with `error: `; stdout only carries results. The connection resolves as follows: `--connection-string` → `REVIEWMEMORY_CONNECTIONSTRING` variable → docker-compose local database (`localhost:5433`). An agent may assume: exit 0 ⇒ parse stdout as JSON; exit ≠ 0 ⇒ read stderr.

## Current limits

- **No embeddings**: purely lexical retrieval with the `'english'` configuration; synonyms or paraphrases without shared tokens are not retrieved.
- **Linear ranking**: weighted sum with fixed weights compiled into code; no learning or per-repository tuning.
- **`cardinality(@paths)` as normalizer**: the more paths are queried, the less each individual overlap contributes; a context PR with many paths dilutes the signal toward 0.
- **Opaque score**: the output exposes only the total; neither the three components nor the band reach the JSON.
- **Duplicated formula**: it lives in SQL (authority) and in `Scoring.Combine` (C#, tests only); they can diverge without search noticing.
- **`ts_rank` without normalization** (Postgres default 0): text length influences scoring.
- **No tie-break**: `ORDER BY score DESC` without secondary criterion; tied hits come in undefined order.

## Acceptance criteria — next iteration

For the embeddings/pgvector iteration (and any retrieval change):

1. New migration `002_*` adding the embedding column and its approximate index (HNSW or IVFFlat); `DbMigrations.ApplyAsync` must apply cleanly both on an empty DB and on one populated by `001`. Verifiable in CI (tests run against real Postgres).
2. The semantic signal enters the formula with its own weight documented in this spec; weights still sum ≤ 1.0 and the score stays in [0, 1], covered by `Scoring` tests.
3. Integration test proving semantic value: a query without lexically shared tokens with the corpus retrieves relevant threads (e.g. searching "duplicated transactions" finds a finding saying "double processing").
4. The JSON exposes the component breakdown (text / files / recency / semantics) and the band; a renderer test asserts presence and types of those fields.
5. Stable contract: exit codes 0/1/2 and existing fields of the JSON table keep name and type; only additions allowed.
6. Any change to weights, band thresholds, or the formula requires updating this spec in the same PR.
