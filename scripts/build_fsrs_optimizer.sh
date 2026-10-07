#!/bin/bash
# ==============================================================================
# build_fsrs_optimizer.sh - 构建 Lexi 3.1.2 独立的 FSRS-6 参数优化器 Native Helper
# 目标架构: macOS arm64 (aarch64-apple-darwin, Apple Silicon)
# 工具链: 优先使用项目隔离目录下的 Rust 工具链 (rustc/cargo 1.99.0)，不污染全局环境
# 运行时: 无需 Rust / Python / Node / 外部动态库依赖
# ==============================================================================
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root_dir="$(cd "$script_dir/.." && pwd)"
native_dir="$root_dir/native/fsrs-optimizer"
planning_tools_dir="$(cd "$root_dir/../.planning/fsrs-optimizer/tools" 2>/dev/null && pwd || true)"

# 1. 定位并验证 cargo 工具链
cargo_bin=""
if [ -n "$planning_tools_dir" ] && [ -x "$planning_tools_dir/cargo/bin/cargo" ]; then
    export RUSTUP_HOME="$planning_tools_dir/rustup"
    export CARGO_HOME="$planning_tools_dir/cargo"
    export PATH="$CARGO_HOME/bin:$PATH"
    cargo_bin="$CARGO_HOME/bin/cargo"
elif [ -n "${CARGO_HOME:-}" ] && [ -x "$CARGO_HOME/bin/cargo" ]; then
    cargo_bin="$CARGO_HOME/bin/cargo"
elif command -v cargo >/dev/null 2>&1; then
    cargo_bin="$(command -v cargo)"
fi

if [ -z "$cargo_bin" ]; then
    echo "=================================================================" >&2
    echo "错误: 未找到可用的 cargo 编译器！" >&2
    echo "已检查路径:" >&2
    echo "  - $planning_tools_dir/cargo/bin/cargo" >&2
    echo "  - 环境变量 CARGO_HOME" >&2
    echo "  - 系统 PATH" >&2
    echo "请先在隔离目录安装 Rust 开发工具链。" >&2
    echo "=================================================================" >&2
    exit 1
fi

echo "使用 Rust 工具链: $cargo_bin"
"$cargo_bin" --version
rustc_bin="$(dirname "$cargo_bin")/rustc"
"$rustc_bin" --version

# 2. 隐私路径重映射 CARGO_ENCODED_RUSTFLAGS 配置 (防止二进制包含开发者用户名与本地绝对路径)
export CARGO_ENCODED_RUSTFLAGS="--remap-path-prefix=${root_dir}=/lexi-source"$'\x1f'"--remap-path-prefix=${planning_tools_dir}=/cargo-tools"$'\x1f'"--remap-path-prefix=${HOME}=/home-user"

# 3. 锁定构建原生 aarch64-apple-darwin release 二进制
target_arch="aarch64-apple-darwin"
echo "正在构建 native/fsrs-optimizer (profile: release, target: $target_arch, locked: true)..."
cd "$native_dir"
"$cargo_bin" build --release --locked --target "$target_arch" --manifest-path "$native_dir/Cargo.toml"

target_bin="$native_dir/target/$target_arch/release/fsrs-optimizer"
if [ ! -f "$target_bin" ]; then
    echo "错误: 构建产物未找到: $target_bin" >&2
    exit 1
fi

# 4. 部署并验证二进制
mkdir -p "$native_dir/bin"
cp "$target_bin" "$native_dir/bin/fsrs-optimizer"
chmod +x "$native_dir/bin/fsrs-optimizer"

echo "构建成功: $native_dir/bin/fsrs-optimizer"
ls -lh "$native_dir/bin/fsrs-optimizer"
file "$native_dir/bin/fsrs-optimizer"

# 5. 隐私与安全扫描 (确保二进制不泄漏本地用户名)
if [ -n "${USER:-}" ] && strings "$native_dir/bin/fsrs-optimizer" | grep -F "/Users/$USER" >/dev/null; then
    echo "警告: 检测到二进制中包含本地用户名路径，请检查 remap-path-prefix 配置！" >&2
    exit 1
else
    echo "隐私路径扫描通过: 二进制中未发现 /Users/$USER 绝对路径泄露。"
fi

# 6. 验证版本与探针
echo "验证版本与探针..."
"$native_dir/bin/fsrs-optimizer" --version
"$native_dir/bin/fsrs-optimizer" --probe

echo "================================================================="
echo "FSRS-6 Native Optimizer Helper 构建与验证完成！"
echo "================================================================="
