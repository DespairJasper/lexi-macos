#!/usr/bin/env python3
"""Regression coverage for preserving the local social profile database on upgrade."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
BACKUP_SCRIPT = ROOT / "scripts/backup_user_data.sh"
INSTALLER_PATH = ROOT / "scripts/install_app.py"


def load_installer():
    spec = importlib.util.spec_from_file_location("lexi_install_app", INSTALLER_PATH)
    module = importlib.util.module_from_spec(spec)
    import sys
    sys.path.insert(0, str(INSTALLER_PATH.parent))
    spec.loader.exec_module(module)
    return module


class SocialBackupTests(unittest.TestCase):
    def test_backup_includes_social_database_and_manifest_hash(self):
        with tempfile.TemporaryDirectory(prefix="lexi-social-backup-test-") as temporary:
            root = Path(temporary)
            data = root / "data"
            backup_root = root / "backups"
            data.mkdir()
            import sqlite3
            for name, schema in (
                ("vocab.sqlite3", "CREATE TABLE words(id INTEGER); CREATE TABLE fsrs_cards(id INTEGER); CREATE TABLE canonical_reviews(id INTEGER);"),
                ("social.sqlite3", "CREATE TABLE local_social_state(value TEXT); INSERT INTO local_social_state VALUES('preserve-me');"),
            ):
                connection = sqlite3.connect(data / name)
                connection.executescript(schema)
                connection.commit()
                connection.close()
            (data / "daily-study-plans.json").write_text('{"keep":true}\n')
            before = {name: hashlib.sha256((data / name).read_bytes()).hexdigest()
                      for name in ("vocab.sqlite3", "social.sqlite3", "daily-study-plans.json")}
            env = os.environ.copy()
            env.update({"LEXI_USER_DATA_DIR": str(data), "LEXI_APP_BUNDLE": str(root / "no-app"), "HOME": str(root)})
            result = subprocess.run(["bash", str(BACKUP_SCRIPT), str(backup_root)], env=env,
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            match = re.search(r"^备份完成: (.+)$", result.stdout, re.MULTILINE)
            self.assertIsNotNone(match, result.stdout)
            backup = Path(match.group(1))
            self.assertTrue((backup / "social.sqlite3").is_file(), "social.sqlite3 missing from pre-install snapshot")
            self.assertIn("social.sqlite3", (backup / "MANIFEST.sha256").read_text())
            check = subprocess.run(["/usr/bin/shasum", "-a", "256", "-c", "MANIFEST.sha256"],
                                   cwd=backup, capture_output=True, text=True)
            self.assertEqual(check.returncode, 0, check.stdout + check.stderr)
            social_value = subprocess.run(["/usr/bin/sqlite3", str(backup / "social.sqlite3"),
                                           "SELECT value FROM local_social_state"],
                                          capture_output=True, text=True, check=True).stdout.strip()
            self.assertEqual(social_value, "preserve-me")
            self.assertEqual({name: hashlib.sha256((data / name).read_bytes()).hexdigest() for name in before}, before)

    def test_installer_requires_social_backup_when_user_data_has_it(self):
        with tempfile.TemporaryDirectory(prefix="lexi-social-installer-test-") as temporary:
            root = Path(temporary)
            data = root / "data"
            backup = root / "backup"
            data.mkdir()
            backup.mkdir()
            (data / "social.sqlite3").write_bytes(b"local social database")
            vocab = backup / "vocab.sqlite3"
            vocab.write_bytes(b"vocab snapshot")
            manifest = backup / "MANIFEST.sha256"
            digest = hashlib.sha256(vocab.read_bytes()).hexdigest()
            manifest.write_text(f"{digest}  vocab.sqlite3\n")
            (backup / "SNAPSHOT.json").write_text(json.dumps({
                "source_db_sha256": digest,
                "source_social_db_sha256": hashlib.sha256((data / "social.sqlite3").read_bytes()).hexdigest(),
                "app_mtime": 0,
            }))
            installer = load_installer()
            original = os.environ.get("LEXI_USER_DATA_DIR")
            os.environ["LEXI_USER_DATA_DIR"] = str(data)
            try:
                with self.assertRaisesRegex(RuntimeError, "social.sqlite3"):
                    installer.require_data_backup(backup)
                social = backup / "social.sqlite3"
                social.write_bytes((data / "social.sqlite3").read_bytes())
                digest_social = hashlib.sha256(social.read_bytes()).hexdigest()
                manifest.write_text(f"{digest}  vocab.sqlite3\n{digest_social}  social.sqlite3\n")
                installer.require_data_backup(backup)
            finally:
                if original is None:
                    os.environ.pop("LEXI_USER_DATA_DIR", None)
                else:
                    os.environ["LEXI_USER_DATA_DIR"] = original


if __name__ == "__main__":
    unittest.main(verbosity=2)
