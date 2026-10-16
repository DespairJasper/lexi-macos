using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Social;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow
{
    private ScrollViewer? _socialPage;
    private StackPanel? _socialBody;
    private Button? _socialNav;
    private TextBlock? _socialMessage;
    private Border? _socialMessageCard;
    private SocialCloudOptions? _socialOptions;
    private string? _socialConfigurationError;
    private SupabaseSocialClient? _socialClient;
    private Func<SocialCloudOptions, SupabaseSocialClient>? _socialClientFactory;
    private SocialStore? _socialStore;
    private SocialSyncCoordinator? _socialSync;
    private readonly SocialOAuthFlow _socialOAuth = new();
    private CancellationTokenSource _socialCancellation = new();
    private long _socialGeneration;
    private bool _socialLoginPending;
    private DispatcherTimer? _socialSyncTimer;
    private bool _socialRecoveryPending;
    private string SocialDirectory => Path.GetDirectoryName(_vocabService.DatabasePath)!;
    private sealed record SocialLocalizedText(string DisplayedText, string? Chinese = null, string? English = null);

    private void ConfigureSocial()
    {
        var statistics = this.FindControl<Button>("NavStatistics")!;
        var sidebar = (Grid)statistics.Parent!;
        var row = Grid.GetRow(statistics) + 1;
        sidebar.RowDefinitions.Insert(row, new RowDefinition(GridLength.Auto));
        foreach (var child in sidebar.Children) if (Grid.GetRow(child) >= row) Grid.SetRow(child, Grid.GetRow(child) + 1);
        _socialNav = new Button { Name = "NavSocial", Classes = { "nav" } };
        SocialLocalize(_socialNav, "一起学习");
        Grid.SetRow(_socialNav, row); sidebar.Children.Add(_socialNav);
        _socialNav.Click += (_, _) => { if (FocusCanNavigate) ShowPage("social"); };
        _socialBody = new StackPanel { Spacing = 16, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        _socialPage = new ScrollViewer { Name = "SocialPage", IsVisible = false, Margin = new Thickness(30, 24, 30, 20),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _socialBody };
        ((Panel)LookupPageHost.Parent!).Children.Add(_socialPage);
        // Leaving the page or closing the window must stop every chart timer it owns.
        _socialPage.PropertyChanged += (_, args) =>
        { if (args.Property == Avalonia.Visual.IsVisibleProperty && !_socialPage.IsVisible) SocialStopCharts(); };
        _socialSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _socialSyncTimer.Tick += (_, _) => { _socialSyncTimer.Stop(); if (_socialClient?.UserId is { } actor) _ = SocialRefreshAsync(actor); };
        if (!App.IsAutomatedTestRun() && OperatingSystem.IsMacOS())
            _socialClientFactory = options => new(options, new MacKeychainSecretStore(SocialDirectory, "Lexi.Social"));
        try
        {
            _socialOptions = SocialCloudOptions.FromConfiguration(SocialDirectory, App.IsAutomatedTestRun());
            if (_socialOptions is not null && OperatingSystem.IsMacOS())
            {
                _socialClient = _socialClientFactory!(_socialOptions);
                _socialStore = new(SocialDirectory);
                _socialSync = new(_socialStore, _socialClient);
                SocialSessionReady();
            }
        }
        catch { _socialConfigurationError = "连接配置或登录状态暂无法读取；原文件和账号信息已保留。"; }
        RenderSocialEntry();
        Closed += (_, _) =>
        {
            ++_socialGeneration; _socialOAuth.Cancel(); _socialCancellation.Cancel();
            SocialStopCharts();
            _socialSyncTimer?.Stop();
            _socialClient?.Dispose(); _socialCancellation.Dispose();
        };
    }

    private TextBlock SocialText(string text, double size = 14)
    {
        var control = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
        SocialLocalize(control, text);
        return control;
    }

    private void SocialLocalize(Control control, string text)
    {
        var displayed = UiText.Redisplay(text);
        control.Tag = new SocialLocalizedText(displayed);
        SocialSetLocalizedValue(control, displayed);
    }

    private void SocialLocalize(Control control, string chinese, string english)
    {
        var displayed = UiText.Language == "en" ? english : chinese;
        control.Tag = new SocialLocalizedText(displayed, chinese, english);
        SocialSetLocalizedValue(control, displayed);
    }

    private static void SocialSetLocalizedValue(Control control, string displayed)
    {
        switch (control)
        {
            case TextBlock label: label.Text = displayed; break;
            case CheckBox check: check.Content = displayed; break;
            case Button button: button.Content = displayed; break;
            case TextBox box: box.Watermark = displayed; break;
            case Expander expander: expander.Header = displayed; break;
        }
    }

    private TextBox SocialInput(string name, string watermark, int maxLength)
    {
        var input = new TextBox { Name = name, MaxLength = maxLength };
        SocialLocalize(input, watermark);
        return input;
    }

    private TextBlock SocialTextPair(string chinese, string english, double size = 14)
    {
        var control = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap };
        SocialLocalize(control, chinese, english);
        return control;
    }

    private Expander SocialExpander(string header, Control content)
    {
        var expander = new Expander { Content = content };
        SocialLocalize(expander, header);
        return expander;
    }

    private void SocialSetMessage(string text)
    {
        if (_socialMessage is null) return;
        SocialLocalize(_socialMessage, text);
        if (_socialMessageCard is not null) _socialMessageCard.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void SocialSetMessage(string chinese, string english)
    {
        if (_socialMessage is null) return;
        SocialLocalize(_socialMessage, chinese, english);
        if (_socialMessageCard is not null) _socialMessageCard.IsVisible = !string.IsNullOrWhiteSpace(chinese);
    }

    private void RefreshSocialLanguage()
    {
        if (_socialNav is not null) SocialLocalize(_socialNav, "一起学习");
        if (_socialPage is null) return;
        var selfName = _socialProfile?.Nickname ?? _socialClient?.Nickname;
        for (var index = 0; index < _socialSeries.Count; index++)
            if (_socialSeries[index].IsSelf)
                _socialSeries[index] = _socialSeries[index] with
                { Label = string.IsNullOrWhiteSpace(selfName) ? T("我") : TF($"自己 · {selfName}") };
        foreach (var control in SocialLogicalControls(_socialPage))
        {
            if (control.Tag is SocialLocalizedText localized)
            {
                if (localized.Chinese is not null && localized.English is not null)
                    SocialLocalize(control, localized.Chinese, localized.English);
                else SocialLocalize(control, localized.DisplayedText);
            }
            if (control is SocialWeeklyChart chart)
            {
                chart.Title = UiText.Redisplay(chart.Title);
                chart.Unit = UiText.Redisplay(chart.Unit);
                chart.InvalidateVisual();
            }
            else if (control is SocialCurveOverlay curve)
                curve.InvalidateVisual();
        }
    }

    private static IEnumerable<Control> SocialLogicalControls(ILogical root)
    {
        foreach (var child in root.LogicalChildren)
        {
            if (child is Control control) yield return control;
            if (child is ILogical logical)
                foreach (var nested in SocialLogicalControls(logical)) yield return nested;
        }
    }

    private Button SocialButton(string text, string name, Func<Task> action)
    {
        var button = new Button { Name = name };
        button.Classes.Add(name is "SocialChromeLogin" or "SocialCreateInvite" or "SocialClaimInvite" or "SocialBindProfile"
            ? "primary" : "secondary");
        SocialLocalize(button, text);
        button.Click += async (_, _) =>
        {
            if (!FocusCanNavigate) return;
            button.IsEnabled = false;
            var generation = _socialGeneration;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch { if (generation == _socialGeneration) SocialSetMessage("社交操作暂未完成，请稍后重试。原有学习数据已保留。"); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private void RenderSocialEntry()
    {
        if (_socialBody is null) return;
        _socialBody.Children.Clear();
        _socialBody.Children.Add(SocialText("一起学习", 28));
        _socialBody.Children.Add(SocialText("和朋友各自完成今天的学习计划，共同积累学习天数。", 15));
        _socialMessage = SocialText(_socialConfigurationError ?? "");
        _socialMessageCard = new Border { Classes = { "card" }, Child = _socialMessage,
            IsVisible = !string.IsNullOrWhiteSpace(_socialConfigurationError) };
        _socialBody.Children.Add(_socialMessageCard);
        if (_socialClient?.UserId is { } actor)
        {
            var account = new StackPanel { Spacing = 8 };
            account.Children.Add(string.IsNullOrWhiteSpace(_socialClient.Nickname)
                ? SocialText("已登录 · 学习者") : SocialText(TF($"已登录 · {_socialClient.Nickname}")));
            account.Children.Add(SocialButton("退出 GitHub 登录", "SocialLogout", async () =>
            {
                // Drop every friend, invite, detail and cached avatar before the network revocation.
                SocialClearPrivateView(); SocialCancelOperations(); _socialFriends = null;
                var generation = _socialGeneration; var client = _socialClient;
                await client.LogoutAsync(); if (generation == _socialGeneration) RenderSocialEntry();
            }));
            _socialBody.Children.Add(new Border { Classes = { "card" }, Child = account });
            RenderSocialBinding(actor);
            RenderSocialWorkspace(actor);
            return;
        }
        var loginPanel = new StackPanel { Spacing = 10 };
        loginPanel.Children.Add(SocialText("开始与朋友一起学习", 20));
        loginPanel.Children.Add(SocialText("使用 GitHub 登录，创建自己的学习档案，然后邀请一位朋友。"));
        loginPanel.Children.Add(SocialButton("使用 Chrome 登录 GitHub", "SocialChromeLogin", () =>
        {
            if (App.IsAutomatedTestRun()) { SocialSetMessage("自动测试模式：浏览器授权已禁用。"); return Task.CompletedTask; }
            if (_socialOptions is null || _socialClient is null) { SocialSetMessage("社交服务当前不可用，请稍后重试。"); return Task.CompletedTask; }
            SocialCancelOperations();
            var start = _socialOAuth.Begin(_socialOptions.ProjectUrl, DateTimeOffset.UtcNow);
            try { MacOAuthBrowser.Open(start, _socialOptions); _socialLoginPending = true; SocialSetMessage("等待 Chrome 中的 GitHub 授权…"); }
            catch { _socialOAuth.Cancel(); throw; }
            return Task.CompletedTask;
        }));
        loginPanel.Children.Add(SocialButton("取消登录", "SocialCancelLogin", () =>
        {
            SocialCancelOperations(); SocialSetMessage("已取消登录。"); return Task.CompletedTask;
        }));
        _socialBody.Children.Add(new Border { Classes = { "card" }, Child = loginPanel });
    }

    private void SocialCancelOperations()
    {
        // Any reply still in flight belongs to the previous generation and cannot repopulate the view.
        SocialClearPrivateView(); SocialStopCharts();
        ++_socialGeneration; _socialOAuth.Cancel(); _socialLoginPending = false;
        _socialSyncTimer?.Stop();
        _socialCancellation.Cancel(); _socialCancellation.Dispose(); _socialCancellation = new();
    }

    private void SocialSessionReady()
    {
        _socialRecoveryPending = true;
        if (_socialClient?.UserId is not { } actor || _socialSync is null) return;
        try { _socialSync.SetSecondarySharing(actor, false, false); }
        catch { return; } // Foreign owner stays blocked; the explicit profile action is rendered below.
        _socialSyncTimer?.Start();
    }

    private void SocialEnterPage()
    {
        // Do not reconstruct focused inputs when the page is entered or a background read completes.
        if (_socialClient?.UserId is { } actor) _ = SocialRefreshAsync(actor);
    }

    internal async Task SocialProtocolActivatedAsync(Uri callback)
    {
        // Automated startup never constructs this client. Isolated UI fixtures inject only fake HTTP.
        if (!_socialLoginPending || _socialClient is null) return;
        if (!_socialOAuth.TryConsumeCallback(callback, DateTimeOffset.UtcNow, out var exchange, out var error))
        { if (error is not null) SocialSetMessage(error); return; }
        var generation = _socialGeneration; _socialLoginPending = false;
        try
        {
            await _socialClient.ExchangeAsync(exchange!, _socialCancellation.Token);
            if (generation != _socialGeneration) return;
            SocialSessionReady();
            RenderSocialEntry(); ShowAndActivate(); if (FocusCanNavigate) ShowPage("social");
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (generation != _socialGeneration) return;
            if (_socialClient.UserId is not null)
                SocialSetMessage("GitHub 已登录。请继续绑定当前学习档案。", "You are signed in to GitHub. Continue by linking this study profile.");
            else
                SocialSetMessage("GitHub 登录暂未完成，请重新登录。");
        }
    }
}
