#!/bin/bash
# ==============================================================================
# lexi · 卸载 / Uninstall
#
# 更新与覆盖安装**永远**保留用户数据 —— 本脚本只负责"卸载"这一步。
# 默认行为是**保留用户数据**：只把应用移入废纸篓，词库、学习记录、金句、
# 每日计划、IELTS 进度与全部内部记忆（FSRS 卡、个人参数、Context 校准、
# 轨迹与断点）都留在原处，重新安装后即可继续使用。
#
# 只有在这里明确选择"删除用户数据"并二次确认后，才会删除本应用自己的数据目录。
# 绝不触碰本应用数据目录以外的任何东西。
#
# 用法 / Usage:
#   double-click此文件（Finder 双击）
#   bash 卸载Lexi.command [--dry-run] [--keep-data|--delete-data] [--yes] [--app <path>]
# ==============================================================================
set -u

APP_NAME="Lexi"
BUNDLE_ID="com.lexi.app"
HOME_DIR="${HOME:-}"
SUPPORT_DIR="${HOME_DIR}/Library/Application Support"
DATA_DIR="${SUPPORT_DIR}/Lexi"
LEGACY_DIR="${SUPPORT_DIR}/cn.local.lexi"
KEYCHAIN_SERVICE="Lexi"

DRY_RUN=0
ASSUME_YES=0
MODE=""
APP_PATH=""

usage() {
    cat <<'TXT'
lexi 卸载工具
  --dry-run             只列出将要发生的事，不做任何改动
  --keep-data           保留用户数据（默认）
  --delete-data         删除用户数据（需要二次确认，或在非交互时配合 --yes）
  --yes                 跳过交互确认（仅与 --delete-data 一起使用时才有效）
  --app <path>          指定 Lexi.app 的路径（默认自动查找 /Applications）
  --help                显示本说明
TXT
}

while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRY_RUN=1 ;;
        --keep-data) MODE="keep" ;;
        --delete-data) MODE="delete" ;;
        --yes|-y) ASSUME_YES=1 ;;
        --app) [ $# -ge 2 ] || { echo "--app 需要一个路径。" >&2; exit 2; }; APP_PATH="$2"; shift ;;
        --help|-h) usage; exit 0 ;;
        *) echo "未知参数：$1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

fail() { echo "已中止：$1" >&2; exit 2; }

# ---- 安全护栏：任何删除动作之前必须全部通过 ----------------------------------
[ -n "${HOME_DIR}" ] || fail "无法确定用户主目录。"
case "${HOME_DIR}" in /|"") fail "主目录解析为 ${HOME_DIR}，拒绝继续。" ;; esac
case "${DATA_DIR}" in "${SUPPORT_DIR}"/*) : ;; *) fail "数据目录不在应用支持目录下：${DATA_DIR}" ;; esac
case "${LEGACY_DIR}" in "${SUPPORT_DIR}"/*) : ;; *) fail "旧数据目录不在应用支持目录下：${LEGACY_DIR}" ;; esac
[ "${DATA_DIR}" != "${SUPPORT_DIR}" ] || fail "数据目录解析异常。"

# ---- 定位应用 -----------------------------------------------------------------
# $HOME 被改写（沙箱/测试）时，绝不去动系统级的 /Applications —— 那里装的是登录用户的应用。
LOGIN_HOME="$(/usr/bin/dscl . -read "/Users/$(/usr/bin/id -un)" NFSHomeDirectory 2>/dev/null | /usr/bin/awk '{print $2}')"
SANDBOXED=0
if [ -n "${LOGIN_HOME}" ] && [ "${LOGIN_HOME}" != "${HOME_DIR}" ]; then
    SANDBOXED=1
fi
if [ -z "${APP_PATH}" ]; then
    if [ "${SANDBOXED}" -eq 0 ]; then
        for candidate in "/Applications/${APP_NAME}.app" "${HOME_DIR}/Applications/${APP_NAME}.app"; do
            if [ -d "${candidate}" ]; then APP_PATH="${candidate}"; break; fi
        done
    elif [ -d "${HOME_DIR}/Applications/${APP_NAME}.app" ]; then
        APP_PATH="${HOME_DIR}/Applications/${APP_NAME}.app"
    fi
fi
if [ -n "${APP_PATH}" ] && [ -d "${APP_PATH}" ]; then
    if [ -x /usr/libexec/PlistBuddy ]; then
        found_id="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${APP_PATH}/Contents/Info.plist" 2>/dev/null || true)"
        if [ -n "${found_id}" ] && [ "${found_id}" != "${BUNDLE_ID}" ]; then
            fail "${APP_PATH} 的 bundle id 是 ${found_id}，不是本应用的 ${BUNDLE_ID}。"
        fi
    fi
elif [ -n "${APP_PATH}" ]; then
    echo "提示：找不到 ${APP_PATH}，将只处理用户数据。"
    APP_PATH=""
fi

# ---- 运行中检查 ---------------------------------------------------------------
# 从源码启动（dotnet Lexi.dll）会写同一个数据目录，因此也算"正在运行"。
# 但必须排除本脚本自身与其父进程：脚本路径或参数里出现 "Lexi.dll" 时会误判成"应用在跑"。
lexi_running() {
    /usr/bin/pgrep -x "${APP_NAME}" >/dev/null 2>&1 && return 0
    local self=$$ parent=""
    parent="$(/bin/ps -o ppid= -p $$ 2>/dev/null | /usr/bin/tr -d ' ')"
    local pid
    for pid in $(/usr/bin/pgrep -f 'Lexi\.dll' 2>/dev/null || true); do
        [ "${pid}" = "${self}" ] && continue
        [ -n "${parent}" ] && [ "${pid}" = "${parent}" ] && continue
        return 0
    done
    return 1
}
if [ "${DRY_RUN}" -eq 0 ] && lexi_running; then
    fail "lexi 正在运行。请先从菜单栏退出 lexi，再运行本工具。"
fi

# ---- 盘点 ---------------------------------------------------------------------
echo "=============================================================="
echo " lexi 卸载工具 / Uninstaller"
echo "=============================================================="
echo "应用：      ${APP_PATH:-（未找到，将跳过）}"
[ "${SANDBOXED}" -eq 1 ] && echo "注意：       HOME 不是登录用户主目录，已跳过 /Applications 探测。"
echo "用户数据：  ${DATA_DIR}"
echo "旧版数据：  ${LEGACY_DIR}"
echo "钥匙串条目：service = ${KEYCHAIN_SERVICE}"
echo
if [ -d "${DATA_DIR}" ]; then
    echo "用户数据目录当前内容："
    ls -la "${DATA_DIR}" 2>/dev/null | sed 's/^/    /'
    echo "    合计：$(du -sh "${DATA_DIR}" 2>/dev/null | awk '{print $1}')"
else
    echo "（尚未创建用户数据目录）"
fi
echo

# ---- 选择：默认保留 -----------------------------------------------------------
if [ -z "${MODE}" ]; then
    if [ -t 0 ] && [ "${ASSUME_YES}" -eq 0 ]; then
        echo "请选择卸载方式："
        echo "  1) 保留用户数据（推荐，默认）— 只移除应用，数据留给下次安装使用"
        echo "  2) 删除用户数据            — 同时删除上面的数据目录与钥匙串中的 API Key"
        printf "输入 1 或 2 [默认 1]："
        read -r choice || choice=""
        case "${choice:-1}" in
            1|"") MODE="keep" ;;
            2) MODE="delete" ;;
            *) fail "无法识别的选择：${choice}" ;;
        esac
    else
        MODE="keep"
    fi
fi

if [ "${MODE}" = "delete" ]; then
    echo
    echo "⚠️  将永久删除以下本应用数据："
    [ -d "${DATA_DIR}" ] && echo "    - ${DATA_DIR}（词库、备份、每日计划、IELTS 进度、全部内部记忆）"
    [ -d "${LEGACY_DIR}" ] && echo "    - ${LEGACY_DIR}（旧版数据目录）"
    echo "    - 钥匙串 service=${KEYCHAIN_SERVICE} 下的条目（API Key）"
    if [ "${ASSUME_YES}" -eq 0 ]; then
        [ -t 0 ] || fail "非交互环境下删除数据必须显式加 --yes。"
        printf "确认删除请输入 DELETE（其它任何输入都会取消）："
        read -r confirm || confirm=""
        [ "${confirm}" = "DELETE" ] || fail "未确认，已取消。未删除任何数据。"
    fi
fi

if [ "${DRY_RUN}" -eq 1 ]; then
    echo
    echo "[dry-run] 模式：${MODE}。未做任何改动。"
    [ "${MODE}" = "delete" ] && echo "[dry-run] 会删除：${DATA_DIR}、${LEGACY_DIR}（若存在）、钥匙串 service=${KEYCHAIN_SERVICE}"
    [ -n "${APP_PATH}" ] && echo "[dry-run] 会移入废纸篓：${APP_PATH}"
    exit 0
fi

# ---- 执行：移入废纸篓（可恢复）------------------------------------------------
if [ -n "${APP_PATH}" ]; then
    TRASH_DIR="${HOME_DIR}/.Trash"
    mkdir -p "${TRASH_DIR}"
    DEST="${TRASH_DIR}/$(basename "${APP_PATH}")"
    if [ -e "${DEST}" ]; then
        DEST="${TRASH_DIR}/${APP_NAME}.app.$(date +%Y%m%d-%H%M%S)"
    fi
    if mv "${APP_PATH}" "${DEST}" 2>/dev/null; then
        echo "已移入废纸篓：${DEST}"
    else
        echo "未能自动移除应用。请手动把 ${APP_PATH} 拖到废纸篓。" >&2
    fi
fi

# ---- 执行：按选择处理数据 -----------------------------------------------------
if [ "${MODE}" = "keep" ]; then
    if [ -d "${DATA_DIR}" ]; then
        echo "已保留用户数据：${DATA_DIR}"
        echo "重新安装 lexi 后，原有词库、学习记录与记忆状态可继续使用。"
    else
        echo "无用户数据需要保留。"
    fi
else
    for target in "${DATA_DIR}" "${LEGACY_DIR}"; do
        case "${target}" in
            "${SUPPORT_DIR}"/*) : ;;
            *) fail "拒绝删除越界路径：${target}" ;;
        esac
        if [ -e "${target}" ]; then
            rm -rf -- "${target}" && echo "已删除：${target}"
        fi
    done
    # 钥匙串 account 是确定值：api-key:<数据目录绝对路径>。
    # 只按这两个精确 account 删除，不做枚举，也就不会碰到任何别的条目。
    for account in "api-key:${DATA_DIR}" "api-key:${LEGACY_DIR}"; do
        case "${account}" in
            "api-key:${SUPPORT_DIR}"/*) : ;;
            *) fail "拒绝删除越界钥匙串条目：${account}" ;;
        esac
        if /usr/bin/security find-generic-password -s "${KEYCHAIN_SERVICE}" -a "${account}" >/dev/null 2>&1; then
            if /usr/bin/security delete-generic-password -s "${KEYCHAIN_SERVICE}" -a "${account}" >/dev/null 2>&1; then
                echo "已删除钥匙串条目：service=${KEYCHAIN_SERVICE}"
            else
                echo "未能删除钥匙串条目，请在“钥匙串访问”中手动删除 service=${KEYCHAIN_SERVICE}。" >&2
            fi
        fi
    done
    echo "用户数据已删除。"
fi

echo
echo "提示：辅助功能授权由系统管理，可在“系统设置 → 隐私与安全性 → 辅助功能”中手动关闭。"
echo "完成。"
