# Exam vocabulary source

Source: https://github.com/kajweb/dict
Pinned commit: 3992bcb94c800a2fd38a9fd6ff95b2353e755363

All 20 upstream books for CET-4 (5), CET-6 (4), postgraduate English (4), GMAT (3), TOEFL (2) and GRE (2) are included. Actual book titles are shown in Chinese or English; the right selector chooses a unit of that book. The original six base books retain their IDs, entries, order and units.

The upstream repository attributes its dictionaries to the Youdao vocabulary app and the respective contributors. Its README includes a rights-holder contact statement; no new license is inferred here.

The reproducible importer is scripts/import_exam_books.py. Assets/Exams/manifest.json records source URLs, archive SHA256, original counts and removed duplicate ranks. Learning IDs use exam:<bookId>:<original wordRank>; section IDs and word order remain stable. Chapters contain at most 100 words. Three repeated spellings are removed within their own books (one TOEFL and one in each of the two postgraduate core-word editions), leaving 66,974 book entries. Translations, phonetics, sentences, phrases and synonyms are included where supplied upstream. Audio uses the existing IELTS pronunciation fallback when no bundled recording exists.
