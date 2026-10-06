#!/bin/bash
# ==============================================================================
# install_dotnet.sh - 独立非特权下载并安装官方 .NET 8 SDK
# 约束: 不使用 sudo，不修改系统 shell profile，仅在本地目录部署
# ==============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# 默认安装至项目内置 .tools/dotnet 或传入的第一参数
INSTALL_DIR="${1:-"$ROOT_DIR/.tools/dotnet"}"
CHANNEL="8.0"

# 检测架构
ARCH="$(uname -m)"
case "$ARCH" in
    arm64|aarch64)
        DOTNET_ARCH="arm64"
        ;;
    x86_64|amd64)
        DOTNET_ARCH="x64"
        ;;
    *)
        echo "警告: 未知系统架构 $ARCH，默认尝试 arm64"
        DOTNET_ARCH="arm64"
        ;;
esac

echo "================================================================="
echo "正在为 macOS ($DOTNET_ARCH) 本地安装 .NET $CHANNEL SDK"
echo "目标安装路径: $INSTALL_DIR"
echo "（无需管理员 sudo 权限，不修改 ~/.zshrc 或 ~/.bash_profile）"
echo "================================================================="

mkdir -p "$INSTALL_DIR"

INSTALL_SCRIPT="$ROOT_DIR/.tools/dotnet-install.sh"
if [ ! -f "$INSTALL_SCRIPT" ]; then
    INSTALL_SCRIPT="/tmp/dotnet-install-$$.sh"
    echo "正在从微软官方下载 dotnet-install.sh..."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_SCRIPT"
    chmod +x "$INSTALL_SCRIPT"
fi

bash "$INSTALL_SCRIPT" \
    --channel "$CHANNEL" \
    --install-dir "$INSTALL_DIR" \
    --architecture "$DOTNET_ARCH" \
    --no-path

echo "================================================================="
if [ -x "$INSTALL_DIR/dotnet" ]; then
    echo "✓ .NET SDK 安装成功！"
    "$INSTALL_DIR/dotnet" --info
    echo "================================================================="
    echo "提示: 您可以随时通过运行启动脚本直接调用该 SDK。"
else
    echo "错误: 安装完成但未在 $INSTALL_DIR 找到可执行文件 dotnet" >&2
    exit 1
fi
