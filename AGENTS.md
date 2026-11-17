# Coding-agent adapter

This is the primary adapter for tools that support `AGENTS.md`, including Codex and Cursor. Project guidance is
tool-independent: `CONTRIBUTING.md` is the development contract, and `CLAUDE.md` is only a compatibility adapter to the
same canonical documents. Do not treat either adapter as a separate source of project rules.

- Follow `CONTRIBUTING.md` for workflow, safety, architecture, language and verification.
- Use `PROJECT_BRIEF.md` for product goals and architectural constraints.
- Read `docs/STATUS.md` and the relevant stage file when selecting or finishing work, not for every small edit.
- Read the nearest module `README.md` before changing that module.
- If `LOCAL_GUIDANCE.md` exists, follow it as private author guidance. Never copy its private content into tracked files.
- Treat `/start_session` and `/end_session` as workflow commands and follow `docs/development-sessions.md`.

Keep this file short. Put durable knowledge in the canonical human-readable documentation.
