# 0001. Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-10-14

## Context

Transit Pulse makes decisions that are reasonable to dispute: a Kafka-compatible broker on a small VPS, at-least-once delivery instead of exactly-once, tiered storage with thinned positions, three different feed protocols behind one model. Some of them will be revisited when measurements come in. Without a record, the reasoning behind a decision is lost, and the same trade-offs get argued again.

## Decision

We record every decision that could reasonably be made differently as a lightweight Architecture Decision Record in `docs/adr/`:

- One file per decision, `NNNN-kebab-case-title.md`, numbered in order, based on [the template](0000-template.md).
- Sections: Context, Decision, Alternatives considered, Consequences; at most one page.
- An ADR is merged in the same pull request as the change it describes.
- An accepted ADR is not rewritten. A changed decision gets a new ADR, and the old one is marked `Superseded by`.

## Alternatives considered

- **Pull request descriptions only.** They capture the reasoning at the moment of change, but are scattered across PRs, hard to search, and never show the current state of a decision.
- **GitHub wiki.** Lives outside the code, is not reviewed in pull requests, and drifts from what the code actually does.
- **No written record.** Cheapest now, but every later change to the architecture starts from guessing why the current design exists.

## Consequences

- Decisions and their trade-offs live next to the code and go through the same review.
- A newcomer can read the ADRs in order to understand why the system looks the way it does.
- Writing an ADR takes time, so it is reserved for decisions with real alternatives, not for routine choices.
