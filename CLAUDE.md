# Transit Pulse

Full project context, goals and work plan: @PROJECT_BRIEF.md — read it before any task.

## Working process
- **"Начинаем сессию"** → read `docs/STATUS.md` and the current `docs/stages/stage-N.md`; summarize stage, done, in progress, next step, open questions; propose the session plan.
- **"Заканчиваем сессию"** → update `docs/STATUS.md` (max one screen), tick checkboxes in `stage-N.md`, offer a commit to the working branch.
- Never merge to main, push tags, touch the VPS or spend money without the author's explicit command.
- Propose an ADR when a disputable decision is made; propose a local `CLAUDE.md` rule when a rule is easy to break.

## Key rules (details in the brief)
- Goals over features: every decision must move towards the brief §2 goals and be verifiable by measurement.
- Follow the stages in brief §10 in order; each stage has acceptance criteria.
- Data collection (stage 1) runs first and never stops.
- Team-style process: branches, PRs with descriptions, green CI, ADRs in `docs/adr/`, conventional commits (brief §11.1).
- Four deployable units, modular monolith inside; a new service only with an ADR.
- Only measured numbers in README.
- **Budget (brief §5.1):** anything that increases VPS or external service cost needs explicit justification and the author's approval.
- Small steps (brief §11.2): usually 1–3 files, up to ~150 changed lines; a plan before code.
- Code comments: XML docs on public APIs; inline comments only for non-obvious "why".

## Languages
Talk to the author in Russian; code, comments, commits, README and ADRs in English.
