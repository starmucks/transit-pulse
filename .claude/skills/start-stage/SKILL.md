---
name: start-stage
description: Open a work stage from PROJECT_BRIEF.md §10 — write docs/stages/stage-N.md, create GitHub issues, update status. Use when asked to "start stage N" / "начинаем этап N".
---

# Start stage N

Planning only: no production code in this session.

## Steps

1. **Read.** Stage N in `PROJECT_BRIEF.md` §10, the goals it serves (§2.2), accepted ADRs in `docs/adr/`, the previous `docs/stages/stage-(N-1).md` and `docs/STATUS.md`.
2. **Draft in plan mode** `docs/stages/stage-N.md`, following the structure of [stage-0](../../../docs/stages/stage-0.md):
   - goal and which brief goals (C1–C12) it closes;
   - tasks of 0.5–1 day each, as checkboxes, each with a "Done when" line;
   - acceptance criteria copied from the brief, made checkable;
   - ADRs expected in this stage;
   - risks with mitigations;
   - what is needed from the author (decisions, accounts, money).
   Keep it in Russian, like the other stage specs.
3. **Approve.** The author reviews and approves the spec; mark it "утверждена" with the date.
4. **Issues.** With the author's consent, create one issue per task via `gh issue create`: English title, goal and acceptance criteria in the body (matching `.github/ISSUE_TEMPLATE/task.yml`), milestone of stage N, area label (`domain`, `infra`, `ml`, `ai`, `web`, `perf`). Write the issue numbers back into the spec.
5. **Status.** Update `docs/STATUS.md`: current stage, first task, open questions.
