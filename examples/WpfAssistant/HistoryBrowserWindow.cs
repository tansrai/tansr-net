using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Tansr.Examples;

namespace WpfAssistant;

internal sealed class HistoryBrowserWindow : Window
{
    private readonly ExampleHistoryPager pager;
    private readonly CancellationTokenSource lifetime;
    private readonly Func<Exception, string> formatError;
    private readonly TextBox body = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
    private readonly Button first = new() { Content = "重新读取首页", Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) };
    private readonly Button next = new() { Content = "下一页", Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4), IsEnabled = false };
    private bool closed, reading;

    internal HistoryBrowserWindow(string sessionId, Func<int, int, CancellationToken, Task<JsonElement>> read, Func<bool> isCurrent,
        CancellationToken token, Func<Exception, string> formatError)
    {
        Title = "服务端历史 · 按需分页"; Width = 850; Height = 600;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        this.formatError = formatError;
        pager = new ExampleHistoryPager(sessionId, read, () => !closed && isCurrent());
        var panel = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "仅加载当前浏览页；不会恢复会话状态或写入模型上下文。工具配对可能扩大页面。", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel(); buttons.Children.Add(first); buttons.Children.Add(next); top.Children.Add(buttons); top.Children.Add(status);
        panel.Children.Add(top); panel.Children.Add(body); Content = panel;
        AutomationProperties.SetAutomationId(this, "HistoryBrowser");
        AutomationProperties.SetAutomationId(body, "HistoryPage");
        AutomationProperties.SetAutomationId(next, "HistoryNextPage");
        first.Click += async (_, _) => await LoadAsync(true);
        next.Click += async (_, _) => await LoadAsync(false);
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); body.Clear(); Content = null; };
    }

    internal Task LoadFirstAsync() => LoadAsync(true);
    private async Task LoadAsync(bool fromStart)
    {
        if (closed || reading) return;
        reading = true; first.IsEnabled = next.IsEnabled = false; status.Text = "正在读取本页…";
        try
        {
            if (fromStart) await pager.FirstAsync(lifetime.Token);
            else await pager.NextAsync(lifetime.Token);
            if (closed) return;
            body.Text = pager.Page!.Value.GetRawText();
            status.Text = $"原始下标 [{pager.Offset}, {pager.NextOffset})，总数 {pager.Total}；可见消息 {pager.Page.Value.GetProperty("messages").GetArrayLength()} 条。" +
                (pager.HasNext ? " 下一页仅在点击后读取。" : " 已到末页。");
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "读取已取消或会话已更换；保留此前页面，不自动重试。"; }
        catch (Exception error)
        {
            if (!closed) status.Text = error.Message == "history_page_changed"
                ? "历史水位或总数已改变；此前页面仅供回看。请重新读取首页，不拼接不同版本。"
                : "本页读取失败，保留此前页面：" + formatError(error);
        }
        finally
        {
            reading = false;
            if (!closed) { first.IsEnabled = true; next.IsEnabled = pager.HasNext; }
        }
    }
}
