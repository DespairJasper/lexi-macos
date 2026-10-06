#!/usr/bin/env python3
"""Persistent local signing identity; private key stays in the user's Keychain."""
import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import secrets
import subprocess
import tempfile

BUNDLE_ID = "com.lexi.app"
STATE = Path.home() / "Library/Application Support/LexiBuild/signing.json"


def run(*args, capture=False, env=None):
    result = subprocess.run(args, text=True, capture_output=True, env=env)
    if result.returncode:
        # Do not dump command arguments; Keychain import may use a transient wrapping password.
        raise RuntimeError(result.stderr.strip() or result.stdout.strip() or "Command failed")
    return result.stdout if capture else None


def setup():
    if STATE.exists():
        load()
        print("PASS reusing existing Keychain signing identity")
        return
    installed = Path("/Applications/Lexi.app")
    if installed.exists():
        dr = designated(installed)
        if "certificate" in dr or "anchor" in dr:
            raise RuntimeError("Installed Lexi already has a certificate identity. Restore its signing.json and Keychain key; do not create a replacement identity.")
    STATE.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    keychain = run("/usr/bin/security", "default-keychain", "-d", "user", capture=True).strip().strip('"')
    with tempfile.TemporaryDirectory(prefix="lexi-signing-") as temp:
        folder = Path(temp)
        os.chmod(folder, 0o700)
        config = folder / "certificate.conf"
        config.write_text("[req]\ndistinguished_name=dn\nx509_extensions=signing\nprompt=no\n[dn]\nCN=Lexi Local Code Signing\n[signing]\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=critical,codeSigning\nsubjectKeyIdentifier=hash\n")
        key, cert, p12 = (folder / name for name in ("key.pem", "certificate.pem", "identity.p12"))
        run("/usr/bin/openssl", "req", "-new", "-x509", "-newkey", "rsa:3072", "-nodes", "-days", "36500", "-config", str(config), "-keyout", str(key), "-out", str(cert))
        os.chmod(key, 0o600)
        wrapping_password = secrets.token_hex(32)
        export_env = dict(os.environ, LEXI_SIGNING_WRAP_PASSWORD=wrapping_password)
        run("/usr/bin/openssl", "pkcs12", "-export", "-inkey", str(key), "-in", str(cert), "-name", "Lexi Local Code Signing", "-passout", "env:LEXI_SIGNING_WRAP_PASSWORD", "-out", str(p12), env=export_env)
        os.chmod(p12, 0o600)
        # No all-applications ACL. Import the key as non-extractable, permitting only codesign.
        run("/usr/bin/security", "import", str(p12), "-k", keychain, "-f", "pkcs12", "-P", wrapping_password, "-x", "-T", "/usr/bin/codesign")
        fingerprint = run("/usr/bin/openssl", "x509", "-in", str(cert), "-noout", "-fingerprint", "-sha1", capture=True).split("=", 1)[1].replace(":", "").strip().upper()
        public_cert = STATE.parent / "signing-certificate.pem"
        public_cert.write_bytes(cert.read_bytes())
        metadata = {"identity": fingerprint, "keychain": keychain, "bundle_id": BUNDLE_ID}
        # Create exclusively; an established identity must never be silently replaced.
        fd = os.open(STATE, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w") as out:
            json.dump(metadata, out, indent=2)
    load()
    print("PASS persistent signing identity created; private key stored only in Keychain")


def load():
    if not STATE.exists():
        raise RuntimeError("Stable signing is not configured. Run: python3 scripts/stable_signing.py setup. Existing users must restore the original signing identity, never fall back to ad-hoc signing.")
    state = json.loads(STATE.read_text())
    if state.get("bundle_id") != BUNDLE_ID or not re.fullmatch(r"[A-F0-9]{40}", state.get("identity", "")):
        raise RuntimeError("Invalid local signing metadata; original identity must be restored.")
    identities = run("/usr/bin/security", "find-identity", "-p", "codesigning", state["keychain"], capture=True)
    if state["identity"] not in identities.upper():
        raise RuntimeError("Persistent Keychain signing key missing. Build stopped to protect existing permissions; restore the original keychain.")
    return state


def designated(bundle):
    result = subprocess.run(["/usr/bin/codesign", "-d", "-r-", str(bundle)], capture_output=True, text=True, check=True)
    return next(line.removeprefix("# ") for line in (result.stdout + result.stderr).splitlines() if "designated =>" in line)


def sign(bundle, entitlements=None):
    state = load()
    bundle = Path(bundle).resolve()
    with (bundle / "Contents/Info.plist").open("rb") as f:
        info = plistlib.load(f)
        if info.get("CFBundleIdentifier") != BUNDLE_ID:
            raise RuntimeError("Refusing bundle with a different application identity.")
    main_executable = bundle / "Contents/MacOS" / info["CFBundleExecutable"]
    base_args = ["/usr/bin/codesign", "--force", "--sign", state["identity"], "--keychain", state["keychain"], "--timestamp=none"]
    # Sign real native code inside-out. --deep signing also treats managed DLLs and
    # ordinary files in Contents/MacOS as generic code, producing fragile resource seals.
    native_magic = {b"\xcf\xfa\xed\xfe", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xfe\xed\xfa\xce", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca"}
    for path in sorted((bundle / "Contents").rglob("*")):
        if not path.is_file() or path.is_symlink() or path == main_executable:
            continue
        with path.open("rb") as f:
            native = f.read(4) in native_magic
        if native or path.parent == bundle / "Contents/MacOS":
            run(*base_args, str(path))
    root_args = base_args.copy()
    if entitlements:
        root_args += ["--entitlements", str(entitlements)]
    run(*root_args, str(bundle))
    verify(bundle)


def verify(bundle):
    state = load()
    run("/usr/bin/codesign", "--verify", "--deep", "--strict", str(bundle))
    dr = designated(bundle)
    if "cdhash" in dr or state["identity"].lower() not in dr.lower() or BUNDLE_ID not in dr:
        raise RuntimeError("Build identity drifted; do not install this build.")
    print("PASS stable designated requirement: bundle identifier + persistent signing certificate")
    return dr


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["setup", "sign", "verify"])
    parser.add_argument("bundle", nargs="?")
    parser.add_argument("--entitlements")
    args = parser.parse_args()
    try:
        if args.action == "setup":
            setup()
        elif not args.bundle:
            parser.error("bundle path required")
        elif args.action == "sign":
            sign(args.bundle, args.entitlements)
        else:
            verify(args.bundle)
    except (RuntimeError, OSError, ValueError, subprocess.CalledProcessError) as exc:
        parser.exit(1, f"Signing failed: {exc}\n")
