using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Controls;
using Lexi.Features.Learning;

namespace Lexi;

public partial class MainWindow
{
    private Grid _learningHubPage = null!;
    private Button _navLearningHub = null!;
    private ScrollViewer _managementHost = null!;
    private Grid _studyWorkspaceHost = null!;

    private readonly StackPanel _learningPlansPanel = new() { Spacing = 16 };
    private readonly TextBlock _learningNotice = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 };
    private readonly TextBox _planName = new() { Name = "LearningPlanName", Watermark = "计划名称", Text = "我的学习计划" };
    private readonly TextBox _planQuota = new() { Name = "LearningPlanQuota", Watermark = "每日词数", Text = "20", Width = 120 };
    private readonly CheckBox _planRandom = new() { Name = "LearningPlanRandom", Content = "随机顺序", IsChecked = true };

    private List<DailyStudyPlan> _learningPlans = [];
    private DailyStudyPlanStore? _learningPlanStore;
    private string? _learningPlanPath;
    private bool _learningPlansLoadFailed;
    private DailyStudyPlan? _activeLearningPlan;
    private DailyStudyPlanSession? _dailyLearningSession;
    private DateOnly _dailyLearningDate;
    private bool _planAnswerVisible;
    private bool _completedPlansExpanded;

    // 拼写练习宿主与状态
    private TypingSession? _learningTypingSession;
    private TextBox? _learningTypingInput;
    private StackPanel? _learningTypingLetters;
    private TextBlock? _learningTypingResult;
    private TextBlock? _learningTypingStats;
    private SelectableTextBlock? _learningTypingHint;
    private List<LearningWord> _lastTypingWords = [];
    private Border? _typingHost;
    private string _typingPreviousPage = "learning";

    private void InitializeLearningHub()
    {
        // 1. 管理概览视图（内部独立滚动）
        var managementBody = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(28, 24),
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(LearningText("学习计划", 26));

        // 创建计划收敛为唯一实心主动作按钮 + 来源菜单
        var createBtn = new Button
        {
            Content = UiText.Text("创建学习计划 ▾"),
            Padding = new Thickness(16, 9)
        };
        createBtn.Classes.Add("primary");

        var menuFlyout = new MenuFlyout();
        var itemArchive = new MenuItem { Header = UiText.Text("从词汇档案创建计划...") };
        itemArchive.Click += (_, _) => OpenPlanCreator(DailyStudyPlanSource.Archive);
        var itemIelts = new MenuItem { Header = UiText.Text("从 IELTS 教材创建计划...") };
        itemIelts.Click += (_, _) => OpenPlanCreator(DailyStudyPlanSource.Ielts);

        menuFlyout.Items.Add(itemArchive);
        menuFlyout.Items.Add(itemIelts);
        createBtn.Flyout = menuFlyout;

        Grid.SetColumn(createBtn, 1);
        titleRow.Children.Add(createBtn);
        managementBody.Children.Add(titleRow);

        managementBody.Children.Add(_learningNotice);
        managementBody.Children.Add(_learningPlansPanel);

        _managementHost = new ScrollViewer
        {
            Name = "LearningManagementHost",
            Content = managementBody,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        // 2. 独立专注学习工作区（覆盖管理区，固定页头/底部评分，仅内容滚动）
        _studyWorkspaceHost = new Grid
        {
            Name = "StudyWorkspaceHost",
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            IsVisible = false
        };

        // 3. 根容器为 Grid，实现管理与学习的分离与覆盖
        _learningHubPage = new Grid { Name = "LearningHubPage", IsVisible = false };
        _learningHubPage.Children.Add(_managementHost);
        _learningHubPage.Children.Add(_studyWorkspaceHost);

        this.FindControl<Panel>("PagesHost")!.Children.Add(_learningHubPage);

        // 导航项
        _navLearningHub = new Button { Name = "NavLearningHub", Content = "学习计划", HorizontalAlignment = HorizontalAlignment.Stretch };
        _navLearningHub.Classes.Add("nav");
        _navLearningHub.Click += (_, _) => ShowPage("learning");
        this.FindControl<StackPanel>("LearningNavHost")!.Children.Add(_navLearningHub);

        // 拼写练习全屏宿主
        _typingHost = new Border { IsVisible = false };
        Grid.SetRow(_typingHost, 1);
        _typingHost.SetValue(Panel.ZIndexProperty, 100);
        ((Grid)RootWindowBorder.Child!).Children.Add(_typingHost);

        ReloadLearningPlans();
    }

    private static TextBlock LearningText(string text, double size = 14) => new() { Text = UiText.Text(text), FontSize = size, TextWrapping = TextWrapping.Wrap };

    private static Border LearningCard(Control child)
    {
        var card = new Border { Child = child, Padding = new Thickness(18) };
        card.Classes.Add("card");
        return card;
    }

    private Button LearningButton(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = UiText.Text(label), Padding = new Thickness(14, 8) };
        button.Classes.Add(primary ? "primary" : "secondary");
        button.Click += (_, _) =>
        {
            if (_restoring || !_databaseAvailable) { SetStatus("词库正在恢复或不可用，请稍后再试。"); return; }
            try { action(); } catch (Exception ex) { SetStatus("学习操作失败：" + ex.Message); }
        };
        return button;
    }

    private Button RatingButton(string label, StudyRating rating)
    {
        var button = new Button
        {
            Content = UiText.Text(label),
            MinWidth = 106,
            Padding = new Thickness(8, 8),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        // 统一主题中性，不强行红绿蓝
        button.Classes.Add("secondary");
        button.Click += (_, _) =>
        {
            if (_restoring || !_databaseAvailable) { SetStatus("词库正在恢复或不可用，请稍后再试。"); return; }
            try { RateDailyLearning(rating); } catch (Exception ex) { SetStatus("评分失败：" + ex.Message); }
        };
        return button;
    }

    private void ShowLearningHub(bool visible)
    {
        if (_learningHubPage == null) return;
        _learningHubPage.IsVisible = visible;
        if (_typingHost != null) _typingHost.IsVisible = visible && _learningTypingSession != null;
        _navLearningHub.Classes.Set("active", visible);
        if (!visible) return;

        var path = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "daily-plans.json");
        if (!string.Equals(path, _learningPlanPath, StringComparison.OrdinalIgnoreCase)) ReloadLearningPlans();

        RenderLearningPlans();
        if (_learningTypingSession == null && _dailyLearningSession != null && _studyWorkspaceHost.IsVisible)
        {
            RenderDailyLearning();
        }
    }

    private void ReloadLearningPlans()
    {
        LoadIeltsProgress();
        _activeLearningPlan = null;
        _dailyLearningSession = null;
        _learningTypingSession = null;
        if (_typingHost != null) _typingHost.IsVisible = false;

        _studyWorkspaceHost.Children.Clear();
        _studyWorkspaceHost.IsVisible = false;
        _managementHost.IsVisible = true;

        _learningPlanPath = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "daily-plans.json");
        _learningPlanStore = new DailyStudyPlanStore(_learningPlanPath);

        try
        {
            _learningPlans = _learningPlanStore.Load();
            _learningPlansLoadFailed = false;
            _learningNotice.Text = UiText.Text("已完成的计划进度会自动保存；切换页面保留本轮连击，重启后完整恢复未完成轮次与连击状态。");
        }
        catch (Exception ex)
        {
            _learningPlans = [];
            _learningPlansLoadFailed = true;
            _learningNotice.Text = "无法读取学习计划，已保留原文件且暂停计划写入。请备份并修复 " + _learningPlanPath + "。原因：" + ex.Message;
            SetStatus("计划文件读取失败，原文件未覆盖。");
        }

        RenderLearningPlans();
    }

    private bool SaveLearningPlans(IReadOnlyList<DailyStudyPlan> plans)
    {
        if (_restoring || !_databaseAvailable || _learningPlansLoadFailed || _learningPlanStore == null)
        {
            SetStatus("当前不能保存计划，请先解决词库或计划文件的问题。");
            return false;
        }

        if (!ReplayMemoryJournal()) return false;

        var currentPath = Path.Combine(Path.GetDirectoryName(_vocabService.DatabasePath)!, "daily-plans.json");
        if (!string.Equals(currentPath, _learningPlanPath, StringComparison.OrdinalIgnoreCase))
        {
            ReloadLearningPlans();
            SetStatus("词库位置已改变，请重新选择计划。");
            return false;
        }

        try
        {
            _learningPlanStore.Save(plans);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("计划保存失败，操作已撤回：" + ex.Message);
            return false;
        }
    }

    private List<DailyStudyPlanWord> ArchivePlanWords()
    {
        var selected = _allWords.Where(w => w.Selected).ToList();
        return (selected.Count > 0 ? selected : _allWords).Select(w => new DailyStudyPlanWord
        {
            Id = w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Word = w.Word,
            Meaning = w.Translation,
            Phonetic = w.Phonetic,
            Definition = w.Definition,
            Example = string.Join("\n", w.AiExamples.Select(e => e.English + "\n" + e.Chinese))
        }).ToList();
    }

    private void CreateLearningPlan()
    {
        if (!int.TryParse(_planQuota.Text, out var quota) || quota is < 1 or > 10000)
        {
            SetStatus("每日词数请输入 1–10000 的整数。");
            return;
        }

        var words = ArchivePlanWords();
        if (words.Count == 0)
        {
            SetStatus("词汇档案为空，请先收藏或导入词汇。");
            return;
        }

        var plan = DailyStudyPlanRules.Create(_planName.Text ?? "", DailyStudyPlanSource.Archive, "词汇档案", words, quota, _planRandom.IsChecked == true, Random.Shared.Next());
        var overlaps = DailyStudyPlanRules.FindOverlaps(_learningPlans, plan);
        var updated = _learningPlans.Append(plan).ToList();

        if (!SaveLearningPlans(updated)) return;

        _learningPlans = updated;
        RenderLearningPlans();
        SetStatus(overlaps.Count > 0
            ? $"计划已创建；与其他进行中计划有 {overlaps.Select(o => o.Word).Distinct().Count()} 个重复词，进度分别记录。"
            : $"计划已创建，共 {words.Count} 个词。");
    }

    private void ReplaceLearningPlan(DailyStudyPlan previous, DailyStudyPlan replacement)
    {
        var updated = _learningPlans.Select(p => p.Id == previous.Id ? replacement : p).ToList();
        if (!SaveLearningPlans(updated)) return;

        _learningPlans = updated;
        if (_activeLearningPlan?.Id == previous.Id)
        {
            _activeLearningPlan = null;
            _dailyLearningSession = null;
            _studyWorkspaceHost.Children.Clear();
            _studyWorkspaceHost.IsVisible = false;
            _managementHost.IsVisible = true;
        }

        RenderLearningPlans();
        SetStatus("计划已保存，已完成进度保留。");
    }

    private static DailyStudyPlan CloneLearningPlan(DailyStudyPlan plan) =>
        System.Text.Json.JsonSerializer.Deserialize<DailyStudyPlan>(System.Text.Json.JsonSerializer.Serialize(plan))!;

    private void RenderLearningPlans()
    {
        _learningPlansPanel.Children.Clear();
        var active = _learningPlans.Where(p => p.Status == DailyStudyPlanStatus.Active).ToList();
        var ieltsActive = active.Where(p => p.Source == DailyStudyPlanSource.Ielts).ToList();
        var archiveActive = active.Where(p => p.Source == DailyStudyPlanSource.Archive).ToList();
        var inactive = _learningPlans.Where(p => p.Status != DailyStudyPlanStatus.Active).ToList();
        var today = active.Sum(p => DailyStudyPlanRules.GetTodayWords(p, DateOnly.FromDateTime(DateTime.Now)).Count);

        if (_learningPlansLoadFailed)
        {
            var errBorder = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8) };
            errBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("TintBrush"));
            errBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
            errBorder.BorderThickness = new Thickness(1);
            var errLayout = new StackPanel { Spacing = 8 };
            errLayout.Children.Add(LearningText("计划文件读取失败，原文件已保留。", 14));
            errLayout.Children.Add(LearningButton("修复后重新读取计划文件", ReloadLearningPlans));
            errBorder.Child = errLayout;
            _learningPlansPanel.Children.Add(errBorder);
            return;
        }

        // 统一空状态卡：无计划时不重复空分组
        if (_learningPlans.Count == 0)
        {
            var emptyCard = new Border
            {
                Padding = new Thickness(32, 28),
                CornerRadius = new CornerRadius(12)
            };
            emptyCard.Classes.Add("card");

            var emptyLayout = new StackPanel
            {
                Spacing = 14,
                MaxWidth = 580,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            emptyLayout.Children.Add(new TextBlock
            {
                Text = "暂无学习计划",
                FontSize = 20,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            emptyLayout.Children.Add(new TextBlock
            {
                Text = "从已收藏词汇或 IELTS 教材中选词，安排每天的学习量。\n完成首学后，FSRS 会根据实际记忆表现安排后续复习；到期词进入「今日重逢」。",
                FontSize = 14,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });

            var promptText = new TextBlock
            {
                Text = "请点击右上角「创建学习计划 ▾」选择来源并开始创建。",
                FontSize = 13,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            emptyLayout.Children.Add(promptText);

            emptyCard.Child = emptyLayout;
            _learningPlansPanel.Children.Add(emptyCard);
            return;
        }

        // 有计划时显示统计条
        var statsBorder = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1)
        };
        statsBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBrush"));
        statsBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
        statsBorder.Child = LearningText($"{active.Count} 个活动计划 · 今日 {today} 词待学", 14);
        _learningPlansPanel.Children.Add(statsBorder);

        // 有计划才显示对应分组
        if (ieltsActive.Count > 0)
        {
            _learningPlansPanel.Children.Add(LearningText("IELTS 专题计划", 18));
            RenderPlanBoardGroup(ieltsActive);
        }

        if (archiveActive.Count > 0)
        {
            _learningPlansPanel.Children.Add(LearningText("词汇档案计划", 18));
            RenderPlanBoardGroup(archiveActive);
        }

        if (inactive.Count > 0)
        {
            // 已完成与停止计划默认折叠
            var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var toggleTitle = LearningText($"已完成与已停止的计划 ({inactive.Count})", 16);
            toggleRow.Children.Add(toggleTitle);

            var toggleBtn = LearningButton(_completedPlansExpanded ? "收起 ▴" : "展开 ▾", () =>
            {
                _completedPlansExpanded = !_completedPlansExpanded;
                RenderLearningPlans();
            });
            Grid.SetColumn(toggleBtn, 1);
            toggleRow.Children.Add(toggleBtn);

            _learningPlansPanel.Children.Add(toggleRow);

            if (_completedPlansExpanded)
            {
                RenderPlanBoardGroup(inactive);
            }
        }
    }

    private void RenderSinglePlanCard(DailyStudyPlan plan)
    {
        var panel = new StackPanel { Spacing = 10 };
        var status = plan.Status switch
        {
            DailyStudyPlanStatus.Active => "进行中",
            DailyStudyPlanStatus.Stopped => "已停止",
            _ => "已完成"
        };

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(new TextBlock { Text = plan.Name, FontSize = 17, FontWeight = FontWeight.SemiBold });
        var statusBadge = new TextBlock { Text = status, FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(statusBadge, 1);
        headerGrid.Children.Add(statusBadge);
        panel.Children.Add(headerGrid);

        panel.Children.Add(LearningText($"已完成 {plan.CompletedWordIds.Count}/{plan.Words.Count} · 每日 {plan.DailyWordCount} 词 · 预计剩余 {DailyStudyPlanRules.EstimatedDaysRemaining(plan)} 天"));

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        if (plan.Status == DailyStudyPlanStatus.Active)
        {
            // 单一主动作：开始 / 继续今日学习
            actions.Children.Add(LearningButton("开始 / 继续今日学习", () => StartDailyLearning(plan), primary: true));
            actions.Children.Add(LearningButton("停止计划", () => SetLearningPlanStopped(plan)));

            var name = new TextBox { Text = plan.Name, Watermark = "计划名称" };
            var quota = new TextBox { Text = plan.DailyWordCount.ToString(), Width = 100 };
            var random = new CheckBox { Content = "未来批次随机", IsChecked = plan.RandomOrder };
            var editor = new StackPanel { Spacing = 10, IsVisible = false };
            editor.Children.Add(name);

            var adjust = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            adjust.Children.Add(quota);
            adjust.Children.Add(random);
            adjust.Children.Add(LearningButton("保存调整", () =>
            {
                if (!int.TryParse(quota.Text, out var count) || count is < 1 or > 10000)
                {
                    SetStatus("每日词数请输入 1–10000 的整数。");
                    return;
                }
                AdjustLearningPlan(plan, name.Text ?? "", count, random.IsChecked == true);
            }));

            editor.Children.Add(adjust);
            editor.Children.Add(LearningText("调整在未来批次生效，已完成词保留。", 12));
            panel.Children.Add(editor);

            actions.Children.Add(LearningButton("调整计划", () => editor.IsVisible = !editor.IsVisible));
        }
        else
        {
            if (plan.Status == DailyStudyPlanStatus.Stopped)
            {
                actions.Children.Add(LearningButton("恢复计划", () => ResumeLearningPlan(plan)));
            }

            actions.Children.Add(LearningButton("删除计划", () =>
            {
                panel.Children.Add(LearningText("删除只清除此计划记录，词汇档案不受影响。"));
                panel.Children.Add(LearningButton("确认删除此计划", () =>
                {
                    var updated = _learningPlans.Where(p => p.Id != plan.Id).ToList();
                    if (!SaveLearningPlans(updated)) return;
                    _learningPlans = updated;
                    if (_activeLearningPlan?.Id == plan.Id)
                    {
                        _activeLearningPlan = null;
                        _dailyLearningSession = null;
                        _studyWorkspaceHost.Children.Clear();
                        _studyWorkspaceHost.IsVisible = false;
                        _managementHost.IsVisible = true;
                    }
                    RenderLearningPlans();
                    SetStatus("计划已删除。");
                }));
            }));
        }

        if (plan.CurrentBatchWordIds.Count > 0)
        {
            actions.Children.Add(LearningButton("最近批次拼写练习", () => ShowLastPlanBatch(plan)));
        }

        panel.Children.Add(actions);
        _learningPlansPanel.Children.Add(LearningCard(panel));
    }

    private void SetLearningPlanStopped(DailyStudyPlan plan)
    {
        var copy = CloneLearningPlan(plan);
        DailyStudyPlanRules.Stop(copy);
        ReplaceLearningPlan(plan, copy);
    }

    private void ResumeLearningPlan(DailyStudyPlan plan)
    {
        var copy = CloneLearningPlan(plan);
        DailyStudyPlanRules.Resume(copy);
        ReplaceLearningPlan(plan, copy);
    }

    private void AdjustLearningPlan(DailyStudyPlan plan, string name, int quota, bool random) =>
        ReplaceLearningPlan(plan, DailyStudyPlanRules.Adjust(plan, name, quota, random, Random.Shared.Next()));

    private void ShowLastPlanBatch(DailyStudyPlan plan)
    {
        _learningTypingSession = null;
        var words = plan.CurrentBatchWordIds.Select(id => plan.Words.Single(w => w.Id == id)).ToList();
        var forgotWords = words.Where(w => plan.ForgotWordIds.Contains(w.Id)).ToList();

        ShowPage("learning");
        _managementHost.IsVisible=false;
        _studyWorkspaceHost.IsVisible=true;
        _studyWorkspaceHost.Children.Clear();
        var content=new StackPanel {Spacing=20,Margin=new Thickness(24),MaxWidth=680,HorizontalAlignment=HorizontalAlignment.Center};
        content.Children.Add(LearningText(plan.Name,24));
        content.Children.Add(new PracticeSetupControl(words.Count,forgotWords.Count,
            (onlyWeak,_)=>StartLearningTyping((onlyWeak?forgotWords:words).Select(ToTypingWord).ToList(),true),
            ()=>{_studyWorkspaceHost.IsVisible=false;_managementHost.IsVisible=true;RenderLearningPlans();}));
        _studyWorkspaceHost.Children.Add(new ScrollViewer {Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled});
    }

    private void StartDailyLearning(DailyStudyPlan plan)
    {
        if (plan.Status != DailyStudyPlanStatus.Active)
        {
            SetStatus("该计划已停止或完成。");
            return;
        }

        _learningTypingSession = null;
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (_activeLearningPlan?.Id != plan.Id || _dailyLearningSession == null || _dailyLearningDate != today)
        {
            if (DailyStudyPlanRules.GetTodayWords(plan, today).Count == 0)
            {
                SetStatus("今日计划已完成，请明天继续；可在词汇档案开始拼写训练。");
                return;
            }

            var copy = CloneLearningPlan(plan);
            var session = new DailyStudyPlanSession(copy, today);
            var updated = _learningPlans.Select(p => p.Id == copy.Id ? copy : p).ToList();
            if (!SaveLearningPlans(updated)) return;

            _learningPlans = updated;
            _activeLearningPlan = copy;
            _dailyLearningSession = session;
            _dailyLearningDate = today;

            // 保持主控 MemoryBridge 钩子调用协议
            _planAnswerVisible = false;
            BeginPlanMemory(copy, session);
        }

        // 切换到专注学习工作区
        _managementHost.IsVisible = false;
        _studyWorkspaceHost.IsVisible = true;

        RenderDailyLearning();
        RenderLearningPlans();
    }

    private void ExitDailyLearning()
    {
        PersistLearningSurface("plan");
        _studyWorkspaceHost.IsVisible = false;
        _managementHost.IsVisible = true;
        _planAnswerVisible = false;
        RenderLearningPlans();
        SetStatus("学习会话已暂停，进度已实时保存。");
    }

    private void PlayCurrentLearningWordAudio()
    {
        if (_dailyLearningSession?.Round.HasCurrent == true && _activeLearningPlan != null)
        {
            var wordId = _dailyLearningSession.Round.Current;
            var word = _activeLearningPlan.Words.FirstOrDefault(w => w.Id == wordId);
            if (word != null)
            {
                GetLearningAudio().Play(word.Word, IeltsCatalog.ResolveAsset(word.AudioPath));
            }
        }
    }

    private Control BuildStreakDots(int currentStreak, int targetStreak)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (var i = 0; i < targetStreak; i++)
        {
            var isFilled = i < currentStreak;
            var dot = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1.5)
            };
            if (isFilled)
            {
                dot.Bind(Border.BackgroundProperty, this.GetResourceObservable("PrimaryGreen"));
                dot.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PrimaryGreen"));
            }
            else
            {
                dot.Background = Brushes.Transparent;
                dot.Bind(Border.BorderBrushProperty, this.GetResourceObservable("MutedBrush"));
            }
            panel.Children.Add(dot);
        }

        return panel;
    }

    private void RenderDailyLearning()
    {
        _studyWorkspaceHost.Children.Clear();
        if (_dailyLearningSession == null || _activeLearningPlan == null)
        {
            _studyWorkspaceHost.IsVisible = false;
            _managementHost.IsVisible = true;
            return;
        }

        var session = _dailyLearningSession;
        var round = session.Round;
        var sourceLabel = _activeLearningPlan.Source == DailyStudyPlanSource.Ielts ? "IELTS 专题" : "词汇档案";

        // ==================== 1. 固定页头 (Grid.Row 0) ====================
        var headerBorder = new Border
        {
            Padding = new Thickness(20, 12),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        headerBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        headerBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var btnBack = LearningButton("← 返回计划", ExitDailyLearning);
        headerGrid.Children.Add(btnBack);

        var titleStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = $"{_activeLearningPlan.Name} · {sourceLabel}",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 360
        };
        titleText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("InkBrush"));
        titleStack.Children.Add(titleText);
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);

        var progressBadge = new TextBlock
        {
            Text = $"已完成 {round.Completed} / {round.Total} · 剩余 {round.Remaining} 词",
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        progressBadge.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        Grid.SetColumn(progressBadge, 2);
        headerGrid.Children.Add(progressBadge);

        headerBorder.Child = headerGrid;
        Grid.SetRow(headerBorder, 0);
        _studyWorkspaceHost.Children.Add(headerBorder);

        // ==================== 2. 可滚动中间内容区 (Grid.Row 1 - 仅此区滚动) ====================
        var contentScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var cardCenterContainer = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 680,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(24, 20)
        };

        if (round.IsFinished)
        {
            // 完成状态界面
            var completionCard = new Border
            {
                Padding = new Thickness(28),
                CornerRadius = new CornerRadius(12)
            };
            completionCard.Classes.Add("card");

            var compLayout = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
            var compTitle = new TextBlock
            {
                Text = "🎉 今日批次学习完成！",
                FontSize = 26,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            compTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
            compLayout.Children.Add(compTitle);

            compLayout.Children.Add(new TextBlock
            {
                Text = $"本批全部 {round.Total} 个词汇均已连续认识三次，今日学习目标圆满达成！",
                FontSize = 15,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            // 拼写强化准备：使用既有 PracticeSetupControl（唯一开始键），替代四个大按钮。
            var allBatchWords = session.BatchWords.ToList();
            var forgotBatchWords = allBatchWords.Where(w => _activeLearningPlan.ForgotWordIds.Contains(w.Id)).ToList();

            var setup = new PracticeSetupControl(allBatchWords.Count, forgotBatchWords.Count,
                (onlyWeak, hints) => StartLearningTyping((onlyWeak ? forgotBatchWords : allBatchWords).Select(ToTypingWord).ToList(), hints),
                () => { });
            setup.Margin = new Thickness(0, 10, 0, 0);
            compLayout.Children.Add(setup);

            var finishBtn = LearningButton("完成今日学习，返回计划概览", ExitDailyLearning, primary: true);
            finishBtn.HorizontalAlignment = HorizontalAlignment.Center;
            finishBtn.Margin = new Thickness(0, 10, 0, 0);
            compLayout.Children.Add(finishBtn);

            completionCard.Child = compLayout;
            cardCenterContainer.Children.Add(completionCard);
        }
        else
        {
            // 正常学习中：与 focus 共享 StudyCanvasControl 内容组件。
            var word = _activeLearningPlan.Words.Single(w => w.Id == round.Current);
            PresentPlanMemory(_activeLearningPlan, round.Current, round.CurrentStep);

            var isLearnStep = round.CurrentStep == StudyStep.Learn;

            // 模式与连击强化小标（与 focus 一致的进度表达）。
            var modeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var modeText = new TextBlock
            {
                Text = UiText.Text(isLearnStep ? "先学后测模式 · 请仔细阅读释义与例句" : "回忆卡测试"),
                FontSize = 13,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            modeText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
            modeRow.Children.Add(modeText);

            if (!isLearnStep)
            {
                var streakStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center
                };
                streakStack.Children.Add(BuildStreakDots(round.CurrentStreak, round.CurrentTarget));
                var streakText = new TextBlock
                {
                    Text = UiText.Format($"连击 {round.CurrentStreak}/{round.CurrentTarget}"),
                    FontSize = 12,
                    Opacity = 0.8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                streakText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
                streakStack.Children.Add(streakText);
                Grid.SetColumn(streakStack, 1);
                modeRow.Children.Add(streakStack);
            }

            cardCenterContainer.Children.Add(modeRow);

            // 朗读：沿用现有 LocalWordAudioPlayer 读音机制（代码库无 SpeakWordAsync）。
            var pronounceRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            

            // 统一内容组件：纯色背景上的单词 / 音标 / 释义 / 定义 / 例句，全部 SelectableTextBlock。
            var planCanvas = CreateStudyCanvas();
            planCanvas.Speak=()=>GetLearningAudio().Play(word.Word,IeltsCatalog.ResolveAsset(word.AudioPath));
            planCanvas.Render(new StudyCanvasModel
            {
                Word = word.Word,
                Phonetic = string.IsNullOrWhiteSpace(word.Phonetic) ? "" : $"/{word.Phonetic.Trim('/')}/",
                Meaning = word.Meaning,
                Details = word.Definition,
                Example = word.Example,
                MeaningVisible = isLearnStep || _planAnswerVisible,
                Placeholder = UiText.Text("释义已隐藏，请在心中回忆词义")
            });
            cardCenterContainer.Children.Add(planCanvas);
        }

        contentScroll.Content = cardCenterContainer;
        Grid.SetRow(contentScroll, 1);
        _studyWorkspaceHost.Children.Add(contentScroll);

        // ==================== 3. 固定底部评分操作栏 (Grid.Row 2) ====================
        var bottomBorder = new Border
        {
            Padding = new Thickness(20, 12),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        bottomBorder.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        bottomBorder.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));

        var bottomLayout = new StackPanel { Spacing = 8 };

        // 状态行
        var statusLine = new TextBlock
        {
            Text = UiText.Text("进度已实时保存至计划"),
            FontSize = 12,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        statusLine.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
        bottomLayout.Children.Add(statusLine);

        // 操作按钮行
        var actionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        actionRow.SizeChanged += (_, _) =>
        {
            var narrow = actionRow.Bounds.Width < 640;
            actionRow.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto");
            if (actionRow.Children.Count > 1)
            {
                var actions = actionRow.Children[^1];
                Grid.SetRow(actions, narrow ? 1 : 0); Grid.SetColumn(actions, narrow ? 0 : 2);
                Grid.SetColumnSpan(actions, narrow ? 3 : 1);
                actions.HorizontalAlignment = HorizontalAlignment.Right;
                actions.Margin = new Thickness(0, narrow ? 8 : 0, 0, 0);
            }
        };

        if (!round.IsFinished)
        {
            // 左侧：撤销 / 改判 (保留撤销改判能力)
            var undoBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var btnUndo = LearningButton("撤销上一次", UndoDailyLearning);
            btnUndo.IsEnabled = session.CanUndo;
            undoBox.Children.Add(btnUndo);

            var btnRevise = LearningButton("改判为忘记", ReclassifyDailyLearningAsForgot);
            btnRevise.IsEnabled = session.CanUndo;
            undoBox.Children.Add(btnRevise);

            actionRow.Children.Add(undoBox);

            // 右侧动作
            var rightBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            Grid.SetColumn(rightBox, 2);

            if (round.CurrentStep == StudyStep.Learn)
            {
                // 先学后测主动作
                var btnStartRecall = LearningButton(UiText.Text("看完了，开始回忆")+" ("+ShortcutHint(ShortcutAction.Primary)+")", CompleteDailyLearn, primary: true);
                rightBox.Children.Add(btnStartRecall);
            }
            else if (!_planAnswerVisible)
            {
                // 尚未揭晓
                var btnRevealBottom = LearningButton(UiText.Text("揭晓释义")+" ("+ShortcutHint(ShortcutAction.Primary)+")", () =>
                {
                    _planAnswerVisible = true;
                    RenderDailyLearning();
                }, primary: true);
                rightBox.Children.Add(btnRevealBottom);
            }
            else
            {
                // 三键评分：中性次级按钮，等宽，统一主题中性
                var btnForgot = RatingButton(UiText.Text("忘记")+" ("+ShortcutHint(ShortcutAction.Forgot)+")", StudyRating.Forgot);
                var btnUnsure = RatingButton(UiText.Text("模糊")+" ("+ShortcutHint(ShortcutAction.Unsure)+")", StudyRating.Unsure);
                var btnKnown = RatingButton(UiText.Text("认识")+" ("+ShortcutHint(ShortcutAction.Known)+")", StudyRating.Known);
                btnForgot.Name = "StudyRateForgot";
                btnUnsure.Name = "StudyRateUnsure";
                btnKnown.Name = "StudyRateKnown";

                rightBox.Children.Add(btnForgot);
                rightBox.Children.Add(btnUnsure);
                rightBox.Children.Add(btnKnown);
            }

            actionRow.Children.Add(rightBox);
        }
        else
        {
            // 已完成批次
            var undo = LearningButton("撤销上一次", UndoDailyLearning);
            undo.IsEnabled = session.CanUndo; actionRow.Children.Add(undo);
            var btnReturnOverview = LearningButton("返回计划概览", ExitDailyLearning, primary: true);
            Grid.SetColumn(btnReturnOverview, 2);
            actionRow.Children.Add(btnReturnOverview);
        }

        bottomLayout.Children.Add(actionRow);
        bottomBorder.Child = bottomLayout;
        Grid.SetRow(bottomBorder, 2);
        _studyWorkspaceHost.Children.Add(bottomBorder);
    }

    private void CompleteDailyLearn()
    {
        if (_restoring || !_databaseAvailable || _dailyLearningSession == null) return;
        _dailyLearningSession.CompleteLearn();
        _planPresentation = null;
        PersistLearningSurface("plan");
        _planAnswerVisible = false;
        RenderDailyLearning();
    }

    private void RateDailyLearning(StudyRating rating)
    {
        if (_restoring || !_databaseAvailable || _dailyLearningSession == null) return;
        if (_dailyLearningDate != DateOnly.FromDateTime(DateTime.Now) && _activeLearningPlan != null)
        {
            StartDailyLearning(_activeLearningPlan);
            SetStatus("日期已变化，今日计划卡片已刷新，请重新回忆。");
            return;
        }

        _planStreakBefore = _dailyLearningSession.Round.CurrentStreak;
        _planRatingApplied = false;

        if (_dailyLearningSession.Rate(rating, () => SaveLearningPlans(_learningPlans)) == null)
        {
            PersistLearningSurface("plan"); RenderDailyLearning();
            SetStatus("评分未能保存，已恢复原卡片，请重试。"); return;
        }

        _planAnswerVisible = false;
        RenderDailyLearning();
        RenderLearningPlans();
    }

    private void UndoDailyLearning()
    {
        if (_restoring || !_databaseAvailable || _dailyLearningSession == null) return;
        if (_dailyLearningSession.Undo(() => SaveLearningPlans(_learningPlans)))
        {
            _planAnswerVisible = true;
            RenderDailyLearning();
            RenderLearningPlans();
            SetStatus("已撤销上一次评分。");
        }
    }

    private void ReclassifyDailyLearningAsForgot()
    {
        if (_restoring || !_databaseAvailable || _dailyLearningSession == null || !_dailyLearningSession.CanUndo) return;
        // 先撤销上一作答恢复卡片，再直接提交“忘记了”
        if (_dailyLearningSession.Undo(() => SaveLearningPlans(_learningPlans)))
        {
            _planAnswerVisible = false;
            RateDailyLearning(StudyRating.Forgot);
            SetStatus("已将上一次作答改判为「忘记」。");
        }
    }

    private static LearningWord ToTypingWord(DailyStudyPlanWord word) => new()
    {
        Id = word.Id,
        Words = [word.Word],
        Meaning = word.Meaning,
        Phonetic = word.Phonetic,
        Example = word.Example,
        AudioPath = word.AudioPath
    };

    private void AddBatchTypingButtons(StackPanel panel, string label, List<DailyStudyPlanWord> words)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(LearningText($"{label} · {words.Count} 词"));
        foreach (var hints in new[] { true })
        {
            var button = LearningButton("拼写练习", () => StartLearningTyping(words.Select(ToTypingWord).ToList(), hints));
            button.IsEnabled = words.Count > 0;
            row.Children.Add(button);
        }
        panel.Children.Add(row);
    }

    private void StartArchiveTyping(bool hints) =>
        StartLearningTyping(ArchivePlanWords().Select(ToTypingWord).ToList(), hints);

    private void StartLearningTyping(List<LearningWord> words, bool hints)
    {
        if (words.Count == 0)
        {
            SetStatus("没有可训练的词汇，请先收藏或勾选词汇。");
            return;
        }

        _typingPreviousPage = _currentPage;
        if (_currentPage != "learning") ShowPage("learning");

        _lastTypingWords = words;
        _learningTypingSession = new TypingSession();
        _learningTypingSession.Reset(words, true);
        RenderLearningTyping();
        _typingHost!.IsVisible = true;
    }

    private void ExitLearningTyping()
    {
        _learningTypingSession = null;
        _typingHost!.IsVisible = false;
        if (_studyWorkspaceHost.IsVisible)
        {
            RenderDailyLearning();
        }
        else
        {
            RenderLearningPlans();
        }

        if (_typingPreviousPage != "learning") ShowPage(_typingPreviousPage);
    }

    private Lexi.Features.Learning.SpellingPracticeControl? _spellingPractice;
    private void RenderLearningTyping()
    {
        var session=_learningTypingSession;
        if(session==null)return;
        if(session.Current==null)
        {
            var completed=new StackPanel {Spacing=16,MaxWidth=680,Margin=new Thickness(24),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            completed.Children.Add(LearningText("本轮完成",26));
            completed.Children.Add(LearningText($"练习 {session.Count} 词 · 重试 {session.Retries} 次 · 提示辅助 {session.AssistedCount}"));
            var errors=_lastTypingWords.Where(w=>session.ErrorIds.Contains(w.Id)).ToList();
            if(errors.Count>0)completed.Children.Add(LearningButton("再练错误词",()=>StartLearningTyping(errors,true),true));
            completed.Children.Add(LearningButton("返回",ExitLearningTyping));
            _typingHost!.Child=completed;_typingHost.IsVisible=true;return;
        }
        var word=session.Current;
        _spellingPractice=new Lexi.Features.Learning.SpellingPracticeControl {Name="SpellingPractice"};
        _learningTypingInput=_spellingPractice.InputBox;_learningTypingInput.Name="LearningTypingInput";
        _learningTypingHint=_spellingPractice.HintBlock;
        _learningTypingResult=_spellingPractice.FeedbackBlock;
        _learningTypingLetters=new StackPanel {IsVisible=false};
        _learningTypingStats=new TextBlock();
        _spellingPractice.OnSubmit=_=>SubmitLearningTyping();
        _spellingPractice.OnNext=SubmitLearningTyping;
        _spellingPractice.OnHint=()=>{session.RevealHint();UpdateSpellingSurface();};
        _spellingPractice.OnRevealAnswer=()=>{session.RevealAnswer();UpdateSpellingSurface();};
        _spellingPractice.OnCancel=ExitLearningTyping;
        _spellingPractice.OnSpeak=()=>GetLearningAudio().Play(word.Word,IeltsCatalog.ResolveAsset(word.AudioPath));
        _typingHost!.Child=_spellingPractice;_typingHost.IsVisible=true;
        UpdateTypingStats();UpdateSpellingSurface();
        Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(ReferenceEquals(session,_learningTypingSession))_spellingPractice?.FocusInput();});
    }

    private void UpdateSpellingSurface()
    {
        var s=_learningTypingSession;if(s?.Current==null || _spellingPractice==null)return;
        _spellingPractice.Update(new Lexi.Features.Learning.SpellingPracticeModel
        {
            Meaning=string.IsNullOrWhiteSpace(s.Current.Meaning)?UiText.Bilingual("暂无释义","No meaning available"):s.Current.Meaning,
            Phonetic=s.Current.Phonetic,
            Hint=s.Hints && !string.IsNullOrWhiteSpace(s.HintText.Replace("_","").Trim())?s.HintText:UiText.Bilingual($"{s.Current.Word.Length} 个字母",$"{s.Current.Word.Length} letters"),
            Feedback=_learningTypingResult?.Text??"",
            IsCorrect=s.Outcome==TypingOutcome.Correct?true:s.Outcome==TypingOutcome.Retry?false:null,
            AnswerRevealed=s.Outcome==TypingOutcome.Correct,
            ProgressText=$"{s.Cursor}/{s.Count} · {UiText.Bilingual("重试","Retries")} {s.Retries} · {UiText.Bilingual("提示辅助","Assisted")} {s.AssistedCount}"
        });
    }
    private void SubmitLearningTyping()
    {
        if (_restoring || !_databaseAvailable) { SetStatus("词库正在恢复或不可用。"); return; }
        var session = _learningTypingSession;
        if (session?.Current == null || _learningTypingInput == null) return;

        if (session.Outcome == TypingOutcome.Correct)
        {
            session.Advance();
            RenderLearningTyping();
            return;
        }

        var outcome = session.Submit(_learningTypingInput.Text ?? "");
        if (outcome == TypingOutcome.Correct)
        {
            _ieltsProgress.Typed.Add(session.Current.Id);
            SaveIeltsProgress();
        }
        else if (outcome == TypingOutcome.Retry)
        {
            _ieltsProgress.Errors.Add(session.Current.Id);
            SaveIeltsProgress();
        }

        RenderTypingLetters(true);
        UpdateTypingStats();

        if (outcome == TypingOutcome.Retry)
        {
            _learningTypingResult!.Text = "有错字，请修改后重试。";
            _learningTypingInput.SelectAll();
            SystemFeedbackSound(false);
        }
        else if (outcome == TypingOutcome.Correct)
        {
            _learningTypingResult!.Text = "拼写正确！按 Enter 进入下一词。";
            _learningTypingInput.IsReadOnly = true;
            SystemFeedbackSound(true);
        }
        else
        {
            _learningTypingResult!.Text = "输入尚不完整，请继续输入。";
        }
        UpdateSpellingSurface();
    }

    private void UpdateTypingStats()
    {
        var s = _learningTypingSession;
        if (s != null && _learningTypingStats != null)
        {
            UpdateSpellingSurface();
            _learningTypingStats.Text = $"完成 {s.Cursor}/{s.Count} · 字符准确率 {s.Accuracy:F1}% · 重试 {s.Retries} 次 · 提示辅助 {s.AssistedCount} · {s.Wpm} WPM";
        }
    }

    private void RenderTypingLetters(bool submitted)
    {
        var s = _learningTypingSession;
        if (s?.Current == null || _learningTypingLetters == null || _learningTypingInput == null) return;
        _learningTypingLetters.Children.Clear();

        var input = TypingSession.Normalize(_learningTypingInput.Text ?? "");
        // 输入中（含提示模式）不做即时错误着色，也不按真实答案泄露未输入字母。
        if (!submitted || s.Outcome == TypingOutcome.Pending)
        {
            foreach (var ch in input) _learningTypingLetters.Children.Add(LearningText(ch.ToString(), 23));
            return;
        }

        // 提交后仅按已输入内容逐字母判定，绝不泄露未输入的答案字母。
        foreach (var letter in TypingFeedbackModel.Build(TypingSession.Normalize(s.Current.Word), input, hints: false, s.Outcome))
        {
            var text = LearningText(letter.Character.ToString(), 23);
            if (letter.Tone == TypingLetterTone.Correct)
            {
                text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("PrimaryGreen"));
            }
            else if (letter.Tone == TypingLetterTone.Wrong)
            {
                text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
            }
            else
            {
                text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedBrush"));
            }
            _learningTypingLetters.Children.Add(text);
        }
    }
}
