# ReviewMemory Specs

| Spec | Contents | Status |
|---|---|---|
| [01-vision](01-vision.md) | problem, positioning, no-goals, per-stage goal, success signals | written (PR #6) |
| [02-architecture](02-architecture.md) | stack, solution structure, domain model vs tables, ingestion flow, limitations | written (PR #3) |
| [03-ranking](03-ranking.md) | ranking signals with exact values, search vs context semantics, console/json formats, agent contract | written (PR #5) |
| [04-cli](04-cli.md) | commands, options and literal defaults, exit codes, error messages, verified examples | written (PR #4) |
| [05-decisions](05-decisions.md) | lexical signal catalog, evaluation order, database constraints, post-review learning | written (PR #1) |
| [06-roadmap](06-roadmap.md) | GraphQL resolved, incremental index, MCP server, skills, pgvector, learning — commitment vs proposal | written (PR #2) |

The specs document the behavior implemented in `src/` and define the acceptance
criteria for the next iteration of each area. Series rule: if a spec differs
from the code, the code wins — and the PR that changes one must change the other.
