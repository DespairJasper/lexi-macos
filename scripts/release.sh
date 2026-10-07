#!/bin/bash
# ==============================================================================
# 合格发布唯一入口：完整回归 → 签名打包 → 包内隔离验收 → 许可证/隐私 → 写出合格证明。
# 任一阶段非零即停止，且**不写**合格证明；没有合格证明时 install_app.py 拒绝安装。
# 用法: CARGO_HOME=<tools>/cargo RUSTUP_HOME=<tools>/rustup bash scripts/release.sh
# ==============================================================================
set -uo pipefail
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP="${LEXI_RELEASE_APP:-$ROOT_DIR/dist/Lexi.app}"
FAILED=0
CODES="$(mktemp -d "${TMPDIR:-/tmp}/lexi-release-codes-XXXXXX")"
trap 'rm -rf "$CODES"' EXIT

# 先作废**旧**合格证明：否则一次失败的重跑之后，上一轮的证明仍然存在，
# 而它绑定的 bundle 可能已经被重新打包（同样的固定证书、同样的 DR，事后难以分辨）。
rm -f "$ROOT_DIR/dist/release-proof.json"

if [ -z "${CARGO_HOME:-}" ] || [ -z "${RUSTUP_HOME:-}" ]; then
    cat >&2 <<'MSG'
错误: 打包前置未导出。generate_fsrs_notices.py --check 必须读到**离线** Cargo 缓存与
      与 Cargo.lock 一致的 crate 校验和，否则打包在第 40 行左右的许可证门禁直接中断。
请显式导出（路径按本机工具链实际位置，开发期通常在工作根 .planning/fsrs-optimizer/tools 下）：
  export CARGO_HOME="<...>/tools/cargo"
  export RUSTUP_HOME="<...>/tools/rustup"
MSG
    exit 2
fi

stage() {
    local name="$1"; shift
    echo "================================================================="
    echo ">>> 阶段: $name"
    echo "================================================================="
    "$@"; local code=$?
    printf '%s: %s\n' "$name" "$code"
    printf '%s\n' "$code" > "$CODES/$name"
    if [ "$code" -ne 0 ]; then
        echo "错误: 阶段「${name}」退出码 ${code}，发布流程中止；不写合格证明。" >&2
        FAILED=1
    fi
    return 0
}

stage regression        bash "$ROOT_DIR/scripts/run_tests.sh"
[ "$FAILED" -eq 0 ] && stage package    bash "$ROOT_DIR/scripts/package_app.sh"
[ "$FAILED" -eq 0 ] && stage packaged   bash "$ROOT_DIR/scripts/verify_packaged_app.sh" "$APP"
[ "$FAILED" -eq 0 ] && stage license    python3 "$ROOT_DIR/scripts/generate_fsrs_notices.py" --check
[ "$FAILED" -eq 0 ] && stage privacy    python3 "$ROOT_DIR/scripts/verify_release_privacy.py" "$APP"

if [ "$FAILED" -ne 0 ]; then
    echo "发布未通过：不得安装。先修复上面失败阶段并重跑。" >&2
    exit 1
fi

# 传**实际**退出码，不硬编码 0：证明里必须留下可回溯的阶段证据，
# 否则把 stage() 改成吞掉某个阶段的失败，证明依然一路 0。
# 写证明失败必须是**致命**的：脚本末行若是 echo，write 非零会被吞掉，
# 于是打出"已生成"并返回 0，形成假成功信号（证明文件其实不存在）。
if ! python3 "$ROOT_DIR/scripts/release_proof.py" write --app "$APP" \
        --regression-exit "$(cat "$CODES/regression")" \
        --packaged-smoke-exit "$(cat "$CODES/packaged")" \
        --license-exit "$(cat "$CODES/license")" \
        --privacy-exit "$(cat "$CODES/privacy")"; then
    echo "错误: 合格证明写入失败，发布未完成。" >&2
    exit 1
fi
echo "合格证明已生成；现在可以按「正常退出应用 → 备份 → 安装」流程安装。"
exit 0
