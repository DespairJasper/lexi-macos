#!/bin/bash
# ==============================================================================
# 包内隔离验收：对**打包并签名后的 .app**（不是源码构建输出）做真实验收。
# 覆盖：结构/签名/DR/bundle id、包内 helper 是否**能真的执行**且自报被审计的 provenance、
#       在独立 LEXI_DATA_DIR 下真跑一个"要求隔离目录"的模式、隐私扫描、正式数据目录零改动。
# 用法: bash scripts/verify_packaged_app.sh [path/to/Lexi.app]
# ==============================================================================
set -uo pipefail
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP="${1:-$ROOT_DIR/dist/Lexi.app}"
PIN="$ROOT_DIR/packaging/release-pin.json"
HELPER_PIN="$ROOT_DIR/packaging/fsrs-helper-pin.json"
EVAL_DIR="$(mktemp -d "${TMPDIR:-/tmp}/lexi-packaged-accept-XXXXXX")"
FAILED=0
pass() { printf 'PASS: %s\n' "$1"; }
fail() { printf 'FAIL: %s\n' "$1"; FAILED=1; }
pin() { python3 -c "import json,sys;print(json.load(open(sys.argv[1]))[sys.argv[2]])" "$PIN" "$1"; }
hpin() { python3 -c "import json,sys;print(json.load(open(sys.argv[1]))[sys.argv[2]])" "$HELPER_PIN" "$1"; }

[ -d "$APP" ] || { echo "错误: 未找到 $APP" >&2; exit 1; }

# 1) 结构与签名
for rel in Contents/MacOS/Lexi Contents/Info.plist Contents/Resources/Lexi.icns Contents/MacOS/Notices; do
    [ -e "$APP/$rel" ] && pass "包内存在 $rel" || fail "包内缺失 $rel"
done
if codesign --verify --deep --strict "$APP" >/dev/null 2>&1; then pass "codesign --verify --deep --strict"; else fail "codesign --verify --deep --strict"; fi

BUNDLE_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Contents/Info.plist" 2>/dev/null || echo '')"
[ "$BUNDLE_ID" = "$(pin bundle_id)" ] && pass "bundle id = $BUNDLE_ID" || fail "bundle id 不符：$BUNDLE_ID"
DR="$( { codesign -d -r- "$APP" 2>&1 || true; } | sed -n 's/^designated => //p')"
[ "$DR" = "$(pin designated_requirement)" ] && pass "designated requirement 与 pin 一致" || fail "DR 不符：$DR"

# 2) helper：随包、可执行、arm64、只依赖系统库、嵌套签名有效，并且**签名后仍能真的跑起来**
HELPER="$APP/$(pin helper_relpath)"
if [ -x "$HELPER" ]; then
    pass "helper 随包且可执行（$(basename "$HELPER")）"
    ARCHS="$(lipo -archs "$HELPER" 2>/dev/null || echo unknown)"
    case " $ARCHS " in *" arm64 "*) pass "helper 架构含 arm64（${ARCHS}）";; *) fail "helper 架构不含 arm64（${ARCHS}）";; esac
    NON_SYSTEM="$(otool -L "$HELPER" 2>/dev/null | tail -n +2 | awk '{print $1}' | grep -v '^/usr/lib/' | grep -v '^/System/' || true)"
    [ -z "$NON_SYSTEM" ] && pass "helper 只依赖系统动态库" || fail "helper 依赖非系统动态库：$NON_SYSTEM"
    codesign --verify --strict "$HELPER" >/dev/null 2>&1 && pass "helper 嵌套签名有效" || fail "helper 嵌套签名无效"

    # 签名会改写 Mach-O；"签名后还能执行且仍自报被审计的 provenance"是唯一能证明这一点的检查。
    # 之前的门禁从不执行包内 helper，"能跑"只是假设（entitlements 里还有 disable-library-validation）。
    if "$HELPER" --probe > "$EVAL_DIR/helper-probe.json" 2>"$EVAL_DIR/helper-probe.err"; then
        pass "签名后的包内 helper 可执行（--probe exit 0）"
        python3 - "$EVAL_DIR/helper-probe.json" "$HELPER_PIN" <<'PYEOF'
import json, sys
probe = json.load(open(sys.argv[1]))
pin = json.load(open(sys.argv[2]))
expect = {
    'helper_version': None, 'protocol': pin['protocol'], 'algorithm': pin['algorithm'],
    'protocol_version': 1, 'upstream_version': pin['fsrs_version'], 'upstream_git_sha': pin['upstream_sha'],
    'compatibility_id': pin['compatibility_id'], 'patch_sha256': pin['patch_sha256'],
    'fixed_seed': 2023, 'max_sequence_length': 64,
}
bad = []
for key, want in expect.items():
    got = probe.get(key)
    if want is None:
        if not got: bad.append(f'{key} 缺失')
    elif got != want:
        bad.append(f'{key}={got!r} != pin {want!r}')
if len(probe.get('default_weights') or []) != pin['parameter_count']:
    bad.append(f"default_weights 长度 {len(probe.get('default_weights') or [])} != {pin['parameter_count']}")
if bad:
    print('      helper --probe provenance 不符：' + '; '.join(bad), file=sys.stderr)
    sys.exit(1)
print(f"      helper --probe provenance ✓（21 维 / FSRS-6 / {pin['compatibility_id']}）")
PYEOF
        [ $? -eq 0 ] && pass "helper --probe provenance 与 pin 一致" || fail "helper --probe provenance 与 pin 不符"
    else
        fail "签名后的包内 helper 无法执行（--probe 非零退出，见 $EVAL_DIR/helper-probe.err）"
    fi
else
    fail "包内未找到 helper：$HELPER"
fi

# 3) 隔离实跑：必须用一个**要求** LEXI_DATA_DIR 的模式（--self-test 会自行覆盖该变量，不能作为隔离证据）
REAL_DIR="${LEXI_REAL_DATA_DIR:-$HOME/Library/Application Support/Lexi}"
REAL_DB="$REAL_DIR/vocab.sqlite3"
snapshot_data_dir() {
    if [ ! -d "$REAL_DIR" ]; then echo "no-data-dir"; return; fi
    ( cd "$REAL_DIR" && find . -type f \
        -not -path './backups/*' -not -path './upgrade-backups/*' -not -path './private-*' \
        -not -name '*.log' -not -path './__pycache__/*' -print0 \
      | sort -z | xargs -0 shasum -a 256 | shasum -a 256 | awk '{print $1}' )
}
if [ ! -f "$REAL_DB" ]; then
    fail "正式数据目录里没有 vocab.sqlite3（${REAL_DB}）——'零改动'断言无从谈起，必须显式失败而不是恒真通过"
else
    pass "正式数据目录存在（${REAL_DB}）"
fi
BEFORE="$(snapshot_data_dir)"

ISO="$EVAL_DIR/isolated"
mkdir -p "$ISO"
if LEXI_DATA_DIR="$ISO" "$APP/Contents/MacOS/Lexi" --ui-smoke > "$EVAL_DIR/ui-smoke.log" 2>&1; then
    pass "包内隔离 ui-smoke exit 0"
else
    fail "包内隔离 ui-smoke 非零退出（见 $EVAL_DIR/ui-smoke.log）"
fi
RESULT="$ISO/ui-smoke-result.txt"
if [ -f "$RESULT" ]; then
    N_PASS="$(grep -c '^PASS' "$RESULT" || true)"
    if [ "$N_PASS" -gt 0 ] && ! grep -q '^FAIL' "$RESULT"; then
        pass "隔离实跑产出 $N_PASS 条 PASS 且无 FAIL"
    else
        fail "隔离实跑结果异常（PASS=${N_PASS}，含 FAIL 或全空）"
    fi
else
    fail "隔离模式下没有产出 ui-smoke-result.txt（结果文件缺失按失败处理）"
fi
# 结果文件是**增量**写的：跑到第一条断言就有 PASS 行。因此必须像 run_tests.sh 一样
# 额外要求进度文件出现关键阶段标记——否则"把记忆层那几组从 runner 里删掉"也能算绿，
# 包内这一关会比源码回归那一关弱。
for marker in foundation-ui-done recall-done final-regression-done; do
    if grep -q "$marker" "$ISO/ui-smoke-progress.txt" 2>/dev/null; then
        pass "隔离实跑到达阶段标记 $marker"
    else
        fail "隔离实跑未到达阶段标记 $marker（结果文件可能只写了几条就中断）"
    fi
done
[ -f "$ISO/vocab.sqlite3" ] && pass "隔离目录内确实建了库（LEXI_DATA_DIR 生效）" || fail "隔离目录内没有建库，LEXI_DATA_DIR 可能未生效"

# --self-test 无头运行只作为附加信号（它自己会覆盖 LEXI_DATA_DIR，不承担隔离证据）
if LEXI_DATA_DIR="$EVAL_DIR/selftest" "$APP/Contents/MacOS/Lexi" --self-test > "$EVAL_DIR/self-test.log" 2>&1 \
   && grep -q '15/15' "$EVAL_DIR/self-test.log"; then
    pass "包内 --self-test 15/15 通过"
else
    fail "包内 --self-test 未达 15/15（见 $EVAL_DIR/self-test.log）"
fi

AFTER="$(snapshot_data_dir)"
[ "$BEFORE" = "$AFTER" ] && pass "正式数据目录零改动（整目录快照一致，排除 backups/ 与日志）" || fail "正式数据目录被改动：$BEFORE -> $AFTER"

# 4) 隐私扫描
if python3 "$ROOT_DIR/scripts/verify_release_privacy.py" "$APP" > "$EVAL_DIR/privacy.log" 2>&1; then
    pass "隐私扫描通过"
else
    fail "隐私扫描未通过（见 $EVAL_DIR/privacy.log）"
fi

echo "验收证据目录: $EVAL_DIR"
if [ "$FAILED" -ne 0 ]; then echo "包内验收失败。" >&2; exit 1; fi
echo "包内隔离验收全部通过。"
