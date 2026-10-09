package org.lexi.archive.data

import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/** UTF-8 BOM CSV; spreadsheet-safe cells and lossless Lexi metadata, no settings or keys. */
object ArchiveCsv {
    private val headers = listOf("单词", "音标", "中文释义", "英文释义", "备注", "来源类型", "来源", "来源原句", "标签", "UUID", "阶段", "状态", "入库日期", "学习起始日期", "下次复习日期", "上次复习时间", "复习次数", "遇见次数", "版本", "创建时间UTC", "更新时间UTC", "最后遇见时间UTC", "包含AI扩展", "例句英文", "例句中文", "同义词", "反义词", "常用词组", "词组中文")
    private fun risky(s: String) = s.trimStart().firstOrNull() in listOf('=', '+', '-', '@', '\t', '\r', '\n') || s.firstOrNull() in listOf('\t', '\r', '\n')
    private fun protect(s: String) = if (s.startsWith("'") || risky(s)) "'$s" else s
    private fun unprotect(s: String) = if (s.startsWith("'") && (s.drop(1).startsWith("'") || risky(s.drop(1)))) s.drop(1) else s
    private fun cell(s: String) = "\"" + protect(s).replace("\"", "\"\"") + "\""
    // Lists are readable one item per line. Escaping also preserves literal line breaks in an item.
    private fun pack(items: List<String>) = items.joinToString("\n") { if (it.isEmpty()) "\\0" else it.replace("\\", "\\\\").replace("\r", "\\r").replace("\n", "\\n") }
    private fun unpack(s: String): List<String> = if (s.isEmpty()) emptyList() else s.split('\n').map { line ->
        if (line == "\\0") "" else buildString { var i = 0; while (i < line.length) { val c = line[i++]; if (c == '\\') {
            require(i < line.length) { "CSV 列表转义不完整" }; append(when(val n = line[i++]) { 'n' -> '\n'; 'r' -> '\r'; '\\' -> '\\'; else -> error("CSV 列表转义无效：$n") })
        } else append(c) } }
    }
    fun encode(entries: List<Entry>): ByteArray {
        ArchiveJson.validate(entries)
        return buildString {
            append('\uFEFF'); append(headers.joinToString(",", transform = ::cell)); append("\r\n")
            entries.forEach { e ->
                val a = e.archive; val ai = e.aiResult
                val fields = listOf(e.word, e.phonetic, e.translation, e.definition, e.notes, a.sourceType, a.sourceTitle, a.sourceExcerpt, pack(a.tags), a.uuid,
                    e.stage.toString(), e.status, e.createdAt, e.learningStartDate, e.nextReviewDate.orEmpty(), e.lastReviewedAt.orEmpty(), e.reviewCount.toString(), a.encounterCount.toString(), a.revision.toString(), a.createdAtUtc, a.updatedAtUtc, a.lastEncounteredAtUtc,
                    if (ai == null) "否" else "是", pack(ai?.examples?.map { it.english }.orEmpty()), pack(ai?.examples?.map { it.chinese }.orEmpty()), pack(ai?.synonyms.orEmpty()), pack(ai?.antonyms.orEmpty()), pack(ai?.phrases?.map { it.en }.orEmpty()), pack(ai?.phrases?.map { it.zh }.orEmpty()))
                append(fields.joinToString(",", transform = ::cell)); append("\r\n")
            }
        }.toByteArray(Charsets.UTF_8).also { require(it.size <= ArchiveJson.MAX_BYTES) { "CSV 超过 50 MB" } }
    }
    fun decode(bytes: ByteArray): List<Entry> {
        require(bytes.size <= ArchiveJson.MAX_BYTES) { "CSV 超过 50 MB" }
        val decoder = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
        val rows = parse(decoder.decode(ByteBuffer.wrap(bytes)).toString().removePrefix("\uFEFF"))
        require(rows.isNotEmpty()) { "CSV 为空" }
        val legacy = rows.first().any { it.trim() in listOf("档案标识", "AI例句", "AI同义词", "AI反义词", "AI词组") }
        val aliases = mapOf("学习阶段" to "阶段", "创建时间" to "入库日期", "学习开始日期" to "学习起始日期", "下次重逢日期" to "下次复习日期", "档案标识" to "UUID", "来源标题" to "来源", "首次收藏UTC" to "创建时间UTC", "最近遇见UTC" to "最后遇见时间UTC", "修订版本" to "版本")
        val names = rows.first().map { aliases[it.trim()] ?: it }.map { when(it.trim()) { "word" -> "单词"; "translation", "释义" -> "中文释义"; "phonetic" -> "音标"; else -> it.trim() } }
        require(names.size == names.toSet().size && "单词" in names) { "CSV 需要单词列，且列名不能重复" }
        require(names.all { it in headers || (legacy && it in listOf("AI例句", "AI同义词", "AI反义词", "AI词组")) }) { "CSV 包含不支持的列名" }
        return rows.drop(1).filterNot { it.all(String::isBlank) }.mapIndexed { index, values ->
            require(values.size == names.size) { "CSV 第 ${index + 2} 行列数不一致" }
            val row = names.zip(values.map(::unprotect)).toMap(); val base = Entry()
            fun s(name: String, default: String = "") = row[name] ?: default
            fun num(name: String, default: Int) = row[name]?.let { it.toIntOrNull() ?: error("CSV 第 ${index + 2} 行 $name 必须为整数") } ?: default
            fun pairs(en: String, zh: String): List<Pair<String,String>> {
                val left = unpack(s(en)); val right = unpack(s(zh)); require(left.size == right.size) { "CSV $en 与 $zh 数量不一致" }; return left.zip(right)
            }
            fun legacyList(name: String) = s(name).split("; ").filter(String::isNotBlank)
            fun legacyPairs(name: String) = s(name).split('\n').filter(String::isNotBlank).map { line ->
                val separator = line.indexOf(" / ")
                if (separator < 0) line to "" else line.substring(0, separator) to line.substring(separator + 3)
            }
            val status = when(val raw = s("状态", "learning")) { "学习中" -> "learning"; "已掌握" -> "mastered"; else -> raw }; val stage = num("阶段", if(status == "mastered") 5 else 0)
            val hasAi = s("包含AI扩展", if(listOf("例句英文", "同义词", "反义词", "常用词组", "AI例句", "AI同义词", "AI反义词", "AI词组").any { s(it).isNotEmpty() }) "是" else "否")
            require(hasAi in listOf("是", "否")) { "包含AI扩展应为是或否" }
            Entry(word = s("单词").trim(), phonetic = s("音标"), translation = s("中文释义"), definition = s("英文释义"), notes = s("备注"),
                stage = stage, status = status, createdAt = s("入库日期", base.createdAt), learningStartDate = s("学习起始日期", base.learningStartDate),
                nextReviewDate = if (row.containsKey("下次复习日期")) s("下次复习日期").ifBlank { null } else if(status == "mastered") null else base.nextReviewDate,
                lastReviewedAt = s("上次复习时间").ifBlank { null }, reviewCount = num("复习次数", 0),
                archive = ArchiveMetadata(uuid = s("UUID", base.archive.uuid), sourceType = s("来源类型"), sourceTitle = s("来源"), sourceExcerpt = s("来源原句"), tags = if (legacy) legacyList("标签") else unpack(s("标签")), encounterCount = num("遇见次数", 1), revision = num("版本", 1), createdAtUtc = s("创建时间UTC", base.archive.createdAtUtc), updatedAtUtc = s("更新时间UTC", base.archive.updatedAtUtc), lastEncounteredAtUtc = s("最后遇见时间UTC", base.archive.lastEncounteredAtUtc)),
                aiResult = if (hasAi == "否") null else if (legacy) AIResult(
                    examples = legacyPairs("AI例句").map { Example(it.first, it.second) }, synonyms = legacyList("AI同义词"), antonyms = legacyList("AI反义词"), phrases = legacyPairs("AI词组").map { Phrase(it.first, it.second) }
                ) else AIResult(examples = pairs("例句英文", "例句中文").map { Example(it.first,it.second) }, synonyms = unpack(s("同义词")), antonyms = unpack(s("反义词")), phrases = pairs("常用词组", "词组中文").map { Phrase(it.first,it.second) }))
        }.also(ArchiveJson::validate)
    }
    private fun parse(source: String): List<List<String>> {
        val rows = mutableListOf<List<String>>(); var row = mutableListOf<String>(); val field = StringBuilder()
        var quoted = false; var ended = false; var i = 0
        fun finishField() { row.add(field.toString()); field.setLength(0); ended = false; require(row.size <= 64) { "CSV 列数过多" } }
        fun finishRow() { finishField(); rows.add(row); row = mutableListOf(); require(rows.size <= ArchiveJson.MAX_ENTRIES + 1) { "词条超过 100000 条" } }
        while(i < source.length) {
            val c = source[i++]
            if (quoted) { if(c == '"') { if(i < source.length && source[i] == '"') { field.append('"'); i++ } else { quoted = false; ended = true } } else field.append(c) }
            else when(c) {
                '"' -> { require(field.isEmpty() && !ended) { "CSV 引号位置无效" }; quoted = true }
                ',' -> finishField()
                '\r', '\n' -> { if(c == '\r' && i < source.length && source[i] == '\n') i++; finishRow() }
                else -> { require(!ended) { "CSV 引号后存在多余字符" }; field.append(c) }
            }
        }
        require(!quoted) { "CSV 引号未闭合" }
        if(field.isNotEmpty() || row.isNotEmpty() || ended) finishRow()
        return rows
    }
}
