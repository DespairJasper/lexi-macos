package org.lexi.archive

import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.lexi.archive.data.*
import org.lexi.archive.services.DictionaryService
import java.io.File

@RunWith(AndroidJUnit4::class)
class AcceptanceTest {
    // Runs exclusively in an emulator fixture install; never against a user phone.
    @Test fun dataAndPdf() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val repo = LexiRepository(context)
        val result = DictionaryService(context).lookup("reason")
        assertTrue("bundled dictionary", result.found)
        val old = repo.all().filter { it.word.startsWith("acceptance-") }.map { it.id }.toSet()
        repo.delete(old)
        val e = repo.save(Entry(word="acceptance-word", translation="验收释义", notes="中文备注", archive=ArchiveMetadata(sourceTitle="阅读来源", tags=listOf("测试"))))
        assertEquals("entry saved", "中文备注", repo.all().first { it.id == e.id }.notes)
        repo.scheduleToday(setOf(e.id))
        repo.review(e.id, true)
        val rated = repo.all().first { it.id == e.id }
        assertTrue("rated leaves today's queue", rated.nextReviewDate!! > java.time.LocalDate.now().toString())
        repo.undo(e.id)
        assertEquals(java.time.LocalDate.now().toString(), repo.all().first { it.id == e.id }.nextReviewDate)
        val transfer = repo.exportJSON(setOf(e.id))
        assertEquals(e.archive.uuid, ArchiveJson.decode(transfer).single().archive.uuid)
        val backup = repo.backup()
        repo.delete(setOf(e.id)); repo.restore(backup)
        assertTrue(repo.all().any { it.id == e.id })
        val pdf = File(context.filesDir,"acceptance-long.pdf")
        pdf.outputStream().use { PdfExporter.write(listOf(e.copy(definition=("A long sentence with 中文释义 and IPA /ˈriːzən/.\n").repeat(160), aiResult=AIResult(examples=listOf(Example("There is no reason to worry.","没有理由担心。"))))),it, archive = true) }
        PdfRenderer(ParcelFileDescriptor.open(pdf, ParcelFileDescriptor.MODE_READ_ONLY)).use { renderer ->
            assertTrue("long text paginated", renderer.pageCount > 1)
            for (i in 0 until renderer.pageCount) renderer.openPage(i).use { page ->
                assertEquals(595,page.width); assertEquals(842,page.height)
                if (i == 0 || i == renderer.pageCount - 1) {
                    val bitmap = android.graphics.Bitmap.createBitmap(1190,1684,android.graphics.Bitmap.Config.ARGB_8888)
                    bitmap.eraseColor(android.graphics.Color.WHITE)
                    page.render(bitmap,null,null,PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                    File(context.filesDir,"pdf-page-$i.png").outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,it) }
                    bitmap.recycle()
                }
            }
        }
        repo.delete(setOf(e.id))
    }
}
