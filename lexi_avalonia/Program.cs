using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;

namespace Lexi;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("LEXI_DEBUG_STACK") == "1") Console.Error.WriteLine(ex.ToString());
            var text = "Lexi 未能启动。已保留原有数据。\n\n" + ex.Message + "\n\n自动备份目录：" + Path.Combine(Path.GetDirectoryName(VocabularyService.GetDefaultDatabasePath())!, "backups");
            Console.Error.WriteLine(text);
            if (OperatingSystem.IsWindows() && !args.Any(a => a is "--database-check" or "--backup-database" or "--restore-backup")) MessageBox(IntPtr.Zero, text, "Lexi · 启动失败", 0x10);
            return 1;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string message, string title, uint type);

    private static int Run(string[] args)
    {
        if (args.Contains("--media-test")) return NativeAudioTests.Run();

        // 更新检查的独立入口：真实发起一次 GitHub Release 检查并打印结果，不启动 UI、不读用户数据。
        // 正式启动路径上的检查不阻塞启动且失败静默，因此需要这个入口来验证真实网络路径。
        if (args.Contains("--update-check")) return UpdateCheckCommand.RunAsync().GetAwaiter().GetResult();

        // 1. Check if invoked in self-test mode
        if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            if (OperatingSystem.IsWindows())
            {
                AttachConsole(AttachParentProcess);
            }
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            return SelfTestRunner.RunAllAsync().GetAwaiter().GetResult();
        }

        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (args.Any(a => a is "--ui-smoke" or "--visual-test" or "--language-test" or "--focus-test" or "--learning-test" or "--glacier-test" or "--quick-test" or "--recovery-restart-a" or "--recovery-restart-b") && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEXI_DATA_DIR"))) return 2;
        // 2. Single instance enforcement using Mutex + Named Pipe
        using var singleInstance = new SingleInstanceService();
        var maintenance = args.Any(a => a is "--database-check" or "--backup-database" or "--restore-backup");
        if (!singleInstance.CheckAndAcquire(!maintenance))
        {
            // Another instance is already running; wake signal sent, exit gracefully.
            if (maintenance) { Console.Error.WriteLine("请先退出正在运行的 Lexi，再检查或恢复词库。"); return 3; }
            return 0;
        }
        using var installationGuard = new InstallationGuard(Path.GetDirectoryName(VocabularyService.GetDefaultDatabasePath())!);
        if (maintenance)
        {
            var path = VocabularyService.GetDefaultDatabasePath();
            if (args.Contains("--restore-backup"))
            {
                var index = Array.IndexOf(args, "--restore-backup");
                if (index + 1 >= args.Length) throw new ArgumentException("请指定备份文件路径。");
                var archive = DatabaseSafety.RestoreBackup(args[index + 1], path);
                Console.WriteLine("恢复完成；原始词库文件保存在：" + archive);
            }
            else
            {
                if (!File.Exists(path)) throw new FileNotFoundException("未找到现有词库。");
                DatabaseSafety.ValidateExisting(path);
                using var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                database.Open(); DatabaseSafety.Validate(database); DatabaseSafety.ValidateApplicationSchema(database);
                if (args.Contains("--backup-database")) Console.WriteLine(DatabaseSafety.CreateBackup(database, path));
                else Console.WriteLine("词库完整性和关联检查通过。");
            }
            return 0;
        }

        // Create the global hotkey service; register after the macOS/Avalonia application is initialized.
        var hotkey = new HotkeyService();

        App.SingleInstance = singleInstance;
        App.Hotkey = hotkey;

        try
        {
            return BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            hotkey.Dispose();
            singleInstance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = Environment.GetEnvironmentVariable("LEXI_SOFTWARE_RENDERING") != "0"
                    ? [Win32RenderingMode.Software]
                    : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new FontManagerOptions
            {
                DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter",
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("PingFang SC") }]
            });
            builder = builder.With(new MacOSPlatformOptions
            {
                ShowInDock = true,
                DisableDefaultApplicationMenuItems = true
            });
        }

        return builder.LogToTrace();
    }
}
