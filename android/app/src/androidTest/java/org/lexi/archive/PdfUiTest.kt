package org.lexi.archive
import android.content.Intent
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.uiautomator.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@RunWith(AndroidJUnit4::class)
class PdfUiTest {
 @Test fun exportAndShare() {
    val inst=InstrumentationRegistry.getInstrumentation(); val ctx=inst.targetContext
    val d=UiDevice.getInstance(inst)
    ctx.startActivity(Intent(ctx,MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    fun click(t:String) { val node=d.wait(Until.findObject(By.text(t)),10000); assertNotNull(t,node); node.click(); d.waitForIdle() }
    click("设置")
    click("数据与导出")
    val scroll=UiScrollable(UiSelector().scrollable(true))
    scroll.scrollTextIntoView("导出艾宾浩斯 PDF 复习表")
    click("导出艾宾浩斯 PDF 复习表")
    assertNotNull(d.wait(Until.findObject(By.text("打开 PDF")),15000))
    d.takeScreenshot(File(ctx.filesDir,"ui-pdf-saved-021.png"))
    click("打开 PDF")
    Thread.sleep(1000)
    // On a stock emulator no PDF reader may exist; both system viewer and actionable in-app fallback are supported.
    if (!d.hasObject(By.text("分享 PDF"))) { d.pressBack(); d.waitForIdle() }
    click("分享 PDF")
    assertTrue("Android share sheet opened",d.wait(Until.hasObject(By.pkg("com.android.intentresolver")),5000))
    d.takeScreenshot(File(ctx.filesDir,"ui-pdf-share-021.png"))
    d.pressBack()
 }
}
