package org.lexi.archive.data

import org.json.JSONArray
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.text.Normalizer
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

/** Windows portable archive v1. Only explicitly listed archive fields ever leave the device. */
object ArchiveJson {
    const val MAX_BYTES = 50 * 1024 * 1024
    const val MAX_ENTRIES = 100_000
    internal fun wordKey(word: String): String = Normalizer.normalize(word.trim(), Normalizer.Form.NFC).lowercase(Locale.ROOT)
    private fun bad(message: String): Nothing = throw IllegalArgumentException(message)
    private fun JSONObject.fields(vararg names: String) {
        val expected = names.toSet()
        if (keys().asSequence().toSet() != expected) bad("JSON 字段缺失或包含未知字段")
    }
    private fun JSONObject.text(key: String): String = (get(key) as? String) ?: bad("字段 $key 必须为文本")
    private fun JSONObject.optionalText(key: String): String? = if (isNull(key)) null else text(key)
    private fun JSONObject.number(key: String): Long {
        val v = get(key)
        return when (v) { is Int -> v.toLong(); is Long -> v; else -> bad("字段 $key 必须为整数") }
    }
    private fun JSONObject.integer(key: String): Int = number(key).let {
        if (it !in Int.MIN_VALUE.toLong()..Int.MAX_VALUE.toLong()) bad("字段 $key 超出范围")
        it.toInt()
    }
    private fun JSONArray.objects(): List<JSONObject> = List(length()) { get(it) as? JSONObject ?: bad("列表包含无效对象") }
    private fun JSONArray.strings(): List<String> = List(length()) { get(it) as? String ?: bad("列表包含无效文本") }
    private fun strings(values: List<String>) = JSONArray().apply { values.forEach { put(it) } }
    private fun JSONObject.nullable(key: String, value: Any?) { put(key, value ?: JSONObject.NULL) }

    fun encode(entries: List<Entry>): ByteArray {
        validate(entries)
        val root = JSONObject().put("formatVersion", 1).put("exportedAtUtc", utcNow())
            .put("entries", JSONArray().apply { entries.forEach { put(entryObject(it)) } })
        return root.toString(2).toByteArray(Charsets.UTF_8).also { require(it.size <= MAX_BYTES) { "档案超过 50 MB" } }
    }
    fun decode(data: ByteArray): List<Entry> {
        require(data.size <= MAX_BYTES) { "档案超过 50 MB" }
        val decoder = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
        val text = decoder.decode(ByteBuffer.wrap(data)).toString().removePrefix("\uFEFF")
        val root = parse(text)
        root.fields("formatVersion", "exportedAtUtc", "entries")
        require(root.integer("formatVersion") == 1) { "不支持此档案版本" }
        utc(root.text("exportedAtUtc"))
        val array = root.getJSONArray("entries")
        require(array.length() <= MAX_ENTRIES) { "词条超过 100000 条" }
        return array.objects().map(::entryFromObject).also(::validate)
    }
    internal fun encodeEntry(entry: Entry): String { validate(listOf(entry)); return entryObject(entry).toString() }
    internal fun decodeEntry(text: String): Entry = entryFromObject(parse(text)).also { validate(listOf(it)) }
    private fun entryObject(e: Entry): JSONObject = JSONObject().apply {
        put("id", e.id); put("word", e.word); put("phonetic", e.phonetic); put("translation", e.translation)
        put("definition", e.definition); put("notes", e.notes); put("stage", e.stage); put("status", e.status)
        put("createdAt", e.createdAt); put("learningStartDate", e.learningStartDate)
        nullable("nextReviewDate", e.nextReviewDate); nullable("lastReviewedAt", e.lastReviewedAt); put("reviewCount", e.reviewCount)
        put("archive", JSONObject().apply {
            val a = e.archive
            put("uuid", a.uuid); put("sourceType", a.sourceType); put("sourceTitle", a.sourceTitle); put("sourceExcerpt", a.sourceExcerpt)
            put("tags", strings(a.tags)); put("encounterCount", a.encounterCount); put("revision", a.revision)
            put("createdAtUtc", a.createdAtUtc); put("updatedAtUtc", a.updatedAtUtc); put("lastEncounteredAtUtc", a.lastEncounteredAtUtc)
        })
        nullable("aiResult", e.aiResult?.let(::aiObject))
    }
    private fun entryFromObject(e: JSONObject): Entry {
        e.fields("id", "word", "phonetic", "translation", "definition", "notes", "stage", "status", "createdAt", "learningStartDate", "nextReviewDate", "lastReviewedAt", "reviewCount", "archive", "aiResult")
        val a = e.getJSONObject("archive")
        a.fields("uuid", "sourceType", "sourceTitle", "sourceExcerpt", "tags", "encounterCount", "revision", "createdAtUtc", "updatedAtUtc", "lastEncounteredAtUtc")
        return Entry(e.number("id"), e.text("word"), e.text("phonetic"), e.text("translation"), e.text("definition"), e.text("notes"),
            e.integer("stage"), e.text("status"), e.text("createdAt"), e.text("learningStartDate"), e.optionalText("nextReviewDate"),
            e.optionalText("lastReviewedAt"), e.integer("reviewCount"),
            ArchiveMetadata(a.text("uuid"), a.text("sourceType"), a.text("sourceTitle"), a.text("sourceExcerpt"), a.getJSONArray("tags").strings(),
                a.integer("encounterCount"), a.integer("revision"), a.text("createdAtUtc"), a.text("updatedAtUtc"), a.text("lastEncounteredAtUtc")),
            if (e.isNull("aiResult")) null else aiFromObject(e.getJSONObject("aiResult")))
    }
    fun encodeAI(ai: AIResult): String { validateAI(ai); return aiObject(ai).toString(2) }
    fun decodeAI(text: String): AIResult = aiFromObject(parse(text)).also(::validateAI)
    private fun aiObject(ai: AIResult): JSONObject = JSONObject()
        .put("examples", JSONArray().apply { ai.examples.forEach { put(JSONObject().put("english", it.english).put("chinese", it.chinese)) } })
        .put("synonyms", strings(ai.synonyms)).put("antonyms", strings(ai.antonyms))
        .put("phrases", JSONArray().apply { ai.phrases.forEach { put(JSONObject().put("en", it.en).put("zh", it.zh)) } })
    private fun aiFromObject(a: JSONObject): AIResult {
        a.fields("examples", "synonyms", "antonyms", "phrases")
        return AIResult(a.getJSONArray("examples").objects().map { it.fields("english", "chinese"); Example(it.text("english"), it.text("chinese")) },
            a.getJSONArray("synonyms").strings(), a.getJSONArray("antonyms").strings(),
            a.getJSONArray("phrases").objects().map { it.fields("en", "zh"); Phrase(it.text("en"), it.text("zh")) })
    }
    private fun text(value: String, max: Int, required: Boolean = false) {
        require(value.length <= max && !value.contains('\u0000') && (!required || value.isNotBlank())) { "文本为空、过长或包含空字符" }
    }
    private fun date(value: String, timestamp: Boolean = false) {
        text(value, 40, true)
        require(value.take(4).all(Char::isDigit) && value.length >= 10) { "日期格式无效" }
        if (value.length == 10) LocalDate.parse(value)
        else {
            require(timestamp) { "日期必须为 yyyy-MM-dd" }
            if (' ' in value) LocalDateTime.parse(value, DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm:ss").withResolverStyle(java.time.format.ResolverStyle.STRICT))
            else try { OffsetDateTime.parse(value) } catch (_: java.time.format.DateTimeParseException) { LocalDateTime.parse(value) }
        }
    }
    private fun utc(value: String) {
        text(value, 40, true)
        require(value.endsWith("Z") || value.endsWith("+00:00")) { "档案时间必须为 UTC" }
        require(OffsetDateTime.parse(value).offset == ZoneOffset.UTC) { "档案时间必须为 UTC" }
    }
    internal fun validate(entries: List<Entry>) {
        require(entries.size <= MAX_ENTRIES) { "词条超过 100000 条" }
        val words = HashSet<String>(); val uuids = HashSet<UUID>(); val ids = HashSet<Long>()
        entries.forEach { e ->
            text(e.word, 512, true); require(words.add(wordKey(e.word))) { "档案包含重复单词" }
            text(e.phonetic, 4096); text(e.translation, 100000); text(e.definition, 100000); text(e.notes, 100000)
            require(e.id >= 0 && (e.id == 0L || ids.add(e.id))) { "词条 ID 无效或重复" }
            require(e.stage in 0..5 && e.status in setOf("learning", "mastered") && e.reviewCount >= 0) { "复习进度无效" }
            require(if (e.status == "mastered") e.stage == 5 && e.nextReviewDate == null else e.stage < 5) { "复习状态与阶段不一致" }
            date(e.createdAt, true); date(e.learningStartDate); e.nextReviewDate?.let { date(it) }; e.lastReviewedAt?.let { date(it, true) }
            val a = e.archive; val uuid = UUID.fromString(a.uuid)
            require(uuid.toString().equals(a.uuid, true) && uuid != UUID(0, 0) && uuids.add(uuid)) { "档案标识无效或重复" }
            text(a.sourceType, 256); text(a.sourceTitle, 4096); text(a.sourceExcerpt, 100000)
            require(a.encounterCount >= 1 && a.revision >= 1 && a.tags.size <= 100) { "档案次数、版本或标签无效" }
            a.tags.forEach { text(it, 256, true) }; utc(a.createdAtUtc); utc(a.updatedAtUtc); utc(a.lastEncounteredAtUtc)
            e.aiResult?.let(::validateAI)
        }
    }
    private fun validateAI(ai: AIResult) {
        require(listOf(ai.examples.size, ai.synonyms.size, ai.antonyms.size, ai.phrases.size).all { it <= 1000 }) { "AI 列表过长" }
        ai.examples.forEach { text(it.english, 100000); text(it.chinese, 100000) }
        ai.phrases.forEach { text(it.en, 100000); text(it.zh, 100000) }
        (ai.synonyms + ai.antonyms).forEach { text(it, 4096) }
    }
    private fun parse(text: String): JSONObject {
        require(text.length <= MAX_BYTES) { "JSON 过大" }
        StrictJson(text).check()
        return JSONObject(text)
    }
    /** Android JSONTokener accepts comments, unquoted values and repeated keys. Reject these first. */
    private class StrictJson(private val source: String) {
        private var i = 0
        private fun ws() { while (i < source.length && source[i] in " \r\n\t") i++ }
        private fun take(c: Char): Boolean { ws(); return if (i < source.length && source[i] == c) { i++; true } else false }
        private fun need(c: Char) { require(take(c)) { "JSON 格式无效" } }
        fun check() { value(0); ws(); require(i == source.length) { "JSON 尾部包含额外内容" } }
        private fun string(): String {
            ws(); val start = i; need('"')
            while (i < source.length) {
                val c = source[i++]
                if (c == '"') return org.json.JSONTokener(source.substring(start, i)).nextValue() as String
                require(c >= ' ') { "JSON 字符串包含控制字符" }
                if (c == '\\') {
                    require(i < source.length) { "JSON 转义无效" }
                    val escape = source[i++]
                    require(escape in "\"\\/bfnrtu") { "JSON 转义无效" }
                    if (escape == 'u') { repeat(4) { require(i < source.length && source[i++].digitToIntOrNull(16) != null) { "JSON Unicode 转义无效" } } }
                }
            }
            bad("JSON 字符串未结束")
        }
        private fun value(depth: Int) {
            require(depth <= 32) { "JSON 嵌套过深" }; ws(); require(i < source.length) { "JSON 不完整" }
            when (source[i]) {
                '{' -> {
                    i++; val names = HashSet<String>(); if (take('}')) return
                    do { require(names.add(string())) { "JSON 含重复字段" }; need(':'); value(depth + 1) } while (take(','))
                    need('}')
                }
                '[' -> { i++; if (take(']')) return; do { value(depth + 1) } while (take(',')); need(']') }
                '"' -> string()
                else -> {
                    val start = i
                    while (i < source.length && source[i] !in " \r\n\t,]}") i++
                    val token = source.substring(start, i)
                    require(token in setOf("true", "false", "null") || token.matches(Regex("-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?"))) { "JSON 值无效" }
                }
            }
        }
    }
}
