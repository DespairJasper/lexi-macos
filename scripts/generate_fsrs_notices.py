#!/usr/bin/env python3
"""Generate/check pinned arm64 FSRS notices. Python >=3.11; existing Cargo cache only.

Run with --cargo-home and --rustup-home (or their environment variables).
--check performs no writes and exits nonzero on any missing/drifted public artifact.
No build, dependency changes, registry modifications, or package extraction.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tomllib
import urllib.request

SUPPLEMENTAL = {'burn-ir-0.17.1': [{'filename': 'LICENSE-MIT',
                     'revision': '179731b9e7dacbc287a2ab968e30acb6c4c54b1f',
                     'sha256': 'ef31f0b689aa7e2edb5883c7617361de37b05030389e876d91be6c2f2306bb35',
                     'url': 'https://raw.githubusercontent.com/tracel-ai/burn/179731b9e7dacbc287a2ab968e30acb6c4c54b1f/LICENSE-MIT'},
                    {'filename': 'LICENSE-APACHE',
                     'revision': '179731b9e7dacbc287a2ab968e30acb6c4c54b1f',
                     'sha256': 'a9cfa8a88b2c2b7c9a5f2ef159522b706ea8aaa76448438d98c89c3217e90378',
                     'url': 'https://raw.githubusercontent.com/tracel-ai/burn/179731b9e7dacbc287a2ab968e30acb6c4c54b1f/LICENSE-APACHE'}],
 'embassy-futures-0.1.2': [{'filename': 'LICENSE-MIT',
                            'revision': '3e8d8fec15286eb25b8bba7d103c8fc279544551',
                            'sha256': '83d04e56c5beb631d65d6bd7b78c9350083084acc19753a3e6e1b68806a607f5',
                            'url': 'https://raw.githubusercontent.com/embassy-rs/embassy/3e8d8fec15286eb25b8bba7d103c8fc279544551/LICENSE-MIT'},
                           {'filename': 'LICENSE-APACHE',
                            'revision': '3e8d8fec15286eb25b8bba7d103c8fc279544551',
                            'sha256': '64853d2f346cf20ad5a133c441e792d4b99b45adb2ef3acfa2b890d713e4ed0b',
                            'url': 'https://raw.githubusercontent.com/embassy-rs/embassy/3e8d8fec15286eb25b8bba7d103c8fc279544551/LICENSE-APACHE'}],
 'macerator-macros-0.1.5': [{'filename': 'LICENSE',
                             'revision': '74ff68d11f164d3da077d673a0c0747e71f0c085',
                             'sha256': '5c04b92aec59ceb545aee96042c8258f004d98316cdb173171bb2b34d11b51ef',
                             'url': 'https://raw.githubusercontent.com/wingertge/macerator/74ff68d11f164d3da077d673a0c0747e71f0c085/LICENSE'}],
 'nvml-wrapper-0.10.0': [{'filename': 'LICENSE-MIT',
                          'revision': '940354559b6131b23734a97050c929483c9b4ad0',
                          'sha256': '877160c91906b0f08812fb90ed34b77f2810f262ce66c745c0be3590e36f6953',
                          'url': 'https://raw.githubusercontent.com/Cldfire/nvml-wrapper/940354559b6131b23734a97050c929483c9b4ad0/LICENSE-MIT'},
                         {'filename': 'LICENSE-APACHE',
                          'revision': '940354559b6131b23734a97050c929483c9b4ad0',
                          'sha256': 'e70785c8a3c78593f88a142d254d17dcbf00ded0a7c67e17dc5d9d0fe4d49f7a',
                          'url': 'https://raw.githubusercontent.com/Cldfire/nvml-wrapper/940354559b6131b23734a97050c929483c9b4ad0/LICENSE-APACHE'}],
 'nvml-wrapper-sys-0.8.0': [{'filename': 'LICENSE-MIT',
                             'revision': '940354559b6131b23734a97050c929483c9b4ad0',
                             'sha256': '877160c91906b0f08812fb90ed34b77f2810f262ce66c745c0be3590e36f6953',
                             'url': 'https://raw.githubusercontent.com/Cldfire/nvml-wrapper/940354559b6131b23734a97050c929483c9b4ad0/LICENSE-MIT'},
                            {'filename': 'LICENSE-APACHE',
                             'revision': '940354559b6131b23734a97050c929483c9b4ad0',
                             'sha256': 'e70785c8a3c78593f88a142d254d17dcbf00ded0a7c67e17dc5d9d0fe4d49f7a',
                             'url': 'https://raw.githubusercontent.com/Cldfire/nvml-wrapper/940354559b6131b23734a97050c929483c9b4ad0/LICENSE-APACHE'}],
 'relative-path-1.9.3': [{'filename': 'LICENSE-MIT',
                          'revision': '6d267fbd85b257e4416c9f020131c6da168e1d3d',
                          'sha256': '6485b8ed310d3f0340bf1ad1f47645069ce4069dcc6bb46c7d5c6faf41de1fdb',
                          'url': 'https://raw.githubusercontent.com/udoprog/relative-path/6d267fbd85b257e4416c9f020131c6da168e1d3d/LICENSE-MIT'},
                         {'filename': 'LICENSE-APACHE',
                          'revision': '6d267fbd85b257e4416c9f020131c6da168e1d3d',
                          'sha256': 'a60eea817514531668d7e00765731449fe14d059d3249e0bc93b36de45f759f2',
                          'url': 'https://raw.githubusercontent.com/udoprog/relative-path/6d267fbd85b257e4416c9f020131c6da168e1d3d/LICENSE-APACHE'}]}
FSRS_HEADER = """FSRS-rs Optimizer (FSRS-6 21-Parameter Helper)
================================================================================
Upstream Project: https://github.com/open-spaced-repetition/fsrs-rs
Crate: fsrs
Pinned Upstream Version: 5.2.0
Pinned Upstream Commit SHA: aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c
Pinned Upstream Crate Checksum (SHA-256):
  cab5f80c16d1d07e492a6828478ecb9f379f142ba6f19fa3b7955edd180ddc2e
Primary License: BSD-3-Clause

Compatibility Revision & Minimal Patch:
  Compatibility ID: fsrs6-hard-floor-2-v1
  Helper Version: 0.2.0
  Patch File: native/fsrs-optimizer/patches/fsrs-5.2.0-hard-floor-2.patch
  Patch SHA-256: 4c52a7583e4c939a93641b1655872acc026186a9b5a5524afd7b83a5d1eed5bb
  Official Specification Basis:
    https://github.com/open-spaced-repetition/awesome-fsrs/wiki/The-Algorithm
    FSRS-6 Section: "In practice, we should ensure that SInc >= 1 when G >= 2."
  Scope:
    Minimal 1-line compatibility patch applied in vendored model (`src/model.rs:114`):
    clamps short-term stability floor for ratings >= 2 (Hard, Good, Easy) so same-day
    Hard reviews do not decrease stability.
    Upstream crates.io registry cache remains untouched.

Description:
  Lexi incorporates a standalone native helper binary built from the vendored
  open-spaced-repetition/fsrs-rs library (version 5.2.0 with minimal compatibility
  patch fsrs6-hard-floor-2-v1) to perform local, offline optimization of personal
  FSRS-6 memory parameters (21 parameters). The helper operates strictly locally
  on macOS Apple Silicon (arm64) with zero cloud transmission, zero Python/Node
  runtime dependencies, and adheres strictly to the FSRS-6 specification
  (S_MIN = 0.001, DECAY = -0.1542, fixed seed = 2023, max sequence length = 64).
"""
TARGET = 'aarch64-apple-darwin'
MPL_PACKAGES = {'colored', 'option-ext', 'priority-queue'}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + '\n').encode()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cargo-home', default=os.environ.get('CARGO_HOME'))
    parser.add_argument('--rustup-home', default=os.environ.get('RUSTUP_HOME'))
    parser.add_argument('--cargo', help='Existing cargo executable; defaults to CARGO_HOME/bin/cargo')
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    if not args.cargo_home or not args.rustup_home:
        parser.error('set --cargo-home and --rustup-home or CARGO_HOME/RUSTUP_HOME')
    cargo_home = Path(args.cargo_home).resolve()
    cargo = args.cargo or str(cargo_home / 'bin/cargo')
    repo = Path(__file__).resolve().parents[1]
    native = repo / 'native/fsrs-optimizer'
    notices = repo / 'lexi_avalonia/Notices'
    env = dict(os.environ, CARGO_HOME=str(cargo_home), RUSTUP_HOME=str(Path(args.rustup_home).resolve()))

    def command(extra):
        return subprocess.run([cargo, *extra, '--manifest-path', str(native / 'Cargo.toml')],
                              cwd=repo, env=env, check=True, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE).stdout

    metadata = json.loads(command(['metadata', '--locked', '--offline', '--filter-platform', TARGET,
                                  '--format-version', '1']))
    tree = command(['tree', '--locked', '--offline', '--target', TARGET, '--edges', 'normal',
                    '--prefix', 'depth', '--format', '{p}']).decode()
    selected, stack = {}, []
    for line in tree.splitlines():
        match = re.match(r'(\d+)(\S+) v(\S+)', line)
        if not match:
            raise ValueError('unrecognized Cargo normal graph line')
        depth, name, version = match.groups()
        stack = stack[:int(depth)]
        macro, macro_only = '(proc-macro)' in line, any(stack)
        stack.append(macro or macro_only)
        classification = 'proc-macro' if macro else 'macro-only' if macro_only else 'normal'
        selected.setdefault((name, version), set()).add(classification)
    root = metadata['resolve']['root']
    lock_data = (native / 'Cargo.lock').read_bytes()
    locked = {(p['name'], p['version']): p for p in tomllib.loads(lock_data.decode())['package']}
    expected, records = {}, []

    def put(path, data):
        if path in expected and expected[path] != data:
            raise ValueError('duplicate artifact with conflicting contents: ' + path)
        expected[path] = data

    def archive_for(package):
        name, version = package['name'], package['version']
        candidates = list((cargo_home / 'registry/cache').glob('*/' + name + '-' + version + '.crate'))
        if len(candidates) != 1:
            raise ValueError('missing/ambiguous cached crate: ' + name + '@' + version)
        archive = candidates[0]
        actual = digest(archive.read_bytes())
        if actual != locked[(name, version)].get('checksum'):
            raise ValueError('cached crate checksum differs from Cargo.lock: ' + name)
        return archive, actual

    for package in sorted(metadata['packages'], key=lambda p: (p['name'], p['version'])):
        name, version = package['name'], package['version']
        classes = selected.get((name, version))
        if not classes or package['id'] == root:
            continue
        directory = Path(package['manifest_path']).parent
        key = name + '-' + version
        role = 'normal' if 'normal' in classes else 'proc-macro' if 'proc-macro' in classes else 'macro-only'
        source_url = 'https://static.crates.io/crates/' + name + '/' + key + '.crate'
        row = {'name': name, 'version': version, 'declared_license': package['license'],
               'scope': role, 'source': source_url, 'files': []}
        if name in MPL_PACKAGES:
            row['distribution_license'] = 'MPL-2.0'
        archive, archive_hash = (None, None) if package['source'] is None else archive_for(package)
        if archive_hash:
            row['crate_sha256'] = archive_hash
        vcs = directory / '.cargo_vcs_info.json'
        if vcs.exists():
            row['upstream_revision'] = json.loads(vcs.read_text())['git']['sha1']

        def include(filename, data, origin):
            relative = 'FSRS-Licenses/' + key + '/' + filename
            put(relative, data)
            row['files'].append({'path': relative, 'sha256': digest(data), 'origin': origin})

        local = [f for f in directory.iterdir() if f.is_file() and (
            f.name.upper().startswith(('LICENSE', 'LICENCE', 'NOTICE', 'COPYING', 'COPYRIGHT', 'UNLICENSE'))
            or f == directory / (package.get('license_file') or '<none>'))]
        if archive:
            with tarfile.open(archive, 'r:gz') as source:
                for file in sorted(local):
                    member = source.extractfile(key + '/' + file.name)
                    if member is None or member.read() != file.read_bytes():
                        raise ValueError('registry license differs from verified crate: ' + key + '/' + file.name)
        for file in sorted(local):
            include(file.name, file.read_bytes(), source_url + '#/' + file.name)
        for extra in SUPPLEMENTAL.get(key, []):
            destination = notices / 'FSRS-Licenses' / key / extra['filename']
            if destination.exists():
                data = destination.read_bytes()
            elif args.check:
                raise ValueError('missing supplemental license: ' + key)
            else:
                with urllib.request.urlopen(extra['url'], timeout=30) as response:
                    data = response.read(1024 * 1024 + 1)
                if len(data) > 1024 * 1024:
                    raise ValueError('upstream license exceeds budget')
            if digest(data) != extra['sha256']:
                raise ValueError('supplemental license hash drift: ' + key)
            include(extra['filename'], data, extra['url'])
            row['license_source_revision'] = extra['revision']

        def source_text(filename):
            if archive:
                with tarfile.open(archive, 'r:gz') as source:
                    member = source.extractfile(key + '/' + filename)
                    if member is None:
                        raise ValueError('missing source notice input: ' + key)
                    return member.read().decode().replace('\r\n', '\n')
            return (directory / filename).read_text()

        # Preserve source notices omitted from crate-level license files.
        if name == 'priority-queue':
            data = '\n'.join(source_text('src/lib.rs').splitlines()[:27]) + '\n'
            include('SOURCE-NOTICE.txt', data.encode(), source_url + '#/src/lib.rs:1-27')
            colored = next(p for p in metadata['packages'] if p['name'] == 'colored' and p['version'] == '3.1.1')
            mpl = (Path(colored['manifest_path']).parent / 'LICENSE').read_bytes()
            include('LICENSE-MPL-2.0', mpl, 'https://www.mozilla.org/en-US/MPL/2.0/')
        if name == 'relative-path':
            lines = source_text('src/lib.rs').splitlines()
            index = next(i for i, line in enumerate(lines) if 'Copyright 2015 The Rust Project Developers' in line)
            include('SOURCE-ATTRIBUTION.txt', ('\n'.join(lines[index:index + 3]) + '\n').encode(),
                    source_url + '#/src/lib.rs')
        if not row['files']:
            raise ValueError('no license notices for ' + key)
        if name in MPL_PACKAGES:
            if archive is None:
                raise ValueError('MPL source archive unavailable: ' + key)
            relative = 'FSRS-Sources/' + key + '.crate'
            put(relative, archive.read_bytes())
            row['source_archive'] = {'path': relative, 'sha256': archive_hash,
                                     'format': 'gzip-compressed tar; unmodified published source'}
        records.append(row)
    if len(records) != len(selected) - 1:
        raise ValueError('unmapped selected normal dependencies')

    index = {'schema': 1, 'target': TARGET, 'cargo_lock_sha256': digest(lock_data),
             'cargo_manifest_sha256': digest((native / 'Cargo.toml').read_bytes()),
             'selection': 'Cargo tree --locked --offline --target aarch64-apple-darwin --edges normal; proc macros retained separately',
             'dependencies': records}
    put('FSRS-Licenses/index.json', json_bytes(index))
    summary = FSRS_HEADER + '\n\nThird-party dependency notices\n' + '=' * 80 + '\n'
    summary += ('Exact versions, original complete license texts, copyright notices and SHA-256 mappings\n'
                'are in FSRS-Licenses/index.json and its per-crate directories. Files with identical\n'
                'license names may have different copyright notices; preserve all directories.\n'
                'Scope normal denotes target normal dependencies; proc-macro/macro-only denotes\n'
                'build-time generation dependencies included conservatively, not runtime libraries.\n'
                'Unenabled backend and development-only dependency branches are excluded.\n'
                'SPDX OR alternatives retain their upstream texts; priority-queue is distributed\n'
                'under the MPL-2.0 alternative. Other MPL packages use MPL-2.0.\n\n'
                'MPL source availability\n'
                'The exact unmodified published source for colored 3.1.1, option-ext 0.2.0 and\n'
                'priority-queue 2.5.0 is supplied next to this notice in FSRS-Sources/*.crate.\n'
                'Each .crate is a gzip-compressed tar archive, readable with tar -xzf or an\n'
                'archive utility. Source hashes and upstream download URLs appear in index.json.\n'
                'Covered source is available under MPL-2.0; its original notices remain intact.\n'
                'The MPL text is included in the corresponding FSRS-Licenses directories.\n'
                'This source availability is independent of the license of the larger Lexi work.\n\n'
                'Reproduce/validate: python3 scripts/generate_fsrs_notices.py [--check]\n'
                'with the existing CARGO_HOME/RUSTUP_HOME and locked offline registry cache.\n'
                'A nonzero --check exit blocks publication: missing/changed licenses, sources,\n'
                'lockfile or target dependency selection require regeneration and review.\n\n')
    for row in records:
        summary += row['name'] + ' ' + row['version'] + ' | ' + row['declared_license'] + ' | ' + row['scope'] + '\n'
    put('FSRS-OPTIMIZER.txt', summary.encode())
    # Check exact generated set, so stale dependencies cannot silently survive regeneration.
    existing = {str(f.relative_to(notices)) for folder in ['FSRS-Licenses', 'FSRS-Sources']
                for f in (notices / folder).rglob('*') if f.is_file()}
    stale = existing - set(expected)
    if stale:
        raise ValueError('stale public artifacts require explicit review/removal: ' + ', '.join(sorted(stale)))
    for relative, data in sorted(expected.items()):
        path = notices / relative
        if args.check:
            if not path.exists() or path.read_bytes() != data:
                raise ValueError('public artifact missing or changed: ' + relative)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
    print(('CHECK' if args.check else 'GENERATE') + ' PASS: ' + str(len(records)) +
          ' third-party crates; ' + str(len(expected)) + ' artifacts; 3 MPL source archives')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, KeyError, StopIteration, subprocess.CalledProcessError) as error:
        print('FSRS notices validation failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
