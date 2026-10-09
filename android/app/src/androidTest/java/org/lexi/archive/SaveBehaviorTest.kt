package org.lexi.archive

import android.app.Application
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.lexi.archive.data.*

@RunWith(AndroidJUnit4::class)
class SaveBehaviorTest {
    @Test fun collectDoesNotPersistExpansion() = runBlocking {
        val inst = InstrumentationRegistry.getInstrumentation()
        val app = inst.targetContext.applicationContext as Application
        val repo = LexiRepository(app)
        repo.all().filter { it.word == "collect-fixture" }.forEach { repo.delete(setOf(it.id)) }
        val original = repo.save(Entry(word="collect-fixture", aiResult=AIResult(synonyms=listOf("gather"))))
        lateinit var vm: LexiViewModel
        inst.runOnMainSync { vm = LexiViewModel(app) }
        fun awaitState(check: () -> Boolean) {
            val end = System.currentTimeMillis()+15000
            while (System.currentTimeMillis()<end) {
                var ready=false; inst.runOnMainSync { ready=check() }
                if(ready) return
                Thread.sleep(50)
            }
            fail("State did not settle")
        }
        awaitState { vm.initialized }
        inst.runOnMainSync { vm.search("collect-fixture") }
        awaitState { vm.ai != null }
        inst.runOnMainSync { vm.delete(setOf(original.id)) }
        awaitState { !vm.busy && vm.entries.none { it.word=="collect-fixture" } }
        inst.runOnMainSync { vm.addCurrent() }
        awaitState { !vm.busy && vm.entries.any { it.word=="collect-fixture" } }
        val saved=repo.all().first { it.word=="collect-fixture" }
        assertNull("Collect must save base entry only", saved.aiResult)
        inst.runOnMainSync { vm.saveLookupAI() }
        awaitState { !vm.busy && vm.entries.first { it.word=="collect-fixture" }.aiResult!=null }
        assertEquals(listOf("gather"),repo.all().first { it.word=="collect-fixture" }.aiResult!!.synonyms)
        repo.delete(setOf(saved.id))
    }
}
