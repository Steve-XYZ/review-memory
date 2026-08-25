# Roadmap after stage 1

Stage 1 ([01-vision](01-vision.md)) laid the foundations: REST PR ingestion, FTS +
overlap + recency search, inferred decisions, and the CLI ([04-cli](04-cli.md)).
This roadmap orders what comes next. The order is deliberate: first fix the quality
and durability of the data already indexed, then take it to where reviewers work
(agents via MCP), and finally the improvements conditional on evidence.

Each item marks whether it is a **commitment** (agreed work for this stage) or a
**proposal** (executed when its entry criterion is met). Priority order:

1. `resolved` state via GraphQL — cheap correction of false data persisted today.
2. Incremental re-indexing — avoids destroying owned state on every `index` run.
3. MCP server — distribution: without it, the memory only reaches whoever has the CLI.
4. Integration with review skills — real consumption of item 3 in the workflow.
5. Embeddings + pgvector — only if measurement proves plain FTS falls short.
6. Post-review learning — depends on 1, 2 (correct data) and 4 (real consumption).

## 1. Resolved state via GraphQL (commitment)

**Problem.** GitHub's REST API does not expose the resolved state of review threads;
`GitHubPullRequestSource` reconstructs threads by grouping comments along their
`in_reply_to` chain and all arrive with `Resolved = false`
(`src/ReviewMemory.GitHub/GitHubPullRequestSource.cs`). Consequence: the `resolved`
column stores noise and [05-decisions](05-decisions.md) infers over an always-false
signal — a thread discussed and discarded by the team looks the same as an open
unanswered one.

**Proposal.** A complementary GraphQL query after loading the PR via REST:
`pullRequest.reviewThreads { id isResolved isOutdated }`, mapping `thread id → isResolved`,
and overwriting `Resolved` before persisting. REST remains the source of comments and
pagination; GraphQL contributes only the flag. If the query fails, degrade to current
behavior with a stderr warning and exit 0.

- Entry: none; it is a stage 1 correction.
- Output (definition of done): after `index`, a PR with known resolved threads persists
  `resolved = true` for those threads (test against a GraphQL fixture); GraphQL failure
  degrades without breaking indexing.

## 2. Incremental re-indexing (commitment)

**Problem.** `IndexRepository.UpsertAsync` deletes and recreates all of the PR's threads on
every pass (`DeleteThreadsAsync`, `src/ReviewMemory.Storage/IndexRepository.cs`).
Everything living attached to those rows —a manually corrected decision
(`confidence = manual`) or any future data learned about the thread— is lost every time
someone reruns `index`. It also rewrites identical rows: cost proportional to the whole
history on every run.

**Proposal.** Reconcile incoming threads against existing ones by their stable GitHub id:
insert new ones, update changed ones (content hash: finding, replies, path, line), keep
unchanged ones intact. Re-inference from [05-decisions](05-decisions.md) only applies to new
or changed threads; it never overwrites a manual decision. PR files (`pr_files`, fully
derivable from the API) can stay replace-all.

- Entry: none; also a stage 1 correction.
- Output (definition of done): idempotency test — indexing the same PR twice leaves the
  same counts of threads, comments, and decisions; a manual decision survives re-indexing
  an unchanged PR; a thread changed on GitHub does get updated.

## 3. MCP server (commitment)

**Problem.** The only entry point today is the CLI ([04-cli](04-cli.md)). Target consumers
—Codex, Claude Code, Copilot, Cursor, OpenCode— speak MCP; asking them to invoke a .NET
binary couples every integration to installation and Postgres access details.

**Proposal.** New project `ReviewMemory.Mcp` over stdio transport with two tools reusing
Core/Storage logic without duplicating it: `search` (query, optional repo, optional files,
limit) and `context` (repo, pr, limit), with the same parameters and same JSON output as
the CLI's homonymous commands — the contract of [03-ranking](03-ranking.md) serves humans
and agents alike. No write tools: the memory is fed with `index`, not from the agent.
Distribution as a self-contained .NET tool; registration instructions per client in the
README.

- Entry: none.
- Output (definition of done): the server registered in Claude Code or OpenCode runs
  `search` and `context` against the docker-compose local database; JSON output parity
  with the CLI verified by test; structured error when there is no DB, no process crash.

## 4. Integration with code review skills (commitment)

**Problem.** A memory nobody consults does not exist. What is missing is defining at which
point of the review flow an agent asks ReviewMemory and what it does with the answer.

**Proposal.** A skill that orders the flow: ticket → diff inspection → `context` query of
the PR (via MCP, item 3) → review → validation of each finding against the current code
**and** against the returned history. A finding contradicting a historical `rejected`
decision is marked as already discussed and discarded instead of repeated; one backed by
an `accepted` discussion gains weight.

- Entry: MCP server available (item 3).
- Output (definition of done): a review guided by the skill cites relevant historical
  discussions with their inferred decision and omits or downgrades findings already
  discarded by the team; the skill stays versioned in this repo.

## 5. Embeddings + pgvector (proposal)

**Problem.** FTS fails when vocabulary does not match: "duplicate callback" does not match
lexically with "processed twice", even though they describe the same problem. File overlap
compensates only if the candidate PR touched the same files.

**Proposal.** Second similarity signal: embedding of the reviewer's comment computed at
indexing, cosine search with pgvector, linear combination with the three stage 1 signals.
When it is worth it: not by default. Build first a small set of queries with known relevance
over real repos and measure plain FTS recall@k; implement only if that recall lands below an
agreed threshold. If FTS performs, pgvector is infrastructure and migrations without
justified return.

- Entry: recall@k benchmark published with numbers demonstrating plain FTS's deficit.
- Output (definition of done): same battery before/after with measured recall@k improvement
  without losing top-1 precision beyond the agreed threshold; new embedded migration
  (`schema_migrations`) and ranking documented in [03-ranking](03-ranking.md).

## 6. Post-review learning (proposal)

**Problem.** The lexical inference of [05-decisions](05-decisions.md) resolves the obvious
and leaves `unknown` what is ambiguous; the most valuable knowledge —what the team accepted
or discarded and why— lives in the later conversation and never returns to the memory.

**Proposal.** Close the loop: after a review that consumed context (item 4), record the
effective response to each consulted finding (applied as-is, rejected with reason,
ignored) and persist it as a manual decision linked to the historical thread. That corpus
feeds "Team Review Patterns": aggregates per file, module, and finding type that `context`
returns alongside the raw threads. Depends on items 1–2: without real `resolved` nor safe
re-indexing, what is learned gets lost or starts from false signals.

- Entry: items 2 and 3 in production and real use of item 4's flow — without consumption
  there are no responses to learn from.
- Output (definition of done): a manually recorded decision appears in later `context`
  results with its provenance, survives re-indexing, and the aggregated patterns appear in
  the documented JSON output.

## Not doing

- Own LLM reviewer: the memory retrieves and scores history; judging the diff remains the
  reviewer agent's job ([01-vision](01-vision.md) position).
- Web UI while CLI and MCP cover consumption.
- Default embeddings without item 5's measurement.
- Writes from consuming agents: the memory is fed by `index` and supervised post-review
  learning, never by the read tools.
