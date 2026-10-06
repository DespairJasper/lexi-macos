using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Lexi;

public partial class MainWindow
{
    private sealed record RoundControls(Grid View, NumericUpDown Count, CheckBox All, ComboBox Order);
    private RoundControls _typingRoundOptions = null!, _ieltsRoundOptions = null!;

    private RoundControls CreateRoundControls(string prefix)
    {
        var count = new NumericUpDown { Name = prefix + "Count", Minimum = 1, Maximum = 250000, Increment = 1,
            Value = Math.Clamp(_learningProgress.RoundSize, 1, 250000), FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
        var all = new CheckBox { Name = prefix + "All", Content = T("全部"), IsChecked = _learningProgress.RoundAll,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        var order = new ComboBox { Name = prefix + "Order", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { T("顺序学习"), T("随机学习") }, SelectedIndex = _learningProgress.RoundRandom ? 1 : 0 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), ColumnSpacing = 12 };
        var countField = new StackPanel { Spacing = 6 }; countField.Children.Add(LearningLabel("本轮词数", 12)); countField.Children.Add(count);
        var orderField = new StackPanel { Spacing = 6 }; orderField.Children.Add(LearningLabel("顺序", 12)); orderField.Children.Add(order);
        grid.Children.Add(countField); Grid.SetColumn(all, 1); grid.Children.Add(all); Grid.SetColumn(orderField, 2); grid.Children.Add(orderField);
        count.IsEnabled = !all.IsChecked.GetValueOrDefault();
        all.IsCheckedChanged += (_, _) => count.IsEnabled = !all.IsChecked.GetValueOrDefault();
        return new(grid, count, all, order);
    }
    private List<LearningWord> SelectLearningRound(List<LearningWord> words, RoundControls options)
    {
        _learningProgress.RoundSize = (int)(options.Count.Value ?? 20);
        _learningProgress.RoundAll = options.All.IsChecked.GetValueOrDefault();
        _learningProgress.RoundRandom = options.Order.SelectedIndex == 1;
        if (_learningProgress.RoundRandom) _learningProgress.LastShuffleSeed = Random.Shared.Next();
        SaveLearningProgress();
        return LearningRound.Select(words, _learningProgress.RoundSize, _learningProgress.RoundAll,
            _learningProgress.RoundRandom, _learningProgress.LastShuffleSeed);
    }
    private void RefreshRoundControls(RoundControls? options)
    {
        if (options == null) return;
        var index = options.Order.SelectedIndex;
        options.Order.ItemsSource = new[] { T("顺序学习"), T("随机学习") }; options.Order.SelectedIndex = index;
        options.All.Content = T("全部");
    }
}
