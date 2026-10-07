#!/usr/bin/env python3
"""Write and verify the Finder icon layout without opening Finder or AppleScript."""
from __future__ import annotations

import argparse
from pathlib import Path

from ds_store import DSStore

GUIDE_NAME = "安装说明 · Install.txt"
UNINSTALL_NAME = "卸载Lexi.command"
POSITIONS = {"Lexi.app": (180, 170), "Applications": (500, 170),
             UNINSTALL_NAME: (500, 310), GUIDE_NAME: (340, 400)}


def configure(volume: Path) -> None:
    if not volume.is_dir():
        raise ValueError(f"Not a volume staging directory: {volume}")
    for name in POSITIONS:
        if not (volume / name).exists():
            raise FileNotFoundError(volume / name)
    with DSStore.open(str(volume / ".DS_Store"), "w+") as store:
        store["."]["bwsp"] = {
            "WindowBounds": "{{180, 100}, {680, 470}}",
            "ShowStatusBar": False,
            "ShowToolbar": False,
            "ShowTabView": False,
            "ShowPathbar": False,
            "ShowSidebar": False,
            "SidebarWidth": 0,
            "ContainerShowSidebar": False,
            "PreviewPaneVisibility": False,
        }
        store["."]["icvp"] = {
            "viewOptionsVersion": 1,
            "backgroundType": 1,
            "backgroundColorRed": 0.973,
            "backgroundColorGreen": 0.984,
            "backgroundColorBlue": 1.0,
            "iconSize": 96.0,
            "textSize": 14.0,
            "gridSpacing": 120.0,
            "gridOffsetX": 0.0,
            "gridOffsetY": 0.0,
            "arrangeBy": "none",
            "labelOnBottom": True,
            "showIconPreview": True,
            "showItemInfo": False,
            "scrollPositionX": 0.0,
            "scrollPositionY": 0.0,
        }
        store["."]["vSrn"] = ("long", 1)
        store["."]["icvl"] = ("type", b"icnv")
        for name, position in POSITIONS.items():
            store[name]["Iloc"] = position
    verify(volume)


def verify(volume: Path) -> None:
    with DSStore.open(str(volume / ".DS_Store"), "r") as store:
        for name, position in POSITIONS.items():
            actual = store[name]["Iloc"]
            if actual != position:
                raise RuntimeError(f"Finder icon position mismatch: {name}: {actual}")
            print(f"PASS Finder position {name}: {actual}")
        icons = store["."]["icvp"]
        if store["."]["icvl"] != (b"type", b"icnv"):
            raise RuntimeError("Finder default view is not icon-view")
        if icons["iconSize"] != 96.0 or icons["backgroundType"] != 1:
            raise RuntimeError("Finder icon/background configuration mismatch")
        print("PASS Finder background: glacier white; icon size: 96 pt; window: 680 × 470")
        print("PASS Finder default view: icvl=type/icnv (matches upstream dmgbuild)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("volume", type=Path)
    parser.add_argument("--verify", action="store_true", help="Read and verify an existing .DS_Store")
    args = parser.parse_args()
    (verify if args.verify else configure)(args.volume)
