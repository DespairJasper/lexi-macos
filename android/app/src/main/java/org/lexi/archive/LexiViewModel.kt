package org.lexi.archive

import android.app.Application
import android.net.Uri
import androidx.compose.runtime.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.*
import org.lexi.archive.data.*
import org.lexi.archive.services.*
import java.time.LocalDate

class LexiViewModel(app: Application) : AndroidViewModel(app) {
    private val repo = LexiRepository(app)
    private val dictionary = DictionaryService(app)
    private val preferences = SettingsStore(app)
    var entries by mutableStateOf<List<Entry>>(emptyList()); private set
    var lookup by mutableStateOf<LookupResult?>(null); private set
    var query by mutableStateOf("")
    var busy by mutableStateOf(false); private set
    var aiBusy by mutableStateOf(false); private set
    var initialized by mutableStateOf(false); private set
    var statusVersion by mutableIntStateOf(0); private set
    private var statusText by mutableStateOf("正在打开私人词汇档案…")
    var status: String
        get() = statusText
        private set(value) { statusText = value; statusVersion++ }
    var ai by mutableStateOf<AIResult?>(null); private set
    var savedPdf by mutableStateOf<SavedPdf?>(null); private set
    val aiDrafts = mutableStateMapOf<Long, AIResult>()
    fun dismissPdf() { savedPdf = null }
    var config by mutableStateOf(AIConfig())
    var apiKey by mutableStateOf("")
    var rememberKey by mutableStateOf(false)
    var theme by mutableStateOf("system")
    var reduceMotion by mutableStateOf(false)
    var incomingVersion by mutableIntStateOf(0); private set
    private var savingSettings = false
    val dataLocation get() = repo.dataLocation
    val due get() = entries.filter { it.status == "learning" && it.nextReviewDate != null && it.nextReviewDate <= LocalDate.now().toString() }.sortedWith(compareBy<Entry> { it.nextReviewDate }.thenBy { it.id })
    private var lookupJob: Job? = null
    private var aiJob: Job? = null
    private var lookupGeneration = 0
    private var aiGeneration = 0

    init {
        viewModelScope.launch {
            try {
                entries = repo.all()
                lookup?.let { result -> if (!aiBusy && ai == null) ai = entries.firstOrNull { it.word.equals(result.word, true) }?.aiResult }
                initialized = true; status = "离线词典 · 按需 AI · 私人档案"
            }
            catch (e: Exception) { status = "词库无法打开，已保留原文件：${e.message}" }
            try {
                withContext(Dispatchers.IO) {
                    val loaded = preferences.loadConfig()
                    val t = preferences.theme; val r = preferences.reduceMotion
                    withContext(Dispatchers.Main) { config = loaded; theme = t; reduceMotion = r }
                    val key = preferences.loadKey()
                    withContext(Dispatchers.Main) { apiKey = key; rememberKey = key.isNotEmpty() }
                }
            } catch (e: Exception) { status = e.message ?: "设置读取失败" }
        }
    }

    fun report(message: String) { status = message }
    fun handleIncoming(text: String) {
        val candidate = text.trim()
        if (candidate.length !in 1..512) { report("选中文字过长，请在查词页输入需要查询的单词。"); return }
        query = candidate; incomingVersion++; search(candidate)
    }
    fun search(text: String = query) {
        val clean = text.trim()
        if (clean.length !in 1..512) { report("请输入 1–512 个字符。"); return }
        lookupJob?.cancel(); cancelAI(); val generation = ++lookupGeneration
        query = clean; lookup = null; ai = null
        lookupJob = viewModelScope.launch {
            try {
                val result = dictionary.lookup(clean)
                if (generation == lookupGeneration) {
                    lookup = result
                    ai = entries.firstOrNull { it.word.equals(result.word, true) }?.aiResult
                    status = if (result.found) "查询完成 · 无网络请求" else "离线词典未收录，可手动填写释义后收藏。"
                }
            } catch (_: CancellationException) { }
            catch (e: Exception) { if (generation == lookupGeneration) report(e.message ?: "查询失败") }
        }
    }
    private fun mutate(message: String, allowRecovery: Boolean = false, block: suspend () -> Unit) {
        if (busy || (!initialized && !allowRecovery)) return
        busy = true
        viewModelScope.launch {
            try { block(); entries = repo.all(); initialized = true; if (message.isNotEmpty()) status = message }
            catch (_: CancellationException) { }
            catch (e: Exception) { status = e.message ?: "未完成操作，原数据保留" }
            finally { busy = false }
        }
    }
    fun addCurrent() {
        val r = lookup ?: return
        if (entries.any { it.word.equals(r.word, true) }) { report("已加入词汇档案"); return }
        mutate("已收藏，首次重逢安排在明天。") { repo.save(Entry(word = r.word, phonetic = r.phonetic, translation = r.translation, definition = r.definition)) }
    }
    fun save(entry: Entry) { cancelAI(); mutate("档案已保存") { repo.save(entry) } }
    fun saveEntryAI(entry: Entry) {
        val draft = aiDrafts[entry.id] ?: return
        mutate("AI 内容已保存") { repo.save(entry.copy(aiResult = draft)); aiDrafts.remove(entry.id) }
    }
    fun saveLookupAI() {
        val word = lookup?.word ?: return
        val entry = entries.firstOrNull { it.word.equals(word, true) } ?: return
        val result = ai ?: return
        save(entry.copy(aiResult = result))
    }
    fun delete(ids: Set<Long>) { cancelAI(); mutate("已移出选中的词条") { repo.delete(ids) } }
    fun rate(id: Long, remembered: Boolean) = mutate(if (remembered) "已记下这次重逢" else "明日再见") { repo.review(id, remembered) }
    fun today(ids: Set<Long>) = mutate("已置入今日重逢") { repo.scheduleToday(ids) }
    fun stage(ids: Set<Long>, stage: Int) = mutate("已调整周期") { repo.setStage(ids, stage) }
    fun master(ids: Set<Long>) = mutate("已标记掌握") { repo.markMastered(ids) }
    fun encounter(id: Long) = mutate("已记录再次遇见") { repo.encounter(id) }
    fun undo(id: Long) = mutate("已撤销上次复习") { repo.undo(id) }
    fun cancelAI() { aiGeneration++; aiJob?.cancel(); aiBusy = false }
    fun generate(modules: Set<AIModule>, entry: Entry? = null) {
        val word = entry?.word ?: lookup?.word ?: return
        if (aiBusy || busy || modules.isEmpty()) return
        if (apiKey.isBlank()) { report("请先在设置中填写自己的 API Key。"); return }
        val generation = ++aiGeneration
        val cfg = config; val key = apiKey
        val current = entry ?: entries.firstOrNull { it.word.equals(word, true) }
        val source = if (cfg.includeSource) current?.archive?.sourceExcerpt.orEmpty() else ""
        aiBusy = true
        aiJob = viewModelScope.launch {
            try {
                val result = AIClient.generate(word, modules, cfg, key, source)
                ensureActive()
                if (generation != aiGeneration) return@launch
                if (entry != null) {
                    val latest = repo.all().firstOrNull { it.id == entry.id }
                    if (latest?.archive?.revision != entry.archive.revision) { report("档案已修改，本次生成未覆盖它。"); return@launch }
                    aiDrafts[entry.id] = result
                } else ai = result
                status = "AI 扩展完成 · 仅生成所选内容"
            } catch (_: CancellationException) { }
            catch (e: Exception) { if (generation == aiGeneration) report(e.message ?: "生成失败") }
            finally { if (generation == aiGeneration) aiBusy = false }
        }
    }
    fun saveSettings() {
        if (savingSettings) return
        savingSettings = true
        val cfg = config; val key = apiKey; val remember = rememberKey; val t = theme; val motion = reduceMotion
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) { preferences.saveConfig(cfg); preferences.theme = t; preferences.reduceMotion = motion; if (remember) preferences.saveKey(key) else preferences.clearKey() }
                report("设置已保存")
            } catch (e: Exception) { report(e.message ?: "设置保存失败") }
            finally { savingSettings = false }
        }
    }
    fun importFile(uri: Uri, restore: Boolean = false) {
        cancelAI()
        mutate(if (restore) "已恢复 Android 备份" else "", allowRecovery = restore) {
            val data = withContext(Dispatchers.IO) {
                getApplication<Application>().contentResolver.openInputStream(uri)?.use { stream ->
                    val out = java.io.ByteArrayOutputStream(); val buffer = ByteArray(8192)
                    while (true) { val n = stream.read(buffer); if (n < 0) break; require(out.size() + n <= 64 * 1024 * 1024) { "文件超过 64 MB" }; out.write(buffer, 0, n) }
                    out.toByteArray()
                } ?: error("无法读取文件")
            }
            if (restore) repo.restore(data) else {
                val result = withContext(Dispatchers.IO) {
                    // Keep existing desktop transfer files readable, while CSV is the public default.
                    val legacy = data.toString(Charsets.UTF_8).trimStart().startsWith("{")
                    repo.importJSON(if (legacy) data else ArchiveJson.encode(ArchiveCsv.decode(data)))
                }
                withContext(Dispatchers.Main) { report(result) }
            }
        }
    }
    fun savePdf(kind: String, ids: Set<Long>?) {
        val selected = entries.filter { ids == null || it.id in ids }.toList()
        if (selected.isEmpty()) { report("请先收藏或选择要导出的词条"); return }
        mutate("") {
            savedPdf = withContext(Dispatchers.IO) { PdfStorage.save(getApplication(), selected, kind == "archive_pdf") }
            report("PDF 已保存到 下载 / Lexi词汇库")
        }
    }
    fun writeFile(uri: Uri, kind: String, ids: Set<Long>? = null) {
        mutate("文件已导出") {
            if (kind == "pdf") {
                val selected = entries.filter { ids == null || it.id in ids }.toList()
                withContext(Dispatchers.IO) {
                    getApplication<Application>().contentResolver.openOutputStream(uri, "wt")?.use { PdfExporter.write(selected, it) } ?: error("无法写入所选位置")
                }
                return@mutate
            }
            val data = when (kind) { "backup" -> repo.backup(); "html" -> printHtml(ids).toByteArray(Charsets.UTF_8); else -> ArchiveCsv.encode(entries.filter { ids == null || it.id in ids }) }
            withContext(Dispatchers.IO) { getApplication<Application>().contentResolver.openOutputStream(uri, "wt")?.use { it.write(data) } ?: error("无法写入所选位置") }
        }
    }
    fun printHtml(ids: Set<Long>? = null): String {
        fun escape(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")
        val rows = entries.filter { ids == null || it.id in ids }.joinToString("") {
            "<tr><td><b>${escape(it.word)}</b><br>${escape(it.phonetic)}</td><td>${escape(it.translation)}<br><small>${escape(it.archive.sourceTitle)} ${escape(it.notes)}</small></td><td>□1 □2 □4 □7 □15</td></tr>"
        }
        return "<!doctype html><html lang='zh'><meta charset='utf-8'><title>Lexi 私人词汇档案</title><style>@page{size:A4;margin:15mm}body{font:12px sans-serif;color:#18201e}table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:10px;border-bottom:1px solid #bbb;white-space:pre-wrap}tr{break-inside:avoid}small{color:#555}</style><h1>Lexi · 私人词汇档案</h1><table><tr><th>词条</th><th>释义与备注</th><th>复习打卡</th></tr>$rows</table></html>"
    }
}
