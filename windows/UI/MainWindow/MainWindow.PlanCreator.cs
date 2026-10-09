using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Lexi.Features.Learning;

namespace Lexi;

public partial class MainWindow
{
    private Border? _planCreatorOverlay;
    private ContentControl? _planCreatorContent;

    private void ClosePlanCreator()
    {
        if (_planCreatorOverlay != null)
            ((Grid)((Grid)RootWindowBorder.Child!).Children[0]).Children.Remove(_planCreatorOverlay);
        _planCreatorOverlay = null; _planCreatorContent = null;
    }

    private void OpenPlanCreator(DailyStudyPlanSource source, LearningSection? initialSection = null)
    {
        if (_planCreatorOverlay != null) return;
        if (_learningPlansLoadFailed || !_databaseAvailable || _restoring)
        {
            SetStatus("计划数据暂不可用，请先完成词库恢复。");
            return;
        }

        if (source == DailyStudyPlanSource.Ielts)
        {
            try { _ieltsCatalog ??= IeltsCatalog.Load(); }
            catch (Exception ex) { SetStatus("IELTS 资源加载失败：" + ex.Message); return; }
        }

        var sections = _ieltsCatalog?.Sections.Where(s => s.Kind == "vocabulary").ToList() ?? [];
        if (initialSection != null) initialSection = sections.FirstOrDefault(s => s.Id == initialSection.Id);
        if (source == DailyStudyPlanSource.Ielts && sections.Count == 0)
        {
            SetStatus("IELTS 词汇目录为空。");
            return;
        }

        var archiveWords = _allWords.Select(w => new DailyStudyPlanWord
        {
            Id = w.Id.ToString(CultureInfo.InvariantCulture),
            Word = w.Word,
            Meaning = w.Translation,
            Phonetic = w.Phonetic,
            Definition = w.Definition,
            Example = string.Join("\n", w.AiExamples.Select(e => e.English + "\n" + e.Chinese))
        }).ToList();

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var preselectedNotice = "";

        if (source == DailyStudyPlanSource.Archive)
        {
            var marked = _allWords.Where(w => w.Selected)
                .Select(w => w.Id.ToString(CultureInfo.InvariantCulture))
                .ToList();
            if (marked.Count > 0)
            {
                selected.UnionWith(marked);
                preselectedNotice = $"已预置词汇档案中已勾选的 {marked.Count} 个词条。";
            }
            else
            {
                // 通用创建默认不全选整个档案
                preselectedNotice = "通用创建：未默认全选，请按需选择本页或搜索词条。";
            }
        }
        else
        {
            if (initialSection != null && sections.Contains(initialSection))
            {
                var availableIds = initialSection.Entries.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
                var matchedIelts = _ieltsSelected.Where(availableIds.Contains).ToList();
                if (matchedIelts.Count > 0)
                {
                    selected.UnionWith(matchedIelts);
                    preselectedNotice = $"已从当前章节勾选中预选 {matchedIelts.Count} 个词条。";
                }
                else
                {
                    selected.UnionWith(availableIds);
                    preselectedNotice = $"已预置章节「{initialSection.Title}」的全部 {availableIds.Count} 个词条。";
                }
            }
            else
            {
                if (sections.Count > 0) initialSection = sections[0];
                var availableIds = sections.SelectMany(s => s.Entries).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
                var matched = _ieltsSelected.Where(availableIds.Contains).ToList();
                if (matched.Count > 0)
                {
                    selected.UnionWith(matched);
                    preselectedNotice = $"已预置当前选中的 {matched.Count} 个词条。";
                }
                else
                {
                    // 通用 IELTS 创建不全选整本教材
                    preselectedNotice = "通用创建：未默认全选教材，请在上方选择章节并勾选所需词条。";
                }
            }
        }

        var dialog = new ContentControl
        {
            Name = "PlanCreatorContent", MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var rootGrid = new Grid();

        // ==================== Step 1: 选词视图 ====================
        var step1View = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(24, 20),
            RowSpacing = 12
        };

        // Step 1 Header
        var step1Header = new StackPanel { Spacing = 10 };
        var step1TitleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        step1TitleRow.Children.Add(new TextBlock { Text = "创建每日学习计划", FontSize = 20, FontWeight = FontWeight.SemiBold });
        var step1Badge = new TextBlock
        {
            Text = "第 1 步 / 共 2 步 · 选择词条",
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(step1Badge, 1);
        step1TitleRow.Children.Add(step1Badge);
        step1Header.Children.Add(step1TitleRow);

        var scopeNoticeText = new TextBlock
        {
            Text = preselectedNotice,
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };
        step1Header.Children.Add(scopeNoticeText);

        ComboBox? sectionPicker = null;
        if (source == DailyStudyPlanSource.Ielts)
        {
            var sectionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            sectionRow.Children.Add(new TextBlock { Text = "章节：", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            sectionPicker = new ComboBox
            {
                ItemsSource = sections,
                SelectedItem = initialSection,
                MinWidth = 280,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            sectionRow.Children.Add(sectionPicker);
            step1Header.Children.Add(sectionRow);
        }

        var searchBox = new TextBox
        {
            Name = "PlanCreatorSearch",
            Watermark = "搜索词条（按英文单词或中文释义）"
        };
        step1Header.Children.Add(searchBox);

        var selectionToolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        var btnSelectPage = LearningButton("选本页", () => { });
        var btnSelectFiltered = LearningButton("选当前筛选结果", () => { });
        var btnClearFiltered = LearningButton("清空当前筛选", () => { });
        var btnClearAll = LearningButton("清空全部选择", () => { });
        var chkOnlySelected = new CheckBox
        {
            Name = "PlanCreatorOnlySelected",
            Content = "仅看已选",
            IsChecked = false,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        selectionToolbar.Children.Add(btnSelectPage);
        selectionToolbar.Children.Add(chkOnlySelected);
        var moreActions = new StackPanel { Spacing = 8 };
        moreActions.Children.Add(btnSelectFiltered); moreActions.Children.Add(btnClearFiltered); moreActions.Children.Add(btnClearAll);
        var moreFlyout = new Flyout { Content = moreActions };
        var moreButton = LearningButton("更多…", () => { });
        moreButton.Flyout = moreFlyout;
        foreach (var action in moreActions.Children.OfType<Button>()) action.Click += (_, _) => moreFlyout.Hide();
        selectionToolbar.Children.Add(moreButton);
        step1Header.Children.Add(selectionToolbar);

        Grid.SetRow(step1Header, 0);
        step1View.Children.Add(step1Header);

        // Step 1 Middle Word List (Bounded * row)
        var listContainer = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 6
        };

        var listHeaderBar = new Border
        {
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = this.FindResource("LineBrush") as IBrush ?? Brushes.LightGray
        };
        var listHeaderCols = new Grid { ColumnDefinitions = new ColumnDefinitions("44,180,*") };
        listHeaderCols.Children.Add(new TextBlock { Text = "选择", FontSize = 12, Opacity = 0.6 });
        var colWord = new TextBlock { Text = "单词 · 音标", FontSize = 12, Opacity = 0.6 };
        Grid.SetColumn(colWord, 1);
        listHeaderCols.Children.Add(colWord);
        var colMeaning = new TextBlock { Text = "中文释义与例句", FontSize = 12, Opacity = 0.6 };
        Grid.SetColumn(colMeaning, 2);
        listHeaderCols.Children.Add(colMeaning);
        listHeaderBar.Child = listHeaderCols;
        Grid.SetRow(listHeaderBar, 0);
        listContainer.Children.Add(listHeaderBar);

        var rowsPanel = new StackPanel { Spacing = 4 };
        var listScroll = new ScrollViewer
        {
            Content = rowsPanel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(listScroll, 1);
        listContainer.Children.Add(listScroll);

        var pagerBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var btnPrevPage = LearningButton("上一页", () => { });
        var pageIndicator = new TextBlock
        {
            Text = "第 1 / 1 页",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        var btnNextPage = LearningButton("下一页", () => { });
        pagerBar.Children.Add(btnPrevPage);
        pagerBar.Children.Add(pageIndicator);
        pagerBar.Children.Add(btnNextPage);
        Grid.SetRow(pagerBar, 2);
        listContainer.Children.Add(pagerBar);

        Grid.SetRow(listContainer, 1);
        step1View.Children.Add(listContainer);

        // Step 1 Footer
        var step1Footer = new Border
        {
            Padding = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = this.FindResource("LineBrush") as IBrush ?? Brushes.LightGray
        };
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var selectedCountText = new TextBlock
        {
            Text = "已选 0 词",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeight.Medium
        };
        footerGrid.Children.Add(selectedCountText);

        var step1Buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var btnCancel1 = LearningButton("取消", ClosePlanCreator);
        var btnNextToStep2 = LearningButton("下一步：安排计划 →", () => { }, primary: true);
        step1Buttons.Children.Add(btnCancel1);
        step1Buttons.Children.Add(btnNextToStep2);
        Grid.SetColumn(step1Buttons, 1);
        footerGrid.Children.Add(step1Buttons);
        step1Footer.Child = footerGrid;

        Grid.SetRow(step1Footer, 2);
        step1View.Children.Add(step1Footer);

        // ==================== Step 2: 安排视图 ====================
        var step2View = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(24, 20),
            RowSpacing = 14,
            IsVisible = false
        };

        // Step 2 Header
        var step2Header = new StackPanel { Spacing = 10 };
        var step2TitleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        step2TitleRow.Children.Add(new TextBlock { Text = "创建每日学习计划", FontSize = 20, FontWeight = FontWeight.SemiBold });
        var step2Badge = new TextBlock
        {
            Text = "第 2 步 / 共 2 步 · 安排计划",
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(step2Badge, 1);
        step2TitleRow.Children.Add(step2Badge);
        step2Header.Children.Add(step2TitleRow);

        var step2SummaryCard = new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent
        };
        step2SummaryCard.Background=Brushes.Transparent;
        var step2SummaryText = new TextBlock
        {
            Text = "",
            FontSize = 13,
            FontWeight = FontWeight.Medium
        };
        step2SummaryCard.Child = step2SummaryText;
        step2Header.Children.Add(step2SummaryCard);

        Grid.SetRow(step2Header, 0);
        step2View.Children.Add(step2Header);

        // Step 2 Form Body (ScrollViewer bounded)
        var step2FormScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var step2Form = new StackPanel { Spacing = 14, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch };

        var defaultPlanName=source==DailyStudyPlanSource.Ielts?"我的 IELTS 计划":"我的词汇计划";
        var planEditor=new PlanEditorControl(new PlanEditorModel(defaultPlanName,20,false,selected.Count),_=>{},()=>{});
        planEditor.FutureBatchNoticeBlock.IsVisible=false;
        planEditor.SaveButton.IsVisible=false;planEditor.CancelButton.IsVisible=false;
        var planNameInput=planEditor.NameInput;planNameInput.Name="PlanCreatorName";
        var quotaInput=planEditor.DailyQuotaInput;quotaInput.Name="PlanCreatorQuota";
        var randomCheckbox=planEditor.ShuffleCheckbox;randomCheckbox.Name="PlanCreatorRandom";
        var estimateBody=planEditor.SummaryBlock;
        step2Form.Children.Add(planEditor);
        // Conflict Warning Panel (Explicit Confirmation Area)
        var conflictPanel = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Orange,
            Background = Brushes.Transparent,
            IsVisible = false
        };
        conflictPanel.Bind(Border.BackgroundProperty, this.GetResourceObservable("CardBrush"));
        var conflictLayout = new StackPanel { Spacing = 10 };
        var conflictTitle = new TextBlock
        {
            Text = "⚠️ 检测到与其他进行中计划存在重复词条",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.DarkOrange
        };
        var conflictDetails = new TextBlock
        {
            Text = "",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        var conflictExplanation = new TextBlock
        {
            Text = "说明：每个计划的学习进度独立记录。若确认在不同计划中同时学习这些词条，请点击下方确认继续；亦可返回上一步调整选词。",
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap
        };
        var conflictActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var btnBackToStep1FromConflict = LearningButton("返回修改选词", () => { });
        var btnConfirmConflictContinue = LearningButton("我已知晓，仍然创建计划", () => { }, primary: true);
        conflictActions.Children.Add(btnBackToStep1FromConflict);
        conflictActions.Children.Add(btnConfirmConflictContinue);

        conflictLayout.Children.Add(conflictTitle);
        conflictLayout.Children.Add(conflictDetails);
        conflictLayout.Children.Add(conflictExplanation);
        conflictLayout.Children.Add(conflictActions);
        conflictPanel.Child = conflictLayout;
        step2Form.Children.Add(conflictPanel);

        var formErrorText = new TextBlock
        {
            Text = "",
            FontSize = 13,
            Foreground = Brushes.IndianRed,
            TextWrapping = TextWrapping.Wrap
        };
        step2Form.Children.Add(formErrorText);

        step2FormScroll.Content = step2Form;
        Grid.SetRow(step2FormScroll, 1);
        step2View.Children.Add(step2FormScroll);

        // Step 2 Footer
        var step2Footer = new Border
        {
            Padding = new Thickness(0, 10, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = this.FindResource("LineBrush") as IBrush ?? Brushes.LightGray
        };
        var footer2Grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var btnBackToStep1 = LearningButton("← 上一步：重新选词", () => { });
        btnBackToStep1.HorizontalAlignment=HorizontalAlignment.Left;
        footer2Grid.Children.Add(btnBackToStep1);

        var step2Buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var btnCancel2 = LearningButton("取消", ClosePlanCreator);
        var btnSubmitPlan = LearningButton("创建计划", () => { }, primary: true);
        step2Buttons.Children.Add(btnCancel2);
        step2Buttons.Children.Add(btnSubmitPlan);
        Grid.SetColumn(step2Buttons, 1);
        footer2Grid.Children.Add(step2Buttons);
        step2Footer.Child = footer2Grid;

        Grid.SetRow(step2Footer, 2);
        step2View.Children.Add(step2Footer);

        rootGrid.Children.Add(step1View);
        rootGrid.Children.Add(step2View);
        dialog.Content = rootGrid;

        // ==================== 数据筛选与联动逻辑 ====================
        var page = 0;
        const int pageSize = PlanCreationState.PageSize;
        var isSubmitting = false;

        IReadOnlyList<DailyStudyPlanWord> CurrentCatalogWords()
        {
            if (source == DailyStudyPlanSource.Archive) return archiveWords;
            return (sectionPicker?.SelectedItem as LearningSection)?.Entries.Select(ToPlanWord).ToList() ?? [];
        }

        List<DailyStudyPlanWord> FilteredWords()
        {
            var baseList = CurrentCatalogWords();
            var keyword = searchBox.Text?.Trim();
            var query = baseList.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                query = query.Where(w =>
                    w.Word.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    w.Meaning.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    w.Definition.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }

            if (chkOnlySelected.IsChecked == true)
            {
                query = query.Where(w => selected.Contains(w.Id));
            }

            return query.ToList();
        }

        List<DailyStudyPlanWord> SelectedWords()
        {
            var allSourceWords = source == DailyStudyPlanSource.Archive
                ? archiveWords
                : sections.SelectMany(s => s.Entries).Select(ToPlanWord).ToList();

            return allSourceWords.Where(w => selected.Contains(w.Id)).DistinctBy(w => w.Id).ToList();
        }

        void RefreshStep1Summary()
        {
            var count = selected.Count;
            selectedCountText.Text = $"已选 {count} 词 (跨章节与搜索保留)";
            btnNextToStep2.IsEnabled = count > 0;
        }

        void RefreshStep2Summary()
        {
            var words = SelectedWords();
            var count = words.Count;
            var quota = (int)(quotaInput.Value ?? 20);
            var sourceName = source == DailyStudyPlanSource.Archive ? "词汇档案" : "IELTS 专题";

            step2SummaryText.Text = $"{sourceName} · 已选 {count} 个词条";
            var (_, desc) = PlanCreationState.CalculateEstimate(count, quota);
            estimateBody.Text = desc;
        }

        void RenderStep1Page()
        {
            rowsPanel.Children.Clear();
            var filtered = FilteredWords();
            var pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize);
            page = Math.Clamp(page, 0, pages - 1);

            var pageWords = filtered.Skip(page * pageSize).Take(pageSize).ToList();
            foreach (var word in pageWords)
            {
                var row=new PlanWordRowControl(PlanWordItemModel.FromDailyStudyPlanWord(word),selected.Contains(word.Id),isSelected=>
                {
                    if(isSelected)selected.Add(word.Id);else selected.Remove(word.Id);
                    conflictPanel.IsVisible=false;formErrorText.Text="";RefreshStep1Summary();
                });
                rowsPanel.Children.Add(row);
            }

            if (filtered.Count == 0)
            {
                rowsPanel.Children.Add(new TextBlock
                {
                    Text = chkOnlySelected.IsChecked == true ? "当前暂无已选词条。" : "没有匹配的词条。",
                    Opacity = 0.6,
                    Margin = new Thickness(12)
                });
            }

            pageIndicator.Text = $"第 {page + 1} / {pages} 页 (范围共 {filtered.Count} 词)";
            btnPrevPage.IsEnabled = page > 0;
            btnNextPage.IsEnabled = page < pages - 1;
            RefreshStep1Summary();
        }

        // ==================== 事件交互绑定 ====================

        // Step 1 批量选择操作
        btnSelectPage.Click += (_, _) =>
        {
            var filtered = FilteredWords();
            var pageWords = filtered.Skip(page * pageSize).Take(pageSize).ToList();
            selected.UnionWith(pageWords.Select(w => w.Id));
            conflictPanel.IsVisible = false;
            formErrorText.Text = "";
            RenderStep1Page();
        };

        btnSelectFiltered.Click += (_, _) =>
        {
            var filtered = FilteredWords();
            selected.UnionWith(filtered.Select(w => w.Id));
            conflictPanel.IsVisible = false;
            formErrorText.Text = "";
            RenderStep1Page();
        };

        btnClearFiltered.Click += (_, _) =>
        {
            var filtered = FilteredWords();
            selected.ExceptWith(filtered.Select(w => w.Id));
            conflictPanel.IsVisible = false;
            formErrorText.Text = "";
            RenderStep1Page();
        };

        btnClearAll.Click += (_, _) =>
        {
            selected.Clear();
            conflictPanel.IsVisible = false;
            formErrorText.Text = "";
            RenderStep1Page();
        };

        chkOnlySelected.IsCheckedChanged += (_, _) =>
        {
            page = 0;
            RenderStep1Page();
        };

        btnPrevPage.Click += (_, _) => { page--; RenderStep1Page(); };
        btnNextPage.Click += (_, _) => { page++; RenderStep1Page(); };

        searchBox.TextChanged += (_, _) => { page = 0; RenderStep1Page(); };

        if (sectionPicker != null)
        {
            sectionPicker.SelectionChanged += (_, _) =>
            {
                page = 0;
                RenderStep1Page();
            };
        }

        // Step 1 -> Step 2 切换
        btnNextToStep2.Click += (_, _) =>
        {
            if (selected.Count == 0)
            {
                SetStatus("请至少选择 1 个词条后再安排计划。");
                return;
            }
            step1View.IsVisible = false;
            step2View.IsVisible = true;
            RefreshStep2Summary();
        };

        // Step 2 -> Step 1 切换（保留全部已选与输入）
        void SwitchBackToStep1()
        {
            conflictPanel.IsVisible = false;
            formErrorText.Text = "";
            step2View.IsVisible = false;
            step1View.IsVisible = true;
            RenderStep1Page();
        }

        btnBackToStep1.Click += (_, _) => SwitchBackToStep1();
        btnBackToStep1FromConflict.Click += (_, _) => SwitchBackToStep1();

        quotaInput.ValueChanged += (_, _) => RefreshStep2Summary();

        // 提交创建执行函数
        void ExecuteCreation(DailyStudyPlan planToCreate, int wordCount)
        {
            if (isSubmitting) return;
            isSubmitting = true;
            btnSubmitPlan.IsEnabled = false;
            btnConfirmConflictContinue.IsEnabled = false;

            try
            {
                var updated = _learningPlans.Append(planToCreate).ToList();
                if (!SaveLearningPlans(updated))
                {
                    formErrorText.Text = "保存失败，请检查计划文件权限或磁盘状态。";
                    isSubmitting = false;
                    btnSubmitPlan.IsEnabled = true;
                    btnConfirmConflictContinue.IsEnabled = true;
                    return;
                }

                _learningPlans = updated;
                RenderLearningPlans();
                SetStatus($"计划「{planToCreate.Name}」已成功创建，共 {wordCount} 词。");
                ClosePlanCreator();
            }
            catch (Exception ex)
            {
                formErrorText.Text = "创建计划出错：" + ex.Message;
                isSubmitting = false;
                btnSubmitPlan.IsEnabled = true;
                btnConfirmConflictContinue.IsEnabled = true;
            }
        }

        // Step 2 提交校验与冲突处理
        btnSubmitPlan.Click += (_, _) =>
        {
            formErrorText.Text = "";
            var words = SelectedWords();
            if (words.Count == 0)
            {
                formErrorText.Text = "已选词数必须大于 0，请返回上一步选择词条。";
                return;
            }

            var planName = planNameInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(planName))
            {
                formErrorText.Text = "请输入计划名称。";
                return;
            }

            var dailyCount = (int)(quotaInput.Value ?? 0);
            if (dailyCount is < 1 or > 10000)
            {
                formErrorText.Text = "每日词数应在 1–10000 之间。";
                return;
            }

            var sourceLabel = source == DailyStudyPlanSource.Archive ? "词汇档案" : "IELTS 专题";
            var plan = DailyStudyPlanRules.Create(
                planName,
                source,
                sourceLabel,
                words,
                dailyCount,
                randomCheckbox.IsChecked == true,
                Random.Shared.Next());

            var overlaps = DailyStudyPlanRules.FindOverlaps(_learningPlans, plan);
            if (overlaps.Count > 0)
            {
                // 冲突明确展示，并要求用户点击专用的「我已知晓，仍然创建计划」按钮，杜绝同按钮二次隐式点击
                var grouped = overlaps.GroupBy(o => o.PlanName).ToList();
                var summaryLines = grouped.Select(g =>
                {
                    var sampleWords = g.Select(x => x.Word).Distinct().Take(4).ToList();
                    var totalOverlap = g.Select(x => x.Word).Distinct().Count();
                    return $"• 计划《{g.Key}》: 重叠 {totalOverlap} 词（{string.Join("、", sampleWords)}{(totalOverlap > 4 ? " 等" : "")}）";
                });

                conflictDetails.Text = $"当前选词中有 {overlaps.Select(o => o.Word).Distinct().Count()} 个词正在以下计划中学习：\n" +
                                       string.Join("\n", summaryLines);
                conflictPanel.IsVisible = true;
                formErrorText.Text = "检测到重复词条，请在上方核对并确认是否继续创建。";
                return;
            }

            ExecuteCreation(plan, words.Count);
        };

        // 冲突明确确认按钮
        btnConfirmConflictContinue.Click += (_, _) =>
        {
            var words = SelectedWords();
            var planName = planNameInput.Text?.Trim() ?? defaultPlanName;
            var dailyCount = (int)(quotaInput.Value ?? 20);
            var sourceLabel = source == DailyStudyPlanSource.Archive ? "词汇档案" : "IELTS 专题";

            var plan = DailyStudyPlanRules.Create(
                planName,
                source,
                sourceLabel,
                words,
                dailyCount,
                randomCheckbox.IsChecked == true,
                Random.Shared.Next());

            ExecuteCreation(plan, words.Count);
        };

        RenderStep1Page();
        _planCreatorContent = dialog;
        _planCreatorOverlay = new Border { Child = dialog };
        _planCreatorOverlay.Bind(Border.BackgroundProperty, this.GetResourceObservable("PaperBrush"));
        Grid.SetRow(_planCreatorOverlay, 1); _planCreatorOverlay.SetValue(Panel.ZIndexProperty, 230);
        ((Grid)((Grid)RootWindowBorder.Child!).Children[0]).Children.Add(_planCreatorOverlay);
        searchBox.Focus();
    }

    private static DailyStudyPlanWord ToPlanWord(LearningWord word) => new()
    {
        Id = word.Id,
        Word = word.Word,
        Meaning = word.Meaning,
        Phonetic = word.Phonetic,
        Definition = word.Extra,
        Example = word.Example,
        AudioPath = word.AudioPath
    };
}
