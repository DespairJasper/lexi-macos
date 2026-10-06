#!/usr/bin/env python3
"""Two different builds must retain the same signer-bound application identity."""
from pathlib import Path
import plistlib
import subprocess
import tempfile
import stable_signing as signing


def check(ok, name):
    if not ok:
        raise RuntimeError(name)
    print("PASS " + name)


with tempfile.TemporaryDirectory(prefix="lexi-upgrade-test-") as temp:
    folder = Path(temp)
    bundles = []
    for version in (1, 2):
        bundle = folder / f"build-{version}" / "Lexi.app"
        (bundle / "Contents/MacOS").mkdir(parents=True)
        (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps({
            "CFBundleIdentifier": signing.BUNDLE_ID, "CFBundleExecutable": "Lexi",
            "CFBundleName": "lexi", "CFBundlePackageType": "APPL", "CFBundleVersion": str(version),
        }))
        # Runtime layout: main executable plus managed files; media stays in Resources.
        (bundle / "Contents/MacOS/System.Runtime.dll").write_bytes(b"managed runtime fixture")
        (bundle / "Contents/MacOS/Lexi.runtimeconfig.json").write_text("{}")
        assets = bundle / "Contents/Resources/Assets"
        assets.mkdir(parents=True)
        (assets / "dictionary.txt").write_text("offline dictionary fixture")
        (bundle / "Contents/MacOS/Assets").symlink_to("../Resources/Assets", target_is_directory=True)
        source = folder / f"main-{version}.c"
        source.write_text(f"int main(void) {{ return {version}; }}\n")
        signing.run("/usr/bin/clang", str(source), "-o", str(bundle / "Contents/MacOS/Lexi"))
        signing.run("/usr/bin/codesign", "--deep", "--sign", "-", str(bundle))
        bundles.append(bundle)

    check(signing.designated(bundles[0]) != signing.designated(bundles[1]),
          "reproduced old ad-hoc identity drift when build contents change")
    for bundle in bundles:
        signing.sign(bundle)
    requirements = [signing.designated(bundle) for bundle in bundles]
    check(requirements[0] == requirements[1] and "cdhash" not in requirements[0],
          "different builds retain exactly the same certificate-bound identity")
    for old, new in ((0, 1), (1, 0)):
        external_requirement = requirements[old].split("designated =>", 1)[1].strip()
        signing.run("/usr/bin/codesign", "--verify", "--strict", "--test-requirement", "=" + external_requirement, str(bundles[new]))
    check(True, "each build satisfies the other build's identity requirement")
    (bundles[1] / "Contents/MacOS/Lexi").write_bytes((bundles[1] / "Contents/MacOS/Lexi").read_bytes() + b"tampered")
    result = subprocess.run(["/usr/bin/codesign", "--verify", "--strict", str(bundles[1])], capture_output=True)
    check(result.returncode != 0, "stable identity still rejects a tampered executable")
    actual_state = signing.STATE
    signing.STATE = folder / "missing-signing.json"
    try:
        signing.load()
        raise AssertionError("missing signing identity was accepted")
    except RuntimeError:
        check(True, "missing signing identity stops instead of silently generating a new identity")
    finally:
        signing.STATE = actual_state
