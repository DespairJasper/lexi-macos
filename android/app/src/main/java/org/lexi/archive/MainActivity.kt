package org.lexi.archive

import android.content.Intent
import android.content.ClipData
import android.content.ActivityNotFoundException
import android.content.pm.PackageManager
import android.Manifest
import android.os.Build
import android.os.Bundle
import android.print.PrintAttributes
import android.print.PrintManager
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.SideEffect
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.core.view.WindowCompat
import androidx.core.content.ContextCompat
import org.lexi.archive.ui.LexiApp

class MainActivity : ComponentActivity() {
    private val vm: LexiViewModel by viewModels()
    private var exportKind = "csv"
    private var exportIds: Set<Long>? = null
    private var restoring = false
    private var printView: WebView? = null
    private val createFile = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == RESULT_OK) result.data?.data?.let { vm.writeFile(it, exportKind, exportIds) }
    }
    private val openFile = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) vm.importFile(uri, restoring)
    }
    private val storagePermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) vm.savePdf(exportKind, exportIds)
        else vm.report("未获得存储权限，PDF 尚未保存。请允许存储访问后重新导出。")
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        exportKind = savedInstanceState?.getString("exportKind") ?: "csv"
        exportIds = savedInstanceState?.getLongArray("exportIds")?.toSet()
        restoring = savedInstanceState?.getBoolean("restoring") ?: false
        enableEdgeToEdge()
        setContent {
            val dark = when (vm.theme) { "dark" -> true; "light" -> false; else -> isSystemInDarkTheme() }
            SideEffect {
                WindowCompat.getInsetsController(window, window.decorView).apply {
                    isAppearanceLightStatusBars = !dark
                    isAppearanceLightNavigationBars = !dark
                }
            }
            LexiApp(vm, ::export, ::openDocument, ::print, onOpenPdf = ::openPdf, onSharePdf = ::sharePdf)
        }
        if (savedInstanceState == null) consumeIntent(intent)
    }
    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString("exportKind", exportKind)
        exportIds?.let { outState.putLongArray("exportIds", it.toLongArray()) }
        outState.putBoolean("restoring", restoring)
        super.onSaveInstanceState(outState)
    }
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent); setIntent(intent); consumeIntent(intent)
    }
    private fun consumeIntent(intent: Intent) {
        val text = when (intent.action) {
            Intent.ACTION_PROCESS_TEXT -> intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)
            Intent.ACTION_SEND -> intent.getCharSequenceExtra(Intent.EXTRA_TEXT)
            else -> null
        }
        intent.removeExtra(Intent.EXTRA_PROCESS_TEXT); intent.removeExtra(Intent.EXTRA_TEXT)
        if (text != null) vm.handleIncoming(text.toString())
    }
    private fun export(kind: String, ids: Set<Long>?) {
        exportKind = kind; exportIds = ids?.toSet()
        if (kind == "pdf" || kind == "archive_pdf") {
            if (Build.VERSION.SDK_INT <= 28 && ContextCompat.checkSelfPermission(this, Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
                storagePermission.launch(Manifest.permission.WRITE_EXTERNAL_STORAGE)
            } else vm.savePdf(kind, ids)
            return
        }
        val ext = when (kind) { "html" -> "html"; "backup" -> "sqlite3"; else -> "csv" }
        createFile.launch(Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = when (kind) { "html" -> "text/html"; "backup" -> "application/octet-stream"; else -> "text/csv" }
            putExtra(Intent.EXTRA_TITLE, "Lexi-${java.time.LocalDate.now()}.$ext")
        })
    }
    private fun openPdf() {
        val pdf = vm.savedPdf ?: return
        launchPdfIntent(Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(pdf.uri, "application/pdf")
            clipData = ClipData.newRawUri(pdf.name, pdf.uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        })
    }
    private fun sharePdf() {
        val pdf = vm.savedPdf ?: return
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "application/pdf"
            putExtra(Intent.EXTRA_STREAM, pdf.uri)
            putExtra(Intent.EXTRA_TITLE, pdf.name)
            clipData = ClipData.newRawUri(pdf.name, pdf.uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        launchPdfIntent(Intent.createChooser(send, "分享 Lexi PDF").apply { addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) })
    }
    private fun launchPdfIntent(intent: Intent) {
        try { startActivity(intent) }
        catch (_: ActivityNotFoundException) { vm.report("PDF 已保存在 下载/Lexi词汇库。暂未找到可打开它的应用，可使用分享发送给其他应用。") }
        catch (_: SecurityException) { vm.report("PDF 已保存，但系统未允许打开。请在文件管理器的 下载/Lexi词汇库 中查看。") }
    }
    private fun openDocument(restore: Boolean) { restoring = restore; openFile.launch(arrayOf("*/*")) }
    private fun print(ids: Set<Long>?) {
        // Only the user-requested print preview uses Android's system WebView.
        // Main UI and all word cards are native Compose. No remote resources/JS.
        printView?.destroy()
        val view = WebView(this)
        printView = view
        view.settings.javaScriptEnabled = false
        view.settings.allowFileAccess = false
        view.settings.blockNetworkLoads = true
        view.webViewClient = object : WebViewClient() {
            override fun onPageFinished(v: WebView, url: String?) {
                (getSystemService(PRINT_SERVICE) as PrintManager).print("Lexi 私人词汇档案", v.createPrintDocumentAdapter("Lexi"), PrintAttributes.Builder().setMediaSize(PrintAttributes.MediaSize.ISO_A4).build())
            }
        }
        view.loadDataWithBaseURL(null, vm.printHtml(ids), "text/html", "UTF-8", null)
    }
    override fun onDestroy() { printView?.destroy(); printView = null; super.onDestroy() }
}
