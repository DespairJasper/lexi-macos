#!/usr/bin/env python3
"""Import all twenty pinned kajweb/dict books for the six exam topics. No user data is read or changed."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile

COMMIT = '3992bcb94c800a2fd38a9fd6ff95b2353e755363'
BASE = f'https://raw.githubusercontent.com/kajweb/dict/{COMMIT}/'
BOOKS = [
    ('cet4', '四级英语词汇（正序版）', 'CET-4 Vocabulary (Alphabetical)', 'CET4_2', '1521164635506_CET4_2.zip', 3739, 'cet4'),
    ('cet6', '六级英语词汇', 'CET-6 Vocabulary', 'CET6_2', '1524052554766_CET6_2.zip', 2078, 'cet6'),
    ('kaoyan', '考研英语词汇', 'Postgraduate English Vocabulary', 'KaoYan_2', '1521164654696_KaoYan_2.zip', 4533, 'kaoyan'),
    ('gmat', 'GMAT词汇（正序版）', 'GMAT Vocabulary (Alphabetical)', 'GMAT_2', '1521164662073_GMAT_2.zip', 3254, 'gmat'),
    ('toefl', 'TOEFL词汇', 'TOEFL Vocabulary', 'TOEFL_2', '1521164640451_TOEFL_2.zip', 9213, 'toefl'),
    ('gre', 'GRE词汇', 'GRE Vocabulary', 'GRE_2', '1521164637271_GRE_2.zip', 7199, 'gre'),
    ('cet4', '四级真题核心词（图片记忆）', 'CET-4 Core Exam Words (Picture Memory)', 'CET4luan_1', '1523620217431_CET4luan_1.zip', 1162, 'cet4luan_1'),
    ('cet6', '六级真题核心词（图片记忆）', 'CET-6 Core Exam Words (Picture Memory)', 'CET6luan_1', '1521164660466_CET6luan_1.zip', 1228, 'cet6luan_1'),
    ('kaoyan', '考研必考词汇', 'Essential Postgraduate Exam Words', 'KaoYanluan_1', '1521164661106_KaoYanluan_1.zip', 1341, 'kaoyanluan_1'),
    ('cet4', '四级英语词汇', 'CET-4 Vocabulary', 'CET4luan_2', '1524052539052_CET4luan_2.zip', 3739, 'cet4luan_2'),
    ('cet4', '新东方四级词汇', 'New Oriental CET-4 Vocabulary', 'CET4_3', '1521164643060_CET4_3.zip', 2607, 'cet4_3'),
    ('cet6', '新东方六级词汇', 'New Oriental CET-6 Vocabulary', 'CET6_3', '1521164633851_CET6_3.zip', 2345, 'cet6_3'),
    ('kaoyan', '新东方考研词汇', 'New Oriental Postgraduate Vocabulary', 'KaoYan_3', '1521164658897_KaoYan_3.zip', 3728, 'kaoyan_3'),
    ('cet4', '四级真题核心词（正序版）', 'CET-4 Core Exam Words (Alphabetical)', 'CET4_1', '1521164649209_CET4_1.zip', 1162, 'cet4_1'),
    ('cet6', '六级真题核心词（正序版）', 'CET-6 Core Exam Words (Alphabetical)', 'CET6_1', '1521164668667_CET6_1.zip', 1228, 'cet6_1'),
    ('kaoyan', '考研必考词汇（正序版）', 'Essential Postgraduate Exam Words (Alphabetical)', 'KaoYan_1', '1521164669833_KaoYan_1.zip', 1341, 'kaoyan_1'),
    ('gmat', 'GMAT词汇', 'GMAT Vocabulary', 'GMATluan_2', '1521164629611_GMATluan_2.zip', 3254, 'gmatluan_2'),
    ('toefl', '新东方TOEFL词汇', 'New Oriental TOEFL Vocabulary', 'TOEFL_3', '1521164667985_TOEFL_3.zip', 4264, 'toefl_3'),
    ('gre', '新东方GRE词汇', 'New Oriental GRE Vocabulary', 'GRE_3', '1521164677706_GRE_3.zip', 6515, 'gre_3'),
    ('gmat', '新东方GMAT词汇', 'New Oriental GMAT Vocabulary', 'GMAT_3', '1521164672691_GMAT_3.zip', 3047, 'gmat_3'),
]

def fetch_book(spec):
    topic, title, english, book_id, filename, expected, stable_book_id = spec
    with tempfile.TemporaryDirectory(prefix='lexi-exam-import-') as tmp:
        archive = Path(tmp) / filename
        subprocess.run(['curl', '--fail', '--location', '--retry', '3', '--silent', '--show-error', BASE + 'book/' + filename, '-o', str(archive)], check=True)
        raw = archive.read_bytes()
    with zipfile.ZipFile(io.BytesIO(raw)) as z:
        names = [n for n in z.namelist() if n.endswith('.json') and not n.startswith('__MACOS/')]
        if len(names) != 1:
            raise ValueError(f'{book_id}: expected one JSON dictionary: {names}')
        rows = [json.loads(line) for line in z.read(names[0]).decode('utf-8-sig').splitlines() if line.strip()]
    if len(rows) != expected:
        raise ValueError(f'{book_id}: expected {expected}, got {len(rows)}')
    entries = []
    seen = set()
    duplicate_forms = 0
    for row in rows:
        data = row['content']['word']['content']
        word = row['headWord'].strip()
        identity = f"exam:{book_id}:{row['wordRank']}"
        if identity in seen or not word:
            raise ValueError(f'{book_id}: invalid or duplicate word ID {identity}')
        seen.add(identity)
        translations = data.get('trans', [])
        meaning = '\n'.join((t.get('pos', '').strip('. ') + '. ' if t.get('pos') else '') + t.get('tranCn', '').strip() for t in translations).strip()
        if not meaning:
            raise ValueError(f'{identity}: missing translation')
        sentences = data.get('sentence', {}).get('sentences', [])
        phrases = data.get('phrase', {}).get('phrases', [])
        synonyms = list(dict.fromkeys(w['w'].strip() for s in data.get('syno', {}).get('synos', []) for w in s.get('hwds', []) if w.get('w', '').strip()))
        entries.append(dict(Id=identity, Words=[word], Meaning=meaning,
            Phonetic=data.get('ukphone') or data.get('usphone') or data.get('phone', ''),
            Example='\n'.join(s.get('sContent', '') + ('\n' + s['sCn'] if s.get('sCn') else '') for s in sentences),
            Extra='\n'.join(p.get('pContent', '') + '  ' + p.get('pCn', '') for p in phrases), Synonyms=synonyms))
    # The source may repeat a spelling. Retain the first entry and record the dropped ranks;
    # card rounds use word forms, so repeated forms cannot carry independent identities safely.
    unique = {}
    dropped = []
    for entry in entries:
        form = ' '.join(entry['Words'][0].casefold().split())
        if form in unique:
            duplicate_forms += 1
            dropped.append(entry['Id'])
        else:
            unique[form] = entry
    entries = list(unique.values())
    sections = [dict(Id=f'exam:{book_id}:unit:{i // 100 + 1}', Kind=f'exam:{topic}',
        Title=f'第 {i // 100 + 1:02d} 单元', EnglishTitle=f'Unit {i // 100 + 1:02d}',
        Description=f'kajweb/dict · {book_id}', Entries=entries[i:i+100]) for i in range(0, len(entries), 100)]
    print(f'{english}: {len(entries)} words, {len(sections)} units, {duplicate_forms} repeated forms removed', flush=True)
    return dict(Id=stable_book_id, TopicId=topic, Title=title, EnglishTitle=english, BookId=book_id, Sections=sections), dict(
        topic=topic, bookId=book_id, source=BASE+'book/'+filename, sha256=hashlib.sha256(raw).hexdigest(),
        sourceRows=len(rows), words=len(entries), repeatedFormsRemoved=duplicate_forms, removedIds=dropped)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parent.parent / 'lexi_avalonia/Assets/Exams')
    args = parser.parse_args()
    with ThreadPoolExecutor(max_workers=3) as pool:
        results = list(pool.map(fetch_book, BOOKS))
    args.output.mkdir(parents=True, exist_ok=True)
    for name, content in [('catalog.json', dict(Source='https://github.com/kajweb/dict', Commit=COMMIT, Books=[b for b, _ in results])),
                          ('manifest.json', dict(commit=COMMIT, unitSize=100, books=[m for _, m in results]))]:
        path = args.output / name
        temporary = path.with_suffix('.tmp')
        temporary.write_text(json.dumps(content, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
        temporary.replace(path)

if __name__ == '__main__':
    main()
