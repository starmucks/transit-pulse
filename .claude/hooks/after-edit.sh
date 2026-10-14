#!/usr/bin/env bash
# PostToolUse hook: after a .cs file is edited, build its project (or run its tests).
# Exit code 2 sends stderr back to Claude Code, so a broken build cannot go unnoticed.
set -uo pipefail

input=$(cat)

# Pure bash/sed instead of jq: the hook must work on a bare Git Bash.
# Assumes the first "file_path" in the JSON is tool_input.file_path (it precedes the content).
file=$(printf '%s' "$input" \
  | grep -o '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n 1 \
  | sed -e 's/^.*:[[:space:]]*"//' -e 's/"$//')

[[ "$file" == *.cs ]] || exit 0

root="${CLAUDE_PROJECT_DIR:-$(pwd)}"

# One path form for both: JSON escapes `\` as `\\`, Git Bash may report `/c/...`, Windows `C:\...`.
normalize() {
  local path
  path=$(printf '%s' "$1" | tr -s '\\' '/')
  if command -v cygpath >/dev/null 2>&1; then
    path=$(cygpath -m "$path")
  fi
  printf '%s' "${path%/}"
}
file=$(normalize "$file")
root=$(normalize "$root")
# Windows reports the drive letter as both `c:` and `C:`.
shopt -s nocasematch

# The owning project is the nearest .csproj above the file, never above the repo root.
dir=$(dirname "$file")
project=""
while [[ "$dir/" == "$root/"* ]]; do
  project=$(ls "$dir"/*.csproj 2>/dev/null | head -n 1)
  [[ -n "$project" ]] && break
  dir=$(dirname "$dir")
done
[[ -n "$project" ]] || exit 0

command -v dotnet >/dev/null 2>&1 || { echo "Hook: dotnet not found, build check skipped" >&2; exit 0; }

# English output regardless of the OS locale, so the filters below always match.
export DOTNET_CLI_UI_LANGUAGE=en

if [[ "$project" == "$root/tests/"* ]]; then
  output=$(dotnet test --project "$project" 2>&1)
  status=$?
  # Test name, assertion and message; stack frames are noise here.
  report=$(printf '%s\n' "$output" | grep -vE '^[[:space:]]+at |^[[:space:]]*$' | head -n 40)
else
  output=$(dotnet build "$project" -nologo -v q 2>&1)
  status=$?
  report=$(printf '%s\n' "$output" | grep -E ': error ' | sed 's/ \[[^]]*\.csproj\]$//' | sort -u | head -n 30)
fi

if [[ $status -ne 0 ]]; then
  echo "Hook: $(basename "$project") failed after editing $(basename "$file"):" >&2
  printf '%s\n' "${report:-$(printf '%s\n' "$output" | tail -n 20)}" >&2
  exit 2
fi
