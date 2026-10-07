#!/usr/bin/env python3
"""Audit a distributable app without printing local identifiers or credentials."""
import argparse
import ctypes
import ctypes.util
import os
from pathlib import Path
import socket
import sqlite3
import subprocess
from urllib.parse import urlsplit

def extended_metadata(path):
    if hasattr(os, "listxattr"):
        return [os.getxattr(path, name) for name in os.listxattr(path)]
    # Apple's system Python omits the Linux-only os xattr APIs.
    libc = ctypes.CDLL(ctypes.util.find_library("c"), use_errno=True)
    libc.listxattr.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int]
    libc.listxattr.restype = ctypes.c_ssize_t
    libc.getxattr.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_int]
    libc.getxattr.restype = ctypes.c_ssize_t
    encoded = os.fsencode(path)
    length = libc.listxattr(encoded, None, 0, 0)
    if length < 0:
        raise OSError(ctypes.get_errno(), "Cannot inspect release extended metadata")
    if not length:
        return []
    names = ctypes.create_string_buffer(length)
    if libc.listxattr(encoded, names, length, 0) < 0:
        raise OSError(ctypes.get_errno(), "Cannot enumerate release extended metadata")
    values = []
    for name in names.raw.split(b"\0"):
        if not name:
            continue
        size = libc.getxattr(encoded, name, None, 0, 0, 0)
        if size < 0:
            raise OSError(ctypes.get_errno(), "Cannot read release extended metadata")
        buffer = ctypes.create_string_buffer(size)
        read = libc.getxattr(encoded, name, buffer, size, 0, 0)
        if read < 0:
            raise OSError(ctypes.get_errno(), "Cannot read release extended metadata")
        values.append(buffer.raw[:read])
    return values


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("bundle", type=Path)
args = parser.parse_args()
local_values = {str(Path.home()), Path.home().name, socket.gethostname()}
for setting in ("ComputerName", "LocalHostName"):
    result = subprocess.run(["/usr/sbin/scutil", "--get", setting], capture_output=True, text=True)
    if result.returncode == 0 and result.stdout.strip():
        local_values.add(result.stdout.strip())
data_override = os.environ.get("LEXI_DATA_DIR")
data_root = Path(data_override).expanduser() if data_override else Path.home() / "Library/Application Support/Lexi"
database = data_root / "vocab.sqlite3"
if database.exists():
    with sqlite3.connect(f"file:{database}?mode=ro", uri=True) as connection:
        row = connection.execute(
            "SELECT json_extract(settings_json, '$.Provider'), "
            "json_extract(settings_json, '$.BaseUrl') FROM app_settings WHERE id=1"
        ).fetchone()
        if row:
            if row[0] == "custom":
                endpoint = row[1] or ""
                local_values.update(filter(None, (endpoint, urlsplit(endpoint).hostname)))
patterns = [b"sk-relay-", b"-----BEGIN PRIVATE KEY-----", b"-----BEGIN RSA PRIVATE KEY-----"]
for value in local_values:
    if len(value) >= 4:
        patterns += [value.encode("utf-8"), value.encode("utf-16-le"), value.encode("utf-16-be")]
patterns += ["sk-relay-".encode(encoding) for encoding in ("utf-16-le", "utf-16-be")]
violations = []
files = 0
for path in args.bundle.rglob("*"):
    if not path.is_file() or path.is_symlink():
        continue
    files += 1
    if path.suffix in {".pdb", ".p12", ".pfx", ".key"} or path.name in {"signing.json", "vocab.sqlite3"}:
        violations.append((path, "private config, signing key or debug symbols"))
    with path.open("rb") as source:
        tail = b""
        while chunk := source.read(1024 * 1024):
            content = tail + chunk
            if any(pattern in content for pattern in patterns):
                violations.append((path, "private identifier or credential literal"))
                break
            tail = content[-4096:]
    for metadata in extended_metadata(path):
        if any(pattern in metadata for pattern in patterns):
            violations.append((path, "private extended metadata"))
            break
for path, reason in violations:
    print("FAIL", path.relative_to(args.bundle), reason)
if violations:
    raise SystemExit(1)
print(f"PASS release privacy: {files} files; no local username/hostname/path, private endpoint, relay credential, private signing key or PDB")
