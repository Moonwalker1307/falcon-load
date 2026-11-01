#!/usr/bin/env bash
# Runs falcon-load for the GitHub Action (action.yml). The inputs arrive as INPUT_* environment variables. The script
# always exits 0 once falcon-load has run and hands its exit code to the later steps, so the results are uploaded and the
# pull request comment is posted before the job fails.
set -uo pipefail

results="$(mkdir -p "$INPUT_RESULTS" && cd "$INPUT_RESULTS" && pwd)"

# Git Bash on a Windows runner gives /d/a/... paths, which dotnet can't open; cygpath turns them into D:/a/...
if command -v cygpath > /dev/null; then results="$(cygpath -m "$results")"; fi
summary="$results/summary.md"
rm -f "$summary"

args=(run "$INPUT_FILE"
  --out "$results/json/"
  --html "$results/html/"
  --junit "$results/junit.xml"
  --markdown "$summary")

add() { if [ -n "$2" ]; then args+=("$1" "$2"); fi; }
add --test "$INPUT_TEST"
add --env-file "$INPUT_ENV_FILE"
add --env "$INPUT_ENV"
add --concurrency "$INPUT_CONCURRENCY"
add --duration "$INPUT_DURATION"
add --requests "$INPUT_REQUESTS"
add --max-error-rate "$INPUT_MAX_ERROR_RATE"

# One name=value per line; blank lines and surrounding spaces are ignored.
while IFS= read -r line; do
  line="$(echo "$line" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
  if [ -n "$line" ]; then args+=(--var "$line"); fi
done <<< "$INPUT_VARS"

while IFS= read -r line; do
  line="$(echo "$line" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
  if [ -n "$line" ]; then args+=(--data "$line"); fi
done <<< "$INPUT_DATA"

while IFS= read -r line; do
  read -ra words <<< "$line"
  if [ "${#words[@]}" -gt 0 ]; then args+=("${words[@]}"); fi
done <<< "$INPUT_ARGS"

# The trailing slashes make --out and --html directories with a file per test, however many tests the file holds.
dotnet "$FALCON_LOAD_TOOL" "${args[@]}"
code=$?

passed=false
if [ "$code" -eq 0 ]; then passed=true; fi
{
  echo "exit-code=$code"
  echo "passed=$passed"
  echo "results-directory=$results"
  if [ -f "$summary" ]; then echo "summary=$summary"; fi
} >> "$GITHUB_OUTPUT"

if [ -f "$summary" ]; then
  cat "$summary" >> "$GITHUB_STEP_SUMMARY"
fi

exit 0
