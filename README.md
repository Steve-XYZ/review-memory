# ReviewMemory

Institutional memory for code review. Not another "AI reviewer": it is the memory any reviewer —human, Codex, Claude, Copilot— consults before reviewing a PR.

```
reviewmemory context Shirka-Corporation/player-manager --pr 2268
```

```
3 historically relevant discussion(s)

1. HIGH — Provider callbacks could be processed twice.
   Similarity: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120
   ...
```

The agent reviewing your PR knows which concerns are historically relevant and which were already discarded by the team. That is worth more than stuffing twenty more rules into `AGENTS.md`.

**The memory is the product; the LLM reviewer is not.**

## Stack

.NET 10 · System.CommandLine · Octokit · Npgsql (PostgreSQL full-text search) · ModelContextProtocol (stdio MCP server) · xUnit

## Commands

```bash
# index the last N PRs of a repository
reviewmemory index Shirka-Corporation/player-manager --last 200

# search historical discussions by text and/or touched files
reviewmemory search "duplicate lotto transaction" --repo Shirka-Corporation/player-manager
reviewmemory search "retry idempotency" --files src/LottoPendingTransactionProcessor.cs

# relevant historical context for a specific PR (excludes its own discussions)
reviewmemory context Shirka-Corporation/player-manager --pr 2268 --limit 5
```

`--format json` returns the same result serialized for agent consumption.

## MCP server

The same memory, exposed as read-only MCP tools over stdio: agents (Claude Code, OpenCode, Codex, Copilot, Cursor…) consult the history without invoking the CLI or knowing anything about Postgres.

| Tool | Parameters | Equivalent to |
|---|---|---|
| `search` | `query`, `repo` (optional), `files` (optional), `limit` (optional) | `reviewmemory search --format json` |
| `context` | `repo`, `pr`, `limit` (optional) | `reviewmemory context --format json` |

Each tool returns exactly the same JSON as the CLI's homonymous command with `--format json`. There are no write tools: the memory is fed with `index`, never from the consuming agent. If the database is unreachable, the response is a structured error (`isError: true` with code and message) and the process stays alive.

### Run

```bash
docker compose up -d db                       # the DB must be running
dotnet run --project src/ReviewMemory.Mcp     # MCP server over stdio
```

As a self-contained .NET tool: publish once with your platform's RID (`linux-x64`, `osx-arm64`, `win-x64`…) and point each client to the resulting binary; no .NET installation required on the machine running the server.

```bash
dotnet publish src/ReviewMemory.Mcp -c Release -r linux-x64 --self-contained true -o publish/mcp
./publish/mcp/ReviewMemory.Mcp                # self-contained server binary
```

The connection resolves the same way as in the CLI: the `REVIEWMEMORY_CONNECTIONSTRING` variable or, by default, the docker-compose local database (`localhost:5433`).

### Register in Claude Code

In the project's `.mcp.json` (or global configuration):

```json
{
  "mcpServers": {
    "reviewmemory": {
      "type": "stdio",
      "command": "/absolute/path/to/review-memory/publish/mcp/ReviewMemory.Mcp",
      "env": {
        "REVIEWMEMORY_CONNECTIONSTRING": "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory"
      }
    }
  }
}
```

### Register in OpenCode

In `opencode.json`:

```json
{
  "mcp": {
    "reviewmemory": {
      "type": "local",
      "command": ["/absolute/path/to/review-memory/publish/mcp/ReviewMemory.Mcp"],
      "environment": {
        "REVIEWMEMORY_CONNECTIONSTRING": "Host=localhost;Port=5433;Database=reviewmemory;Username=reviewmemory;Password=reviewmemory"
      }
    }
  }
}
```

## Review skill

The real consumption of the memory in the workflow ([06-roadmap](docs/specs/06-roadmap.md) §4): a skill that orders the review as *ticket → diff inspection → memory query → validation of each finding against the history*. A finding contradicting a `rejected` decision is omitted or flagged as "already discussed and discarded" citing the thread; one backed by an `accepted` discussion gains weight and cites its precedent; nothing is silently discarded. The skill is versioned at [`skills/review-memory/SKILL.md`](skills/review-memory/SKILL.md).

Prerequisite: the [MCP server](#mcp-server) registered in the client that runs the skill.

### Per-project installation

**Claude Code** — copy or link the skill into the reviewed project:

```bash
mkdir -p .claude/skills
cp -r /path/to/review-memory/skills/review-memory .claude/skills/
```

**OpenCode** — same structure (`<name>/SKILL.md` with `name` + `description` frontmatter): place it in `.opencode/skills/review-memory/` or reuse the previous `.claude/skills/`, which OpenCode also discovers at project level. Verified end-to-end only with Claude Code; discovery of both paths in OpenCode is documented in its Agent Skills docs.

## Configuration

| Variable | Purpose |
|---|---|
| `GITHUB_TOKEN` | GitHub token for `index` (without token: 60 req/h) |
| `REVIEWMEMORY_CONNECTIONSTRING` | Postgres connection; defaults to the docker-compose local database |

```bash
docker compose up -d          # Postgres 17 at localhost:5433 (avoids the typical 5432 used by other local DBs)
dotnet run --project src/ReviewMemory.Cli -- index owner/name --last 50
```

| Exit code | Meaning |
|---|---|
| 0 | success |
| 1 | runtime error (GitHub, network, database) |
| 2 | invalid usage or entity not found |

## What the ranking does (stage 1)

No AI. Three combined signals over indexed review discussions:

- **Full-text search**: `tsvector` generated over the reviewer's comment + file path (`websearch_to_tsquery`, GIN index).
- **File overlap**: proportion of queried paths the candidate PR touched.
- **Recency**: exponential decay with a 120-day time constant (factor half-life ≈83 days; detail in `docs/specs/03-ranking.md`).

Every thread carries an **inferred decision** (`accepted` / `rejected` / `partially_accepted` / `unknown`) derived from deterministic lexical signals in the replies; contradictory signals stay `unknown` instead of guessing.

## Status: stage 1 foundations

- [x] Solution `ReviewMemory.slnx`: Cli · Core · GitHub · Storage (+ tests)
- [x] PostgreSQL schema with embedded migrations (`schema_migrations`)
- [x] GitHub REST ingestion: PRs, files, hunks, comments grouped into threads
- [x] FTS + overlap + recency search; `context` command excluding the PR itself
- [x] CI (build + tests against real Postgres)
- [x] Specs in `docs/specs/`
- [x] Thread "resolved" state via GraphQL (degrades without breaking indexing)
- [x] Incremental re-indexing by content hash
- [x] stdio MCP server with `search` and `context` tools, JSON parity with the CLI
- [x] Memory-guided review skill (`skills/review-memory`)
- [ ] embeddings/pgvector · post-review learning

## Documentation

The specs for the next stage are written in [docs/specs](docs/specs/README.md).
