#!/usr/bin/env bash
# =============================================================================
# Smart-X Part 3 baseline verification (P3-01, commit 1)
# =============================================================================
# Runs CI-equivalent checks against the developer's *current local checkout*.
# Updates only the generated section of docs/part3/BASELINE.md.
#
# Safety:
#   - Never runs git commit/push/merge.
#   - Never prints or stores local encryption keys.
#   - Never starts or removes Docker containers or volumes.
#   - Does not change existing application source files.
#   - Reports FAIL/SKIPPED rather than inventing passing test results.
#
# Usage (from anywhere inside the Git repository):
#   bash scripts/capture_part3_baseline.sh
# =============================================================================

set -uo pipefail

# Locate the repository independently of the shell's starting directory.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "ERROR: This script must be placed in scripts/ inside the Smart-X Git repository." >&2
  exit 2
fi

BASELINE_FILE="$REPO_ROOT/docs/part3/BASELINE.md"
if [[ ! -f "$BASELINE_FILE" ]]; then
  echo "ERROR: Missing docs/part3/BASELINE.md. Extract the complete Commit 1 ZIP." >&2
  exit 2
fi

SNAPSHOT_SHA="adb422884438f6a12e330500b08aabb5f9842c33"
WORKING_BRANCH="$(git -C "$REPO_ROOT" branch --show-current)"
WORKING_SHA="$(git -C "$REPO_ROOT" rev-parse HEAD)"

# The feature branch should be based on the Part 2 release. This test is read
# only and prevents accidentally recording an older checkout as the baseline.
if git -C "$REPO_ROOT" cat-file -e "${SNAPSHOT_SHA}^{commit}" 2>/dev/null; then
  if git -C "$REPO_ROOT" merge-base --is-ancestor "$SNAPSHOT_SHA" HEAD; then
    RELEASE_ANCESTRY="Verified: Part 2 release is an ancestor of this checkout"
  else
    echo "ERROR: Current branch does not include Part 2 release $SNAPSHOT_SHA." >&2
    echo "Integrate the Part 2 release into dev before recording the Part 3 baseline." >&2
    exit 2
  fi
else
  echo "ERROR: Cannot find the Part 2 release commit $SNAPSHOT_SHA in local Git history." >&2
  echo "Fetch full history, then confirm dev includes the Part 2 release." >&2
  exit 2
fi

TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/smartx-p3-baseline.XXXXXX")"
trap 'rm -rf "$TEMP_DIR"' EXIT
RESULTS_FILE="$TEMP_DIR/statuses.txt"
: > "$RESULTS_FILE"

# Note: labels cannot contain '|', since this is our internal status delimiter.
# Each test writes its full log outside the repository; only short summaries
# are copied into the baseline document. Exit codes are preserved faithfully.
run_check() {
  local label="$1"
  local log_name="$2"
  shift 2

  echo "[CHECK] $label"
  if "$@" > "$TEMP_DIR/$log_name" 2>&1; then
    LAST_RESULT="PASS"
    echo "  -> PASS"
  else
    LAST_RESULT="FAIL"
    echo "  -> FAIL (last log lines below)"
    tail -n 8 "$TEMP_DIR/$log_name" || true
  fi
  printf '%s|%s\n' "$label" "$LAST_RESULT" >> "$RESULTS_FILE"
}

skip_check() {
  local label="$1"
  local reason="$2"
  printf '%s|SKIPPED (%s)\n' "$label" "$reason" >> "$RESULTS_FILE"
  echo "[SKIP] $label - $reason"
}

# Missing tools are shown explicitly. A version check must not falsely claim
# a tool is present merely because its command name exists on PATH.
get_version() {
  if command -v "$1" >/dev/null 2>&1; then
    "$@" 2>/dev/null | head -n 1 || true
  else
    printf 'Unavailable\n'
  fi
}

cd "$REPO_ROOT" || exit 2

# ------------------------- .NET backend gates -------------------------------
if command -v dotnet >/dev/null 2>&1; then
  run_check "NuGet restore and dependency audit" "dotnet-restore.log" \
    dotnet restore SmartX.sln -p:NuGetAudit=true -p:NuGetAuditMode=all \
    '-warnaserror:NU1901;NU1902;NU1903;NU1904'
  restore_result="$LAST_RESULT"

  if [[ "$restore_result" == "PASS" ]]; then
    run_check ".NET formatting" "dotnet-format.log" \
      dotnet format SmartX.sln --verify-no-changes --no-restore
    run_check "Backend Release build" "dotnet-build.log" \
      dotnet build SmartX.sln --configuration Release --no-restore
    build_result="$LAST_RESULT"

    if [[ "$build_result" == "PASS" ]]; then
      run_check "Backend xUnit regression" "dotnet-test.log" \
        dotnet test SmartX.sln --configuration Release --no-build \
        --logger 'console;verbosity=normal'
    else
      skip_check "Backend xUnit regression" "Release build failed"
    fi
  else
    skip_check ".NET formatting" "NuGet restore failed"
    skip_check "Backend Release build" "NuGet restore failed"
    skip_check "Backend xUnit regression" "NuGet restore failed"
  fi
else
  skip_check "NuGet restore and dependency audit" ".NET SDK not installed"
  skip_check ".NET formatting" ".NET SDK not installed"
  skip_check "Backend Release build" ".NET SDK not installed"
  skip_check "Backend xUnit regression" ".NET SDK not installed"
fi

# -------------------------- React client gates ------------------------------
cd "$REPO_ROOT/src/SmartX.Client" || exit 2
if command -v npm >/dev/null 2>&1; then
  run_check "Reproducible npm install" "npm-ci.log" npm ci
  npm_install_result="$LAST_RESULT"

  if [[ "$npm_install_result" == "PASS" ]]; then
    # An audit failure does not prevent the remaining static checks from being
    # recorded; all are independent evidence of their own gate outcome.
    run_check "npm high-severity audit" "npm-audit.log" npm audit --audit-level=high
    # Production-only auditing makes the boundary explicit: build tooling may
    # have different exposure from packages shipped to the browser.
    run_check "npm production dependency audit" "npm-prod-audit.log" \
      npm audit --omit=dev --audit-level=high
    run_check "Frontend ESLint" "npm-lint.log" npm run lint
    run_check "Frontend Prettier" "npm-format.log" npm run format:check
    run_check "Frontend production build" "npm-build.log" npm run build

    if command -v dotnet >/dev/null 2>&1; then
      run_check "Playwright Chromium installation" "playwright-install.log" \
        npx playwright install chromium
      browser_result="$LAST_RESULT"

      if [[ "$browser_result" == "PASS" ]]; then
        # Playwright manages its own temporary API and Vite processes. It does
        # not reuse a manually started dev server or change production state.
        run_check "Playwright real-API E2E suite" "playwright-test.log" npm run test:e2e
      else
        skip_check "Playwright real-API E2E suite" "Chromium install failed"
      fi
    else
      skip_check "Playwright Chromium installation" ".NET SDK unavailable for E2E"
      skip_check "Playwright real-API E2E suite" ".NET SDK unavailable for E2E"
    fi
  else
    skip_check "npm high-severity audit" "npm ci failed"
    skip_check "npm production dependency audit" "npm ci failed"
    skip_check "Frontend ESLint" "npm ci failed"
    skip_check "Frontend Prettier" "npm ci failed"
    skip_check "Frontend production build" "npm ci failed"
    skip_check "Playwright Chromium installation" "npm ci failed"
    skip_check "Playwright real-API E2E suite" "npm ci failed"
  fi
else
  skip_check "Reproducible npm install" "npm not installed"
  skip_check "npm high-severity audit" "npm not installed"
  skip_check "npm production dependency audit" "npm not installed"
  skip_check "Frontend ESLint" "npm not installed"
  skip_check "Frontend Prettier" "npm not installed"
  skip_check "Frontend production build" "npm not installed"
  skip_check "Playwright Chromium installation" "npm not installed"
  skip_check "Playwright real-API E2E suite" "npm not installed"
fi

# ------------------------ Docker Compose gates ------------------------------
# Compose configuration requires the CLI/plugin, not a running Docker daemon.
# Validate it independently so an inactive Docker Desktop cannot disguise a
# malformed Compose file. Only actual image building requires a live daemon.
cd "$REPO_ROOT" || exit 2
if ! command -v docker >/dev/null 2>&1; then
  skip_check "Docker Compose configuration" "Docker CLI unavailable"
  skip_check "Production Docker image builds" "Docker CLI unavailable"
elif ! docker compose version >/dev/null 2>&1; then
  skip_check "Docker Compose configuration" "Docker Compose plugin unavailable"
  skip_check "Production Docker image builds" "Docker Compose plugin unavailable"
elif ! command -v openssl >/dev/null 2>&1; then
  skip_check "Docker Compose configuration" "openssl unavailable for disposable key"
  skip_check "Production Docker image builds" "openssl unavailable for disposable key"
else
  # A disposable key is scoped to these commands only and is never echoed,
  # stored in the report, or committed. --quiet suppresses config expansion.
  disposable_key="$(openssl rand -base64 32)"
  run_check "Docker Compose configuration" "docker-config.log" \
    env "SMARTX_ATTACHMENT_ENCRYPTION_KEY_BASE64=$disposable_key" \
    docker compose config --quiet
  compose_result="$LAST_RESULT"

  if [[ "$compose_result" != "PASS" ]]; then
    skip_check "Production Docker image builds" "Compose configuration invalid"
  elif ! docker info >/dev/null 2>&1; then
    skip_check "Production Docker image builds" "Docker daemon unavailable (Compose config passed)"
  else
    run_check "Production Docker image builds" "docker-build.log" \
      env "SMARTX_ATTACHMENT_ENCRYPTION_KEY_BASE64=$disposable_key" \
      docker compose build
  fi
  unset disposable_key
fi

# --------------------- Render verified Markdown -----------------------------
# Retain the static historical reference and regenerate only the script-owned
# section, allowing the developer to safely rerun the checks after a fix.
STATIC_FILE="$TEMP_DIR/baseline-static.md"
awk '/^<!-- P3_BASELINE_RESULTS_BEGIN -->$/{exit}{print}' \
  "$BASELINE_FILE" > "$STATIC_FILE"

OUTFILE="$TEMP_DIR/BASELINE.md"
{
  cat "$STATIC_FILE"
  printf '<!-- P3_BASELINE_RESULTS_BEGIN -->\n\n'
  printf '## 5. Current local baseline results\n\n'
  printf '> **Local verification recorded:** %s (UTC). Results refer only to this checkout; they are not GitHub Actions results.\n\n' \
    "$(date -u '+%Y-%m-%d %H:%M:%S')"
  printf '| Git property | Observed value |\n| --- | --- |\n'
  printf '| Active branch | `%s` |\n' "$WORKING_BRANCH"
  printf '| Checked-out HEAD before P3-01 code changes | `%s` |\n' "$WORKING_SHA"
  printf '| Part 2 release ancestry | %s |\n' "$RELEASE_ANCESTRY"
  printf '| Local dev | `%s` |\n' "$(git rev-parse --short dev 2>/dev/null || echo unavailable)"
  printf '| Local main | `%s` |\n' "$(git rev-parse --short main 2>/dev/null || echo unavailable)"
  printf '\n### Tool versions\n\n'
  printf '| Tool | Observed version |\n| --- | --- |\n'
  printf '| Git | `%s` |\n' "$(get_version git --version)"
  printf '| .NET | `%s` |\n' "$(get_version dotnet --version)"
  printf '| Node.js | `%s` |\n' "$(get_version node --version)"
  printf '| npm | `%s` |\n' "$(get_version npm --version)"
  printf '| Docker CLI | `%s` |\n' "$(get_version docker --version)"
  printf '| Docker Compose | `%s` |\n' "$(get_version docker compose version)"
  printf '\n### Quality-gate results\n\n'
  printf '| Local check | Result |\n| --- | --- |\n'
  while IFS='|' read -r label status; do
    printf '| %s | **%s** |\n' "$label" "$status"
  done < "$RESULTS_FILE"

  printf '\n### Test-run summaries\n\n'
  if [[ -s "$TEMP_DIR/dotnet-test.log" ]]; then
    printf '**xUnit output summaries (as printed by the test runner):**\n\n```text\n'
    grep -E 'Passed!|Failed!|Total tests:|Passed:|Failed:' \
      "$TEMP_DIR/dotnet-test.log" | tail -n 24 || true
    printf '\n```\n\n'
  else
    printf '**xUnit:** No fresh test-run output was produced.\n\n'
  fi
  if [[ -s "$TEMP_DIR/playwright-test.log" ]]; then
    printf '**Playwright output summary:**\n\n```text\n'
    grep -E '([0-9]+ passed|[0-9]+ failed|[0-9]+ skipped|[0-9]+ flaky|Running [0-9]+ tests)' \
      "$TEMP_DIR/playwright-test.log" | tail -n 12 || true
    printf '\n```\n\n'
  else
    printf '**Playwright:** No fresh browser test-run output was produced.\n\n'
  fi

  printf '### Interpretation and follow-up\n\n'
  if grep -Eq '\|(FAIL|SKIPPED)' "$RESULTS_FILE"; then
    printf '**Baseline is not fully green.** Review the FAIL/SKIPPED results above, address or document the cause, then rerun the script before committing a claim of full verification.\n\n'
  else
    printf '**All checks executed by this script passed.** Final CI execution and production runtime verification are still required at the appropriate later issues.\n\n'
  fi
  printf 'No application source files were intentionally changed by this baseline script. Install/build tools may create ignored local artifacts such as `node_modules`, `bin`, `obj`, or `dist`. Docker image builds do not start containers or change the attachment volume.\n\n'
  printf '<!-- P3_BASELINE_RESULTS_END -->\n'
} > "$OUTFILE"

mv "$OUTFILE" "$BASELINE_FILE"
echo
echo "Baseline report updated: docs/part3/BASELINE.md"
echo "Inspect the report and git status before creating Commit 1."
