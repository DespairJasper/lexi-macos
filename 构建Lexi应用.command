#!/bin/bash
# ==============================================================================
# 构建Lexi应用.command - 双击或命令行构建独立 macOS 原生 Lexi.app 包
# 兼容性: macOS Bash 3.2+
# 产物目录: dist/Lexi.app
# ==============================================================================

DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$DIR"

echo "================================================================="
echo "准备构建 Lexi.app 独立应用程序包..."
echo "================================================================="

set +e
bash "$DIR/scripts/package_app.sh" "$@"
EXIT_CODE=$?
set -e

if [ $EXIT_CODE -eq 0 ]; then
    echo ""
    echo "构建成功！应用程序位于:"
    echo "  $DIR/dist/Lexi.app"
    echo "您可以在 Finder 中进入 dist 目录并双击 Lexi.app 运行。"
else
    echo ""
    echo "构建出现错误 (退出码: $EXIT_CODE)。" >&2
fi

# 如果是 Finder 双击启动的交互式终端，提示按键后再关闭窗口
if [ -t 0 ] && [ -n "$TERM" ] && [ -z "$NONINTERACTIVE" ]; then
    echo ""
    read -r -p "按回车键退出..." dummy </dev/tty || true
fi

exit $EXIT_CODE
