#!/bin/bash
# ==============================================================================
# 一致快照备份：SQLite(.backup，WAL 安全) + 个人 JSON + 现有 app bundle + 校验和清单。
# 只读用户数据、不修改源文件；应用必须在退出状态（绝不强杀）。
# 用法: bash scripts/backup_user_data.sh [备份根目录]
# ==============================================================================
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
DATA_DIR="${LEXI_USER_DATA_DIR:-$HOME/Library/Application Support/Lexi}"
DEST_ROOT="${1:-$DATA_DIR/upgrade-backups}"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
mkdir -p "$DEST_ROOT"
FINAL="$DEST_ROOT/$STAMP-backup"
TMP="$(mktemp -d "$DEST_ROOT/.$STAMP-backup.tmp.XXXXXX")"
DB="$DATA_DIR/vocab.sqlite3"
APP="${LEXI_APP_BUNDLE:-/Applications/Lexi.app}"

# 运行判定必须覆盖"任意路径下的 Lexi.app + 带参数启动"：
# 只按 ^/Applications/Lexi.app/...$ 锚定会漏判从 dist/、~/Applications 或带参数启动的实例，
# 那样就会对一个正在写入的库做快照。
# 三条取并集：改名 bundle / 非 /Applications 副本 / 从源码经 dotnet 启动（启动Lexi源码.command 走这条）
# 都会命中。单条正则总有漏判面。
if ps -Ao command= | grep -E 'Lexi\.app/Contents/MacOS/Lexi' >/dev/null 2>&1 \
   || pgrep -x Lexi >/dev/null 2>&1 \
   || pgrep -f 'Lexi\.dll' >/dev/null 2>&1; then
    echo "错误: Lexi 正在运行（含非 /Applications 副本或带参数启动）。请先用 Command+Q 正常退出，再执行备份（绝不强杀进程）。" >&2
    rmdir "$TMP" 2>/dev/null || true
    exit 1
fi
if [ ! -f "$DB" ]; then
    echo "错误: 未找到词库 $DB" >&2
    rmdir "$TMP" 2>/dev/null || true
    exit 1
fi

chmod 700 "$TMP"
# 失败时在临时目录里留 FAILED 标记并保留（带点前缀，不会被"取最新备份"的逻辑选中）。
mark_failed() { printf '%s\n' "$1" > "$TMP/FAILED" 2>/dev/null || true; }
trap 'mark_failed "备份未完成：脚本在第 $LINENO 行中止"' ERR
DEST="$TMP"

# SQLite 一致性快照：.backup 走 SQLite 自己的备份 API，天然处理 WAL/并发，不用手工拼 cp。
/usr/bin/sqlite3 "$DB" ".backup '$DEST/vocab.sqlite3'"
INTEGRITY="$(/usr/bin/sqlite3 "$DEST/vocab.sqlite3" 'PRAGMA integrity_check;')"
if [ "$INTEGRITY" != "ok" ]; then
    echo "错误: 备份副本 integrity_check 未通过：$INTEGRITY" >&2
    exit 1
fi
# 备份副本必须与源库"同内容"：用 SQLite 自己的比较，避免只看文件大小。
SRC_COUNTS="$(/usr/bin/sqlite3 "$DB" "SELECT (SELECT COUNT(*) FROM canonical_reviews)||'/'||(SELECT COUNT(*) FROM fsrs_cards)||'/'||(SELECT COUNT(*) FROM words);")"
BAK_COUNTS="$(/usr/bin/sqlite3 "$DEST/vocab.sqlite3" "SELECT (SELECT COUNT(*) FROM canonical_reviews)||'/'||(SELECT COUNT(*) FROM fsrs_cards)||'/'||(SELECT COUNT(*) FROM words);")"
if [ "$SRC_COUNTS" != "$BAK_COUNTS" ]; then
    echo "错误: 备份副本关键表行数与源库不一致（源 $SRC_COUNTS / 备份 ${BAK_COUNTS}）。" >&2
    exit 1
fi
echo "词库一致快照 ✓ (canonical/cards/words = $SRC_COUNTS)"

# 源库的 WAL/SHM 现状（只记录，不移动）：让恢复方能判断备份时刻的日志状态。
for suffix in -wal -shm; do
    if [ -f "$DB$suffix" ]; then
        printf '%s %s\n' "$(stat -f%z "$DB$suffix")" "$(basename "$DB")$suffix" >> "$DEST/WAL-STATE.txt"
    fi
done
[ -f "$DEST/WAL-STATE.txt" ] || echo "(无 WAL/SHM 残留：快照时源库未处于活跃写入)" > "$DEST/WAL-STATE.txt"

# 个人 JSON（存在才拷）
for name in ielts-learning.json daily-study-plans.json; do
    [ -f "$DATA_DIR/$name" ] && cp -p "$DATA_DIR/$name" "$DEST/"
done

# 现有 app 私有副本（可恢复回滚）
if [ -d "$APP" ]; then
    /usr/bin/ditto "$APP" "$DEST/Lexi.previous.bundle"
fi

# 校验和清单
( cd "$DEST" && find . -type f ! -name MANIFEST.sha256 -print0 | sort -z | xargs -0 shasum -a 256 > MANIFEST.sha256 )
trap - ERR
# 只有全部步骤成功才改名为正式备份目录：半套备份绝不混进"最新备份"的命名空间。
# 目录已存在时必须显式失败：`mv dir existingdir` 会把 TMP **塞进** 已存在目录并返回 0，
# 于是脚本打印"备份完成"而该路径下其实没有 vocab.sqlite3。
if [ -e "$FINAL" ]; then
    mark_failed "目标备份目录已存在：$FINAL"
    echo "错误: 目标备份目录已存在（同一秒内重复运行？）：$FINAL" >&2
    exit 1
fi
mv "$TMP" "$FINAL"
echo "备份完成: $FINAL"
echo "校验: (cd \"$FINAL\" && shasum -a 256 -c MANIFEST.sha256)"
# 快照来源锚：安装门禁要求备份**晚于**待安装 bundle，否则一份半年前的备份也能当"前置"。
python3 - "$FINAL" "$APP" <<'PYEOF'
import json, os, sys, time
from pathlib import Path
snapshot, app = Path(sys.argv[1]), Path(sys.argv[2])
info = {
    'created_at_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
    'source_db_bytes': (snapshot / 'vocab.sqlite3').stat().st_size,
    'source_db_sha256': __import__('hashlib').sha256((snapshot / 'vocab.sqlite3').read_bytes()).hexdigest(),
    'app_bundle': str(app),
    'app_mtime': app.stat().st_mtime if app.exists() else None,
}
(snapshot / 'SNAPSHOT.json').write_text(json.dumps(info, ensure_ascii=False, indent=2) + '\n')
print('快照来源锚 SNAPSHOT.json 已写入')
PYEOF
echo "安装前置: python3 scripts/install_app.py --data-backup \"$FINAL\""
