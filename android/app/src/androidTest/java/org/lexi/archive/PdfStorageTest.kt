package org.lexi.archive

import android.graphics.pdf.PdfRenderer
import android.os.Build
import android.provider.MediaStore
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.lexi.archive.data.Entry

@RunWith(AndroidJUnit4::class)
class PdfStorageTest {
    @Test fun publishesReadablePdfInLexiDownloadFolder() {
        assumeTrue(Build.VERSION.SDK_INT >= 29)
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val pdf = PdfStorage.save(context, listOf(Entry(word = "reason", phonetic = "/ˈriːzən/", translation = "原因；理由")))
        try {
            assertEquals("content", pdf.uri.scheme)
            assertTrue(pdf.name.contains("艾宾浩斯复习表"))
            context.contentResolver.query(pdf.uri, arrayOf(MediaStore.MediaColumns.RELATIVE_PATH, MediaStore.MediaColumns.IS_PENDING, MediaStore.MediaColumns.SIZE), null, null, null)!!.use {
                assertTrue(it.moveToFirst())
                assertEquals("Download/Lexi词汇库/", it.getString(0))
                assertEquals(0, it.getInt(1))
                assertTrue(it.getLong(2) > 0)
            }
            val descriptor = context.contentResolver.openFileDescriptor(pdf.uri, "r")!!
            PdfRenderer(descriptor).use { renderer ->
                assertTrue(renderer.pageCount > 0)
                renderer.openPage(0).use { page -> assertEquals(595, page.width); assertEquals(842, page.height) }
            }
        } finally { context.contentResolver.delete(pdf.uri, null, null) }
    }
}
