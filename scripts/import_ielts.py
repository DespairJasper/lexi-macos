#!/usr/bin/env python3
"""Reproduce native IELTS assets from a pinned my-ielts checkout; never edits upstream."""
import argparse, hashlib, html, json, re, shutil, subprocess, sqlite3, unicodedata
from pathlib import Path

SHA = '5cef573933663c4673c6e0093f1df04e68018b1a'
p = argparse.ArgumentParser()
p.add_argument('checkout', type=Path)
p.add_argument('--node', required=True)
a = p.parse_args()
source = a.checkout.resolve()
actual = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
if actual != SHA: raise SystemExit(f'Expected {SHA}, got {actual}')
dest = Path(__file__).resolve().parents[1] / 'lexi_avalonia/Assets/IELTS'
dest.mkdir(parents=True, exist_ok=True)
def load(relative):
    path = source / relative
    if path.suffix == '.json': return json.loads(path.read_text())
    # Upstream modules contain literal data; isolated module evaluation, no project install.
    script = "import {readFileSync} from 'node:fs'; const s=readFileSync(process.argv[1],'utf8'); const m=await import('data:text/javascript;base64,'+Buffer.from(s).toString('base64')); process.stdout.write(JSON.stringify(m.default));"
    return json.loads(subprocess.check_output([a.node, '--input-type=module', '-e', script, str(path)], text=True))
assets = set()
missing = []
def packaged_path(relative):
    # HFS+ normalises accents, which changes signed resource names. Use a stable
    # ASCII basename for decomposable filenames and retain the upstream mapping.
    path = Path(relative)
    if unicodedata.normalize('NFD', path.name) == path.name: return relative
    stem = unicodedata.normalize('NFKD', path.stem).encode('ascii', 'ignore').decode()
    return str(path.with_name(stem + '-' + hashlib.sha256(relative.encode()).hexdigest()[:8] + path.suffix))
def audio(relative):
    if (source/'public'/relative).is_file():
        assets.add(relative); return packaged_path(relative)
    missing.append(relative); return ''
dictionary = dest.parent / 'dictionary.sqlite3'
with sqlite3.connect('file:' + str(dictionary) + '?mode=ro', uri=True) as connection:
    phonetics = {w.lower(): p for w,p in connection.execute('SELECT word,phonetic FROM ecdict') if p}
sections=[]
for title,c in load('src/pages/vocabulary/vocabulary.js').items():
    entries=[]
    for group, words in enumerate(c['words'],1):
        for w in words:
            entries.append(dict(id=f"vocab:{title}:{w['id']}", words=w['word'], pos=w['pos'], meaning=w['meaning'], example=w['example'], extra='' if w['extra']=='-' else w['extra'], group=group, audioPath=audio(f"vocabulary/audio/{title}/{w['word'][0]}.mp3"), synonyms=[]))
    assert len(entries)==c['wordCount']
    sections.append(dict(id=title, kind='vocabulary', title=title.replace('_',' · '), description='逻辑词群 · 雅思词汇真经', audioPath=audio(f"vocabulary/audio/{c['audio']}"), entries=entries))
entries=[]
for w in load('src/pages/listening/listening179.json'):
    entries.append(dict(id=f"listening:{w['index']}",words=[w['word']],pos=w['type'],meaning=w['meaning'],synonyms=w['replace'],audioPath=audio(f"179_audios/{w['word']}.mp3")))
sections.append(dict(id='listening179',kind='listening',title='听力 179 考点词',description='考点词与同义替换',entries=entries))
for i,c in enumerate(load('src/pages/reading/reading538words.js'),1):
    entries=[]
    for w in c['words']:
        entries.append(dict(id=f'reading:{w[0]}',words=[w[1]],pos=' / '.join(w[2]),meaning='；'.join(w[3]),synonyms=w[4],extra=w[5]))
    sections.append(dict(id=f'reading{i}',kind='reading',title=c['title'],description=c['define']+' · '+c['require'],entries=entries))
for title,c in load('src/pages/listening/spelling_convention.js').items():
    entries=[]
    for i,row in enumerate(c['rows'],1):
        forms=list(dict.fromkeys(s.strip() for col in row[:2] for s in col.split(',') if s.strip()))
        entries.append(dict(id=f'spelling:{i}',words=forms,pos='',meaning=row[2],extra=f'British: {row[0]}\nAmerican: {row[1]}',synonyms=[]))
    sections.append(dict(id='spelling',kind='spelling',title=title,description=c['title']+' · '+c['desc'],entries=entries))
writing=load('src/pages/writing/100sentences.js')
category=''
for item in writing:
    if item.get('no') is None: category=item['title']
    else: item['category']=category
sentences=[dict(number=w['no'],category=w['category'],chinese=w['sentence'],bookAnswer=w['translationFromBook'],alternateAnswer=w['chatgpt'],remark=w['remark']) for w in writing if w.get('no') is not None]
assert len(sentences)==100
assets.update(['grammar/雅思语法.svg','grammar/雅思基础语法配套课程讲义.pdf'])
# Preserve complete notes as readable text, including table data and section hierarchy.
s=(source/'src/pages/listening/index.vue').read_text()
score=re.search(r'const scoreTable = (\[.*?\])\s*</script>',s,re.S).group(1)
score=json.loads(subprocess.check_output([a.node,'-e','process.stdout.write(JSON.stringify('+score+'))'],text=True))
body=s.split('<template>',1)[1].split('</template>',1)[0]
body=re.sub(r'<table\b.*?</table>','',body,flags=re.S)
body=re.sub(r'<!--.*?-->','',body,flags=re.S)
body=re.sub(r'<li\b[^>]*>','\n• ',body)
body=re.sub(r'<(?:br|h3|p)\b[^>]*>','\n',body)
body=html.unescape(re.sub(r'<[^>]+>','',body))
body='\n'.join(line.strip() for line in body.splitlines() if line.strip())
(dest/'listening-notes.txt').write_text('来源：my-ielts 作者学习笔记；非官方考试规则。\n\n'+body+'\n\n原仓库评分参考表：\n'+'\n'.join(' → '.join(row) for row in score))
# Preserve the legacy variant as source data; canonical user-facing set uses newer listening path.
legacy=load('src/pages/ielts/listening179.json')
(dest/'listening179-legacy.json').write_text(json.dumps(legacy,ensure_ascii=False,indent=2))
for relative in sorted(assets):
    target=dest/packaged_path(relative); target.parent.mkdir(parents=True,exist_ok=True)
    legacy=dest/relative
    if target != legacy and legacy.exists() and not target.exists(): legacy.rename(target)
    shutil.copy2(source/'public'/relative,target)
for section in sections:
    for entry in section['entries']:
        phonetic = phonetics.get(entry['words'][0].lower(), '')
        entry['phonetic'] = '/' + phonetic.strip('/') + '/' if phonetic else ''
sharp = Path(a.node).parent.parent / 'node_modules/sharp'
render = "const sharp=require(process.argv[3]);sharp(process.argv[1]).resize({width:1400}).png().toFile(process.argv[2]).catch(e=>{console.error(e.message);process.exitCode=1});"
subprocess.run([a.node, '-e', render, str(dest/'grammar/雅思语法.svg'), str(dest/'grammar/mindmap.png'), str(sharp)], check=True)
assets.add('grammar/mindmap.png')
catalog=dict(source='https://github.com/hefengxian/my-ielts',commit=SHA,sections=sections,sentences=sentences,grammarVideo='https://www.youtube.com/watch?v=bxvyZwACfNk')
(dest/'catalog.json').write_text(json.dumps(catalog,ensure_ascii=False,indent=2))
manifest=dict(commit=SHA,chapters=22,vocabularyEntries=sum(len(c['entries']) for c in sections if c['kind']=='vocabulary'),listeningEntries=179,readingEntries=sum(len(c['entries']) for c in sections if c['kind']=='reading'),writingSentences=len(sentences),spellingEntries=sum(len(c['entries']) for c in sections if c['kind']=='spelling'),assets={packaged_path(r):hashlib.sha256((dest/packaged_path(r)).read_bytes()).hexdigest() for r in sorted(assets)},sourcePaths={packaged_path(r):r for r in sorted(assets) if packaged_path(r) != r},audioFallbacks=missing)
(dest/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2))
notice=dest.parents[1]/'Notices/MY-IELTS-SOURCE.md'
notice.write_text(f'# my-ielts 资料来源\n\nhttps://github.com/hefengxian/my-ielts\n\n固定版本：{SHA}\n\n保留原作者与资料来源。原仓库 README 明确禁止商业用途。本次适配用于本地个人学习。原书音频、词汇、讲义的权利归各权利人所有。原有 ChatGPT 翻译保留来源标识，不作为标准答案。阅读“538”文件实际仅376条；原仓库待办内容未补造。\n')
print(json.dumps({k:v for k,v in manifest.items() if k not in ['assets','audioFallbacks']},ensure_ascii=False))
print(f'Packaged {len(assets)} assets; {len(missing)} audio paths use local speech fallback.')
