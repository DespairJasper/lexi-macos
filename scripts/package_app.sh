#!/bin/bash
# ==============================================================================
# package_app.sh - 构建并打包独立 macOS 原生 Lexi.app
# 架构: 默认采用当前系统架构 (arm64 / x64)，支持自包含发布 (self-contained)
# 产物: 独立输出至 dist/Lexi.app，复用钥匙串中的固定签名证书
# ==============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$ROOT_DIR/lexi_avalonia"
CSPROJ="$PROJECT_DIR/Lexi.csproj"
DIST_DIR="$ROOT_DIR/dist"
APP_NAME="Lexi"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"

# 1. 探测并定位 .NET SDK
# shellcheck source=/dev/null
source "$SCRIPT_DIR/locate_dotnet.sh"
if ! locate_dotnet; then
    exit 1
fi

# 2. 确定目标架构 RID
TARGET_RID=""
for arg in "$@"; do
    case "$arg" in
        --rid=*)
            TARGET_RID="${arg#*=}"
            ;;
        osx-arm64|osx-x64)
            TARGET_RID="$arg"
            ;;
    esac
done

if [ -z "$TARGET_RID" ]; then
    ARCH="$(uname -m)"
    case "$ARCH" in
        arm64|aarch64)
            TARGET_RID="osx-arm64"
            ;;
        x86_64|amd64)
            TARGET_RID="osx-x64"
            ;;
        *)
            TARGET_RID="osx-arm64"
            ;;
    esac
fi

echo "================================================================="
echo "开始构建 $APP_NAME.app"
echo "目标运行时 RID: $TARGET_RID"
echo "发布配置: Release, Self-Contained"
echo "输出目录: $DIST_DIR"
echo "================================================================="

# 3. 准备临时发布目录
PUBLISH_TMP="$(mktemp -d /tmp/lexi_publish_XXXXXX)"
trap 'rm -rf "$PUBLISH_TMP"' EXIT

# 4. 执行 dotnet publish
echo ">>> 正在编译与自包含发布..."
"$DOTNET_BIN" publish "$CSPROJ" \
    -c Release \
    -r "$TARGET_RID" \
    --self-contained true \
    -o "$PUBLISH_TMP"

# 5. 确保 ICNS 图标就绪
ICNS_FILE="$ROOT_DIR/packaging/macOS/Lexi.icns"
if [ ! -f "$ICNS_FILE" ]; then
    echo ">>> 生成 macOS ICNS 图标..."
    bash "$ROOT_DIR/packaging/macOS/generate_icns.sh" "$PROJECT_DIR/Assets/icon.ico" "$ICNS_FILE"
fi

# 6. 组装 .app 包结构
echo ">>> 组装 macOS 应用程序包 ($APP_BUNDLE)..."
FINAL_BUNDLE="$APP_BUNDLE"
mkdir -p "$DIST_DIR"
STAGING_DIR="$(mktemp -d "$DIST_DIR/.lexi-staging-XXXXXX")"
APP_BUNDLE="$STAGING_DIR/Lexi.app"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources/Assets"
mkdir -p "$APP_BUNDLE/Contents/Resources/Notices"

# 复制发布二进制文件至 Contents/MacOS
# Data belongs in Resources; MacOS is a code-only directory for certificate signing.
for published in "$PUBLISH_TMP/"*; do
    published_name="$(basename "$published")"
    case "$published_name" in
        Assets|Notices)
            mkdir -p "$APP_BUNDLE/Contents/Resources/$published_name"
            cp -R "$published/." "$APP_BUNDLE/Contents/Resources/$published_name/"
            ;;
        *) cp -R "$published" "$APP_BUNDLE/Contents/MacOS/" ;;
    esac
done
# Preserve the application's existing resource paths without duplicating or signing media.
ln -s ../Resources/Assets "$APP_BUNDLE/Contents/MacOS/Assets"
ln -s ../Resources/Notices "$APP_BUNDLE/Contents/MacOS/Notices"

# 确保主执行程序权限
chmod +x "$APP_BUNDLE/Contents/MacOS/$APP_NAME"

# 复制 Info.plist 与 PkgInfo
PLIST_SRC="$ROOT_DIR/packaging/macOS/Info.plist"
if [ -f "$PLIST_SRC" ]; then
    cp "$PLIST_SRC" "$APP_BUNDLE/Contents/Info.plist"
else
    echo "错误: 未找到 $PLIST_SRC" >&2
    exit 1
fi
echo -n "APPL????" > "$APP_BUNDLE/Contents/PkgInfo"

# 复制图标
cp "$ICNS_FILE" "$APP_BUNDLE/Contents/Resources/Lexi.icns"

# 严格保留词典与版权声明文件 (双重保障: Contents/MacOS 与 Contents/Resources)
if [ -f "$PROJECT_DIR/Assets/dictionary.sqlite3" ]; then
    mkdir -p "$APP_BUNDLE/Contents/MacOS/Assets"
    cp "$PROJECT_DIR/Assets/dictionary.sqlite3" "$APP_BUNDLE/Contents/MacOS/Assets/"
    cp "$PROJECT_DIR/Assets/dictionary.sqlite3" "$APP_BUNDLE/Contents/Resources/Assets/"
fi

if [ -f "$PROJECT_DIR/Assets/icon.ico" ]; then
    mkdir -p "$APP_BUNDLE/Contents/MacOS/Assets"
    cp "$PROJECT_DIR/Assets/icon.ico" "$APP_BUNDLE/Contents/MacOS/Assets/"
    cp "$PROJECT_DIR/Assets/icon.ico" "$APP_BUNDLE/Contents/Resources/Assets/"
fi

if [ -d "$PROJECT_DIR/Notices" ]; then
    mkdir -p "$APP_BUNDLE/Contents/MacOS/Notices"
    cp -R "$PROJECT_DIR/Notices/"* "$APP_BUNDLE/Contents/MacOS/Notices/"
    cp -R "$PROJECT_DIR/Notices/"* "$APP_BUNDLE/Contents/Resources/Notices/"
fi

# 7. Persistent certificate identity keeps macOS permission mappings stable across upgrades.
ENTITLEMENTS="$ROOT_DIR/packaging/macOS/entitlements.plist"
echo ">>> 使用钥匙串中的固定代码签名身份..."
python3 "$SCRIPT_DIR/stable_signing.py" sign "$APP_BUNDLE" --entitlements "$ENTITLEMENTS"
python3 "$SCRIPT_DIR/verify_release_privacy.py" "$APP_BUNDLE"

# 8. Verify before replacing the previous locally generated build.
codesign --verify --deep --strict "$APP_BUNDLE"
if [ -d "$FINAL_BUNDLE" ]; then
    # Recoverable replacement without searchable duplicate app bundles.
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$FINAL_BUNDLE" || true
    /usr/bin/swift "$SCRIPT_DIR/trash_generated.swift" "$FINAL_BUNDLE"
fi
mv "$APP_BUNDLE" "$FINAL_BUNDLE"
/usr/bin/swift "$SCRIPT_DIR/trash_generated.swift" "$STAGING_DIR"
APP_BUNDLE="$FINAL_BUNDLE"

# 8. 校验包结构与完整性
echo "================================================================="
echo "✓ $APP_NAME.app 打包完成！"
echo "应用路径: $APP_BUNDLE"
TOTAL_SIZE="$(du -sh "$APP_BUNDLE" | cut -f1)"
echo "应用大小: $TOTAL_SIZE"
echo ""
echo "包内关键资源核对:"
echo "  - 可执行程序: $([ -x "$APP_BUNDLE/Contents/MacOS/$APP_NAME" ] && echo "✓ 正常" || echo "✗ 缺失")"
echo "  - Info.plist: $([ -f "$APP_BUNDLE/Contents/Info.plist" ] && echo "✓ 正常" || echo "✗ 缺失")"
echo "  - Lexi.icns:  $([ -f "$APP_BUNDLE/Contents/Resources/Lexi.icns" ] && echo "✓ 正常" || echo "✗ 缺失")"
echo "  - 词典数据:   $([ -f "$APP_BUNDLE/Contents/MacOS/Assets/dictionary.sqlite3" ] && echo "✓ 正常" || echo "✗ 缺失")"
echo "  - Notices:    $([ -d "$APP_BUNDLE/Contents/MacOS/Notices" ] && echo "✓ 正常" || echo "✗ 缺失")"
echo ""
echo "代码签名验证状态:"
codesign -dvvv "$APP_BUNDLE" 2>&1 | grep -E "Signature|Identifier|TeamIdentifier|Authority" || true
echo "本机固定证书签名；不是 Apple Developer ID，未进行 Apple 公证。"
echo "================================================================="
