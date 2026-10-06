#!/bin/bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
bash "$ROOT_DIR/scripts/package_dmg.sh" "$@"
printf '\n安装包已保存至 dist / Installer saved to dist.\n'
