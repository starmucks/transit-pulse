# Contributing to Transit Pulse

This file is the tool-independent development contract for people and coding agents. Product goals and architectural
constraints live in `PROJECT_BRIEF.md`; current work lives in `docs/STATUS.md`; accepted design decisions live in
`docs/adr/`. Tool-specific instruction files must only point to these canonical documents and must not own project rules.

## Working process

- Use the tool-independent workflow in `docs/development-sessions.md` for `/start_session` and `/end_session`.
- Work towards the goals and measurable acceptance criteria in `PROJECT_BRIEF.md`, in stage order.
- Before selecting work, read `docs/STATUS.md` and the relevant `docs/stages/stage-N.md`.
- Keep `docs/STATUS.md` to roughly one screen. Update it and the current stage checklist when finishing a task.
- Put status updates in the task's branch and pull request. Use a separate documentation pull request only to open or
  close a stage.
- Use focused branches, descriptive pull requests, green CI and conventional commits.
- Prefer small reviewable changes: normally 1–3 files and about 150 changed lines. Explain the plan before larger work.
- Propose an ADR for a disputable or long-lived architectural decision.
- When a recurring mistake reveals a missing rule, update the canonical document that owns the rule instead of adding
  it to a tool-specific adapter.

## Safety and operations

- While phase A runs, data collection must never stop as a side effect of development work. Ending phase A is an
  explicit author decision ([ADR-0016](docs/adr/0016-run-the-public-demo-on-demand-with-archive-replay.md)); the local
  raw archive remains the source of truth for ML and golden replay.
- Never merge to `main`, push tags, deploy to or modify production, delete production data, or spend money without the
  author's explicit approval for that action.
- Anything that increases VPS or external-service cost needs an explicit justification and the author's approval.
- Keep secrets and local data out of Git. Use the existing ignored local files and directories.

## Architecture and code

- Keep the four deployable units and the modular monolith described in `PROJECT_BRIEF.md`. A new service requires an ADR.
- Read the nearest module `README.md` before changing code in that module.
- Add XML documentation to public APIs. Add inline comments only for non-obvious reasons, not to narrate the code.
- Put only measured numbers in the public `README.md`.
- Write code, comments, commits, public documentation and ADRs in English.

## Verification

Run checks appropriate to the change. The full local verification is:

```powershell
dotnet build TransitPulse.slnx -c Release
dotnet test --solution TransitPulse.slnx -c Release --no-build
dotnet format TransitPulse.slnx --verify-no-changes --no-restore
```

Some integration tests require Docker. Report exactly which checks ran, their results, and anything that was skipped.
