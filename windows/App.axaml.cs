using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        ShowHideCommand = new SimpleRelayCommand(ToggleMainWindow);
        QuitCommand = new SimpleRelayCommand(QuitApplication);
        DataContext = this;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            if (Environment.GetCommandLineArgs().Any(a => a is "--glass124-test" or "--visual123-test" or "--ui-smoke" or "--visual-test" or "--learning-integration" or "--focus-integration" or "--quick-test" or "--ai-settings-test" or "--language-test" or "--redesign-test" or "--interaction-test" or "--improvement-test"))
            {
                var started = false;
                mainWindow.Opened += async (_, _) =>
                {
                    if (started) return;
                    started = true;
                    await System.Threading.Tasks.Task.Delay(300);
                    if (Environment.GetCommandLineArgs().Contains("--glass124-test")) await Glass124Tests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--visual123-test")) await Visual123Tests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--improvement-test")) await Improvement122Tests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--interaction-test")) await InteractionRegressionTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--redesign-test")) await WindowsRedesignTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--language-test")) await LanguageUiTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--ai-settings-test")) await AiSettingsUiTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--quick-test")) await QuickActionUiTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--focus-integration")) await FocusIntegrationTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--learning-integration")) await LearningIntegrationTests.RunAsync(mainWindow);
                    else if (Environment.GetCommandLineArgs().Contains("--visual-test")) await VisualAcceptanceTests.RunAsync(mainWindow);
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

            // Bind global hotkey Alt+D
            if (Hotkey != null)
            {
                Hotkey.QuickActionPressed += action =>
                {
                    var source = OperatingSystem.IsWindows() ? Win32SelectionClipboard.CurrentForeground : 0;
                    Dispatcher.UIThread.Post(async () => { if (desktop.MainWindow is MainWindow win) await win.HandleQuickActionAsync(action, source); });
                };
            }

            if (Hotkey != null)
            {

            }
        }

        base.OnFrameworkInitializationCompleted();
    }

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
                if (win.IsRestoring || !win.TryFlushWritingDraft()) return;
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
