package org.lexi.archive

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.core.content.FileProvider
import org.lexi.archive.data.Entry
import java.io.File
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import java.util.UUID

data class SavedPdf(val uri: Uri, val name: String)

/** Call on the IO dispatcher. Publish only a fully written PDF. */
object PdfStorage {
    const val FOLDER = "Lexi词汇库"
    fun save(context: Context, entries: List<Entry>, archive: Boolean = false): SavedPdf {
        require(entries.isNotEmpty()) { "请先选择需要导出的词汇。" }
        val stamp = LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss"))
        val name = "Lexi-${if (archive) "词汇档案" else "艾宾浩斯复习表"}-$stamp-${UUID.randomUUID().toString().take(4)}.pdf"
        return if (Build.VERSION.SDK_INT >= 29) {
            val resolver = context.contentResolver
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, "application/pdf")
                put(MediaStore.MediaColumns.RELATIVE_PATH, "${Environment.DIRECTORY_DOWNLOADS}/$FOLDER/")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: error("无法创建 PDF，请检查手机存储空间。")
            try {
                resolver.openOutputStream(uri, "w")?.use { PdfExporter.write(entries, it, archive) }
                    ?: error("无法打开 PDF 保存位置。")
                check(resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null) == 1) {
                    "PDF 保存未完成，请重试。"
                }
                SavedPdf(uri, name)
            } catch (failure: Throwable) {
                runCatching { resolver.delete(uri, null, null) }
                throw failure
            }
        } else {
            @Suppress("DEPRECATION")
            val folder = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), FOLDER)
            check(folder.isDirectory || folder.mkdirs()) { "无法创建 下载/$FOLDER，请允许存储访问并检查可用空间。" }
            val file = File(folder, name)
            // An incomplete file is not exposed as a PDF or handed to another app.
            val pending = File(folder, ".$name.pending")
            try {
                pending.outputStream().use { PdfExporter.write(entries, it, archive) }
                check(pending.renameTo(file)) { "无法完成 PDF 保存。" }
                SavedPdf(FileProvider.getUriForFile(context, "${context.packageName}.files", file), name)
            } catch (failure: Throwable) {
                pending.delete()
                file.delete()
                throw failure
            }
        }
    }
}
