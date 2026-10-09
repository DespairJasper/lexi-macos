package org.lexi.archive

import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.pdf.PdfDocument
import android.text.Layout
import android.text.StaticLayout
import android.text.TextPaint
import org.lexi.archive.data.Entry
import java.io.OutputStream

/** Native, paginated A4 export. No HTML, network or print-service dependency. */
object PdfExporter {
    fun write(entries: List<Entry>, output: OutputStream, archive: Boolean = false) {
        if (!archive) { writeReview(entries, output); return }
        val document = PdfDocument()
        var page: PdfDocument.Page? = null
        var number = 0
        var y = 0f
        val text = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(30, 42, 36); textSize = 11f; typeface = Typeface.create("sans-serif", Typeface.NORMAL) }
        fun finish() { page?.let { document.finishPage(it) }; page = null }
        fun next() {
            finish(); number++
            page = document.startPage(PdfDocument.PageInfo.Builder(595, 842, number).create())
            val header = Paint(text).apply { textSize = 10f; color = Color.rgb(85, 110, 96) }
            page!!.canvas.drawText("Lexi · 私人词汇档案", 40f, 33f, header)
            page!!.canvas.drawText("$number", 540f, 814f, header)
            y = 58f
        }
        fun paragraph(value: String, size: Float = 11f, bold: Boolean = false) {
            if (value.isBlank()) return
            text.textSize = size; text.typeface = Typeface.create("sans-serif", if (bold) Typeface.BOLD else Typeface.NORMAL)
            // Split by layout lines, so even one unusually long field can span pages.
            val layout = StaticLayout.Builder.obtain(value, 0, value.length, text, 515)
                .setAlignment(Layout.Alignment.ALIGN_NORMAL).setIncludePad(false).setLineSpacing(3f, 1f).build()
            for (line in 0 until layout.lineCount) {
                val top = layout.getLineTop(line); val bottom = layout.getLineBottom(line)
                val height = (bottom - top).toFloat()
                if (y + height > 784f) next()
                val canvas = page!!.canvas
                canvas.save(); canvas.clipRect(40f, y, 555f, y + height)
                canvas.translate(40f, y - top); layout.draw(canvas); canvas.restore()
                y += height
            }
            y += 6f
        }
        try {
            next()
            if (entries.isEmpty()) paragraph("暂无词条")
            entries.forEach { entry ->
                if (y > 680f) next()
                paragraph(entry.word, 17f, true)
                paragraph(entry.phonetic, 10f)
                paragraph(entry.translation)
                paragraph(entry.definition, 10f)
                paragraph(if (entry.archive.sourceTitle.isBlank()) "" else "来源：${entry.archive.sourceTitle}", 10f)
                paragraph(entry.archive.sourceExcerpt, 10f)
                paragraph(if (entry.notes.isBlank()) "" else "备注：${entry.notes}", 10f)
                paragraph(if (entry.archive.tags.isEmpty()) "" else "标签：${entry.archive.tags.joinToString(" · ")}", 10f)
                entry.aiResult?.let { ai ->
                    ai.examples.forEach { paragraph(it.english); paragraph(it.chinese, 10f) }
                    if (ai.synonyms.isNotEmpty()) paragraph("同义词：${ai.synonyms.joinToString(" · ")}", 10f)
                    if (ai.antonyms.isNotEmpty()) paragraph("反义词：${ai.antonyms.joinToString(" · ")}", 10f)
                    ai.phrases.forEach { paragraph("${it.en}  ${it.zh}", 10f) }
                }
                if (y + 35 > 784f) next()
                val pen = Paint(text).apply { style = Paint.Style.STROKE; strokeWidth = .6f }
                listOf(1, 2, 4, 7, 15).forEachIndexed { index, day ->
                    val x = 40f + index * 93f
                    page!!.canvas.drawRect(x, y + 2, x + 9, y + 11, pen)
                    page!!.canvas.drawText("第 $day 天", x + 15, y + 11, text)
                }
                y += 29
                page!!.canvas.drawLine(40f, y, 555f, y, pen); y += 16
            }
            finish(); document.writeTo(output)
        } finally { document.close() }
    }

    /** Paper-first review grid. AI, notes and sources belong only in the optional archive PDF. */
    private fun writeReview(entries: List<Entry>, output: OutputStream) {
        val document = PdfDocument()
        val text = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(35, 45, 40); textSize = 10f; typeface = Typeface.create("sans-serif", Typeface.NORMAL) }
        val line = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(197, 208, 201); strokeWidth = .5f; style = Paint.Style.STROKE }
        val fill = Paint().apply { color = Color.rgb(238, 243, 239) }
        var page: PdfDocument.Page? = null; var count = 0; var y = 0f
        val edges = floatArrayOf(40f, 194f, 395f, 427f, 459f, 491f, 523f, 555f)
        fun finish() { page?.let(document::finishPage); page = null }
        fun next() {
            finish(); count++; page = document.startPage(PdfDocument.PageInfo.Builder(595, 842, count).create())
            val canvas = page!!.canvas
            canvas.drawText("Lexi · 艾宾浩斯单词复习表", 40f, 33f, text)
            canvas.drawText("学习日期：____________    按第 1、2、4、7、15 天复习并打卡", 40f, 52f, text)
            canvas.drawText("$count", 540f, 814f, text)
            canvas.drawRect(40f, 64f, 555f, 91f, fill)
            listOf("单词 / 音标", "释义", "1天", "2天", "4天", "7天", "15天").forEachIndexed { i, label -> canvas.drawText(label, edges[i] + 6f, 81f, text) }
            canvas.drawRect(40f, 64f, 555f, 91f, line); y = 91f
        }
        fun layout(value: String, width: Int): StaticLayout = StaticLayout.Builder.obtain(value, 0, value.length, text, width).setIncludePad(false).setLineSpacing(3f, 1f).build()
        try {
            next()
            if(entries.isEmpty()) page!!.canvas.drawText("暂无词条", 46f, y + 25f, text)
            entries.forEach { entry ->
                val word = layout(listOf(entry.word, entry.phonetic).filter(String::isNotBlank).joinToString("\n"), 142)
                val meaning = layout(entry.translation, 189)
                val layouts = listOf(word, meaning); val indexes = intArrayOf(0,0); var first = true
                while(first || indexes.indices.any { indexes[it] < layouts[it].lineCount }) {
                    if(y + 40f > 784f) next()
                    val available = 784f - y - 12f; val ends = indexes.copyOf(); var contentHeight = 0f
                    layouts.forEachIndexed { i, l ->
                        val start = indexes[i]; val top = l.getLineTop(start)
                        while(ends[i] < l.lineCount && l.getLineBottom(ends[i]) - top <= available) ends[i]++
                        if(ends[i] > start) contentHeight = maxOf(contentHeight, (l.getLineBottom(ends[i]-1) - top).toFloat())
                    }
                    val height = maxOf(40f, contentHeight + 12f); val canvas = page!!.canvas
                    layouts.forEachIndexed { i, l ->
                        if(ends[i] > indexes[i]) {
                            canvas.save(); canvas.clipRect(edges[i] + 6f, y + 6f, edges[i+1] - 6f, y + 6f + contentHeight)
                            canvas.translate(edges[i] + 6f, y + 6f - l.getLineTop(indexes[i])); l.draw(canvas); canvas.restore()
                        }
                    }
                    canvas.drawRect(40f, y, 555f, y + height, line)
                    edges.drop(1).dropLast(1).forEach { canvas.drawLine(it,y,it,y+height,line) }
                    if(first) (2..6).forEach { val x = edges[it]+11f; canvas.drawRect(x,y+15f,x+10f,y+25f,line) }
                    y += height; ends.copyInto(indexes); first = false
                }
            }
            finish(); document.writeTo(output)
        } finally { document.close() }
    }
}
