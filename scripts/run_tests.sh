#!/bin/bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
source "$SCRIPT_DIR/locate_dotnet.sh"
locate_dotnet
PROJECT="$ROOT_DIR/lexi_avalonia/Lexi.csproj"
mkdir -p "$ROOT_DIR/verification"
RESULTS="$(mktemp -d "$ROOT_DIR/verification/run-$(date +%Y%m%d-%H%M%S)-XXXXXX")"
FAILED=0
run_check() {
    local name="$1"
    shift
    local code=0
    echo "Running $name"
    "$@" > "$RESULTS/$name.log" 2>&1 || code=$?
    cat "$RESULTS/$name.log"
    printf '%s: %s\n' "$name" "$code" >> "$RESULTS/summary.txt"
    if [ "$code" -ne 0 ]; then FAILED=1; fi
}
run_check integrity python3 "$SCRIPT_DIR/verify_source_integrity.py"
run_check build "$DOTNET_BIN" build "$PROJECT" -c Release
# Never execute stale binaries when compilation failed.
if ! grep -q '^build: 0$' "$RESULTS/summary.txt"; then
    echo "Build failed; evidence: $RESULTS"
    exit 1
fi
for suite in AiTests SelectionTests MacPlatformTests LearningTests QuoteTests GlassTests; do
    run_check "$suite" "$DOTNET_BIN" run --project "$ROOT_DIR/lexi_avalonia/tests/$suite/$suite.csproj" -c Release
 done
for mode in media-test self-test ui-smoke visual-test language-test focus-test learning-test quick-test; do
    mkdir -p "$RESULTS/$mode"
    run_check "$mode" env LEXI_DATA_DIR="$RESULTS/$mode" "$DOTNET_BIN" "$ROOT_DIR/lexi_avalonia/bin/Release/net8.0/Lexi.dll" "--$mode"
 done
printf 'Evidence: %s\n' "$RESULTS"
exit "$FAILED"
