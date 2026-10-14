---
name: new-adr
description: Create an Architecture Decision Record in docs/adr/. Use when a debatable decision is made (technology, pattern, trade-off, delivery guarantee, storage, deployment) or when asked to "write an ADR" / "оформи ADR", including replacing an earlier decision.
---

# New ADR

Follow [ADR-0001](../../../docs/adr/0001-record-architecture-decisions.md) and the [template](../../../docs/adr/0000-template.md).

## Steps

1. **Number.** Take the highest `NNNN` in `docs/adr/` and add 1. Never reuse a number.
2. **File.** `docs/adr/NNNN-kebab-case-title.md`, title in imperative form ("Use Redpanda instead of Apache Kafka").
3. **Status.** `Proposed` until the author approves the decision, then `Accepted`. Date is today.
4. **Content.**
   - Context: constraints from the project brief (goals, budget, single VPS) and measured facts. No invented numbers.
   - Decision: one or two sentences first, then only the details that matter.
   - Alternatives: at least two real options, each with the concrete reason it lost.
   - Consequences: include the costs and risks accepted, not only benefits.
5. **Limits.** At most one page, in English, neutral project language only.
6. **Replacing a decision.** Write a new ADR that links the old one; in the old ADR change only the status line to `Superseded by [NNNN](NNNN-slug.md)`.
7. **Links.** Reference the ADR from the current `docs/stages/stage-N.md` and from the pull request description. Commit it in the same pull request as the change it describes.

Show the draft to the author before saving it as `Accepted`.
