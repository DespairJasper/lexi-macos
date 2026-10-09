package org.lexi.archive

import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.uiautomator.*
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.lexi.archive.data.*
import java.io.File

@RunWith(AndroidJUnit4::class)
class UiAcceptanceTest {
    @Test fun phoneWorkflow() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val device = UiDevice.getInstance(instrumentation)
        val repo = LexiRepository(context)
        repo.all().filter { it.word.startsWith("reason") }.forEach { repo.delete(setOf(it.id)) }
        val fixture = repo.save(Entry(word="reason",phonetic="/ˈriːzən/",translation="n. 理由；原因\nv. 推理",notes="验收笔记",aiResult=AIResult(
            examples=listOf(Example("She gave a good reason for being late.","她为迟到给出了一个充分的理由。"),Example("There is no reason to worry.","没有理由担心。")),synonyms=listOf("cause","motive","explanation"))))
        repo.scheduleToday(setOf(fixture.id))
        context.startActivity(Intent(context,MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK).setAction(Intent.ACTION_SEND).putExtra(Intent.EXTRA_TEXT,"reason").setType("text/plain"))
        fun click(text:String) { val item=device.wait(Until.findObject(By.text(text)),10000); assertNotNull(text,item); item.click(); device.waitForIdle() }
        fun shot(name:String) { device.takeScreenshot(File(context.filesDir,"ui-$name.png")) }
        assertNotNull(device.wait(Until.findObject(By.text("reason")),10000))
        device.swipe(device.displayWidth/2,device.displayHeight*3/4,device.displayWidth/2,device.displayHeight/3,400)
        shot("lookup-ai")
        click("档案")
        assertNotNull(device.wait(Until.findObject(By.text("reason")),5000))
        device.findObject(By.text("reason")).click(); device.waitForIdle(); shot("detail")
        click("编辑")
        device.wait(Until.hasObject(By.clazz("android.widget.EditText")),5000)
        shot("editor")
        val fields = device.findObjects(By.clazz("android.widget.EditText"))
        assertTrue("editable fields",fields.isNotEmpty())
        device.wait(Until.findObject(By.clazz("android.widget.EditText").text("reason")),5000).text = "reason-edited"
        click("保存档案")
        device.wait(Until.gone(By.text("编辑 · reason")),5000)
        assertEquals("reason-edited",repo.all().first { it.id == fixture.id }.word)
        shot("editor")
        click("重逢 1")
        device.wait(Until.hasObject(By.text("揭晓释义")),5000)
        assertNull("answer concealed",device.findObject(By.textContains("n. 理由")))
        shot("review-front")
        val reveal=device.wait(Until.findObject(By.text("揭晓释义")),5000)
        assertNotNull("reveal",reveal); reveal.click(); device.waitForIdle(); shot("review-answer")
        val remember=device.wait(Until.findObject(By.text("记住了")),5000)
        assertNotNull("rating",remember); remember.click()
        assertNotNull(device.wait(Until.findObject(By.text("重逢 0")),5000))
        click("设置"); shot("settings")
    }
}
