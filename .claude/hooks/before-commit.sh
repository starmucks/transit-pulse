#!/usr/bin/env bash
# PreToolUse hook: block `git commit` while the solution is not formatted.
# Exit code 2 cancels the tool call and shows stderr to Claude Code.
set -uo pipefail

input=$(cat)
printf '%s' "$input" | grep -q 'git commit' || exit 0

cd "${CLAUDE_PROJECT_DIR:-.}" || exit 0

if ! output=$(dotnet format TransitPulse.slnx --verify-no-changes 2>&1); then
  echo "Hook: commit blocked, code is not formatted. Run: dotnet format TransitPulse.slnx" >&2
  printf '%s\n' "$output" | grep -E 'error|warning' | head -n 20 >&2
  exit 2
fi
