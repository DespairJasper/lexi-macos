package org.lexi.archive.data

import android.content.ContentValues
import android.content.Context
import android.database.DatabaseErrorHandler
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import java.time.LocalDate
import java.util.UUID

/** One serialized writer; DELETE journaling makes a closed database a complete native backup. */
class LexiRepository(context: Context) {
    private val app = context.applicationContext
    private val file = app.getDatabasePath("lexi.sqlite")
    val dataLocation: String get() = file.absolutePath
    private var helper: Store? = null
    private var verified = false
    private companion object {
        val gate = Mutex()
        val offsets = intArrayOf(1, 2, 4, 7, 15)
        const val MAX_BACKUP = 256 * 1024 * 1024
        const val APPLICATION_ID = 0x4c455849
        val corruptionHandler = DatabaseErrorHandler { throw IllegalStateException("词库完整性异常；原文件已保留，请先备份后处理。") }
    }
    private class Store(context: Context) : SQLiteOpenHelper(context, "lexi.sqlite", null, 1, corruptionHandler) {
        override fun onCreate(db: SQLiteDatabase) {
            db.execSQL("PRAGMA application_id=$APPLICATION_ID")
            db.execSQL("CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT, word_key TEXT NOT NULL UNIQUE, uuid TEXT NOT NULL UNIQUE, payload TEXT NOT NULL)")
            db.execSQL("CREATE TABLE undo(word_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE, payload TEXT NOT NULL)")
        }
        override fun onConfigure(db: SQLiteDatabase) { db.setForeignKeyConstraintsEnabled(true) }
        override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) { error("不支持的词库版本，原文件已保留") }
        override fun onDowngrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) { error("此词库来自更新版本，请更新应用") }
    }
    private fun db(): SQLiteDatabase {
        if (helper == null) {
            if (!file.exists()) {
                require(listOf("-wal", "-shm", "-journal").none { File(file.path + it).exists() }) { "发现残留词库日志；停止新建以保留数据" }
            } else {
                // Verify before SQLiteOpenHelper can mistake an unrelated version-0 file for a new database.
                require(file.length() in 100..MAX_BACKUP.toLong()) { "词库为空或超出大小限制；原文件已保留" }
                SQLiteDatabase.openDatabase(file.path, null, SQLiteDatabase.OPEN_READONLY, corruptionHandler).use(::verifyDatabase)
            }
            helper = Store(app)
        }
        val database = helper!!.writableDatabase
        if (!verified) {
            database.rawQuery("PRAGMA journal_mode=DELETE", null).use { require(it.moveToFirst() && it.getString(0).equals("delete", true)) { "无法准备安全词库日志" } }
            verifyDatabase(database); verified = true
        }
        return database
    }
    private fun close() { helper?.close(); helper = null; verified = false }
    private suspend fun <T> locked(block: () -> T): T = withContext(Dispatchers.IO) { gate.withLock { block() } }
    private fun <T> transaction(database: SQLiteDatabase, block: () -> T): T {
        database.beginTransaction()
        try { return block().also { database.setTransactionSuccessful() } } finally { database.endTransaction() }
    }
    private fun readAll(database: SQLiteDatabase): List<Entry> {
        val result = ArrayList<Entry>()
        database.rawQuery("SELECT id,word_key,uuid,payload FROM entries ORDER BY id DESC", null).use { c ->
            while (c.moveToNext()) {
                require(result.size < ArchiveJson.MAX_ENTRIES) { "词条超过数量限制" }
                val entry = ArchiveJson.decodeEntry(c.getString(3))
                require(entry.id > 0 && entry.id == c.getLong(0) && ArchiveJson.wordKey(entry.word) == c.getString(1) && entry.archive.uuid.lowercase(java.util.Locale.ROOT) == c.getString(2)) { "词库索引与记录不一致" }
                result.add(entry)
            }
        }
        ArchiveJson.validate(result)
        return result
    }
    private fun get(database: SQLiteDatabase, id: Long): Entry = database.rawQuery("SELECT payload FROM entries WHERE id=?", arrayOf(id.toString())).use {
        require(it.moveToFirst()) { "词条已不存在" }; ArchiveJson.decodeEntry(it.getString(0))
    }
    private fun put(database: SQLiteDatabase, e: Entry) {
        val values = ContentValues().apply {
            put("word_key", ArchiveJson.wordKey(e.word)); put("uuid", e.archive.uuid.lowercase(java.util.Locale.ROOT)); put("payload", ArchiveJson.encodeEntry(e))
        }
        require(database.update("entries", values, "id=?", arrayOf(e.id.toString())) == 1) { "词条已不存在" }
    }
    private fun insert(database: SQLiteDatabase, e: Entry): Entry {
        require(android.database.DatabaseUtils.longForQuery(database, "SELECT count(*) FROM entries", null) < ArchiveJson.MAX_ENTRIES) { "词条超过数量限制" }
        val values = ContentValues().apply {
            put("word_key", ArchiveJson.wordKey(e.word)); put("uuid", e.archive.uuid.lowercase(java.util.Locale.ROOT)); put("payload", ArchiveJson.encodeEntry(e.copy(id = 0)))
        }
        val id = database.insertOrThrow("entries", null, values)
        return e.copy(id = id).also { put(database, it) }
    }
    private fun snapshot(database: SQLiteDatabase, e: Entry) {
        database.insertWithOnConflict("undo", null, ContentValues().apply { put("word_id", e.id); put("payload", ArchiveJson.encodeEntry(e)) }, SQLiteDatabase.CONFLICT_REPLACE).also { require(it != -1L) { "无法保存撤销记录" } }
    }
    private fun touched(e: Entry): Entry = e.copy(archive = e.archive.copy(updatedAtUtc = utcNow(), revision = Math.addExact(e.archive.revision, 1)))
    private fun clearUndo(database: SQLiteDatabase, id: Long) { database.delete("undo", "word_id=?", arrayOf(id.toString())) }
    suspend fun all(): List<Entry> = locked { readAll(db()) }
    suspend fun save(entry: Entry): Entry = locked {
        val database = db()
        transaction(database) {
            if (entry.id == 0L) insert(database, entry.copy(word = entry.word.trim()))
            else {
                val old = get(database, entry.id)
                require(old.archive.uuid.equals(entry.archive.uuid, true)) { "不能更改档案标识" }
                require(old.archive.revision == entry.archive.revision) { "词条已更新，请重新打开后保存" }
                val updated = touched(entry.copy(word = entry.word.trim()))
                put(database, updated); clearUndo(database, entry.id); updated
            }
        }
    }
    suspend fun delete(ids: Set<Long>) = locked {
        val database = db(); transaction(database) { ids.forEach { database.delete("entries", "id=?", arrayOf(it.toString())) } }
    }
    private fun change(ids: Set<Long>, preserveUndo: Boolean = false, transform: (Entry) -> Entry) {
        val database = db()
        transaction(database) { ids.forEach { id ->
            val previous = get(database, id)
            val next = transform(previous)
            if (next != previous) {
                if (preserveUndo) snapshot(database, previous) else clearUndo(database, id)
                put(database, touched(next))
            }
        } }
    }
    suspend fun review(id: Long, remembered: Boolean) = locked {
        change(setOf(id), true) { e ->
            val today = LocalDate.now(); val tomorrow = today.plusDays(1).toString()
            if (!remembered) e.copy(stage = minOf(e.stage, 4), status = "learning", nextReviewDate = tomorrow)
            else if (e.status == "mastered") e
            else if (e.lastReviewedAt?.take(10) == today.toString()) {
                // Explicitly consume a same-day reassignment without awarding another stage.
                if (e.nextReviewDate != null && LocalDate.parse(e.nextReviewDate) <= today) e.copy(nextReviewDate = tomorrow) else e
            } else {
                val stage = e.stage + 1
                val reviewed = e.copy(stage = stage, lastReviewedAt = LocalDateTime.now().format(DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm:ss")), reviewCount = Math.addExact(e.reviewCount, 1))
                if (stage == 5) reviewed.copy(status = "mastered", nextReviewDate = null)
                else {
                    val late = e.nextReviewDate?.let { LocalDate.parse(it) < today } == true
                    val planned = if (late) today.plusDays((offsets[stage] - offsets[e.stage]).toLong()) else LocalDate.parse(e.learningStartDate).plusDays(offsets[stage].toLong())
                    val next = if (planned > today) planned else today.plusDays(1)
                    reviewed.copy(nextReviewDate = next.toString(), learningStartDate = if (late) next.minusDays(offsets[stage].toLong()).toString() else e.learningStartDate)
                }
            }
        }
    }
    suspend fun scheduleToday(ids: Set<Long>) = locked { change(ids) { it.copy(stage = minOf(it.stage, 4), status = "learning", nextReviewDate = LocalDate.now().toString()) } }
    suspend fun setStage(ids: Set<Long>, stage: Int) = locked {
        require(stage in 0..5) { "阶段必须在 0–5 之间" }
        change(ids) { e -> if (stage == 5) e.copy(stage = 5, status = "mastered", nextReviewDate = null)
            else e.copy(stage = stage, status = "learning", learningStartDate = LocalDate.now().toString(), nextReviewDate = LocalDate.now().plusDays(offsets[stage].toLong()).toString()) }
    }
    suspend fun markMastered(ids: Set<Long>) = locked { change(ids) { it.copy(stage = 5, status = "mastered", nextReviewDate = null, lastReviewedAt = LocalDateTime.now().format(DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm:ss"))) } }
    suspend fun encounter(id: Long) = locked { change(setOf(id)) { it.copy(archive = it.archive.copy(encounterCount = Math.addExact(it.archive.encounterCount, 1), lastEncounteredAtUtc = utcNow())) } }
    suspend fun undo(id: Long) = locked {
        val database = db(); transaction(database) {
            val previous = database.rawQuery("SELECT payload FROM undo WHERE word_id=?", arrayOf(id.toString())).use {
                require(it.moveToFirst()) { "此词条没有可撤销的复习" }; ArchiveJson.decodeEntry(it.getString(0))
            }
            require(previous.id == id && previous.archive.uuid == get(database, id).archive.uuid) { "撤销记录与词条不一致" }
            put(database, previous); clearUndo(database, id)
        }
    }
    suspend fun importJSON(data: ByteArray): String = locked {
        val imported = ArchiveJson.decode(data); val database = db()
        transaction(database) {
            val existing = readAll(database)
            val words = existing.map { ArchiveJson.wordKey(it.word) }.toHashSet()
            val uuids = existing.map { UUID.fromString(it.archive.uuid) }.toHashSet()
            var count = 0
            imported.forEach { e ->
                if (ArchiveJson.wordKey(e.word) !in words && UUID.fromString(e.archive.uuid) !in uuids) {
                    insert(database, e); count++; words.add(ArchiveJson.wordKey(e.word)); uuids.add(UUID.fromString(e.archive.uuid))
                }
            }
            "已导入 $count 条；跳过 ${imported.size - count} 条已有单词或档案标识冲突的词条。"
        }
    }
    suspend fun exportJSON(ids: Set<Long>? = null): ByteArray = locked { ArchiveJson.encode(readAll(db()).filter { ids == null || it.id in ids }) }
    suspend fun backup(): ByteArray = locked {
        db(); close()
        require(file.length() <= MAX_BACKUP) { "原生备份超过 256 MB" }
        SQLiteDatabase.openDatabase(file.path, null, SQLiteDatabase.OPEN_READONLY, corruptionHandler).use(::verifyDatabase)
        file.readBytes()
    }
    suspend fun restore(data: ByteArray): Unit = locked {
        require(data.size in 100..MAX_BACKUP) { "备份文件大小无效（上限 256 MB）" }
        require(data.take(16).toByteArray().contentEquals("SQLite format 3\u0000".toByteArray(Charsets.US_ASCII))) { "请选择 Android 原生 SQLite 备份；Windows 数据请通过 JSON 导入" }
        file.parentFile!!.mkdirs()
        val candidate = File(file.parentFile, "restore-${UUID.randomUUID()}.sqlite")
        try {
            writeDurably(candidate, data)
            SQLiteDatabase.openDatabase(candidate.path, null, SQLiteDatabase.OPEN_READONLY, corruptionHandler).use(::verifyDatabase)
            // Finish all writes and retain a complete safety snapshot before the atomic replacement.
            db(); close()
            val safety = File(file.parentFile, "before-restore-${System.currentTimeMillis()}-${UUID.randomUUID()}.sqlite")
            Files.copy(file.toPath(), safety.toPath())
            FileOutputStream(safety, true).use { it.fd.sync() }
            try {
                Files.move(candidate.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
                db()
                Unit
            } catch (failure: Exception) {
                close()
                val rollback = File(file.parentFile, "rollback-${UUID.randomUUID()}.sqlite")
                try {
                    Files.copy(safety.toPath(), rollback.toPath())
                    Files.move(rollback.toPath(), file.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
                } catch (rollbackFailure: Exception) {
                    failure.addSuppressed(rollbackFailure)
                    throw IllegalStateException("恢复失败；安全副本保存在 ${safety.absolutePath}，请保留文件。", failure)
                } finally { rollback.delete() }
                throw IllegalStateException("恢复失败，原词库已保留。", failure)
            }
        } finally { candidate.delete() }
    }
    private fun writeDurably(target: File, bytes: ByteArray) { FileOutputStream(target).use { it.write(bytes); it.fd.sync() } }
    private fun verifyDatabase(database: SQLiteDatabase) {
        fun scalar(sql: String): Long = android.database.DatabaseUtils.longForQuery(database, sql, null)
        require(scalar("PRAGMA application_id") == APPLICATION_ID.toLong() && scalar("PRAGMA user_version") == 1L) { "非本应用的 Android 原生备份，或版本不受支持；请用 JSON 跨平台迁移" }
        database.rawQuery("PRAGMA integrity_check", null).use { require(it.moveToFirst() && it.getString(0) == "ok" && !it.moveToNext()) { "词库完整性检查失败" } }
        database.rawQuery("PRAGMA foreign_key_check", null).use { require(!it.moveToFirst()) { "词库关联检查失败" } }
        database.rawQuery("PRAGMA journal_mode", null).use { require(it.moveToFirst() && it.getString(0).equals("delete", true)) { "备份必须是完整的 Android 原生备份（DELETE 日志模式）" } }
        val schemas = mutableMapOf<String, String>()
        database.rawQuery("SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", null).use { while (it.moveToNext()) schemas[it.getString(0)] = it.getString(1) }
        // Android's SQLite connection creates android_metadata for its locale.
        // It is platform-owned, optional in transferred backups, and not an app table.
        require(schemas.keys - "android_metadata" == setOf("entries", "undo")) { "词库表结构不匹配" }
        require(schemas["entries"] == "CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT, word_key TEXT NOT NULL UNIQUE, uuid TEXT NOT NULL UNIQUE, payload TEXT NOT NULL)" &&
            schemas["undo"] == "CREATE TABLE undo(word_id INTEGER PRIMARY KEY REFERENCES entries(id) ON DELETE CASCADE, payload TEXT NOT NULL)") { "词库约束不匹配" }
        require(scalar("SELECT count(*) FROM sqlite_master WHERE type IN ('trigger','view')") == 0L) { "词库包含不受支持的结构" }
        database.rawQuery("PRAGMA table_info(entries)", null).use { c ->
            val columns = mutableListOf<String>(); while(c.moveToNext()) columns.add(c.getString(1)); require(columns == listOf("id", "word_key", "uuid", "payload")) { "词库字段不匹配" }
        }
        database.rawQuery("PRAGMA table_info(undo)", null).use { c ->
            val columns = mutableListOf<String>(); while(c.moveToNext()) columns.add(c.getString(1)); require(columns == listOf("word_id", "payload")) { "撤销字段不匹配" }
        }
        val entries = readAll(database).associateBy { it.id }
        database.rawQuery("SELECT word_id,payload FROM undo", null).use { c ->
            val ids = hashSetOf<Long>()
            while (c.moveToNext()) {
                val previous = ArchiveJson.decodeEntry(c.getString(1)); val id = c.getLong(0)
                require(ids.add(id) && previous.id == id && entries[id]?.archive?.uuid == previous.archive.uuid) { "撤销记录无效" }
            }
        }
    }
}


