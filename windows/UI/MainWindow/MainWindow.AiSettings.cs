using System.Threading;
using System.Threading.Tasks;

namespace Lexi;

public partial class MainWindow
{
    private CancellationTokenSource? _connectionTestCts;
    private int _connectionTestEpoch;
    private CancellationTokenSource? _lookupAiCts;

    private void BindAiConnectionTest()
    {
        SettingsTestConnectionBtn.Click += async (_, _) => await TestAiConnectionAsync();
        SettingsCancelConnectionBtn.Click += (_, _) => _connectionTestCts?.Cancel();
        Closing += (_, _) => { CancelAiConnectionTest(); CancelLookupFallback(); };
        Closed += (_, _) => CancelAiConnectionTest();
    }

    private void CancelLookupFallback()
    {
        ++_lookupVersion;
        _lookupAiCts?.Cancel();
        LookupBtn.IsEnabled = true;
        LookupBtn.Content = "查询 ↵";
    }

    private void CancelAiConnectionTest()
    {
        ++_connectionTestEpoch;
        _connectionTestCts?.Cancel();
        SettingsTestConnectionBtn.IsEnabled = true;
        SettingsCancelConnectionBtn.IsVisible = false;
    }

    private async Task TestAiConnectionAsync()
    {
        if (_connectionTestCts != null || _restoring || !_databaseAvailable) return;
        var epoch = ++_connectionTestEpoch;
        using var cancel = new CancellationTokenSource();
        _connectionTestCts = cancel;
        SettingsTestConnectionBtn.IsEnabled = false;
        SettingsCancelConnectionBtn.IsVisible = true;
        try
        {
            var config = ReadQuickAiSettings();
            config.IncludeSourceInAi = SettingsIncludeSourceBox.IsChecked == true;
            SetStatus("正在测试连接…本次测试会调用所选模型。");
            var translation = await _aiService.TranslateAsync("Hello", config, cancel.Token);
            if (epoch == _connectionTestEpoch && !cancel.IsCancellationRequested)
                SetStatus("连接成功 · " + translation);
        }
        catch (Exception ex)
        {
            if (epoch == _connectionTestEpoch) SetStatus(cancel.IsCancellationRequested ? "已取消连接测试。" : "连接测试失败：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_connectionTestCts, cancel)) _connectionTestCts = null;
            if (epoch == _connectionTestEpoch)
            {
                SettingsTestConnectionBtn.IsEnabled = true;
                SettingsCancelConnectionBtn.IsVisible = false;
            }
        }
    }
}
