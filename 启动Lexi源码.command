#!/bin/bash
# ==============================================================================
# 启动Lexi源码.command - 双击或命令行直接从源码启动 Lexi (macOS)
# 兼容性: macOS Bash 3.2+
# ==============================================================================

DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$DIR"

# 1. 探测并定位 .NET SDK
# shellcheck source=/dev/null
source "$DIR/scripts/locate_dotnet.sh"
if ! locate_dotnet; then
    EXIT_CODE=1
else
    PROJECT_FILE="$DIR/lexi_avalonia/Lexi.csproj"
    echo "================================================================="
    echo "正在从源码启动 Lexi (Release 模式)..."
    echo "项目文件: $PROJECT_FILE"
    echo ".NET SDK: $DOTNET_BIN"
    echo "================================================================="

    set +e
    "$DOTNET_BIN" run --project "$PROJECT_FILE" -c Release "$@"
    EXIT_CODE=$?
    set -e
fi

# 如果是 Finder 双击启动的交互式终端，提示按键后再关闭窗口
if [ -t 0 ] && [ -n "$TERM" ] && [ -z "$NONINTERACTIVE" ]; then
    echo ""
    read -r -p "进程已结束 (退出码: $EXIT_CODE)。按回车键退出..." dummy </dev/tty || true
fi

exit $EXIT_CODE
