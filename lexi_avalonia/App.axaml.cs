using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Threading;

namespace Lexi;

public partial class App : Application
{
    private void OnTrayToggle(object? sender, EventArgs e) => ToggleMainWindow();
    private void OnTrayQuit(object? sender, EventArgs e) => QuitApplication();
    public static SingleInstanceService? SingleInstance { get; set; }
    public static HotkeyService? Hotkey { get; set; }

    public ICommand ShowHideCommand { get; }
    public ICommand QuitCommand { get; }

    public App()
    {
        Name = "lexi";
        ShowHideCommand = new SimpleRelayCommand(ToggleMainWindow);
        QuitCommand = new SimpleRelayCommand(QuitApplication);
        DataContext = this;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        UiText.Apply("zh-CN");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = Environment.GetCommandLineArgs().Contains("--learning-test") ? LearningUiTests.CreateWindow() : new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) =>
            {
                if (!mainWindow.IsRestoring) mainWindow.PrepareForApplicationShutdown();
            };
            if (Environment.GetCommandLineArgs().Any(a => a is "--ui-smoke" or "--visual-test" or "--language-test" or "--focus-test" or "--learning-test" or "--quick-test" or "--recovery-restart-a" or "--recovery-restart-b"))
            {
                var started = false;
                mainWindow.Opened += async (_, _) =>
                {
                    if (started) return;
                    started = true;
                    await System.Threading.Tasks.Task.Delay(300);
                    if (Environment.GetCommandLineArgs().Contains("--quick-test")) await QuickActionUiTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--learning-test")) await LearningUiTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--focus-test")) await FocusUiTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--language-test")) await LanguageTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--visual-test")) await VisualAcceptanceTests.RunAsync(mainWindow);
                    // 双进程重启恢复：A 写现场后进程退出，B 是另一次启动，只凭磁盘断言恢复结果。
                    else if (Environment.GetCommandLineArgs().Contains("--recovery-restart-a")) await MemoryRestartTests.RunPhaseAAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--recovery-restart-b")) await MemoryRestartTests.RunPhaseBAsync(mainWindow);
                    else await UiSmokeTests.RunAsync(mainWindow);
                };
            }

            // Set up system tray click handler if tray icon exists
            var trayIcons = TrayIcon.GetIcons(this);
            if (trayIcons != null && trayIcons.Count > 0)
            {
                trayIcons[0].Clicked += (_, _) => ToggleMainWindow();
            }

            // Bind single instance wake up
            if (SingleInstance != null)
            {
                SingleInstance.WakeupRequested += () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (desktop.MainWindow is MainWindow win)
                        {
                            win.ShowAndActivate();
                        }
                    });
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                InstallMacApplicationMenu(desktop);
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                    activatable.Activated += (_, e) =>
                    {
                        if (e.Kind == ActivationKind.Reopen)
                            Dispatcher.UIThread.Post(() => mainWindow.ShowAndActivate());
                    };
                // Register Lexi in the Accessibility list with the system prompt; automated runs stay silent.
                if (!IsAutomatedTestRun())
                    Dispatcher.UIThread.Post(() => MacOSNative.RequestAccessibilityPermission(), DispatcherPriority.Background);
            }

            // Capture the source PID before dispatching UI work; showing a window must not change the capture source.
            if (Hotkey != null)
            {
                Hotkey.QuickActionPressed += action =>
                {
                    var source = OperatingSystem.IsWindows()
                        ? Win32SelectionClipboard.CurrentForeground
                        : (OperatingSystem.IsMacOS() ? MacOSSelectionClipboard.CurrentForeground : 0);
                    Dispatcher.UIThread.Post(async () =>
                    {
                        if (desktop.MainWindow is MainWindow win)
                        {
                            await win.HandleQuickActionAsync(action, source);
                        }
                    });
                };
                Hotkey.Start();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InstallMacApplicationMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // The native exporter already owns this application-menu instance.
        // Mutate it so both the visible menu and native gestures update.
        var appMenu = NativeMenu.GetMenu(this) ?? new NativeMenu();
        appMenu.Items.Clear();
        var show = new NativeMenuItem(UiText.Text("显示主窗口"));
        show.Click += (_, _) => { if (desktop.MainWindow is MainWindow win) win.ShowAndActivate(); };
        appMenu.Items.Add(show);
        foreach (var (action, label) in new[] { (QuickAction.Lookup, "查词"), (QuickAction.Translate, "翻译句子"), (QuickAction.SaveQuote, "收藏金句") })
        {
            var item = new NativeMenuItem(UiText.Text(label));
            item.Click += async (_, _) => { if (desktop.MainWindow is MainWindow win) await win.HandleQuickActionAsync(action, 0); };
            appMenu.Items.Add(item);
        }
        var accessibility = new NativeMenuItem(UiText.Language == "en" ? "Open Accessibility Settings…" : "打开辅助功能设置…");
        accessibility.Click += (_, _) => MacOSNative.OpenAccessibilitySettings();
        appMenu.Items.Add(accessibility);
        appMenu.Items.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem(UiText.Text("退出 lexi")) { Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta) };
        quit.Click += (_, _) => QuitApplication();
        appMenu.Items.Add(quit);
        appMenu.NeedsUpdate += (_, _) =>
        {
            foreach (var item in appMenu.Items.OfType<NativeMenuItem>())
                item.Header = UiText.Redisplay(item.Header?.ToString());
        };
        NativeMenu.SetMenu(this, appMenu);
    }

    // Self-test, smoke and visual runs must never raise a system permission dialog.
    private static bool IsAutomatedTestRun() =>
        Environment.GetCommandLineArgs().Any(a => a is "--self-test" or "--ui-smoke" or "--visual-test" or "--language-test" or "--focus-test" or "--learning-test" or "--quick-test" or "--media-test" or "--recovery-restart-a" or "--recovery-restart-b");

    public void ToggleMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow win)
        {
            win.ToggleVisibility();
        }
    }

    public void QuitApplication()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow win)
            {
                if (win.IsRestoring) return;
                win.ForceClose();
            }
            desktop.Shutdown(0);
        }
    }
}

public sealed class SimpleRelayCommand : ICommand
{
    private readonly Action _action;
    public SimpleRelayCommand(Action action) => _action = action ?? throw new ArgumentNullException(nameof(action));
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
