#!/bin/bash
# ==============================================================================
# locate_dotnet.sh - 探测并定位 macOS 本地可用 .NET 8 SDK
# 兼容性: macOS Bash 3.2+
# 优先级: .tools/dotnet -> PATH -> ~/.dotnet -> /usr/local/share/dotnet -> Homebrew
# ==============================================================================

locate_dotnet() {
    local candidate=""
    local script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local root_dir="$(cd "$script_dir/.." && pwd)"

    # 1. 优先检查源码树内部 .tools/dotnet
    if [ -x "$root_dir/.tools/dotnet/dotnet" ]; then
        candidate="$root_dir/.tools/dotnet/dotnet"
    # 2. 检查环境变量 DOTNET_ROOT
    elif [ -n "${DOTNET_ROOT:-}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
        candidate="$DOTNET_ROOT/dotnet"
    # 3. 检查系统 PATH
    elif command -v dotnet >/dev/null 2>&1; then
        candidate="$(command -v dotnet)"
    # 4. 检查用户主目录 ~/.dotnet
    elif [ -x "$HOME/.dotnet/dotnet" ]; then
        candidate="$HOME/.dotnet/dotnet"
    # 5. 检查系统通用安装路径 /usr/local/share/dotnet
    elif [ -x "/usr/local/share/dotnet/dotnet" ]; then
        candidate="/usr/local/share/dotnet/dotnet"
    # 6. 检查 Homebrew 路径
    elif [ -x "/opt/homebrew/bin/dotnet" ]; then
        candidate="/opt/homebrew/bin/dotnet"
    elif [ -x "/opt/homebrew/opt/dotnet/bin/dotnet" ]; then
        candidate="/opt/homebrew/opt/dotnet/bin/dotnet"
    elif [ -x "/usr/local/bin/dotnet" ]; then
        candidate="/usr/local/bin/dotnet"
    fi

    if [ -n "$candidate" ]; then
        export DOTNET_BIN="$candidate"
        export DOTNET_ROOT="$(cd "$(dirname "$candidate")" && pwd)"
        export PATH="$DOTNET_ROOT:$PATH"
        return 0
    else
        echo "=================================================================" >&2
        echo "错误: 未能在本地找到可用的 .NET SDK (需要 .NET 8.0)！" >&2
        echo "已检索路径:" >&2
        echo "  - $root_dir/.tools/dotnet/dotnet" >&2
        echo "  - 系统 PATH" >&2
        echo "  - $HOME/.dotnet/dotnet" >&2
        echo "  - /usr/local/share/dotnet/dotnet" >&2
        echo "  - /opt/homebrew/bin/dotnet" >&2
        echo "您可以运行以下独立安装脚本自动下载官方 .NET 8 SDK（无需 sudo）：" >&2
        echo "  bash \"$root_dir/scripts/install_dotnet.sh\"" >&2
        echo "或者访问微软官方下载手动安装: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
        echo "=================================================================" >&2
        return 1
    fi
}

# 若直接执行则打印定位结果，若 source 引入则导出变量
if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    if locate_dotnet; then
        echo "已定位 .NET: $DOTNET_BIN"
        echo "DOTNET_ROOT: $DOTNET_ROOT"
        "$DOTNET_BIN" --version
    else
        exit 1
    fi
fi
