# CLI

The CLI surface lives in `src/ReviewMemory.Cli/Program.cs` on System.CommandLine. The binary is `ReviewMemory.Cli`; in development it is invoked with `dotnet run --project src/ReviewMemory.Cli -- <command>`. The `search` and `context` commands only talk to PostgreSQL; only `index` talks to the GitHub API.

## Commands

### index

```
reviewmemory index <repo> [options]
```

Downloads and indexes a repository's recent PRs. Pages the REST API (state `all`, sorted by `updated` descending, pages of 100) until covering `--last` PRs or exhausting history; per PR it downloads files and review comments, groups comments into threads by their `in_reply_to` chain, infers each thread's decision, and upserts. Re-indexing reconciles the PR's threads instead of recreating them (see 02-architecture §Incremental re-indexing).

| Argument/Option | Type | Default | Description |
|---|---|---|---|
| `repo` | `owner/name` | — | Repository to index; another format is a usage error (exit 2) |
| `--last` | int | `200` | Number of most recently updated PRs to index |
| `--token` | string? | `$GITHUB_TOKEN` | GitHub token; empty or absent ⇒ anonymous client (60 req/h) |
| `--connection-string` | string? | see [Configuration](#configuration) | Postgres connection |

### search

```
reviewmemory search <query> [options]
```

Searches historical review discussions by text (full-text search) and/or by touched files (overlap). At least one of the two signals must be present.

| Argument/Option | Type | Default | Description |
|---|---|---|---|
| `query` | string | — | Free text to search in the review history |
| `--repo` | string? | — | Filters by `owner/name` repository |
| `--files` | string? (csv) | — | Comma-separated paths for overlap-based search |
| `--limit` | int | `10` | Maximum number of results |
| `--format` | `console` \| `json` | `console` | Report format |
| `--connection-string` | string? | see [Configuration](#configuration) | Postgres connection |

### context

```
reviewmemory context <repo> [options]
```

Retrieves relevant historical context for a specific PR, **excluding the PR's own discussions**. The PR must be indexed beforehand.

| Argument/Option | Type | Default | Description |
|---|---|---|---|
| `repo` | `owner/name` | — | Repository of the PR; another format is a usage error (exit 2) |
| `--pr` | int | — (**required**) | Number of the PR to contextualize |
| `--limit` | int | `10` | Maximum number of results |
| `--format` | `console` \| `json` | `console` | Report format |
| `--connection-string` | string? | see [Configuration](#configuration) | Postgres connection |

Note: `--limit` shares its definition between `search` and `context`; its default is 10 in both. The README examples pass an explicit `--limit 5`.

## Configuration

Both resolutions happen at the start of every command; schema migrations (`schema_migrations`) are applied before any read or write.

**Connection string**, in this precedence order:

1. Flag `--connection-string`
2. Environment variable `REVIEWMEMORY_CONNECTIONSTRING`
3. Default: `Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory` — the local database from `docker-compose.yml` (Postgres 17 published at `localhost:5433` to avoid clashing with local Postgres instances on 5432)

**GitHub token** (only used by `index`):

1. Flag `--token`
2. Environment variable `GITHUB_TOKEN`

Without a token, Octokit operates anonymously: 60 req/h per IP. Authenticated: 5,000 req/h. Each indexed PR costs ~3 requests (paginated listing + files + review comments); indexing 200 PRs is ~600 requests.

## Exit codes

| Code | Condition |
|---|---|
| `0` | Success, including a search without results (`0 relevant discussions found`) |
| `1` | System.CommandLine parse error (missing command, missing required `--pr`, unknown option): prints message + help. Caught runtime error (`Octokit.ApiException`, `HttpRequestException`, `NpgsqlException`): prints `error: <message>` to stderr |
| `2` | Invalid usage detected by the program, with message on stderr (see next table) |

Validations that produce exit 2, with their literal message:

| Command | Condition | Message (stderr) |
|---|---|---|
| `index`, `context` | `repo` is not `owner/name` (two non-empty segments) | `error: repository must be in owner/name format` |
| `search`, `context` | `--format` other than `console`/`json` | `error: unknown --format '<value>' (console\|json)` |
| `search` | empty `query` AND no usable `--files` | `error: search requires text or --files` |
| `context` | The PR is not in the memory | `error: PR <owner>/<name>#<n> is not indexed; run 'reviewmemory index' first` |

Validation order in `search`: first `--format`, then text/files. In `context`: first `--format`, then repo format, then the database query. A Postgres connection failure (down port) is exit 1: e.g. `error: Failed to connect to 127.0.0.1:5999`.

## index output

One line per PR as it is processed (visibility during long runs), followed by a final summary:

```
#11 feat(search): content search on webhook body → 1 discussion(s)
Indexed 1 PRs · 1 discussions · 0 decisions with outcome
```

The summary counts processed PRs, total threads, and threads with an inferred decision other than `unknown`. Everything goes to stdout; errors go to stderr.

## console output (search / context)

`context` prepends a header with the PR state (console format only; `json` does not emit it) followed by a blank line:

```
MERGED PR #1 "feat(ui): Next.js UI — endpoints, webhook feed, detail viewer, replay" (Steve-XYZ)
```

Afterwards, both commands render identically (`SearchRenderer`):

```
N historically relevant discussion(s)

1. HIGH — Provider callbacks could be processed twice.
   Similarity: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120

   Previous reviewer concern:
   <finding on one line, truncated to 240 characters>

   Resolution: Finding accepted — implementation was fixed. <reason>
   https://github.com/<owner>/<name>/pull/<n>#discussion_r<id>
```

No results: `0 relevant discussions found`.

Details:

- The band comes from the combined score (text 0.55 + overlap 0.30 + recency 0.15): `HIGH` ≥ 0.65, `MEDIUM` ≥ 0.40, otherwise `LOW`.
- `Similarity` is the score with two decimals.
- Each hit's finding first line truncates to 72 characters with ellipsis `…`; the PR title does not appear in console output (only in JSON as `prTitle`).
- `File:` omits `:<line>` when the thread has no position.
- `Resolution:` only appears if the outcome is not `unknown` or there is a reason. Labels: `Finding accepted — implementation was fixed` / `Finding rejected` / `Finding partially accepted` / `Unknown outcome`.

## json output

Serialization of the hit list with `System.Text.Json`: indented, camelCase properties, enums as camelCase strings, null fields omitted (`reason` and `line` disappear when null). Fields per hit:

| Field | Type | Description |
|---|---|---|
| `threadId` | long | Id of the thread's root comment |
| `repo` | string | `owner/name` |
| `number` | int | PR number |
| `prTitle` | string | PR title |
| `path` | string | File of the finding |
| `line` | int? | Position in the diff; absent when null |
| `finding` | string | Full body of the reviewer's comment |
| `outcome` | string | `accepted` \| `rejected` \| `partiallyAccepted` \| `unknown` |
| `reason` | string? | Inferred reason; absent when null |
| `score` | double | Combined score, unrounded |
| `createdAt` | datetime | ISO 8601 with offset (`2026-08-22T02:37:24+00:00`) |
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

This schema is the contract for agents and for the future MCP server: breaking changes require updating this spec in the same PR.

## Prerequisites

```bash
docker compose up -d db   # postgres:17 at localhost:5433, container review-memory-db
```

The compose defines healthcheck `pg_isready -U reviewmemory -d reviewmemory` (every 5 s, 10 retries). Default credentials: database `reviewmemory`, user/password `reviewmemory`. Without the DB running, all three commands fail with exit 1 and `error: <Npgsql message>` on stderr.

`GITHUB_TOKEN` is only needed for `index`. `search` and `context` work without token or network.

## Examples

Verified against the binary (`dotnet run --project src/ReviewMemory.Cli -- …`):

```bash
# global and per-command help (-?, -h, --help and --version come from System.CommandLine)
dotnet run --project src/ReviewMemory.Cli -- --help
dotnet run --project src/ReviewMemory.Cli -- context --help

# prerequisite: start the local database
docker compose up -d db

# index the last 200 PRs (default)
export GITHUB_TOKEN=ghp_xxx   # recommended: without token, 60 req/h
dotnet run --project src/ReviewMemory.Cli -- index Shirka-Corporation/player-manager

# index only the last 50, with explicit token
dotnet run --project src/ReviewMemory.Cli -- index Steve-XYZ/webhook-replay --last=50 --token ghp_xxx

# search by free text, scoped to one repo
dotnet run --project src/ReviewMemory.Cli -- search "duplicate lotto transaction" --repo Shirka-Corporation/player-manager

# search by touched files (csv for multiple paths)
dotnet run --project src/ReviewMemory.Cli -- search "retry idempotency" --files src/LottoPendingTransactionProcessor.cs

# serialized output for agents
dotnet run --project src/ReviewMemory.Cli -- search "signature digest" --format json

# historical context of a PR, top 3, excluding its own discussions
dotnet run --project src/ReviewMemory.Cli -- context Steve-XYZ/webhook-replay --pr 1 --limit=3

# point to another database without touching the environment
dotnet run --project src/ReviewMemory.Cli -- search "x" \
  --connection-string "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory"
```

Verified error paths (all exit 2 except the last, exit 1):

```bash
dotnet run --project src/ReviewMemory.Cli -- index owner           # error: repository must be in owner/name format
dotnet run --project src/ReviewMemory.Cli -- search ""             # error: search requires text or --files
dotnet run --project src/ReviewMemory.Cli -- search x --format xml # error: unknown --format 'xml' (console|json)
dotnet run --project src/ReviewMemory.Cli -- context a/b --pr 999  # error: PR a/b#999 is not indexed; run 'reviewmemory index' first
dotnet run --project src/ReviewMemory.Cli -- context a/b           # Option '--pr' is required. + help (exit 1)
```

## Acceptance criteria (next CLI iteration)

1. **Spec as contract**: every option, default, and message in this spec matches the binary; a smoke test running `--help` of each command and the five previous error paths must keep passing after any change to `Program.cs`. Changing a message requires updating this spec in the same PR. Covered by `tests/ReviewMemory.Cli.Tests`, which runs the real binary as a subprocess.
2. **Stable exit codes**: 0 success, 1 parsing/runtime, 2 invalid usage. CI verifies all three via `dotnet test` (`tests/ReviewMemory.Cli.Tests`).
3. **Stable JSON**: same camelCase fields, enums as camelCase strings, nulls omitted. It is the schema agents and the MCP server will consume; breaking changes require an explicit note.
4. **`context` preserves its semantics**: excludes the PR's own discussions, requires `--pr`, and the header only appears in console format.
5. **Degradation without token**: `index` works anonymously (60 req/h) and exhausting the rate limit is exit 1 with `error: <message>` on stderr, never an uncaught crash.
6. **Copyable examples**: the example blocks in this spec and the README keep running verbatim on a clean checkout with `docker compose up -d db`.

## Decisions

- **Defaults are `Program.cs`'s, not the examples'**: `--limit` is 10 in `context` too; the README uses an explicit `--limit 5` in its example, not as a default.
- **Parse errors (System.CommandLine) are exit 1, not 2**: exit 2 stays reserved for domain validations with their own messages; the library's raw text is not translated.
- **`index` output without `--format`**: the line-by-line progress is already consumable; a json mode for index will be decided when a real consumer exists.
