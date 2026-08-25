# Vision

## Problem

More code is being written with agents and, as a result, more is being reviewed. Generating another diff or another list of findings is cheap and abundant. The scarce resource is something else: knowing the codebase and the decisions the team already made about it.

That knowledge exists but it is buried. It was written in the review threads of hundreds of merged PRs: rejected patterns with their reason, accepted exceptions with conditions, recurring bugs in certain modules. Today nobody consults it before reviewing. Predictable result: every reviewer —human or agent— asks again what was already answered, re-approves what was rejected, or rejects what was debated and accepted.

## What it is

ReviewMemory indexes a repository's PRs and their review discussions into PostgreSQL. Before reviewing a PR, any reviewer consults the history:

```
reviewmemory context Shirka-Corporation/player-manager --pr 2268
```

```
3 historically relevant discussion(s)

1. HIGH — Provider callbacks could be processed twice.
   Similarity: 0.91
   PR #1943 (Shirka-Corporation/player-manager) · 2026-03-10
   File: src/AdJoePayoutHandler.cs:120

   Previous reviewer concern:
   Provider callbacks could be processed twice during retries...

   Resolution: Finding accepted — implementation was fixed
   https://github.com/Shirka-Corporation/player-manager/pull/1943#discussion_r…
```

Each result carries what a reviewer needs: the previous reviewer's textual concern, where it was raised, how it ended (accepted, rejected, partially accepted, or unknown), and the link to the original thread.

## Positioning

Not another AI reviewer. The memory is the product; the LLM reviewer is not.

| | Generic AI reviewer | ReviewMemory |
|---|---|---|
| Product | the model that generates findings | the memory that contextualizes them |
| Moat | none: replaced by the next model | cumulative: grows with every review |
| Criterion | generic prompt rules | this team's real decisions |

It is deliberately reviewer-agnostic: it works the same for a person, for Codex, for Claude, or for Copilot. Today it integrates via CLI (`--format json` for agents) and via MCP server (spec 06-roadmap).

## What it does NOT do

Explicit no-goals; proposing any of these requires changing this spec first:

- **Does not comment on PRs** or open reviews on GitHub. It only reads history and answers queries.
- **Does not score code** or emit its own findings about the current diff. It classifies historical precedents, not quality.
- **Does not replace the reviewer**: it neither approves nor blocks. A human or agent decides; this only provides context.
- **Does not train or host models**: stage 1 retrieval uses no AI, only FTS, file overlap, and recency.
- **Does not modify the source repository**: it is a read-only index over GitHub.

## Target user and moment of use

Two users, same moment:

- **Human reviewer**: runs `context` on the PR before reading the diff, to know which concerns apply and which were already discarded by the team.
- **Reviewer agent** (Codex, Claude, Copilot, or another): consumes `search`/`context` as part of its context when starting the review, today by invoking the CLI with `--format json`, tomorrow via MCP.

The moment is before and during a specific review. Not after (it is not a post-mortem tool) nor as an automatic CI gate.

## Per-stage goal

### Stage 1 — foundations (done)

Verifiable against the README and current code:

- Built the `Cli · Core · GitHub · Storage` solution with tests (xUnit) and CI that builds and runs tests against a real Postgres.
- Implemented GitHub REST ingestion: PRs, touched files, hunks, and comments grouped into threads.
- Created the PostgreSQL schema with embedded migrations versioned in `schema_migrations`.
- Implemented AI-free ranking with three combined signals —FTS over comment and path (`tsvector`, GIN index), file overlap, and exponential recency with a 120-day time constant (actual half-life ≈ 83 days)—, weights 0.55/0.30/0.15, and bands HIGH ≥ 0.65, MEDIUM ≥ 0.40 (see spec 03-ranking).
- Inferred each thread's decision (`accepted`/`rejected`/`partially_accepted`/`unknown`) with deterministic lexical signals; contradictory signals stay `unknown` instead of guessing (see spec 05-decisions).
- Shipped the `index`, `search`, and `context` commands with equivalent `console` and `json` output; `context` excludes the PR's own discussions (see spec 04-cli).

Deliberately left out: the actual resolution state of threads (the REST API does not expose it), incremental re-indexing, and any agent integration.

### Stage 2 — harden what exists and close the gaps

- Harden specs 03-ranking, 04-cli, and 05-decisions: turn implemented behavior into verifiable contract (stable output format, score semantics, decision rules with contract tests).
- Resolve threads' `resolved` state via GraphQL and distinguish it from the lexically inferred outcome.
- Incremental indexing: re-indexing a PR updates its threads instead of deleting and recreating them.
- The MCP server, embeddings/pgvector, and post-review learning stay planned in spec 06-roadmap; this stage does not commit to them.

## Success signal

Measurable with our own data, no third parties:

- **Precedent coverage**: percentage of comments in a new review that already had a retrievable precedent (hit in the HIGH or MEDIUM band) in the memory. If the memory works, repeatable findings should appear with precedent; genuinely new ones should not.
- **Manual Recall@k over past reviews**: for a sample of already-reviewed and merged PRs, run `context` (which already excludes the PR's own threads) and check whether the concerns that actually arose appear in the top-k results. Manual procedure, initial k = 5.
- **Real consumption by an agent** (stage 2): at least one consumer outside the project —human or agent— completes reviews consulting the memory regularly. It is binary and observable, not a vanity metric.

These measures calibrate stage 2 thresholds; this spec fixes the method, not arbitrary figures.

## Acceptance criteria of this spec

- Every stage 1 claim corresponds to verifiable behavior in the current code, README, or CI.
- A new reader understands in one read what it is, what it is not, and who it is for, without opening the code.
- The no-goals are actionable: any contribution that comments on PRs, scores code, or intends to replace the reviewer contradicts this document and must be discussed here first.
- Sibling specs (02-architecture, 03-ranking, 04-cli, 05-decisions, 06-roadmap) are referenced by number; this document does not duplicate their details.
- Every proposed metric is computable from the repository's own data and memory, with no market figures or external citations.
