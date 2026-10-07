#!/usr/bin/env python3
"""发布合格证明：把"回归 / 包内隔离验收 / 许可证 / 隐私"四项**真实退出码**绑到**这一个** bundle 上。

为什么需要它：install_app.py 的唯一来源是 dist/Lexi.app，而 dist 里那份可能是一个旧的、
不含 helper 的、从未被验收过的构建。没有证明时安装既不报错也不可分辨，事后极难发现。

边界（不要再声称更多）：本文件是**一致性**绑定，不是密码学证明。它能保证
"待安装的 bundle 就是被 write 时那一个，且各阶段当时退出码为 0"；
它**不能**证明这些阶段真的跑过——真正的强制来自 release.sh 只在全部阶段为 0 时才调用 write。
因此 release.sh 必须把各阶段**实际**退出码传进来，不得硬编码 0。

用法:
  python3 scripts/release_proof.py write --app dist/Lexi.app \\
      --regression-exit 0 --packaged-smoke-exit 0 --license-exit 0 --privacy-exit 0
  python3 scripts/release_proof.py check --app dist/Lexi.app     # 任一不满足即非零退出
"""
import argparse
import hashlib
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PIN = ROOT / 'packaging/fsrs-helper-pin.json'
RELEASE_PIN = ROOT / 'packaging/release-pin.json'
DEFAULT_PROOF = ROOT / 'dist/release-proof.json'
PRESIGN = ROOT / 'dist/fsrs-helper-presign.sha256'
GATES = ('regression_exit', 'packaged_smoke_exit', 'license_exit', 'privacy_exit')


def codesign_field(app: Path, prefix: str) -> str:
    out = subprocess.run(['/usr/bin/codesign', '-dvvv', str(app)],
                         capture_output=True, text=True).stderr
    for line in out.splitlines():
        line = line.strip()
        if line.startswith(prefix):
            return line.split('=', 1)[1].strip()
    return ''


def designated(app: Path) -> str:
    # 流很反直觉：`codesign -d -r-` 把 `designated => ...` 写到 **stdout**，
    # 而 `Executable=...` 写到 stderr。只读 stderr 会静默拿到空串，
    # 于是 DR 断言退化成"空 == 空"。两个流都看，避免再次踩到。
    done = subprocess.run(['/usr/bin/codesign', '-d', '-r-', str(app)],
                          capture_output=True, text=True)
    for stream in (done.stdout, done.stderr):
        for line in stream.splitlines():
            line = line.strip()
            if line.startswith('designated => '):
                return line[len('designated => '):].strip()
    return ''


def bundle_facts(app: Path) -> dict:
    helper = app / 'Contents/MacOS/fsrs-optimizer'
    return {
        'path': str(app),
        'cdhash': codesign_field(app, 'CDHash'),
        'identifier': codesign_field(app, 'Identifier'),
        'designated_requirement': designated(app),
        'helper_present': helper.is_file(),
        # 这是**签名之后**的字节：签名会改写 Mach-O，因此它与"签名前 pin"必然不同。
        'helper_sha256_signed': hashlib.sha256(helper.read_bytes()).hexdigest() if helper.is_file() else '',
        'helper_bytes_signed': helper.stat().st_size if helper.is_file() else 0,
    }


def pins() -> tuple:
    helper_pin = json.loads(PIN.read_text())
    release_pin = json.loads(RELEASE_PIN.read_text())
    return helper_pin, release_pin


def presign_recorded() -> str:
    return PRESIGN.read_text().strip() if PRESIGN.is_file() else ''


def write(args) -> None:
    app = Path(args.app).resolve()
    if not app.is_dir():
        sys.exit(f'错误: 未找到 app bundle: {app}')
    helper_pin, _ = pins()
    facts = bundle_facts(app)
    if not facts['cdhash']:
        sys.exit('错误: 无法读取 code signature CDHash；app 可能未签名。')
    if not facts['designated_requirement']:
        sys.exit('错误: 读不到 designated requirement（codesign 输出流变了？）；拒绝写合格证明。')
    if not facts['helper_present']:
        sys.exit('错误: bundle 内没有 FSRS helper，拒绝写合格证明。')
    presign = presign_recorded()
    helper_matches_pin = presign == helper_pin['sha256']
    if not helper_matches_pin:
        sys.exit(f'错误: 打包时记录的签名前 helper 指纹（{presign[:16] or "<缺失>"}…）'
                 f'与已审计 pin（{helper_pin["sha256"][:16]}…）不一致，拒绝写合格证明。')
    proof = {
        'created_at_utc': datetime.now(timezone.utc).isoformat(),
        'app': facts,
        'helper_pin_sha256_pre_sign': helper_pin['sha256'],
        'helper_presign_recorded': presign,
        'helper_matches_pin': helper_matches_pin,
        # 记录不变量清单自身的指纹：source-invariants.json 不在自己的 protected 列表里，
        # 因此"改门禁脚本 + 顺手刷新该清单里的哈希"是一条零权限绕过。把它的指纹写进证明，
        # 至少让这一次发布留下可被外部比对的锚。
        'source_invariants_sha256': hashlib.sha256(
            (ROOT / 'packaging/source-invariants.json').read_bytes()).hexdigest(),
        'source_head': subprocess.run(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'],
                                      capture_output=True, text=True, check=True).stdout.strip(),
        'status_sha256': hashlib.sha256(subprocess.run(
            ['git', '-C', str(ROOT), 'status', '--short'],
            capture_output=True, check=True).stdout).hexdigest(),
    }
    for gate in GATES:
        proof[gate] = getattr(args, gate)
    out = Path(args.out).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)
    tmp = out.with_suffix('.json.tmp')
    tmp.write_text(json.dumps(proof, ensure_ascii=False, indent=2) + '\n')
    tmp.replace(out)
    print(f'已写入合格证明: {out}')
    for gate in GATES:
        print(f'  {gate} = {proof[gate]}')
    print(f"  helper_matches_pin = {proof['helper_matches_pin']}")


def check(args) -> None:
    app = Path(args.app).resolve()
    proof_path = Path(args.proof).resolve()
    if not proof_path.is_file():
        sys.exit(f'错误: 缺少合格证明 {proof_path}。先跑 scripts/release.sh 完成'
                 f'回归 → 打包 → 包内隔离验收 → 许可证 → 隐私，再由它写证明；未验证的包不得安装。')
    proof = json.loads(proof_path.read_text())
    helper_pin, release_pin = pins()
    facts = bundle_facts(app)
    failures = []

    # `check` 不能只说"证明自洽"：先确认这个 bundle 的封条本身是有效的。
    # CDHash 读取不校验 nested code，所以两个破损的 bundle 也能有一模一样的 CDHash。
    if subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)],
                      capture_output=True).returncode != 0:
        failures.append('codesign --verify --deep --strict 失败：bundle 的封条无效')

    for gate in GATES:
        value = proof.get(gate)
        # bool 是 int 的子类：`False == 0` 会让手写 proof 用 false 混过类型检查，必须显式排除。
        if isinstance(value, bool) or not isinstance(value, int) or value != 0:
            failures.append(f'{gate} = {value!r}（必须是整数 0）')

    if facts['cdhash'] != proof.get('app', {}).get('cdhash'):
        failures.append(f"bundle CDHash {facts['cdhash']} != 证明记录的 {proof.get('app', {}).get('cdhash')}")
    if not facts['helper_present']:
        failures.append('待安装的 bundle 内没有 FSRS helper')
    elif facts['helper_sha256_signed'] != proof.get('app', {}).get('helper_sha256_signed'):
        failures.append('待安装 bundle 的 helper（签名后）与证明记录的不一致——helper 被替换过')
    if facts['designated_requirement'] != release_pin['designated_requirement']:
        failures.append(f"bundle 的 DR 与 packaging/release-pin.json 不符：{facts['designated_requirement']}")
    # 证明里记录的 DR 也必须是 pin 那一条：否则一份 DR 为空的旧证明会"自洽地"通过。
    if proof.get('app', {}).get('designated_requirement') != release_pin['designated_requirement']:
        failures.append('证明记录的 DR 与 release-pin.json 不符（旧证明或伪造证明）')
    if facts['identifier'] != release_pin['bundle_id']:
        failures.append(f"bundle identifier {facts['identifier']} != {release_pin['bundle_id']}")
    if presign_recorded() != helper_pin['sha256']:
        failures.append('dist/fsrs-helper-presign.sha256 与已审计 pin 不一致（打包时未按 pin 断言，或事后被改）')

    if failures:
        for f in failures:
            print('错误: 合格证明校验失败：' + f, file=sys.stderr)
        sys.exit(1)
    print(f'合格证明校验通过：{proof_path}')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    w = sub.add_parser('write')
    w.add_argument('--app', default=str(ROOT / 'dist/Lexi.app'))
    w.add_argument('--out', default=str(DEFAULT_PROOF))
    for gate in GATES:
        w.add_argument('--' + gate.replace('_', '-'), type=int, required=True)
    c = sub.add_parser('check')
    c.add_argument('--app', default=str(ROOT / 'dist/Lexi.app'))
    c.add_argument('--proof', default=str(DEFAULT_PROOF))
    args = parser.parse_args()
    (write if args.command == 'write' else check)(args)


if __name__ == '__main__':
    main()
