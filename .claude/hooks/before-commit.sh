#!/usr/bin/env bash
# PreToolUse hook (Bash and PowerShell): block `git commit` while the solution is not formatted.
# Exit code 2 cancels the tool call and shows stderr to Claude Code.
# Checks the working copy, not only staged files: simpler, and CI checks the final result anyway.
set -uo pipefail

input=$(cat)

# The "command" JSON string, escaped quotes included; the description field is ignored.
command=$(printf '%s' "$input" \
  | sed -n 's/.*"command"[[:space:]]*:[[:space:]]*"\(\([^"\\]\|\\.\)*\)".*/\1/p' | head -n 1)

# `git commit`, also with options before the subcommand: `git -C dir commit`, `git -c k=v commit`.
commit_re='(^|[;&|(]|[[:space:]])git([[:space:]]+-[Cc][[:space:]]+[^[:space:]]+)*[[:space:]]+commit([[:space:]]|$)'
[[ "$command" =~ $commit_re ]] || exit 0

command -v dotnet >/dev/null 2>&1 || { echo "Hook: dotnet not found, format check skipped" >&2; exit 0; }
cd "${CLAUDE_PROJECT_DIR:-.}" || exit 0
export DOTNET_CLI_UI_LANGUAGE=en

if ! output=$(dotnet format TransitPulse.slnx --verify-no-changes 2>&1); then
  echo "Hook: commit blocked, code is not formatted. Run: dotnet format TransitPulse.slnx" >&2
  report=$(printf '%s\n' "$output" | grep -E 'error|warning' | head -n 20)
  printf '%s\n' "${report:-$(printf '%s\n' "$output" | tail -n 20)}" >&2
  exit 2
fi
