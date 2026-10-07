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

# UI 模式会真实启动主窗口并在自己的数据目录里写结果文件。
# 单实例保护会让"第二个实例"以退出码 0 静默返回，因此只信退出码会假报成功。
# 这里按模式核对结果文件确实存在、有 PASS、且没有 FAIL。
ui_result_file() {
    case "$1" in
        ui-smoke) echo "ui-smoke-result.txt" ;;
        visual-test) echo "visual-result.txt" ;;
        language-test) echo "language-result.txt" ;;
        focus-test) echo "focus-result.txt" ;;
        learning-test) echo "learning-ui-result.txt" ;;
        quick-test) echo "quick-test-result.txt" ;;
        recovery-restart-b) echo "recovery-restart-result.txt" ;;
        *) echo "" ;;
    esac
}

require_ui_evidence() {
    local mode="$1"
    local result
    result="$(ui_result_file "$mode")"
    [ -z "$result" ] && return 0
    local path="$RESULTS/$mode/$result"
    if [ ! -s "$path" ]; then
        echo "UI mode $mode produced no $result (blocked instance or silent exit); treating as failure."
        printf '%s: 1\n' "$mode-evidence" >> "$RESULTS/summary.txt"
        FAILED=1
        return 0
    fi
    if ! grep -q '^PASS' "$path"; then
        echo "UI mode $mode produced $result with no PASS line; treating as failure."
        printf '%s: 1\n' "$mode-evidence" >> "$RESULTS/summary.txt"
        FAILED=1
        return 0
    fi
    if grep -q '^FAIL' "$path"; then
        echo "UI mode $mode produced $result containing FAIL lines."
        printf '%s: 1\n' "$mode-evidence" >> "$RESULTS/summary.txt"
        FAILED=1
        return 0
    fi
    # ui-smoke 的结果文件是**增量**写的：只要跑到第一条断言就有 PASS 行。
    # 因此额外要求进度文件里出现关键阶段标记，否则"把记忆层那几组从 runner 里删掉"也能算绿。
    if [ "$mode" = "ui-smoke" ]; then
        local progress="$RESULTS/$mode/ui-smoke-progress.txt"
        for marker in foundation-ui-done recall-done update-notice-done final-regression-done; do
            if ! grep -q "$marker" "$progress" 2>/dev/null; then
                echo "UI mode $mode never reached phase $marker; treating as failure."
                printf '%s: 1\n' "$mode-evidence" >> "$RESULTS/summary.txt"
                FAILED=1
                return 0
            fi
        done
    fi
    printf '%s: 0 (%s PASS)\n' "$mode-evidence" "$(grep -c '^PASS' "$path")" >> "$RESULTS/summary.txt"
}

run_check integrity python3 "$SCRIPT_DIR/verify_source_integrity.py"
run_check build "$DOTNET_BIN" build "$PROJECT" -c Release
# Never execute stale binaries when compilation failed.
if ! grep -q '^build: 0$' "$RESULTS/summary.txt"; then
    echo "Build failed; evidence: $RESULTS"
    exit 1
fi
for suite in AiTests SelectionTests MacPlatformTests LearningTests QuoteTests GlassTests MemoryTests; do
    run_check "$suite" "$DOTNET_BIN" run --project "$ROOT_DIR/lexi_avalonia/tests/$suite/$suite.csproj" -c Release
 done
for mode in media-test self-test ui-smoke visual-test language-test focus-test learning-test quick-test; do
    mkdir -p "$RESULTS/$mode"
    run_check "$mode" env LEXI_DATA_DIR="$RESULTS/$mode" "$DOTNET_BIN" "$ROOT_DIR/lexi_avalonia/bin/Release/net8.0/Lexi.dll" "--$mode"
    require_ui_evidence "$mode"
 done
# 双进程重启恢复：A 阶段写现场后进程退出，B 阶段是另一次进程启动，只凭磁盘断言恢复结果。
# 两个阶段共用同一个数据目录；A 的结果文件同样纳入证据门禁，避免 A 静默失败后 B 报"没有现场"。
mkdir -p "$RESULTS/recovery-restart-b"
run_check recovery-restart-a env LEXI_DATA_DIR="$RESULTS/recovery-restart-b" "$DOTNET_BIN" "$ROOT_DIR/lexi_avalonia/bin/Release/net8.0/Lexi.dll" --recovery-restart-a
# A 阶段的报告与 B 阶段同目录（两阶段必须共用一个数据目录），因此单独门禁而不是复用 require_ui_evidence。
if grep -q '^PASS' "$RESULTS/recovery-restart-b/recovery-restart-phase-a.txt" 2>/dev/null \
    && ! grep -q '^FAIL' "$RESULTS/recovery-restart-b/recovery-restart-phase-a.txt" 2>/dev/null; then
    printf '%s: 0 (%s PASS)\n' recovery-restart-a-evidence "$(grep -c '^PASS' "$RESULTS/recovery-restart-b/recovery-restart-phase-a.txt")" >> "$RESULTS/summary.txt"
else
    echo "recovery-restart-a produced no PASS or contained FAIL; treating as failure."
    printf '%s: 1\n' recovery-restart-a-evidence >> "$RESULTS/summary.txt"
    FAILED=1
fi
run_check recovery-restart-b env LEXI_DATA_DIR="$RESULTS/recovery-restart-b" "$DOTNET_BIN" "$ROOT_DIR/lexi_avalonia/bin/Release/net8.0/Lexi.dll" --recovery-restart-b
require_ui_evidence recovery-restart-b
printf 'Evidence: %s\n' "$RESULTS"
exit "$FAILED"
