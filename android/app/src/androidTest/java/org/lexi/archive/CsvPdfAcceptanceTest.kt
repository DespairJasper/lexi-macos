package org.lexi.archive

import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.lexi.archive.data.*
import java.io.File

@RunWith(AndroidJUnit4::class)
class CsvPdfAcceptanceTest {
    @Test fun csvRoundTripAndValidation() {
        val e = Entry(word="reason", translation="理由,原因\n\"中文\"", notes="=HYPERLINK(\"https://example.com\")", archive=ArchiveMetadata(tags=listOf("阅读", "literal\\n", "multi\nline")), aiResult=AIResult(examples=listOf(Example("A reason.\nIndeed.", "")), synonyms=listOf("'cause"), phrases=listOf(Phrase("by reason of", "由于"))))
        val bytes = ArchiveCsv.encode(listOf(e))
        assertTrue(bytes.toString(Charsets.UTF_8).startsWith("\uFEFF"))
        assertTrue(bytes.toString(Charsets.UTF_8).contains("'=HYPERLINK"))
        assertEquals(e, ArchiveCsv.decode(bytes).single())
        File(InstrumentationRegistry.getInstrumentation().targetContext.filesDir, "android-transfer.csv").writeBytes(bytes)
        assertEquals("苹果", ArchiveCsv.decode("单词,释义\r\napple,苹果\r\n".toByteArray()).single().translation)
        listOf("单词,释义\na,a\na,b", "单词,释义\n\"abc,x", "单词,释义\na,b,c", "单词,单词\na,b", "单词,阶段\na,7").forEach { malformed ->
            assertTrue("must reject malformed CSV", runCatching { ArchiveCsv.decode(malformed.toByteArray()) }.isFailure)
        }
        assertTrue(runCatching { ArchiveCsv.decode(byteArrayOf(0xff.toByte())) }.isFailure)
    }
    @Test fun importOldWindowsCsv() {
        val ctx=InstrumentationRegistry.getInstrumentation().targetContext
        val csv=InstrumentationRegistry.getInstrumentation().context.assets.open("fixtures/windows-legacy.csv").use { it.readBytes() }
        val entry=ArchiveCsv.decode(csv).single()
        assertEquals("résumé",entry.word)
        assertEquals("learning",entry.status)
        assertEquals(2,entry.stage)
        assertEquals("7b0a67a6-577c-4913-bb8d-7e18288d24df",entry.archive.uuid)
        assertEquals("Send a résumé.",entry.aiResult!!.examples.single().english)
        assertEquals("发送简历。",entry.aiResult!!.examples.single().chinese)
    }
    @Test fun importWindowsCurrentAndReexport() = kotlinx.coroutines.runBlocking {
        val inst=InstrumentationRegistry.getInstrumentation()
        val entries=ArchiveCsv.decode(inst.context.assets.open("fixtures/windows-current.csv").use { it.readBytes() })
        val repo=LexiRepository(inst.targetContext)
        repo.all().filter { it.archive.uuid in entries.map { e -> e.archive.uuid } }.forEach { repo.delete(setOf(it.id)) }
        repo.importJSON(ArchiveJson.encode(entries))
        val imported=repo.all().filter { it.archive.uuid in entries.map { e -> e.archive.uuid } }
        assertEquals(entries.map { it.copy(id=0) }, imported.map { it.copy(id=0) })
        File(inst.targetContext.filesDir,"windows-via-android.csv").writeBytes(ArchiveCsv.encode(imported))
        repo.delete(imported.map { it.id }.toSet())
    }
    @Test fun reviewPdfOmitsArchiveAndPaginates() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val entry = Entry(word="reason", phonetic="/ˈriːzən/", translation="理由；原因", definition="ENGLISH_DEFINITION_MUST_NOT_APPEAR", notes="仅档案显示\n".repeat(400), aiResult=AIResult(examples=listOf(Example("Long private expansion.\n".repeat(300),"中文扩展"))))
        fun pages(name: String, entries: List<Entry>, archive: Boolean): Int {
            val file = File(context.filesDir,name)
            file.outputStream().use { PdfExporter.write(entries,it,archive) }
            return PdfRenderer(ParcelFileDescriptor.open(file,ParcelFileDescriptor.MODE_READ_ONLY)).use { renderer ->
                renderer.openPage(0).use { assertEquals(595,it.width); assertEquals(842,it.height) }
                renderer.pageCount
            }
        }
        assertEquals(1,pages("review-default.pdf",listOf(entry),false))
        assertTrue(pages("archive-expanded.pdf",listOf(entry),true)>1)
        assertTrue(pages("review-many.pdf",List(50) { entry.copy(word="word-$it") },false)>1)
        assertTrue(pages("review-long.pdf",listOf(entry.copy(translation="很长的释义。\n".repeat(250))),false)>1)
    }
}
