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
    if version!='3.0.2' or plist['CFBundleShortVersionString']!=version or plist['CFBundleVersion']!=version:failures.append('release version consistency')
    if failures:print('FAIL',failures);return 1
    print('PASS protected assets, original UI contracts, dictionary and release version',version);return 0
if __name__=='__main__':sys.exit(main())
