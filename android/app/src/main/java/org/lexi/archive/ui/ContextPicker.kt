package org.lexi.archive.ui

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import org.lexi.archive.LexiViewModel

@Composable
internal fun ContextPicker(vm: LexiViewModel) {
    val presets = listOf("日常表达", "工作写作", "学术阅读", "技术文档", "考试备考", "旅行交流")
    var expanded by remember { mutableStateOf(false) }
    var customMode by rememberSaveable { mutableStateOf(vm.config.context !in presets) }
    var customDraft by rememberSaveable { mutableStateOf(if (customMode) vm.config.context else "") }
    val custom = customMode || vm.config.context !in presets
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("例句语境", style = MaterialTheme.typography.labelLarge)
        Box {
            OutlinedButton(onClick = { expanded = true }, modifier = Modifier.fillMaxWidth()) {
                Text(if (custom) "自定义语境 ▾" else "${vm.config.context} ▾")
            }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                (presets + "自定义").forEach { name ->
                    DropdownMenuItem(text = { Text(name) }, onClick = {
                        if (custom) customDraft = vm.config.context
                        customMode = name == "自定义"
                        vm.config = vm.config.copy(context = if (customMode) customDraft else name)
                        expanded = false
                    })
                }
            }
        }
        if (custom) OutlinedTextField(
            value = vm.config.context,
            onValueChange = { if (it.length <= 2000) { customDraft = it; vm.config = vm.config.copy(context = it) } },
            modifier = Modifier.fillMaxWidth(), minLines = 2,
            label = { Text("自定义语境") },
            placeholder = { Text("例如：软件开发团队的日常沟通，用词自然简洁") }
        )
        Text("指定这个词实际使用的场合与表达方式，不是例句讨论的话题。选择后点击“保存设置”。", style = MaterialTheme.typography.bodySmall)
    }
}
