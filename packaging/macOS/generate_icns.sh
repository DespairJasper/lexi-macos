#!/bin/bash
# ==============================================================================
# generate_icns.sh - 将 icon.ico 转换为 macOS 原生 Lexi.icns 图标
# 兼容性: macOS Bash 3.2+
# ==============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

SOURCE_ICO="${1:-"$ROOT_DIR/lexi_avalonia/Assets/icon.ico"}"
OUTPUT_ICNS="${2:-"$SCRIPT_DIR/Lexi.icns"}"

if [ ! -f "$SOURCE_ICO" ]; then
    echo "错误: 未找到源图标文件: $SOURCE_ICO" >&2
    exit 1
fi

TMP_DIR="$(mktemp -d /tmp/lexi_icon_XXXXXX)"
trap 'rm -rf "$TMP_DIR"' EXIT

PNG_BASE="$TMP_DIR/base_icon.png"
ICONSET_DIR="$TMP_DIR/Lexi.iconset"
mkdir -p "$ICONSET_DIR"

# 1. 使用 macOS 原生 sips 提取高分辨率 PNG
sips -s format png "$SOURCE_ICO" --out "$PNG_BASE" >/dev/null 2>&1

# 2. 生成各标准尺寸图标
for size in 16 32 128 256 512; do
    size_2x=$((size * 2))
    sips -z "$size" "$size" "$PNG_BASE" --out "$ICONSET_DIR/icon_${size}x${size}.png" >/dev/null 2>&1
    sips -z "$size_2x" "$size_2x" "$PNG_BASE" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" >/dev/null 2>&1
done

# 3. 使用 iconutil 打包为 .icns
mkdir -p "$(dirname "$OUTPUT_ICNS")"
iconutil -c icns "$ICONSET_DIR" -o "$OUTPUT_ICNS"

if [ -f "$OUTPUT_ICNS" ]; then
    echo "成功生成 ICNS 图标: $OUTPUT_ICNS ($(stat -f%z "$OUTPUT_ICNS" 2>/dev/null || wc -c < "$OUTPUT_ICNS") 字节)"
else
    echo "错误: 生成 ICNS 图标失败。" >&2
    exit 1
fi
