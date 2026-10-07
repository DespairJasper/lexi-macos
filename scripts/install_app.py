#!/usr/bin/env python3
"""Install at one canonical path, refusing unexpected signing identity changes."""
import argparse
from datetime import datetime
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import stable_signing as signing

DESTINATION = Path("/Applications/Lexi.app")
SOURCE = Path(__file__).resolve().parent.parent / "dist/Lexi.app"


PROOF = SOURCE.parent / "release-proof.json"


def cdhash(path):
    out = subprocess.run(["/usr/bin/codesign", "-dvvv", str(path)],
                         capture_output=True, text=True).stderr
    for line in out.splitlines():
        line = line.strip()
        if line.startswith("CDHash="):
            return line.split("=", 1)[1].strip()
    return ""


def require_data_backup(backup_dir):
    """用户数据的一致快照必须**先存在**，才允许替换 app。
    只备份旧 bundle 是不够的：装坏之后数据没有可回退点，而"先备份再安装"在此之前只是文档约定。"""
    if not backup_dir:
        raise RuntimeError(
            "拒绝安装：未提供用户数据备份目录。请先运行\n"
            "  bash scripts/backup_user_data.sh\n"
            "（要求 Lexi 已用 Command+Q 正常退出），再用 --data-backup <该目录> 重新执行安装。")
    folder = Path(backup_dir)
    manifest = folder / "MANIFEST.sha256"
    snapshot = folder / "vocab.sqlite3"
    if not snapshot.is_file() or not manifest.is_file():
        raise RuntimeError(f"拒绝安装：备份目录 {folder} 缺少 vocab.sqlite3 或 MANIFEST.sha256。")
    if snapshot.is_symlink():
        # 符号链接不是快照而是**活指针**：安装若写坏正式库，"备份"会同时被写坏。
        raise RuntimeError(f"拒绝安装：备份里的 vocab.sqlite3 是符号链接（{os.readlink(snapshot)}），不是快照。")
    check = subprocess.run(["/usr/bin/shasum", "-a", "256", "-c", manifest.name],
                           cwd=str(folder), capture_output=True, text=True)
    if check.returncode != 0:
        raise RuntimeError(f"拒绝安装：备份目录 {folder} 校验和不通过：{check.stdout}{check.stderr}")
    # 来源锚与时效：只保证"存在一个自洽目录"是不够的——它可能来自半年前或另一台机器，
    # 而这里的语义要求的是"这次安装**之前**的一致快照"。
    anchor = folder / "SNAPSHOT.json"
    if not anchor.is_file():
        raise RuntimeError(f"拒绝安装：备份目录 {folder} 缺少 SNAPSHOT.json 来源锚（请用 backup_user_data.sh 生成）。")
    try:
        info = json.loads(anchor.read_text())
    except ValueError as exc:
        raise RuntimeError(f"拒绝安装：SNAPSHOT.json 无法解析：{exc}") from exc
    if info.get("source_db_sha256") != hashlib.sha256(snapshot.read_bytes()).hexdigest():
        raise RuntimeError("拒绝安装：SNAPSHOT.json 记录的库哈希与备份内容不符。")
    if SOURCE.exists():
        source_mtime = SOURCE.stat().st_mtime
        if info.get("app_mtime") is None or info.get("app_mtime", 0) > source_mtime:
            raise RuntimeError("拒绝安装：备份是在待安装 bundle 生成之后才对 bundle 取的 mtime，来源不可信。")
        if os.path.getmtime(folder) < source_mtime:
            raise RuntimeError("拒绝安装：备份早于待安装 bundle（不是本次安装前拍的快照），请重新运行 backup_user_data.sh。")


def install(migrate=False, data_backup=None):
    signing.verify(SOURCE)
    # 合格证明门禁：证明**这一个** bundle（按 CDHash 绑定）已经过完整回归、包内隔离验收、
    # 许可证与隐私门禁，且包内 helper 与已审计 pin 逐字节一致。
    # 没有这道门禁时，一个旧的、不含 helper 的 dist/Lexi.app 会被静默装上去，
    # 且因为签名身份相同，事后无法从 DR 看出装错了。
    subprocess.run(
        [sys.executable, str(Path(__file__).resolve().parent / "release_proof.py"),
         "check", "--app", str(SOURCE)],
        check=True,
    )
    require_data_backup(data_backup)
    # 不能锚定 ^...$：带参数启动、改名 bundle、非 /Applications 副本、从源码经 dotnet 启动都会漏判，
    # 而它们都可能正在写同一个数据目录。
    probes = (
        ["/usr/bin/pgrep", "-f", r"Lexi\.app/Contents/MacOS/Lexi"],
        ["/usr/bin/pgrep", "-x", "Lexi"],
        ["/usr/bin/pgrep", "-f", r"Lexi\.dll"],
    )
    if any(subprocess.run(p, capture_output=True).returncode == 0 for p in probes):
        raise RuntimeError("Quit Lexi with Command+Q before installing. User data will not be modified.")
    new_requirement = signing.designated(SOURCE)
    if DESTINATION.exists():
        signing.run("/usr/bin/codesign", "--verify", "--deep", "--strict", str(DESTINATION))
        previous_requirement = signing.designated(DESTINATION)
        if previous_requirement != new_requirement:
            if not migrate or "cdhash" not in previous_requirement:
                raise RuntimeError("Signing identity changed. Update refused to protect permission mappings. Only the first ad-hoc-to-certificate migration may use --migrate-identity.")
            print("First migration from ad-hoc identity; one final Accessibility authorization is required.")
    backup_folder = Path.home() / "Library/Application Support/Lexi/upgrade-backups" / datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    backup_folder.mkdir(parents=True, mode=0o700)
    backup = backup_folder / "Lexi.previous.bundle"
    with tempfile.TemporaryDirectory(prefix=".lexi-install-", dir="/Applications") as temp:
        staging = Path(temp) / "Lexi.app"
        signing.run("/usr/bin/ditto", str(SOURCE), str(staging))
        if signing.verify(staging) != new_requirement:
            raise RuntimeError("Staged application identity does not match the verified source.")
        # 关掉 check → ditto 之间的 TOCTOU：签名身份相同不代表内容相同，
        # 必须比对**内容指纹**（CDHash），确认搬进来的就是被证明过的那一个 bundle。
        expected = json.loads(PROOF.read_text())["app"]["cdhash"]
        if cdhash(staging) != expected:
            raise RuntimeError(
                f"Staged bundle CDHash {cdhash(staging)} 与合格证明记录的 {expected} 不一致；拒绝安装。")
        if DESTINATION.exists():
            shutil.move(str(DESTINATION), str(backup))
        try:
            shutil.move(str(staging), str(DESTINATION))
            signing.verify(DESTINATION)
        except Exception:
            if DESTINATION.exists():
                shutil.move(str(DESTINATION), str(backup_folder / "Lexi.failed.bundle"))
            if backup.exists():
                shutil.move(str(backup), str(DESTINATION))
            raise
    print("PASS installed at /Applications/Lexi.app with a stable identity; prior app preserved privately")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--migrate-identity", action="store_true")
    parser.add_argument("--data-backup", help="backup_user_data.sh 产出的备份目录（安装前置，必填）")
    args = parser.parse_args()
    try:
        install(args.migrate_identity, args.data_backup)
    except (RuntimeError, OSError, subprocess.CalledProcessError) as exc:
        parser.exit(1, f"Installation stopped: {exc}\n")
