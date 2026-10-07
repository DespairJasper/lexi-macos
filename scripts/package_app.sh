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

# Fixed native dependency notices/source archives are a release requirement.
# This is a development-only offline check; the packaged application needs no Rust runtime.
python3 "$SCRIPT_DIR/generate_fsrs_notices.py" --check

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

# 6b. 个人 FSRS-6 参数训练用的本地原生 helper
#     它由 Lexi.csproj 的 Content(Link=fsrs-optimizer) 复制到 publish 输出根目录，
#     上面的循环已经把它当作普通文件放进 Contents/MacOS —— 与 Lexi.dll 同级，
#     正是 MainWindow.MemoryResolveOptimizerHelperPath() 运行时要找的位置。
#     stable_signing.py 会对 Contents/** 下任何 Mach-O 做 inside-out 嵌套签名，无需在此手动签。
HELPER_PATH=""
for helper_name in fsrs-optimizer lexi-fsrs-optimizer; do
    if [ -f "$APP_BUNDLE/Contents/MacOS/$helper_name" ]; then
        HELPER_PATH="$APP_BUNDLE/Contents/MacOS/$helper_name"
        break
    fi
done

if [ -n "$HELPER_PATH" ]; then
    chmod +x "$HELPER_PATH"
    echo ">>> 已内置个人 FSRS 参数训练 helper：$(basename "$HELPER_PATH")"
    # 架构必须与主可执行一致：codesign --strict 可能放过一个跑不起来的二进制，运行期才失败。
    HELPER_ARCHS="$(lipo -archs "$HELPER_PATH" 2>/dev/null || echo unknown)"
    echo "    helper 架构: $HELPER_ARCHS"
    case " $HELPER_ARCHS " in
        *" arm64 "*) ;;
        *)
            echo "错误: helper 不含 arm64 切片（${HELPER_ARCHS}）；本机发布要求 arm64。" >&2
            exit 1
            ;;
    esac
    # Rust 构建若未做路径 remap，会把构建机的本地绝对路径编进二进制（隐私 + 可复现性问题）。
    # 这里把它当成**发布阻断**：泄漏一次就会被分发出去，事后无法收回。
    for leak in "$ROOT_DIR" "/Users/"; do
        if LC_ALL=C grep -q -a -F "$leak" "$HELPER_PATH"; then
            echo "错误: helper 内嵌了本地构建路径「${leak}」（Rust 路径 remap 未生效）。" >&2
            echo "      请在 native 构建脚本里加上 --remap-path-prefix，重新构建后再打包。" >&2
            exit 1
        fi
    done
    echo "    helper 未内嵌本地构建路径 ✓"

    # 出厂可执行体必须与**已审计**的那一个逐字节相同。
    # helper 不在 source-index / recovery-copies / source-invariants 任一面内，只断言"存在 + 含 arm64"
    # 就等于放行一个可被无声替换的 1.7MB 黑盒。签名会改写 Mach-O，因此本断言必须在签名之前。
    HELPER_PIN="$ROOT_DIR/packaging/fsrs-helper-pin.json"
    PIN_SHA="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['sha256'])" "$HELPER_PIN")"
    HELPER_SHA="$(shasum -a 256 "$HELPER_PATH" | awk '{print $1}')"
    if [ "$HELPER_SHA" != "$PIN_SHA" ]; then
        echo "错误: FSRS optimizer helper 与已审计 pin 不一致。" >&2
        echo "      包内: $HELPER_SHA" >&2
        echo "      pin : $PIN_SHA ($HELPER_PIN)" >&2
        echo "      重新构建 helper 后必须重新审计并显式更新 pin，不得直接发布。" >&2
        exit 1
    fi
    echo "    helper 与已审计 pin 逐字节一致 ✓"
    # 签名会改写 Mach-O，因此"包内 helper == pin"只能在签名前断言。
    # 把这一刻的指纹落盘，让 release_proof 能把"事后包内那份签名后二进制"与"被审计的那一份"连起来。
    printf '%s\n' "$HELPER_SHA" > "$DIST_DIR/fsrs-helper-presign.sha256"
else
    # **硬失败**，不是告警：helper 缺失时个人参数训练只会打一行 stderr 诊断，
    # 打包日志若也只是提醒，就等于放行了一个"看起来有、实际永远不会训练"的**缺 optimizer 伪完成包**。
    echo "错误: 未找到 FSRS optimizer helper（native/fsrs-optimizer/bin/fsrs-optimizer）。" >&2
    echo "      缺少它时个人 FSRS 参数训练永远不可用（始终使用官方默认参数），" >&2
    echo "      因此本包属于**未完成产物**，拒绝打包。" >&2
    echo "      先运行 scripts/build_fsrs_optimizer.sh 再打包。" >&2
    exit 1
fi

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

# 6c. 成品许可证闭环：断言进入发布包的 FSRS 许可证/源码归档确实是被复核过的那一份。
#     只打印"Notices 目录存在"是不够的——目录在、内容被换掉，同样是没验证。
NOTICES_PIN="$ROOT_DIR/packaging/fsrs-notices-pin.json"
python3 - "$NOTICES_PIN" "$APP_BUNDLE" "$ROOT_DIR" <<'PYEOF'
import hashlib, json, sys
from pathlib import Path
pin = json.loads(Path(sys.argv[1]).read_text())
bundle, root = Path(sys.argv[2]), Path(sys.argv[3])
failures = []
txt = pin['fsrs_optimizer_txt']
for base in ('Contents/MacOS/Notices', 'Contents/Resources/Notices'):
    target = bundle / base / 'FSRS-OPTIMIZER.txt'
    if not target.is_file():
        failures.append(f'缺失 {base}/FSRS-OPTIMIZER.txt'); continue
    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    if digest != txt['sha256']:
        failures.append(f'{base}/FSRS-OPTIMIZER.txt 哈希不符 {digest} != {txt["sha256"]}')
# 两侧都要查：只查 MacOS 侧时 Resources 侧可以被清空而无人发现。
for base in ('Contents/MacOS/Notices', 'Contents/Resources/Notices'):
    lic_dir = bundle / base / 'FSRS-Licenses'
    src_dir = bundle / base / 'FSRS-Sources'
    if not lic_dir.is_dir():
        failures.append(f'缺失 {base}/FSRS-Licenses'); continue
    entries = sorted(p.name for p in lic_dir.iterdir())
    if len(entries) != pin['license_file_count']:
        failures.append(f'{base}/FSRS-Licenses 顶层条目 {len(entries)} != {pin["license_file_count"]}')
    # 只数顶层条目会被"清空子目录、只留目录名"绕过，因此再数真实文件数并与源树比对。
    real = sum(1 for path in lic_dir.rglob('*') if path.is_file())
    if real != pin['license_real_file_count']:
        failures.append(f'{base}/FSRS-Licenses 实际文件数 {real} != {pin["license_real_file_count"]}')
    archives = sorted(p.name for p in src_dir.glob('*.crate')) if src_dir.is_dir() else []
    if len(archives) != pin['source_archive_count']:
        failures.append(f'{base}/FSRS-Sources 归档数 {len(archives)} != {pin["source_archive_count"]}')
    if archives != pin['source_archive_names']:
        failures.append(f'{base}/FSRS-Sources 归档名不符：{archives} != {pin["source_archive_names"]}')
# 源树里的那一份也必须与 pin 相同：源树**缺失**同样算失败，不能静默跳过
src = root / txt['path']
if not src.is_file():
    failures.append(f'源树缺少 {txt["path"]}（pin 的锚点不存在）')
elif hashlib.sha256(src.read_bytes()).hexdigest() != txt['sha256']:
    failures.append(f'源树 {txt["path"]} 哈希已漂移，需重新复核并更新 pin')
if failures:
    for f in failures:
        print('错误: FSRS 许可证闭环断言失败：' + f, file=sys.stderr)
    sys.exit(1)
print(f'    FSRS 许可证闭环 ✓（1 份主声明 + {pin["license_file_count"]} 顶层条目 / '
      f'{pin["license_real_file_count"]} 个许可证文件 + {pin["source_archive_count"]} 份 MPL 源码归档，两侧均已核对）')
PYEOF

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
echo "  - FSRS helper: ✓ 已内置 $(basename "$HELPER_PATH")"
echo ""
echo "代码签名验证状态:"
codesign -dvvv "$APP_BUNDLE" 2>&1 | grep -E "Signature|Identifier|TeamIdentifier|Authority" || true
echo "本机固定证书签名；不是 Apple Developer ID，未进行 Apple 公证。"
echo "================================================================="
