using System.Windows;
using System.Windows.Controls;
using Tansr.Examples;
using Tansr.Sdk.Client;

namespace WpfAssistant;

internal sealed class SessionControlsWindow : Window
{
    internal SessionControlsWindow(ExampleSessionControls? controls)
    {
        Title = "配置 / 记忆 · 显式 preview"; Width = 900; Height = 650;
        var panel = new DockPanel { Margin = new Thickness(12) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "此入口使用 schema 固定的 preview 合同。配置只修改本会话 model/thinking；记忆来源、修订、durable 和 consumed 均显示原回执。未知结果不会自动重投。", TextWrapping = TextWrapping.Wrap });
        if (controls == null)
        {
            top.Children.Add(new TextBlock { Text = "尚未启用。宿主需显式提供 TANSR_TERMINAL_PREVIEW=1 与 TANSR_TRUSTED_SCOPE_FILE；按 examples/Shared/session-controls.md 装配，重新连接后使用。", TextWrapping = TextWrapping.Wrap });
            return;
        }
        var actions = new ComboBox { ItemsSource = ExampleSessionControls.Actions, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 5) }; top.Children.Add(actions);
        top.Children.Add(new TextBlock { Text = "参数：模型填写受信模型别名；思考预算填非负整数或 null（清除）；置顶填全文，遗忘填主题。其余动作不需参数。", TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; top.Children.Add(input);
        var execute = new Button { Content = "执行选中操作", Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 6) }; top.Children.Add(execute);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = controls.DescribePending() }; panel.Children.Add(output);
        execute.Click += async (_, _) =>
        {
            execute.IsEnabled = false;
            try { output.Text = (await controls.ExecuteAsync(actions.SelectedIndex, input.Text)).GetRawText() + "\n\n" + controls.DescribePending(); }
            catch (Exception error) { output.Text = (error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name) + "\n发送前已持久保存的原操作保留；配置没有单独回执 GET。"; }
            finally { execute.IsEnabled = true; }
        };
    }
}
