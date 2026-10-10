#!/usr/bin/env python3
"""Offline checks for preserved assets, UI contracts and actual release identity."""
import argparse,hashlib,json,re,sqlite3,sys
from pathlib import Path
from collections import Counter
import xml.etree.ElementTree as ET
ROOT_DIR=Path(__file__).resolve().parents[1]
EVENT_ATTRIBUTES={"Click","PointerPressed","KeyDown","TextChanged","SelectionChanged","Opened","Closing"}
def ui_contract(content):
    root = ET.fromstring(content)
    names = {}
    bindings = Counter()
    events = Counter()
    for element in root.iter():
        name = element.get("Name") or element.get("{http://schemas.microsoft.com/winfx/2006/xaml}Name")
        if name:
            if name in names:
                raise ValueError("Duplicate UI control: " + name)
            names[name] = element.tag
        if name == "LookupActionBar" and element.get("IsVisible") not in {"{Binding #LookupResultCard.IsVisible}", "False"}:
            raise ValueError("Missing or unexpected lookup action visibility contract")
        for attr, value in element.attrib.items():
            if name == "LookupActionBar" and attr == "IsVisible":
                # 2026-10-05 authorized footer fix: focus cards must hide ordinary lookup actions.
                # Runtime synchronization is covered by learning-test and backup restore ui-smoke.
                continue
            if "{Binding" in value:
                # Localization changes display formatting only; retain binding path and behavioral options.
                normalized = re.sub(r",\s*Converter=\{StaticResource UiValueConverter\}", "", value)
                normalized = re.sub(r",\s*ConverterParameter=(?:encounters|created)", "", normalized)
                normalized = re.sub(r",\s*StringFormat='(?:遇见 \{0\} 次 · AI 辅助内容可在编辑档案中修改|录入于 \{0\})'", "", normalized)
                bindings[(element.tag, attr, normalized)] += 1
            if attr in EVENT_ATTRIBUTES:
                events[(element.tag, attr, value)] += 1
    return names, bindings, events


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--mac-dir',default=str(ROOT_DIR/'lexi_avalonia'));args=parser.parse_args()
    source=Path(args.mac_dir); manifest=json.loads((ROOT_DIR/'packaging/source-invariants.json').read_text());failures=[]
    for relative,expected in manifest['protected'].items():
        p=ROOT_DIR/relative
        ok=p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==expected
        print(('PASS ' if ok else 'FAIL ')+relative)
        if not ok:failures.append(relative)
    names,bindings,events=ui_contract((source/'MainWindow.axaml').read_bytes())
    for name,kind in manifest['named_controls'].items():
        if names.get(name)!=kind: failures.append('UI type/name '+name)
    for original,current,label in [(manifest['bindings'],bindings,'binding'),(manifest['events'],events,'event')]:
        for *key,count in original:
            if current[tuple(key)]<count:failures.append(label+' '+str(key))
    print('PASS original UI names/types/events/bindings' if not failures else 'UI contract check completed')
    db=source/'Assets/dictionary.sqlite3'
    with sqlite3.connect('file:'+str(db)+'?mode=ro',uri=True) as connection:
        integrity=connection.execute('PRAGMA integrity_check').fetchone()[0]
        table_names={row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        table='ecdict'
        if table not in table_names: raise ValueError('offline dictionary table ecdict missing')
        words=connection.execute('SELECT count(*) FROM '+table).fetchone()[0]
        if integrity!='ok' or words!=59026:failures.append('offline dictionary integrity/word count')
        print('Dictionary entries:',words,'integrity:',integrity)
    version=ET.parse(source/'Lexi.csproj').getroot().findtext('./PropertyGroup/Version')
    import plistlib
    with (ROOT_DIR/'packaging/macOS/Info.plist').open('rb') as file: plist=plistlib.load(file)
    expected_version='3.2.0'
    ui=(source/'MainWindow.axaml').read_text()
    ai=(source/'Services/AiService.cs').read_text()
    dmg=(ROOT_DIR/'scripts/package_dmg.sh').read_text()
    readme=(ROOT_DIR/'README.md').read_text()
    updater=(source/'Update/UpdateChecker.cs').read_text()
    release_checks={
        'project version':version==expected_version,
        'macOS bundle versions':plist['CFBundleShortVersionString']==expected_version and plist['CFBundleVersion']==expected_version,
        'status bar version':f'Text="lexi {expected_version}"' in ui,
        'AI User-Agent version':f'Lexi/{expected_version}' in ai,
        'DMG name and version gate':f'Lexi-{expected_version}-macOS-arm64.dmg' in dmg and f'== "{expected_version}"' in dmg,
        'README current source version':f'当前源码版本为 **{expected_version}**' in readme,
        'README 3.1.2 history':re.search(r'^\| 第三代 · 3\.1\.2 \|.*\|$',readme,re.M) is not None,
        'README 3.1.1 history':re.search(r'^\| 第三代 · 3\.1\.1 \|.*FSRS.*\|$',readme,re.M) is not None,
        'README 3.0.4 history':re.search(r'^\| 第三代 · 3\.0\.4 \|.*每日学习计划.*\|$',readme,re.M) is not None,
        'README 3.0.3 history':re.search(r'^\| 第三代 · 3\.0\.3 \|.*英文打字完成反馈修复.*\|$',readme,re.M) is not None,
        'README 3.0.3 release link':'https://github.com/DespairJasper/lexi-macos/releases/tag/v3.0.3' in readme,
        # 更新检查必须指向本项目的正式 Release API，且不得在客户端硬编码第二份产品版本
        # （User-Agent 走 AppVersion.Display，与程序集版本同源）。
        'update check endpoint':('https://api.github.com/repos/{Owner}/{Repository}/releases/latest' in updater),
        'update check single version source':re.search(r'Lexi/\d',updater) is None and 'AppVersion.Display' in updater,
        # 卸载工具必须随安装包提供：默认保留用户数据的选择由它承载。
        'uninstall helper exists':(ROOT_DIR/'卸载Lexi.command').is_file(),
        'uninstall helper shipped':'卸载Lexi.command' in dmg,
    }
    for label,ok in release_checks.items():
        print(('PASS ' if ok else 'FAIL ')+label)
        if not ok:failures.append(label)
    if failures:print('FAIL',failures);return 1
    print('PASS protected assets, original UI contracts, dictionary and release version',version);return 0
if __name__=='__main__':sys.exit(main())
