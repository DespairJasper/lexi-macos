package org.lexi.archive
import android.content.Intent
import android.content.pm.PackageManager
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.uiautomator.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class ProcessTextTest {
 @Test fun selectedTextResolvesAndQueries() {
    val inst=InstrumentationRegistry.getInstrumentation(); val ctx=inst.targetContext
    val action=Intent(Intent.ACTION_PROCESS_TEXT).setType("text/plain").setPackage(ctx.packageName)
    val handlers=ctx.packageManager.queryIntentActivities(action,PackageManager.MATCH_DEFAULT_ONLY)
    assertEquals(1,handlers.size)
    assertEquals("Lexi 查词",handlers.single().loadLabel(ctx.packageManager).toString())
    val d=UiDevice.getInstance(inst)
    fun select(word:String) {
        ctx.startActivity(Intent(action).putExtra(Intent.EXTRA_PROCESS_TEXT,word).putExtra(Intent.EXTRA_PROCESS_TEXT_READONLY,true).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        assertNotNull(d.wait(Until.findObject(By.text(word)),10000))
        assertNotNull(d.wait(Until.findObject(By.textContains("收藏到私人档案")),10000))
    }
    select("serendipity")
    select("resilience")
 }
}
