---
name: close-stage
description: Close a work stage — verify every acceptance criterion with evidence, update README, progress notes and status, prepare release notes. Use when asked to "close stage N" / "закрываем этап N".
---

# Close stage N

A stage is closed only when every acceptance criterion in `docs/stages/stage-N.md` is proven.

## Steps

1. **Verify criteria.** For each criterion give evidence: a command and its output, a test name, a measurement, or a merged PR link. A criterion without evidence stays unchecked and is reported as open — never tick it on trust.
2. **Open work.** List unfinished tasks; with the author decide: finish now, or move to the next stage with an issue.
3. **README.** Add what can now be shown (features, how to run, screenshots). Numbers only if measured; put measurements in `docs/benchmarks.md` with the method used.
4. **Progress.** Append a short entry to `docs/progress.md`: what was done, what was learned about the system, problems.
5. **Release notes.** Draft notes for tag `v0.N` from the merged PRs of the milestone. The author creates the tag and release; do not push tags.
6. **Status.** Update `docs/STATUS.md` to point at stage N+1 and its first step; close the milestone only with the author's consent.

All of the above goes through one pull request, like any other change.
