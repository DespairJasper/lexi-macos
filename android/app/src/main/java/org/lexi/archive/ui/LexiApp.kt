package org.lexi.archive.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.lexi.archive.LexiViewModel
import org.lexi.archive.data.*
import org.lexi.archive.services.AIModule

private val Sage = Color(0xFF78AD98)
private val LexiTypography = Typography(
    headlineLarge = TextStyle(fontSize = 30.sp, lineHeight = 38.sp, fontWeight = FontWeight.Medium),
    headlineSmall = TextStyle(fontSize = 22.sp, lineHeight = 30.sp, fontWeight = FontWeight.Medium),
    titleLarge = TextStyle(fontSize = 20.sp, lineHeight = 26.sp, fontWeight = FontWeight.Medium),
    titleMedium = TextStyle(fontSize = 16.sp, lineHeight = 22.sp, fontWeight = FontWeight.Medium),
    bodyLarge = TextStyle(fontSize = 15.sp, lineHeight = 24.sp),
    bodyMedium = TextStyle(fontSize = 14.sp, lineHeight = 22.sp),
    bodySmall = TextStyle(fontSize = 12.sp, lineHeight = 18.sp),
    labelLarge = TextStyle(fontSize = 13.sp, lineHeight = 18.sp, fontWeight = FontWeight.Medium),
    labelMedium = TextStyle(fontSize = 12.sp, lineHeight = 16.sp),
    labelSmall = TextStyle(fontSize = 11.sp, lineHeight = 16.sp)
)
private val Pages = listOf("查词", "档案", "重逢", "设置")
private val PageIcons: List<ImageVector> = listOf(Icons.Outlined.Search, Icons.Outlined.Bookmarks, Icons.Outlined.Layers, Icons.Outlined.Tune)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LexiApp(vm: LexiViewModel, onExport: (String, Set<Long>?) -> Unit, onImport: (Boolean) -> Unit, onPrint: (Set<Long>?) -> Unit, onOpenPdf: () -> Unit, onSharePdf: () -> Unit) {
    val dark = when (vm.theme) { "dark" -> true; "light" -> false; else -> isSystemInDarkTheme() }
    val colors = if (dark) darkColorScheme(primary = Sage, onPrimary = Color(0xFF102B20), background = Color(0xFF101714), surface = Color(0xFF19221E), surfaceVariant = Color(0xFF25332C), secondaryContainer = Color(0xFF2B4437))
        else lightColorScheme(primary = Color(0xFF3F755D), onPrimary = Color.White, background = Color(0xFFF3F5F0), surface = Color(0xFFFCFDF9), surfaceVariant = Color(0xFFE5ECE3), secondaryContainer = Color(0xFFDCEBDD))
    MaterialTheme(colorScheme = colors, typography = LexiTypography, shapes = Shapes(small = RoundedCornerShape(8.dp), medium = RoundedCornerShape(14.dp), large = RoundedCornerShape(18.dp))) {
        var page by rememberSaveable { mutableIntStateOf(0) }
        val pages = rememberSaveableStateHolder()
        var notice by remember { mutableStateOf("") }
        LaunchedEffect(vm.statusVersion) {
            notice = vm.status
            kotlinx.coroutines.delay(4500)
            notice = ""
        }
        BackHandler(enabled = page != 0) { page = 0 }
        LaunchedEffect(vm.incomingVersion) { if (vm.incomingVersion > 0) page = 0 }
        BoxWithConstraints(Modifier.fillMaxSize().background(colors.background)) {
            val wide = maxWidth >= 840.dp
            Scaffold(containerColor = colors.background, topBar = {
                TopAppBar(title = { Text(if (page == 0) "Lexi · 查词" else Pages[page], style = MaterialTheme.typography.titleLarge) }, actions = {
                    Text(if (page == 2) "${vm.due.size} 待重逢" else "${vm.entries.size} 词", Modifier.padding(end = 20.dp), style = MaterialTheme.typography.labelMedium, color = colors.onSurfaceVariant)
                }, colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.background))
            }, bottomBar = {
                if (!wide) NavigationBar(containerColor = colors.surface) {
                    Pages.forEachIndexed { i, title -> NavigationBarItem(selected = page == i, onClick = { page = i }, icon = { Icon(PageIcons[i], null) }, label = { Text(if (i == 2) "$title ${vm.due.size}" else title) }) }
                }
            }) { padding ->
                Row(Modifier.fillMaxSize().padding(padding).imePadding()) {
                    if (wide) NavigationRail(Modifier.fillMaxHeight(), containerColor = colors.surface, header = {
                        Text("L", Modifier.padding(vertical = 20.dp), style = MaterialTheme.typography.headlineLarge, color = colors.primary, fontWeight = FontWeight.Light)
                    }) {
                        Spacer(Modifier.height(28.dp))
                        Pages.forEachIndexed { i, title -> NavigationRailItem(selected = page == i, onClick = { page = i }, icon = { Icon(PageIcons[i], null) }, label = { Text(if (i == 2) "$title ${vm.due.size}" else title) }) }
                    }
                    Column(Modifier.weight(1f).fillMaxHeight().padding(horizontal = if (wide) 24.dp else 16.dp).padding(bottom = 8.dp)) {
                        androidx.compose.animation.AnimatedVisibility(notice.isNotBlank()) {
                            Surface(Modifier.fillMaxWidth().padding(bottom = 8.dp), shape = RoundedCornerShape(12.dp), color = colors.secondaryContainer) {
                                Row(Modifier.padding(start = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Text(notice, Modifier.weight(1f), style = MaterialTheme.typography.bodySmall, color = colors.onSecondaryContainer)
                                    IconButton(onClick = { notice = "" }) { Icon(Icons.Outlined.Close, "关闭提示", Modifier.size(16.dp)) }
                                }
                            }
                        }
                        vm.savedPdf?.let { pdf ->
                            Surface(Modifier.fillMaxWidth().padding(bottom = 8.dp), shape = RoundedCornerShape(12.dp), color = colors.surfaceVariant) {
                                Column(Modifier.padding(12.dp)) {
                                    Text(pdf.name, style = MaterialTheme.typography.labelMedium, maxLines = 1)
                                    Row {
                                        TextButton(onClick = onOpenPdf) { Text("打开 PDF") }
                                        TextButton(onClick = onSharePdf) { Text("分享 PDF") }
                                        Spacer(Modifier.weight(1f))
                                        IconButton(onClick = vm::dismissPdf) { Icon(Icons.Outlined.Close, "收起文件操作", Modifier.size(16.dp)) }
                                    }
                                }
                            }
                        }
                        if (vm.busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                        Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.TopCenter) {
                          pages.SaveableStateProvider(page) {
                           Box(if (page == 1) Modifier.fillMaxSize() else Modifier.widthIn(max = 760.dp).fillMaxSize()) {
                            when (page) {
                                0 -> LookupPage(vm)
                                1 -> ArchivePage(vm, wide, onExport, onPrint)
                                2 -> ReviewPage(vm)
                                else -> SettingsPage(vm, onExport, onImport, onPrint)
                            }
                           }
                        }
                        }
                    }
                }
            }
        }
    }
}

@Composable private fun Section(title: String, caption: String? = null, content: @Composable ColumnScope.() -> Unit) {
    Card(Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            if (caption != null) Text(caption, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            content()
        }
    }
}

@Composable private fun EmptyState(title: String, detail: String, icon: ImageVector = Icons.Outlined.AutoStories) {
    Column(Modifier.fillMaxWidth().padding(vertical = 34.dp, horizontal = 20.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Icon(icon, null, Modifier.size(42.dp), tint = MaterialTheme.colorScheme.primary)
        Text(title, style = MaterialTheme.typography.titleLarge)
        Text(detail, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable private fun Field(value: String, label: String, onChange: (String) -> Unit, lines: Int = 1, enabled: Boolean = true) {
    OutlinedTextField(value, onChange, Modifier.fillMaxWidth(), label = { Text(label) }, minLines = lines, singleLine = lines == 1, enabled = enabled, shape = RoundedCornerShape(14.dp))
}

@OptIn(ExperimentalLayoutApi::class)
@Composable private fun AiControls(vm: LexiViewModel, entry: Entry? = null, enabled: Boolean = true) {
    var modules by rememberSaveable { mutableStateOf(listOf("EXAMPLES")) }
    Column(Modifier.fillMaxWidth().padding(horizontal = 2.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text("AI 拓展", Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
            Text(vm.config.provider, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            AIModule.entries.forEach { module -> FilterChip(selected = module.name in modules, onClick = { modules = if (module.name in modules) modules - module.name else modules + module.name }, label = { Text(module.title) }) }
        }
        if (vm.aiBusy) {
            LinearProgressIndicator(Modifier.fillMaxWidth())
            OutlinedButton(onClick = vm::cancelAI) { Text("取消生成") }
        } else OutlinedButton(onClick = { vm.generate(AIModule.entries.filter { it.name in modules }.toSet(), entry) }, enabled = enabled && modules.isNotEmpty() && !vm.busy && (entry != null || vm.lookup != null)) {
            Icon(Icons.Outlined.AutoAwesome, null, Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)); Text("生成所选内容")
        }
    }
}

@Composable private fun AIContent(ai: AIResult, onWord: (String) -> Unit = {}) {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        if (ai.examples.isNotEmpty()) Section("例句") {
            ai.examples.forEachIndexed { index, example ->
                if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = .5f))
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("%02d".format(index + 1), Modifier.padding(top = 3.dp), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
                    Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
                        Text(example.english, style = MaterialTheme.typography.bodyLarge)
                        Text(example.chinese, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
        if (ai.synonyms.isNotEmpty() || ai.antonyms.isNotEmpty()) Section("近义与反义") {
            if (ai.synonyms.isNotEmpty()) { Text("同义词", style = MaterialTheme.typography.labelMedium); WordChips(ai.synonyms, onWord) }
            if (ai.antonyms.isNotEmpty()) { Text("反义词", style = MaterialTheme.typography.labelMedium); WordChips(ai.antonyms, onWord) }
        }
        if (ai.phrases.isNotEmpty()) Section("常用词组") {
            ai.phrases.forEachIndexed { index, phrase ->
                if (index > 0) HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = .5f))
                Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                    Text(phrase.en, style = MaterialTheme.typography.bodyLarge)
                    Text(phrase.zh, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable private fun WordChips(words: List<String>, onWord: (String) -> Unit) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        words.forEach { word -> Surface(onClick = { onWord(word) }, shape = RoundedCornerShape(50), color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = .6f)) { Text(word, Modifier.padding(horizontal = 12.dp, vertical = 8.dp), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary) } }
    }
}

@Composable private fun LookupPage(vm: LexiViewModel) {
    val result = vm.lookup
    val exists = result != null && vm.entries.any { it.word.equals(result.word, true) }
    Column(Modifier.fillMaxSize()) {
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(vm.query, { vm.query = it }, Modifier.fillMaxWidth(), placeholder = { Text("输入英文单词") }, singleLine = true, shape = RoundedCornerShape(14.dp), leadingIcon = { Icon(Icons.Outlined.Search, null) }, keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search), keyboardActions = KeyboardActions(onSearch = { vm.search() }), trailingIcon = { IconButton(onClick = { vm.search() }, enabled = vm.query.isNotBlank()) { Icon(Icons.Outlined.ArrowForward, "查询") } })
            if (result == null) EmptyState("词语，值得慢慢认识", "离线查词随时可用，也可以从其他应用分享文字到 Lexi。")
            else {
                Surface(Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp), color = MaterialTheme.colorScheme.surface) {
                  Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(result.word, style = MaterialTheme.typography.headlineLarge)
                    Text(result.phonetic.ifBlank { if (result.found) "离线词典" else "未收录词条" }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = .5f))
                    Text(result.translation.ifBlank { "暂无释义，收藏后可在档案中补充。" }, style = MaterialTheme.typography.bodyLarge)
                    if (result.definition.isNotBlank()) Text(result.definition, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                  }
                }
                AiControls(vm)
                vm.ai?.let { AIContent(it, vm::search) }
                if (exists && vm.ai != null) OutlinedButton(onClick = vm::saveLookupAI, enabled = !vm.busy && !vm.aiBusy) { Text("保存本次 AI 内容到档案") }
            }
            Spacer(Modifier.height(6.dp))
        }
        Button(onClick = vm::addCurrent, enabled = result != null && !exists && vm.initialized && !vm.busy, modifier = Modifier.fillMaxWidth().padding(top = 10.dp).heightIn(min = 52.dp)) {
            Icon(if (exists) Icons.Outlined.Check else Icons.Outlined.BookmarkAdd, null, Modifier.size(20.dp)); Spacer(Modifier.width(9.dp)); Text(if (exists) "已在私人档案" else "收藏到私人档案")
        }
    }
}

@Composable private fun ArchivePage(vm: LexiViewModel, wide: Boolean, onExport: (String, Set<Long>?) -> Unit, onPrint: (Set<Long>?) -> Unit) {
    var search by rememberSaveable { mutableStateOf("") }
    var selected by remember { mutableStateOf(setOf<Long>()) }
    var activeId by rememberSaveable { mutableStateOf<Long?>(null) }
    var deleting by remember { mutableStateOf(setOf<Long>()) }
    var stageDialog by remember { mutableStateOf(false) }
    var batchMenu by remember { mutableStateOf(false) }
    val visible = vm.entries.filter { e -> listOf(e.word, e.translation, e.notes, e.archive.sourceTitle, e.archive.tags.joinToString()).any { it.contains(search, true) } }
    val active = vm.entries.firstOrNull { it.id == activeId }
    val details = rememberSaveableStateHolder()
    BackHandler(enabled = active != null) { activeId = null }
    LaunchedEffect(vm.entries) { selected = selected.intersect(vm.entries.map { it.id }.toSet()) }
    if (deleting.isNotEmpty()) AlertDialog(onDismissRequest = { deleting = emptySet() }, title = { Text("移出 ${deleting.size} 个词条？") }, text = { Text("所选词条及其来源、笔记和复习记录将被删除。可先导出保存。") }, confirmButton = { TextButton(onClick = { vm.delete(deleting); deleting = emptySet(); activeId = null }, enabled = !vm.busy) { Text("删除") } }, dismissButton = { TextButton(onClick = { deleting = emptySet() }) { Text("保留") } })
    if (stageDialog) AlertDialog(onDismissRequest = { stageDialog = false }, title = { Text("调整复习周期") }, text = {
        Column { listOf(1, 2, 4, 7, 15).forEachIndexed { index, days -> TextButton(onClick = { vm.stage(selected, index); stageDialog = false }, enabled = !vm.busy) { Text("阶段 ${index + 1} · $days 天") } } }
    }, confirmButton = { TextButton(onClick = { stageDialog = false }) { Text("取消") } })
    Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(18.dp)) {
        if (wide || active == null) Column(Modifier.weight(if (wide) 0.43f else 1f).fillMaxHeight(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth(), singleLine = true, placeholder = { Text("搜索单词、笔记、来源或标签") }, leadingIcon = { Icon(Icons.Outlined.Search, null) }, shape = RoundedCornerShape(16.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("${visible.size} 个词条", style = MaterialTheme.typography.labelMedium, modifier = Modifier.weight(1f))
                TextButton(onClick = { selected = visible.map { it.id }.toSet() }) { Text("全选") }
                TextButton(onClick = { val ids = visible.map { it.id }.toSet(); selected = (selected - ids) + (ids - selected) }) { Text("反选") }
                if (selected.isNotEmpty()) IconButton(onClick = { selected = emptySet() }) { Icon(Icons.Outlined.Close, "清空选择") }
            }
            if (selected.isNotEmpty()) Surface(color = MaterialTheme.colorScheme.secondaryContainer, shape = RoundedCornerShape(16.dp)) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("已选 ${selected.size}", Modifier.weight(1f), style = MaterialTheme.typography.labelLarge)
                    TextButton(onClick = { onExport("pdf", selected) }, enabled = !vm.busy) { Text("PDF") }
                    Box {
                        TextButton(onClick = { batchMenu = true }, enabled = !vm.busy) { Text("批量操作") }
                        DropdownMenu(batchMenu, { batchMenu = false }) {
                            DropdownMenuItem(text = { Text("置入今日重逢") }, onClick = { vm.today(selected); batchMenu = false })
                            DropdownMenuItem(text = { Text("标记已掌握") }, onClick = { vm.master(selected); batchMenu = false })
                            DropdownMenuItem(text = { Text("调整阶段") }, onClick = { stageDialog = true; batchMenu = false })
                            DropdownMenuItem(text = { Text("导出 CSV") }, onClick = { onExport("csv", selected); batchMenu = false })
                            DropdownMenuItem(text = { Text("打印所选") }, onClick = { onPrint(selected); batchMenu = false })
                            DropdownMenuItem(text = { Text("删除所选") }, onClick = { deleting = selected; batchMenu = false })
                        }
                    }
                }
            }
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp), contentPadding = PaddingValues(bottom = 12.dp)) {
                if (visible.isEmpty()) item { EmptyState("还没有词条", "从查词页收藏，或在设置中导入已有档案。") }
                items(visible, key = { it.id }) { entry ->
                    Card(Modifier.fillMaxWidth().clickable { activeId = entry.id }, colors = CardDefaults.cardColors(containerColor = if (entry.id == activeId) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface)) {
                        Row(Modifier.padding(vertical = 7.dp, horizontal = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(entry.id in selected, { checked -> selected = if (checked) selected + entry.id else selected - entry.id })
                            Column(Modifier.weight(1f).padding(end = 8.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                                Text(entry.word, style = MaterialTheme.typography.titleMedium)
                                Text(entry.translation.ifBlank { "待补充释义" }, maxLines = 2, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                Text(if (entry.status == "mastered") "已掌握" else "阶段 ${entry.stage + 1} · ${entry.nextReviewDate ?: "未安排"}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
                            }
                            TextButton(onClick = { activeId = entry.id }, enabled = !vm.busy) {
                                Icon(Icons.Outlined.ChevronRight, "查看详情", Modifier.size(20.dp))
                            }
                        }
                    }
                }
            }
        }
        if (wide || active != null) Box(Modifier.weight(if (wide) 0.57f else 1f).fillMaxHeight()) {
            if (active == null) EmptyState("每个词都有自己的故事", "选择词条，整理来源、笔记与语言拓展。", Icons.Outlined.EditNote)
            else details.SaveableStateProvider(active.id) { EntryDetail(active, vm, onBack = { activeId = null }, onDelete = { deleting = setOf(active.id) }, onExport = { onExport("pdf", setOf(active.id)) }) }
        }
    }
}

@Composable private fun EntryDetail(entry: Entry, vm: LexiViewModel, onBack: () -> Unit, onDelete: () -> Unit, onExport: () -> Unit) {
    var editing by rememberSaveable { mutableStateOf(false) }
    if (editing) {
        EntryEditor(entry, vm, onBack = { editing = false })
        return
    }
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.Outlined.ArrowBack, "返回列表") }
            Text("词条详情", Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
            IconButton(onClick = onExport, enabled = !vm.busy) { Icon(Icons.Outlined.PictureAsPdf, "导出 PDF") }
            TextButton(onClick = { editing = true }) { Icon(Icons.Outlined.Edit, null, Modifier.size(16.dp)); Spacer(Modifier.width(6.dp)); Text("编辑") }
        }
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Section(entry.word, entry.phonetic.ifBlank { "收藏于 ${entry.createdAt}" }) {
                Text(entry.translation.ifBlank { "尚未添加释义" }, style = MaterialTheme.typography.bodyLarge)
                if (entry.definition.isNotBlank()) Text(entry.definition, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(if (entry.status == "mastered") "已掌握" else "阶段 ${entry.stage + 1} · 下次重逢 ${entry.nextReviewDate ?: "未安排"}", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
            }
            if (entry.notes.isNotBlank()) Section("我的笔记") { Text(entry.notes, style = MaterialTheme.typography.bodyMedium) }
            if (entry.archive.sourceTitle.isNotBlank() || entry.archive.sourceExcerpt.isNotBlank() || entry.archive.tags.isNotEmpty()) Section("来源与标签") {
                if (entry.archive.sourceTitle.isNotBlank()) Text(listOf(entry.archive.sourceType, entry.archive.sourceTitle).filter { it.isNotBlank() }.joinToString(" · "), style = MaterialTheme.typography.bodyMedium)
                if (entry.archive.sourceExcerpt.isNotBlank()) Text(entry.archive.sourceExcerpt, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (entry.archive.tags.isNotEmpty()) Text(entry.archive.tags.joinToString("  ") { "#$it" }, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
            }
            AiControls(vm, entry)
            (vm.aiDrafts[entry.id] ?: entry.aiResult)?.let { AIContent(it, vm::handleIncoming) }
            if (vm.aiDrafts.containsKey(entry.id)) OutlinedButton(onClick = { vm.saveEntryAI(entry) }, enabled = !vm.busy) { Text("保存本次 AI 内容到档案") }
            Section("复习与遇见", "遇见 ${entry.archive.encounterCount} 次 · 复习 ${entry.reviewCount} 次") {
                TextButton(onClick = { vm.encounter(entry.id) }, enabled = !vm.busy && !vm.aiBusy) { Text("记录再次遇见") }
                TextButton(onClick = { vm.today(setOf(entry.id)) }, enabled = !vm.busy) { Text("置入今日重逢") }
                TextButton(onClick = { vm.master(setOf(entry.id)) }, enabled = !vm.busy) { Text("标记已掌握") }
                TextButton(onClick = { vm.undo(entry.id) }, enabled = !vm.busy && entry.reviewCount > 0) { Text("撤销上次复习") }
            }
            TextButton(onClick = onDelete, enabled = !vm.busy) { Text("删除词条", color = MaterialTheme.colorScheme.error) }
        }
    }
}

@Composable private fun EntryEditor(entry: Entry, vm: LexiViewModel, onBack: () -> Unit) {
    var word by rememberSaveable(entry.id) { mutableStateOf(entry.word) }
    var translation by rememberSaveable(entry.id) { mutableStateOf(entry.translation) }
    var definition by rememberSaveable(entry.id) { mutableStateOf(entry.definition) }
    var phonetic by rememberSaveable(entry.id) { mutableStateOf(entry.phonetic) }
    var notes by rememberSaveable(entry.id) { mutableStateOf(entry.notes) }
    var sourceType by rememberSaveable(entry.id) { mutableStateOf(entry.archive.sourceType) }
    var source by rememberSaveable(entry.id) { mutableStateOf(entry.archive.sourceTitle) }
    var excerpt by rememberSaveable(entry.id) { mutableStateOf(entry.archive.sourceExcerpt) }
    var tags by rememberSaveable(entry.id) { mutableStateOf(entry.archive.tags.joinToString(", ")) }
    var aiJson by rememberSaveable(entry.id) { mutableStateOf(entry.aiResult?.let { ArchiveJson.encodeAI(it) }.orEmpty()) }
    var error by remember { mutableStateOf<String?>(null) }
    var discard by remember { mutableStateOf(false) }
    var savingRevision by rememberSaveable { mutableStateOf<Int?>(null) }
    LaunchedEffect(entry.archive.revision) { if (savingRevision != null && entry.archive.revision != savingRevision) onBack() }
    val dirty = word != entry.word || translation != entry.translation || definition != entry.definition || phonetic != entry.phonetic || notes != entry.notes || sourceType != entry.archive.sourceType || source != entry.archive.sourceTitle || excerpt != entry.archive.sourceExcerpt || tags != entry.archive.tags.joinToString(", ") || aiJson != entry.aiResult?.let { ArchiveJson.encodeAI(it) }.orEmpty()
    BackHandler { if (dirty) discard = true else onBack() }
    if (discard) AlertDialog(onDismissRequest = { discard = false }, title = { Text("放弃未保存的修改？") }, text = { Text("返回后将恢复为上次保存的内容。切换页面会保留草稿。") }, confirmButton = { TextButton(onClick = { discard = false; onBack() }) { Text("放弃修改") } }, dismissButton = { TextButton(onClick = { discard = false }) { Text("继续编辑") } })
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { if (dirty) discard = true else onBack() }) { Icon(Icons.Outlined.Close, "关闭编辑") }
            Text("编辑 · ${entry.word}", Modifier.weight(1f), style = MaterialTheme.typography.titleMedium)
            if (dirty) Text("未保存", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
        }
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Section("词条信息", "收藏于 ${entry.createdAt} · 遇见 ${entry.archive.encounterCount} 次") {
                Field(word, "单词", { word = it })
                Field(phonetic, "音标", { phonetic = it })
                Field(translation, "中文释义", { translation = it }, 2)
                Field(definition, "英文释义", { definition = it }, 2)
                Field(notes, "我的笔记", { notes = it }, 3)
            }
            Section("遇见的地方") {
                Field(sourceType, "来源类型（书籍 / 网页 / 对话…）", { sourceType = it })
                Field(source, "来源标题", { source = it })
                Field(excerpt, "原文摘录", { excerpt = it }, 3)
                Field(tags, "标签（用逗号分隔）", { tags = it })
                OutlinedButton(onClick = { vm.encounter(entry.id) }, enabled = !dirty && !vm.busy && !vm.aiBusy) { Text("记录再次遇见") }
            }
            if (dirty) Text("有未保存的修改。请先保存，再生成 AI 内容或调整复习记录。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
            entry.aiResult?.let { AIContent(it, vm::handleIncoming) }
            var editingAI by rememberSaveable(entry.id) { mutableStateOf(false) }
            TextButton(onClick = { editingAI = !editingAI }) { Text(if (editingAI) "收起 AI 内容编辑" else "编辑 / 清除 AI 内容") }
            if (editingAI) Section("AI 内容编辑", "JSON 可直接修改；留空会移除 AI 内容。请先保存笔记，再生成新内容。") {
                Field(aiJson, "examples / synonyms / antonyms / phrases", { aiJson = it; error = null }, 7)
                if (error != null) Text(error!!, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
            Section("复习记录", "已复习 ${entry.reviewCount} 次 · 下次 ${entry.nextReviewDate ?: "无需复习"}") {
                OutlinedButton(onClick = { vm.today(setOf(entry.id)) }, enabled = !dirty && !vm.busy) { Text("置入今日重逢") }
                TextButton(onClick = { vm.undo(entry.id) }, enabled = !dirty && !vm.busy && entry.reviewCount > 0) { Text("撤销上次复习") }
            }
        }
        Button(onClick = {
            try {
                require(word.trim().isNotEmpty() && word.trim().length <= 512) { "单词须为 1–512 个字符" }
                require(vm.entries.none { it.id != entry.id && it.word.equals(word.trim(), true) }) { "档案中已存在这个单词，请使用已有词条" }
                val parsed = if (aiJson.isBlank()) null else ArchiveJson.decodeAI(aiJson)
                error = null
                savingRevision = entry.archive.revision
                vm.save(entry.copy(word = word.trim(), phonetic = phonetic, translation = translation, definition = definition, notes = notes, archive = entry.archive.copy(sourceType = sourceType, sourceTitle = source, sourceExcerpt = excerpt, tags = tags.split(',', '，').map { it.trim() }.filter { it.isNotEmpty() }.distinct()), aiResult = parsed))
            } catch (e: Exception) { error = "暂未保存：${e.message ?: "请检查填写内容"}"; vm.report(error!!) }
        }, modifier = Modifier.fillMaxWidth().padding(top = 10.dp).heightIn(min = 50.dp), enabled = !vm.busy && !vm.aiBusy) { Text("保存档案") }
    }
}

@Composable private fun SettingsGroup(title: String, summary: String, initiallyOpen: Boolean = false, content: @Composable ColumnScope.() -> Unit) {
    var expanded by rememberSaveable { mutableStateOf(initiallyOpen) }
    Surface(Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp), color = MaterialTheme.colorScheme.surface) {
        Column {
            Row(Modifier.fillMaxWidth().clickable { expanded = !expanded }.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(title, style = MaterialTheme.typography.titleMedium)
                    Text(summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Icon(if (expanded) Icons.Outlined.ExpandLess else Icons.Outlined.ExpandMore, if (expanded) "收起" else "展开")
            }
            if (expanded) Column(Modifier.padding(start = 16.dp, end = 16.dp, bottom = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) { content() }
        }
    }
}

@Composable private fun ReviewPage(vm: LexiViewModel) {
    val current = vm.due.firstOrNull()
    var revealed by remember(current?.id) { mutableStateOf(false) }
    var lastId by rememberSaveable { mutableStateOf<Long?>(null) }
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("先回想，再揭晓", Modifier.weight(1f), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (lastId != null) TextButton(onClick = { lastId?.let(vm::undo); lastId = null }, enabled = !vm.busy) { Text("撤销上次") }
        }
        Box(Modifier.weight(1f).fillMaxWidth()) {
            AnimatedContent(modifier = Modifier.fillMaxSize(), targetState = current, label = "review card", transitionSpec = {
                (fadeIn(tween(if (vm.reduceMotion) 0 else 220)) + slideInHorizontally(tween(if (vm.reduceMotion) 0 else 220)) { it / 5 }) togetherWith
                    (fadeOut(tween(if (vm.reduceMotion) 0 else 140)) + slideOutHorizontally(tween(if (vm.reduceMotion) 0 else 180)) { -it / 5 })
            }, contentKey = { it?.id }) { entry ->
                if (entry == null) EmptyState("今天的重逢已完成", "新收藏的单词明天再见。保持自己的节奏。", Icons.Outlined.CheckCircleOutline)
                else Card(Modifier.fillMaxSize(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)) {
                    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(20.dp)) {
                        Text("阶段 ${entry.stage + 1} / 5", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.height(16.dp))
                        Text(entry.word, style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Light)
                        if (entry.phonetic.isNotBlank()) Text(entry.phonetic, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        if (revealed && current?.id == entry.id) {
                            HorizontalDivider()
                            Text(entry.translation.ifBlank { "暂无中文释义" }, style = MaterialTheme.typography.bodyLarge)
                            if (entry.definition.isNotBlank()) Text(entry.definition, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            if (entry.notes.isNotBlank()) Text(entry.notes, style = MaterialTheme.typography.bodyMedium)
                            entry.aiResult?.examples?.firstOrNull()?.let { Text("${it.english}\n${it.chinese}", style = MaterialTheme.typography.bodyMedium) }
                        } else Text("先在心里想一想它的意思", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
        if (current != null) {
            if (!revealed) Button(onClick = { revealed = true }, modifier = Modifier.fillMaxWidth().heightIn(min = 54.dp), enabled = !vm.busy) { Text("揭晓释义") }
            else Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                OutlinedButton(onClick = { lastId = current.id; vm.rate(current.id, false) }, modifier = Modifier.weight(1f).heightIn(min = 54.dp), enabled = !vm.busy) { Text("还不熟悉") }
                Button(onClick = { lastId = current.id; vm.rate(current.id, true) }, modifier = Modifier.weight(1f).heightIn(min = 54.dp), enabled = !vm.busy) { Text("记住了") }
            }
        }
    }
}

@Composable private fun Toggle(title: String, checked: Boolean, change: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) { Text(title, Modifier.weight(1f)); Switch(checked, change) }
}

@Composable private fun SettingsPage(vm: LexiViewModel, onExport: (String, Set<Long>?) -> Unit, onImport: (Boolean) -> Unit, onPrint: (Set<Long>?) -> Unit) {
    var restoreConfirm by remember { mutableStateOf(false) }
    var providerMenu by remember { mutableStateOf(false) }
    if (restoreConfirm) AlertDialog(onDismissRequest = { restoreConfirm = false }, title = { Text("恢复 Android 完整备份") }, text = { Text("恢复会替换当前全部词条与复习记录。建议先导出当前完整备份。请选择由 Lexi Android 创建的备份；词条迁移请使用 CSV 导入。") }, confirmButton = { TextButton(onClick = { restoreConfirm = false; onImport(true) }) { Text("选择备份文件") } }, dismissButton = { TextButton(onClick = { restoreConfirm = false }) { Text("取消") } })
    Column(Modifier.fillMaxSize()) {
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            SettingsGroup("外观与体验", "主题 · 动效") {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("system" to "跟随系统", "light" to "浅色", "dark" to "深色").forEach { (value, label) -> FilterChip(selected = vm.theme == value, onClick = { vm.theme = value }, label = { Text(label) }) }
                }
                Toggle("减少动态效果", vm.reduceMotion) { vm.reduceMotion = it }
            }
            SettingsGroup("选词即查", "选中文字 → 更多 → Lexi 查词") {
                Text("在阅读器、浏览器等应用长按选中英文，打开文字菜单中的“更多”，选择“Lexi 查词”，即可自动查询。", style = MaterialTheme.typography.bodyMedium)
                Text("菜单由原应用和系统决定；未显示时，可使用“分享 → Lexi”。无需开启悬浮窗或无障碍权限。", style = MaterialTheme.typography.bodySmall)
            }
            SettingsGroup("AI 服务", "${vm.config.provider} · ${vm.config.model}") {
                Box {
                    OutlinedButton(onClick = { providerMenu = true }) { Text("服务预设：${vm.config.provider}") }
                    DropdownMenu(providerMenu, { providerMenu = false }) {
                        listOf(Triple("DeepSeek", "https://api.deepseek.com", "deepseek-chat"), Triple("智谱 AI", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"), Triple("阿里百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"), Triple("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"), Triple("自定义", "", "")).forEach { (name, base, model) -> DropdownMenuItem(text = { Text(name) }, onClick = { vm.config = vm.config.copy(provider = name, baseUrl = base, model = model); providerMenu = false }) }
                    }
                }
                Field(vm.config.provider, "服务名称", { vm.config = vm.config.copy(provider = it) })
                Field(vm.config.baseUrl, "API Base URL（HTTPS）", { vm.config = vm.config.copy(baseUrl = it) })
                Field(vm.config.model, "模型名称", { vm.config = vm.config.copy(model = it) })
                OutlinedTextField(vm.apiKey, { vm.apiKey = it }, Modifier.fillMaxWidth(), label = { Text("API Key") }, singleLine = true, visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), shape = RoundedCornerShape(14.dp))
                Toggle("在此设备加密记住 Key", vm.rememberKey) { vm.rememberKey = it }
                Text("关闭记住后，Key 仅用于本次打开。点击下方保存设置生效；导出文件不包含 Key。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            SettingsGroup("生成偏好", "${vm.config.context} · 按需生成") {
                ContextPicker(vm)
                Toggle("生成时附带来源摘录", vm.config.includeSource) { vm.config = vm.config.copy(includeSource = it) }
                Text("开启后，当前词条的来源摘录会发给所选 AI 服务。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text("请求超时：${vm.config.timeoutSeconds} 秒", style = MaterialTheme.typography.labelLarge)
                Slider(value = vm.config.timeoutSeconds.toFloat().coerceIn(10f, 120f), onValueChange = { vm.config = vm.config.copy(timeoutSeconds = it.toInt()) }, valueRange = 10f..120f, steps = 10)
            }
            SettingsGroup("数据与导出", "PDF 复习表 · CSV 迁移 · 完整备份") {
                Button(onClick = { onExport("pdf", null) }, enabled = !vm.busy && vm.initialized, modifier = Modifier.fillMaxWidth()) { Icon(Icons.Outlined.PictureAsPdf, null, Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)); Text("导出艾宾浩斯 PDF 复习表") }
                Text("PDF 默认保存在 下载 / Lexi词汇库，保存后可直接打开或分享。", style = MaterialTheme.typography.bodySmall)
                OutlinedButton(onClick = { onExport("archive_pdf", null) }, enabled = !vm.busy && vm.initialized, modifier = Modifier.fillMaxWidth()) { Text("导出完整档案 PDF（含备注与 AI）") }
                OutlinedButton(onClick = { onImport(false) }, enabled = !vm.busy && vm.initialized, modifier = Modifier.fillMaxWidth()) { Text("导入 CSV 词表") }
                OutlinedButton(onClick = { onExport("csv", null) }, enabled = !vm.busy && vm.initialized, modifier = Modifier.fillMaxWidth()) { Text("导出全部词条 · CSV") }
                OutlinedButton(onClick = { onExport("backup", null) }, enabled = !vm.busy && vm.initialized, modifier = Modifier.fillMaxWidth()) { Text("保存 Android 完整备份") }
                OutlinedButton(onClick = { restoreConfirm = true }, enabled = !vm.busy, modifier = Modifier.fillMaxWidth()) { Text("恢复 Android 完整备份…") }
                OutlinedButton(onClick = { onPrint(null) }, enabled = !vm.busy && vm.initialized, modifier = Modifier.fillMaxWidth()) { Text("系统打印") }
                Text("私人档案位置", style = MaterialTheme.typography.labelLarge)
                Text(vm.dataLocation, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            SettingsGroup("关于 Lexi", "0.2.2 · Android Alpha") {
                Text("为日常阅读留下一本私人词汇档案。\n本地保存 · 无需账户 · 无自动同步", style = MaterialTheme.typography.bodyMedium)
                Text("离线词典：ECDICT（MIT License）。手机与平板根据窗口宽度自动适配。", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Spacer(Modifier.height(6.dp))
        }
        Button(onClick = vm::saveSettings, modifier = Modifier.fillMaxWidth().padding(top = 10.dp).heightIn(min = 52.dp), enabled = !vm.busy && !vm.aiBusy) { Text("保存设置") }
    }
}
