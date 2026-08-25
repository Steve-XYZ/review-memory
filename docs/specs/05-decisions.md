# Decisions

Every review discussion ends in an outcome. In stage 1 that outcome is **inferred** with deterministic lexical signals (no AI) at indexing time: one decision per thread, stored alongside it and exposed in every search result. `confidence` distinguishes what is inferred from what a human corrects (stage 2).

Source of truth: `DecisionInferrer.Infer` (`src/ReviewMemory.Core/Decisions/DecisionInferrer.cs`). If this document differs from the code, the code wins.

## Outcomes

| Database value | Meaning |
|---|---|
| `accepted` | Finding accepted — implementation was fixed |
| `rejected` | Finding rejected (won't fix, false positive, already covered…) |
| `partially_accepted` | Partially addressed; something remains pending |
| `unknown` | No conclusive signal or contradictory signals |

## Signals

Three compiled regex patterns, all `RegexOptions.IgnoreCase`, applied as a *substring* of the full comment body (no anchors):

```csharp
// Rejection
@"won'?t ?fix|wontfix|not an issue|non-?issue|by design|as designed|works as intended|intentional|false positive|already (?:enforced|handled|covered)|no (?:aplica|hace falta)"

// Partial
@"partially|partial fix|parcialmente"

// Acceptance
@"fixed|addressed|good catch|done in|changed to|refactored|added (?:a )?(?:check|guard|test)|arreglado|corregido"
```

The lexicon is mostly English with a few Spanish fragments (`no aplica`, `hace falta`, `parcialmente`, `arreglado`, `corregido`) because the indexed reviews themselves are multilingual; the project language being English does not change what historical comments say.

### Rejection → `rejected`

| Fragment | Examples it matches |
|---|---|
| `won'?t ?fix` | "won't fix", "wont fix", "wontfix" |
| `wontfix` | "wontfix" (redundant with the previous) |
| `not an issue` | "Not an issue: there is already a constraint" |
| `non-?issue` | "non-issue", "nonissue" |
| `by design` | "this is by design" |
| `as designed` | "fails as designed" |
| `works as intended` | "it works as intended" |
| `intentional` | "that's intentional" |
| `false positive` | "False positive from my own tooling" |
| `already (?:enforced\|handled\|covered)` | "already enforced by a unique constraint" |
| `no (?:aplica\|hace falta)` | "no aplica aquí" (ES: doesn't apply), "no hace falta loggear" (ES: no need to log) |

### Partial → `partially_accepted`

| Fragment | Examples it matches |
|---|---|
| `partially` | "Partially addressed" |
| `partial fix` | "partial fix, rest in follow-up" |
| `parcialmente` | "parcialmente corregido" (ES: partially fixed) |

### Acceptance → `accepted`

| Fragment | Examples it matches |
|---|---|
| `fixed` | "Fixed, moved the check" |
| `addressed` | "addressed in commit abc" |
| `good catch` | "Good catch, fixed" |
| `done in` | "done in #1234" |
| `changed to` | "changed to TryParse" |
| `refactored` | "refactored into a service" |
| `added (?:a )?(?:check\|guard\|test)` | "added a guard", "added test" |
| `arreglado` | "arreglado en el último commit" (ES: fixed in the last commit) |
| `corregido` | "corregido, gracias" (ES: fixed, thanks) |

## Which comment counts

Candidates = all replies of the thread **plus** the initial comment (finding), stable-ordered like this:

1. First comments from whoever did **not** write the finding — typically the PR author, who is the one acting.
2. Then comments from the finding's own author (the reviewer), including the original finding.

For each category (rejection / partial / acceptance) the **first** candidate matching in that order wins; its author and body feed the reason. Matching is case-insensitive over the full body: the finding itself may carry the decisive signal (e.g. a reviewer marking their own comment "False positive … ignore this comment").

## Evaluation order

```
rejection ∧ (acceptance ∨ partial)  → unknown   reason: "contradictory signals — {A} / {B}"
rejection                           → rejected
partial                             → partially_accepted
acceptance                          → accepted
nothing                             → unknown   reason: null
```

- Conflict requires rejection against another category; **partial + acceptance without rejection falls to partial** (see example 3).
- In conflict, `{B}` quotes the acceptance if present; otherwise the partial.
- Each quote format: `response from {author}: "{body}"`, body truncated to 120 visible characters (119 + `…`) after `Trim()`.

## Confidence

| Confidence | Origin |
|---|---|
| `inferred` | Inferred by the catalog above (database default) |
| `manual` | Reserved for human correction (stage 2): lexical false positives and outcomes the text does not reveal |

Today no CLI path writes `manual`; every indexed decision is `inferred`.

## Database constraints

```sql
CREATE TABLE decisions (
    thread_id  bigint PRIMARY KEY REFERENCES review_threads (id) ON DELETE CASCADE,
    outcome    text   NOT NULL CHECK (outcome IN ('accepted', 'rejected', 'partially_accepted', 'unknown')),
    reason     text,
    confidence text   NOT NULL DEFAULT 'inferred' CHECK (confidence IN ('inferred', 'manual')),
    decided_at timestamptz NOT NULL DEFAULT now()
);
```

(`001_init.sql`; also `idx_decisions_outcome`.) One decision per thread. The indexer persists per thread within the same transaction as the rest of the PR: `INSERT … ON CONFLICT (thread_id) DO UPDATE` updates `outcome`, `reason`, `confidence`, and sets `decided_at = now()`. Since incremental re-indexing, unchanged threads are not touched and inference only runs on new or changed threads: **re-indexing preserves existing decisions**, and a row with `confidence = 'manual'` is never overwritten — the indexer skips inference for that thread. The CLI counter ("decisions with outcome") excludes `unknown`s.

## Exposure in search

Search does `LEFT JOIN decisions`: a thread without a decision row appears as `unknown` without reason. Every hit carries `outcome` and `reason`; console output prints `Resolution:` only if `outcome ≠ unknown` **or** there is a reason (the contradictory unknown is explained). Labels:

| Outcome | Text |
|---|---|
| `accepted` | Finding accepted — implementation was fixed |
| `rejected` | Finding rejected |
| `partially_accepted` | Finding partially accepted |
| `unknown` | Unknown outcome |

The reason is appended after the labels separated by ". ". The JSON serializes `outcome` in camelCase (`"accepted"`). `confidence` exists in the database but is not yet exposed in results.

## Input → output examples (from the tests)

| Decisive reply | Outcome | Reason |
|---|---|---|
| "Good catch, fixed by adding an idempotency check." (dev) | `accepted` | — |
| "Not an issue: providerRequestId already enforced by a unique constraint." (dev) | `rejected` | — |
| "Partially addressed; full retry policy lands in a follow-up." (dev) | `partially_accepted` | — |
| dev: "Fixed, moved the check." + reviewer: "Hmm, actually not an issue, the provider dedupes." | `unknown` | "contradictory signals — response from reviewer: … / response from dev: …" |
| "Sure, will look into it next sprint." (dev) | `unknown` | `null` |
| Reviewer finding: "False positive from my own tooling, ignore this comment." (+ reply "ok") | `rejected` | — |

## Limitations (explicit)

- Full English, partial Spanish: only `no aplica`, `hace falta`, `parcialmente`, `arreglado`, `corregido`.
- Substring without word boundaries or negation detection: "not fixed" contains `fixed` → false `accepted`; "unfixed" likewise.
- Does not look at later diffs, commits, or PR state; the thread's `resolved` is persisted but takes no part in inference.
- Per category only the first matching comment is quoted, not all.
- Re-indexing no longer loses `manual` decisions: guaranteed since incremental re-indexing — rows of unchanged threads are not touched and a manual row is never overwritten. `confidence` does not appear in search results yet.

## Stage 2: post-review learning (proposal, not implemented)

Combine PR lifecycle signals with the current lexicon. Each signal contributes evidence toward an outcome:

| Signal | Strength | Evidence toward |
|---|---|---|
| Thread/comment marked `fixed` | Strong positive | `accepted` |
| Reviewer replies `agreed` | Positive | `accepted` |
| `dismissed` by the reviewer | Negative | `rejected` |
| Thread `resolved` | Ambiguous | Inspect the resulting diff: did the flagged hunk change between base and merge? Change ⇒ `accepted`; no change ⇒ stays `unknown` or goes to manual review |
| Abandoned PR (closed without merge) | Weak | Not conclusive on its own; only reinforces what the lexicon says |

Proposed combination rule: the strongest available signal decides; the stage 1 lexicon remains the floor for every indexed PR. Strong signals could raise the declared confidence of the result without changing `confidence` (still reserved for humans).

Manual decisions:

- Flow (command or review skill) to set `outcome` with `confidence='manual'`.
- A previous `manual` decision prevails over any re-inference and survives re-indexing: behavior guaranteed by incremental re-indexing — the indexer skips inference when a manual row exists and does not touch it.
- Use cases: correcting lexical false positives ("not fixed"), recording outcomes the text does not show, and serving as training data for later stages.
