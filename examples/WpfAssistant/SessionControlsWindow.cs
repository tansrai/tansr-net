using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Tansr.Examples;
using Tansr.Sdk.Client;

namespace WpfAssistant;

internal sealed class SessionControlsWindow : Window
{
    internal SessionControlsWindow(ExampleSessionControls? controls, CancellationToken sessionLifetime = default)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime);
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
        AutomationProperties.SetAutomationId(this, "SessionControls");
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
        AutomationProperties.SetAutomationId(actions, "SessionControlAction");
        top.Children.Add(new TextBlock { Text = "参数：模型填写受信模型别名；思考预算填非负整数或 null（清除）；置顶填全文，遗忘填主题。其余动作不需参数。", TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { AcceptsReturn = true, Height = 80, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; top.Children.Add(input);
        AutomationProperties.SetAutomationId(input, "SessionControlArgument");
        var execute = new Button { Content = "执行选中操作", Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 6) }; top.Children.Add(execute);
        AutomationProperties.SetAutomationId(execute, "SessionControlExecute");
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = controls.DescribePending() }; panel.Children.Add(output);
        AutomationProperties.SetAutomationId(output, "SessionControlResult");
        var models = new ComboBox { DisplayMemberPath = "Value", SelectedValuePath = "Key", MinWidth = 400 };
        AutomationProperties.SetAutomationId(models, "AuthorizedModelSelector"); top.Children.Add(models);
        var applyModel = new Button { Content = "应用选中授权模型", Padding = new Thickness(8) };
        AutomationProperties.SetAutomationId(applyModel, "ApplyAuthorizedModel"); top.Children.Add(applyModel);
        top.Children.Add(new TextBlock { Text = "先执行“授权模型目录”。配额由网关执行，数值未向终端开放；本人1天用量不代表账户余额。", TextWrapping = TextWrapping.Wrap });
        execute.Click += async (_, _) =>
        {
            execute.IsEnabled = applyModel.IsEnabled = false;
            try
            {
                if (actions.SelectedIndex == 10)
                {
                    var catalog = await controls.ReadCatalogAsync(lifetime.Token);
                    if (IsVisible) { models.ItemsSource = catalog.Models.Select(model => new KeyValuePair<string, string>(model.Handle, model.DisplayName + " [" + model.Handle + "]")).ToArray(); models.SelectedIndex = catalog.Models.Count > 0 ? 0 : -1; output.Text = catalog.Raw.GetRawText(); }
                }
                else { var result = await controls.ExecuteAsync(actions.SelectedIndex, input.Text, lifetime.Token); if (IsVisible) output.Text = result.GetRawText() + "\n\n" + controls.DescribePending(); }
            }
            catch (Exception error) { if (IsVisible) output.Text = (error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name) + "\n发送前已持久保存的原操作保留；配置没有单独回执 GET。"; }
            finally { if (IsVisible) execute.IsEnabled = applyModel.IsEnabled = !lifetime.IsCancellationRequested; }
        };
        applyModel.Click += async (_, _) =>
        {
            execute.IsEnabled = applyModel.IsEnabled = false;
            try
            {
                if (models.SelectedValue is not string model) throw new InvalidOperationException("select_authorized_model");
                var result = await controls.ExecuteAsync(1, model, lifetime.Token); if (IsVisible) output.Text = result.GetRawText();
            }
            catch (Exception error) { if (IsVisible) output.Text = error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name; }
            finally { if (IsVisible) execute.IsEnabled = applyModel.IsEnabled = !lifetime.IsCancellationRequested; }
        };
    }
}
