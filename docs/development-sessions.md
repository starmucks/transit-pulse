# Development sessions

This workflow lets the author move between coding tools without maintaining separate project rules or writing a long
handoff prompt. Tools work sequentially, never in parallel. The repository and `LOCAL_SESSION.md` carry the state from
one session to the next.

The workflow commands are `/start_session [optional task]` and `/end_session`. A tool that does not register custom
slash commands must still treat these exact messages as workflow commands.

## Sources of truth

Use evidence in this order:

1. The current branch, working tree, diff and recent commits.
2. Canonical project documents, issues and pull requests.
3. The ignored `LOCAL_SESSION.md` handoff note.

The handoff note is a convenience, not a source of project rules. Verify it against Git before relying on it. Never put
credentials, tokens or other secrets in it.

## `/start_session [optional task]`

Start or resume work without requiring another prompt from the author:

1. Read `CONTRIBUTING.md`, `PROJECT_BRIEF.md` as needed and `LOCAL_GUIDANCE.md` when it exists.
2. Inspect the branch, working tree, diff and recent commits. Never discard or overwrite changes whose origin is not
   understood. Do not switch a dirty checkout to another branch without explicit approval.
3. Read `LOCAL_SESSION.md` when it exists and compare it with Git. Git wins when they disagree, and the discrepancy must
   be reported.
4. Determine the task in this order: the optional command argument; work already present in the checkout; the handoff
   note; then the next task in `docs/STATUS.md` and the current stage document. Read the nearest module `README.md` and
   relevant ADRs before editing that module.
5. When network access and credentials are available, refresh or inspect relevant remote branches, issues, pull requests
   and CI state using read-only operations. A network failure must not prevent local work when local evidence is enough.
6. Create or refresh `LOCAL_SESSION.md` with the selected task, branch, known uncommitted baseline and first next action.
7. Report a compact start summary: task, branch, existing work, known verification state, next action and blockers. Ask
   one concise question only when a materially different result depends on the answer; otherwise begin the work.

Record the starting diff in the handoff note. Pre-existing changes may be continued only when they clearly belong to the
selected task. Do not start another coding tool, agent or worktree as part of the normal session workflow. Refresh the
handoff note after a material checkpoint during a long session, but do not turn it into a running log.

## `/end_session`

Bring the current session to a safe local checkpoint without requiring separate instructions:

1. Stop at a coherent point and inspect the complete diff. Separate active-task files from unrelated pre-existing work.
2. Run verification appropriate to the change. Use the full checks from `CONTRIBUTING.md` when the task is ready for
   review; otherwise run the narrowest checks that establish a safe checkpoint. Report skipped checks exactly.
3. Check the proposed commit for secrets, private local content and accidental unrelated changes.
4. Update durable status, stage or module documentation only when the underlying project facts changed.
5. Rewrite `LOCAL_SESSION.md` using the format below. It remains ignored and is never committed.
6. Create one local conventional commit when the active-task checkpoint is coherent and its required verification
   passes. Stage only reviewed active-task files. Do not create an empty commit.

Invoking `/end_session` authorizes that one local commit. It does **not** authorize a push, pull request, merge, tag,
release, deployment, production mutation, production-data deletion or paid external action. Never amend, rebase or
rewrite existing commits unless the author explicitly asks. If verification fails or unrelated changes cannot be
separated safely, do not commit; preserve the work and record the exact blocker in `LOCAL_SESSION.md`.

Finish with a compact summary: completed work, verification, local commit hash (or why none was created), next action,
blockers and confirmation that no external write occurred.

## `LOCAL_SESSION.md` format

```markdown
# Local session state

Updated: YYYY-MM-DD HH:MM with time zone
Branch: branch-name
Task: issue number or short description
Status: in-progress | ready-for-review | blocked

## Done

- Concrete completed work.

## Decisions

- Decisions that the next tool must know; use canonical docs for durable architectural decisions.

## Verification

- Exact command and result, or an explicit note that it was not run.

## Next

- The first concrete next action.

## Blockers

- Blocker, or `None`.

## Uncommitted work

- Files and reason, or `None`.
```
