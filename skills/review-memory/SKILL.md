---
name: review-memory
description: Guided GitHub PR code review that consults the team's historical review memory. Use when asked to review a pull request (or a diff) while the ReviewMemory MCP server with its search and context tools is available; it orders the flow, calibrates every finding against prior team decisions, and reports suppressed findings instead of silently repeating them.
---

# Review guided by ReviewMemory

Institutional code review memory: ReviewMemory retrieves the team's historical discussions with their inferred decision (`accepted`, `rejected`, `partially_accepted`,
`unknown`). This skill orders the review flow to consume it. The memory informs;
judging the diff remains your job.

## Mandatory flow

The order is deliberate and anti-anchoring: first you think, then you consult.

1. **Ticket**: understand what the PR changes and why.
2. **Diff inspection**: read the diff and form your candidate findings WITHOUT consulting
   the memory yet. Write them all down, even if you suspect some were already discussed.
3. **Memory query** (before closing the report):
   - Mandatory: `context` of the PR under review — `context(repo: "owner/name", pr: N)`.
   - Optional but recommended if the diff touches paths with history or key terms:
     `search(query: "<finding terms>", files: ["touched/path.go"])`.
4. **Validation**: contrast each candidate finding against the current code AND against
   the returned history (rules below).
5. **Report**: calibrated findings + transparency section (below).

## Calibration of each finding

- **Contradicts a `rejected` decision** → omit it from the report body or flag it
  explicitly as "already discussed and discarded", citing `url` and `threadId`.
- **Backed by an `accepted` discussion** → keep it and cite it as
  precedent with its `url`.
- **Partially matches a `partially_accepted` discussion** → partial precedent: gains some weight, but requires verifying that the current code still applies
  the part the team accepted before leaning on it.
- **`unknown` or no signal** → normal weight, as in any review.
- Match is judged by touched file and by substance of the finding, not by
  superficial textual similarity. A low score does not invalidate a pertinent precedent;
  a high score does not confirm it if the code changed.

Note: the current JSON contract does not expose decision provenance (`inferred` vs
`manual`); if a future version adds it, use it to prioritize manual precedents.

## Transparency

Nothing is silently discarded. Every finding omitted due to history appears in a final section "Findings discarded by prior decisions" with: what was omitted, which thread discarded it (`url` + `threadId`) and what the decision was. If the memory contributed nothing relevant say so too: "the memory returned no applicable precedents".

## Degradation — never block the review

- `context` responds with error `pr_not_indexed` → try once
  `reviewmemory index owner/name --last N` **only if the `reviewmemory` binary is
  available in PATH**; if it is not, ask the user to index the PR. In both cases retry `context` once.
- If the retry also fails or nobody can index → continue with whatever `search`
  returns plus a normal review, declaring it at the start of the report:
  "review without context of the PR itself: <reason>".
- DB or MCP server unavailable → continue as a normal review and declare it at the start
  of the report: "review without memory: <reason>".
- A memory failure never aborts or delays the review beyond that single retry.

## Limits of responsibility

A historical decision is not proof that the current code is correct: validate the precedent against the current state of the file. If the code changed since the thread,
the precedent loses force and the finding returns to normal weight.
