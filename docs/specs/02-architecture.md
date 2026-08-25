# Architecture

## Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10 (`net10.0`) |
| CLI | System.CommandLine `3.0.0-preview.7.26381.103` |
| GitHub client | Octokit `14.0.0` (REST API v3) · own GraphQL v4 client (`HttpClient` + System.Text.Json, no new dependencies) |
| Persistence | Npgsql `10.0.3` on PostgreSQL 17 (native full-text search) |
| MCP server | official MCP C# SDK (`ModelContextProtocol` `2.2.0`), stdio transport |
| Tests | xUnit `2.9.3` + Microsoft.NET.Test.Sdk `17.14.1`, coverlet.collector `6.0.4` |
| Frontend | none: CLI with `console` and `json` output for agents |

Local Postgres via docker-compose (`postgres:17` at `localhost:5433`, database
`reviewmemory`). CI runs the same Postgres 17 as a service container and executes
`dotnet build` + `dotnet test --no-build`.

## Solution structure

```
review-memory/
├── ReviewMemory.slnx
├── src/
│   ├── ReviewMemory.Core/            # pure library: model, diff, decisions, ranking, reporting
│   │   ├── Model.cs                  # PullRequestData, ReviewThreadData, CodeHunk, IPullRequestSource
│   │   ├── SearchQuery.cs
│   │   ├── Diff/PatchHunks.cs        # unified patch → CodeHunk[]
│   │   ├── Decisions/DecisionInferrer.cs
│   │   ├── Ranking/Scoring.cs        # ranking weights and HIGH / MEDIUM / LOW bands
│   │   └── Reporting/SearchRenderer.cs
│   ├── ReviewMemory.GitHub/          # GitHubPullRequestSource: REST → PullRequestData
│   ├── ReviewMemory.Storage/         # IndexRepository, SearchRepository, DbMigrations
│   │   └── Migrations/*.sql          # embedded resources of the Storage assembly (001_init, 002_thread_content_hash)
│   ├── ReviewMemory.Cli/             # System.CommandLine host: index · search · context
│   └── ReviewMemory.Mcp/             # MCP stdio server: search · context tools
├── tests/
│   ├── ReviewMemory.Core.Tests/
│   └── ReviewMemory.Storage.Tests/   # integration against real Postgres (skipped without a connection)
└── docs/specs/
```

Dependency direction:

```
              ┌────────────────────────────┐
              │      ReviewMemory.Cli      │
              └───┬─────────┬────────┬─────┘
                  ▼         ▼        ▼
               GitHub    Storage    Core
                  └────▶ Core ◀──────┘
```

- **Cli → { Core, GitHub, Storage }**: composes the three; parses args and maps to exit codes (0 success; 1 parsing or runtime error; 2 invalid usage detected by the program — detail in 04-cli §Exit codes).
- **Mcp → { Core, Storage }**: read-only MCP stdio server; reuses `SearchRepository` and `SearchRenderer` to return the same JSON as the CLI (03-ranking §Agent contract).
- **GitHub → Core** and **Storage → Core**: both speak the domain's language.
- **Core → nothing**: zero `PackageReference` and zero `ProjectReference`. The `IPullRequestSource` contract lives in Core, so tests can feed in-memory sources and a future GraphQL source would just add another project referencing Core.

## Domain model

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

Everything is an immutable record. `PatchHunks.Parse` derives the hunks from the unified patch
returned by the API (`Hunks` property of `PullRequestFileData`); binary files or files without a
patch arrive without hunks.

## Domain model vs tables

Full schema in `src/ReviewMemory.Storage/Migrations/001_init.sql`.

| Domain (Core) | Table | Key |
|---|---|---|
| `PullRequestData` | `pull_requests` | natural PK `(repo, number)` — mirrors how GitHub identifies a PR |
| `PullRequestFileData` | `pr_files` | surrogate `id`; FK `(pr_repo, pr_number)` ON DELETE CASCADE; `UNIQUE (pr_repo, pr_number, path)` |
| `CodeHunk` | `code_hunks` | surrogate `id`; FK `file_id` ON DELETE CASCADE |
| `ReviewThreadData` | `review_threads` | PK = GitHub root comment id (globally unique) |
| `ReviewCommentData` | `review_comments` | PK = comment id on GitHub; FK `thread_id` |
| `Decision` (inferred) | `decisions` | PK `thread_id`; FK ON DELETE CASCADE |

Columns that define behavior:

| Table | Key columns |
|---|---|
| `pull_requests` | `state` constrained to `open/closed/merged`; `indexed_at` refreshed on every upsert |
| `code_hunks` | hunk `old_start/old_lines/new_start/new_lines`; `body` keeps the lines |
| `review_threads` | `resolved boolean NOT NULL DEFAULT false` (REST does not expose it, see Limitations); `search_vec tsvector GENERATED ALWAYS AS (to_tsvector('english', finding \|\| ' ' \|\| replace(path, '/', ' '))) STORED` with GIN index |
| `decisions` | `outcome CHECK IN ('accepted','rejected','partially_accepted','unknown')`; `confidence CHECK IN ('inferred','manual')` |

Two modeling decisions:

- **Natural PKs where the domain already has identity.** `(repo, number)` for PRs; the GitHub comment id as PK for threads and comments. This gives direct traceability: every search hit reconstructs its URL `https://github.com/{repo}/pull/{n}#discussion_r{id}`.
- **The search index is a generated column, not ingestion work.** `search_vec` is computed in Postgres from `finding` + path; indexing needs to know nothing about full-text search.

## Ingestion flow

```
reviewmemory index owner/name --last N
   ↓ GitHubPullRequestSource.GetRecentPullRequestsAsync
     GET /pulls paginated (State=all, sort=updated desc, PageSize=100) up to N PRs
   ↓ per PR:
     GET /pulls/{n}/files     → PullRequestFileData[] (+ patch → hunks)
     GET /pulls/{n}/comments  → PullRequestReviewComment[]
     BuildThreads()           → in_reply_to chains → ReviewThreadData[]
     GraphQL reviewThreads    → isResolved per thread (see Thread reconstruction)
   ↓ PullRequestData                       (Core record)
    ↓ IndexRepository.UpsertAsync           ONE transaction per PR:
        UPSERT pull_requests                ON CONFLICT (repo, number)
        DELETE pr_files → INSERT pr_files + code_hunks
        thread reconciliation by id + content_hash (see below):
          new → INSERT · changed → UPDATE (+comments) · unchanged → intact
          missing from GitHub → DELETE            cascade: comments and decisions
        DecisionInferrer.Infer only on new/changed → UPSERT decisions
      COMMIT
```

The per-PR transaction is the consistency boundary: if ingesting one PR fails halfway
(network, rate limit), either the previous version or nothing remains; never a PR with
files but no threads. The output reports per PR how many discussions and decisions
with outcome (`outcome ≠ unknown`) were stored.

### Incremental re-indexing

Each thread carries a `content_hash` (SHA256 hex) of its visible content:
path, line, `resolved` state, finding author and body, and replies ordered by date
(tie-break: id) with their author and body — no ids or timestamps. The exact format is
documented in the code (`IndexRepository.ContentHash`). When indexing a PR:

- new threads are inserted;
- threads whose hash changed are updated and their comments rewritten
  (delete by `thread_id` + insert);
- unchanged threads are not touched: their rows —and their decisions— stay
  intact;
- threads that no longer exist on GitHub are deleted (the cascade removes their
  comments and decisions).

Decision re-inference runs only for new or changed threads, and the indexer skips it if
the thread already has a `confidence = 'manual'` decision: a human correction survives
any re-indexing. `pr_files` stays replace-all because it is entirely derivable from
the API.

Retrieval (`search`, `context`) uses `SearchRepository`: one SQL query that scores each
thread with three signals — FTS over `search_vec` (`websearch_to_tsquery`), path overlap
against `pr_files`, and exponential recency with a 120-day time constant (factor half-life ≈83 days; see
03-ranking §Recency), with weights 0.55 / 0.30 / 0.15 defined in
Core. `context` excludes the PR's own discussions. Signals are detailed in
`03-ranking.md`.

## Thread reconstruction

The REST API returns review comments flat; grouping them into discussions is done by
`BuildThreads`:

1. Index all comments of the PR by `Id`.
2. For each comment, walk up the `InReplyToId` chain until reaching one without a parent (with cycle guard).
3. Group by root comment, sort by `CreatedAt`: the first is the `Finding`, the rest are `Replies`.
4. The thread id is the root comment id; `Path` and `Line` come from the finding (`Position ?? OriginalPosition`).

A comment whose root is missing from the response (broken chain) is discarded instead
of inventing a new thread.

### Resolved state via GraphQL

The `isResolved` flag only exists in GraphQL v4. After loading a PR via REST, the source
queries `pullRequest.reviewThreads(first: 100)` with `pageInfo` pagination and overwrites
`Resolved` before persisting:

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

- **Id cross-referencing.** The GraphQL node carries an opaque id (`PRRT_…`) that does not match
  the numeric id ReviewMemory uses as thread identity
  (the REST v3 id of the root comment). So each thread requests its first
  comment —replies arrive in chronological order and the first is the one that
  opened the thread— and cross-references by its `databaseId`, the same numeric id
  REST v3 uses for that comment. Threads without a match keep `false`.
- **Token required.** The GraphQL client only works with a token; without one it
  is not queried.
- **Degradation.** On missing token, HTTP failure, or rate limit, ingestion
  continues with `Resolved = false` and exit 0. The responsibility lives in
  `GitHubPullRequestSource` —it is the component able to keep the PR stream alive—,
  which reports the reason once per run (after the first failure it does not retry); the CLI presents it as
  `warning: could not fetch resolved state via GraphQL: <reason>` on stderr.
  The client (`GitHubGraphQLClient`) throws typed exceptions; it never swallows
  failures on its own.
- **Inference unchanged.** `resolved` is persisted but still takes no part in
  `DecisionInferrer` (see 05-decisions §Limitations).

## Embedded migrations

No external tools or loose scripts:

1. `Migrations/*.sql` travel as `EmbeddedResource` inside the Storage assembly.
2. `DbMigrations.ApplyAsync` creates `schema_migrations (name text PRIMARY KEY, applied_at timestamptz DEFAULT now())`.
3. It reads the embedded `.sql` resources, orders them by name (ordinal order: `001_init.sql`, `002_…`), and applies each in its own transaction along with the `INSERT INTO schema_migrations`.
4. It is idempotent: already recorded names are skipped. The CLI calls `ApplyAsync` when opening the database, so every command leaves the schema up to date.

Adding a schema change = creating `002_whatever.sql` under `Migrations/`. Nothing else.

## Known limitations

| Limitation | Detail |
|---|---|
| ~~`resolved` always `false`~~ resolved | Since this PR `resolved` is filled via GraphQL (`pullRequest.reviewThreads`); requires a GitHub token and degrades to `false` with a stderr warning when the query is unavailable (see Thread reconstruction §Resolved state). |
| ~~Re-index deletes and recreates the PR's threads~~ — resolved | Incremental reconciliation by id + content hash: unchanged threads are not rewritten and a `confidence = 'manual'` decision survives any re-indexing. Mechanism in "Incremental re-indexing" (Ingestion flow). |
| Orphan chains discarded | If a thread's root is absent from the REST response, the whole discussion is silently lost. |
| Sub-resources without explicit pagination | `files` and `comments` are requested without `ApiOptions`; a PR with more than one page may be incomplete. |
| Approximate line coordinates | `line` stores `Position ?? OriginalPosition` (position in the diff). For outdated comments the original position remains, which may not match the current file. |
| REST rate limit | Without token: 60 req/h; ingestion consumes ~2 extra calls per PR (`files` + `comments`). |

## Acceptance criteria for the next iteration

**GraphQL for `resolved`**

- `review_threads.resolved` is filled from GraphQL's `isResolved`
  (`pullRequest.reviewThreads`), keeping the rest of the REST ingestion.
- An integration test indexes a PR with resolved and open threads and verifies
  the column distinguishes both states.
- If GraphQL fails, ingestion degrades to the documented `false` instead of aborting.

**Incremental indexing**

- Re-indexing an unchanged PR does not rewrite `review_threads`,
  `review_comments`, or `decisions` rows (verifiable in a test).
- Rows with `decisions.confidence = 'manual'` survive any re-index of the same PR.
- Unchanged PRs consume fewer REST calls (conditional on `updated_at` or ETag).

**Ingestion completeness**

- `files` and `comments` are read with explicit pagination; a test covers the
  more-than-one-page case without losing comments.
