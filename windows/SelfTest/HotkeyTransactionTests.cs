using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lexi;

public static class HotkeyTransactionTests
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;

    public static async Task RunAsync(MainWindow main, Action<bool, string>? report = null)
    {
        report ??= (ok, msg) => Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {msg}");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            report(true, "非 Windows 环境，跳过 Native 全局热键事务测试");
            return;
        }

        var hotkey = App.Hotkey;
        if (hotkey == null)
        {
            report(false, "App.Hotkey 为空，未初始化全局快捷键服务");
            return;
        }

        var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var vocabField = typeof(MainWindow).GetField("_vocabService", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var lookupInput = (TextBox)typeof(MainWindow).GetField("_lookupShortcutInput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var transInput = (TextBox)typeof(MainWindow).GetField("_translateShortcutInput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var quoteInput = (TextBox)typeof(MainWindow).GetField("_quoteShortcutInput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var statusBlock = (TextBlock)typeof(MainWindow).GetField("_quickShortcutStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        var saveMethod = typeof(MainWindow).GetMethod("SaveQuickShortcuts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var vocab = (IVocabularyArchive)vocabField.GetValue(main)!;

        // 记录初始三个状态与设置
        var origStat0 = hotkey.GetStatus(QuickAction.Lookup);
        var origStat1 = hotkey.GetStatus(QuickAction.Translate);
        var origStat2 = hotkey.GetStatus(QuickAction.SaveQuote);

        var origSettings = (AppSettings)settingsField.GetValue(main)!;
        var origLookup = origSettings.LookupShortcut;
        var origTrans = origSettings.TranslateShortcut;
        var origQuote = origSettings.QuoteShortcut;

        async Task RunOnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(action);
            }
        }

        // 1. 重复键校验不得改变数据
        await RunOnUi(() =>
        {
            lookupInput.Text = "X";
            transInput.Text = "X";
            quoteInput.Text = "Y";
            saveMethod.Invoke(main, null);
        });

        var sAfterDup = (AppSettings)settingsField.GetValue(main)!;
        var dbAfterDup = vocab.LoadSettings();
        var stat0Dup = hotkey.GetStatus(QuickAction.Lookup);
        var stat1Dup = hotkey.GetStatus(QuickAction.Translate);
        var stat2Dup = hotkey.GetStatus(QuickAction.SaveQuote);

        bool dupBlocked = (statusBlock.Text?.Contains("请选择三个不同的英文字母") == true ||
                           statusBlock.Text?.Contains("Choose three different") == true);
        bool sUnchanged = sAfterDup.LookupShortcut == origLookup &&
                          sAfterDup.TranslateShortcut == origTrans &&
                          sAfterDup.QuoteShortcut == origQuote;
        bool dbUnchanged = dbAfterDup.LookupShortcut == origLookup &&
                           dbAfterDup.TranslateShortcut == origTrans &&
                           dbAfterDup.QuoteShortcut == origQuote;
        bool hkUnchanged = stat0Dup == origStat0 && stat1Dup == origStat1 && stat2Dup == origStat2;

        report(dupBlocked && sUnchanged && dbUnchanged && hkUnchanged,
            "重复键校验拦截成功，拦截后配置数据与三个热键注册状态保持不变");

        // 2. 仅在测试进程临时占用一键，申请失败保留原三个状态
        string[] candidates = ["J", "K", "L"];
        if (candidates.Any(c => c == origLookup || c == origTrans || c == origQuote))
        {
            candidates = ["U", "V", "W"];
        }

        int testOccupiedId = 9888;
        uint occupiedVk = (uint)candidates[1][0];
        bool occupied = RegisterHotKey(IntPtr.Zero, testOccupiedId, ModAlt | ModNoRepeat, occupiedVk);
        report(occupied, $"测试进程成功临时占用热键 Alt+{candidates[1]}");

        try
        {
            await RunOnUi(() =>
            {
                lookupInput.Text = candidates[0];
                transInput.Text = candidates[1];
                quoteInput.Text = candidates[2];
                saveMethod.Invoke(main, null);
            });

            var sAfterFail = (AppSettings)settingsField.GetValue(main)!;
            var dbAfterFail = vocab.LoadSettings();
            var stat0Fail = hotkey.GetStatus(QuickAction.Lookup);
            var stat1Fail = hotkey.GetStatus(QuickAction.Translate);
            var stat2Fail = hotkey.GetStatus(QuickAction.SaveQuote);

            bool failSettingsPreserved = sAfterFail.LookupShortcut == origLookup &&
                                         sAfterFail.TranslateShortcut == origTrans &&
                                         sAfterFail.QuoteShortcut == origQuote;
            bool failDbPreserved = dbAfterFail.LookupShortcut == origLookup &&
                                   dbAfterFail.TranslateShortcut == origTrans &&
                                   dbAfterFail.QuoteShortcut == origQuote;
            bool failHkPreserved = stat0Fail == origStat0 &&
                                   stat1Fail == origStat1 &&
                                   stat2Fail == origStat2;

            report(failSettingsPreserved && failDbPreserved && failHkPreserved,
                "占用冲突申请失败时完整保留原三个状态与持久化配置，避免了重配置先全注销或部分生效");

            // 3. 释放占用后成功并实际三个注册
            bool unreg = UnregisterHotKey(IntPtr.Zero, testOccupiedId);
            report(unreg, $"测试进程已释放临时占用的热键 Alt+{candidates[1]}");

            await RunOnUi(() =>
            {
                lookupInput.Text = candidates[0];
                transInput.Text = candidates[1];
                quoteInput.Text = candidates[2];
                saveMethod.Invoke(main, null);
            });

            var sAfterSuccess = (AppSettings)settingsField.GetValue(main)!;
            var dbAfterSuccess = vocab.LoadSettings();
            var stat0Success = hotkey.GetStatus(QuickAction.Lookup);
            var stat1Success = hotkey.GetStatus(QuickAction.Translate);
            var stat2Success = hotkey.GetStatus(QuickAction.SaveQuote);

            bool successSettings = sAfterSuccess.LookupShortcut == candidates[0] &&
                                   sAfterSuccess.TranslateShortcut == candidates[1] &&
                                   sAfterSuccess.QuoteShortcut == candidates[2];
            bool successDb = dbAfterSuccess.LookupShortcut == candidates[0] &&
                             dbAfterSuccess.TranslateShortcut == candidates[1] &&
                             dbAfterSuccess.QuoteShortcut == candidates[2];
            bool successHk = stat0Success.Key == candidates[0] && stat0Success.IsRegistered &&
                             stat1Success.Key == candidates[1] && stat1Success.IsRegistered &&
                             stat2Success.Key == candidates[2] && stat2Success.IsRegistered;

            report(successSettings && successDb && successHk,
                "释放占用后重新申请成功，三个快捷键全部实际注册且原子保存持久化配置");
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, testOccupiedId);

            // 恢复初始配置
            await RunOnUi(() =>
            {
                lookupInput.Text = origLookup;
                transInput.Text = origTrans;
                quoteInput.Text = origQuote;
                saveMethod.Invoke(main, null);
            });
        }
    }
}
