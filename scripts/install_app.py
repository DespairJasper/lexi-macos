#!/usr/bin/env python3
"""Install at one canonical path, refusing unexpected signing identity changes."""
import argparse
from datetime import datetime
from pathlib import Path
import shutil
import subprocess
import tempfile
import stable_signing as signing

DESTINATION = Path("/Applications/Lexi.app")
SOURCE = Path(__file__).resolve().parent.parent / "dist/Lexi.app"


def install(migrate=False):
    signing.verify(SOURCE)
    running = subprocess.run(["/usr/bin/pgrep", "-f", r"^/Applications/Lexi\.app/Contents/MacOS/Lexi$"], capture_output=True)
    if running.returncode == 0:
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
    args = parser.parse_args()
    try:
        install(args.migrate_identity)
    except (RuntimeError, OSError, subprocess.CalledProcessError) as exc:
        parser.exit(1, f"Installation stopped: {exc}\n")
