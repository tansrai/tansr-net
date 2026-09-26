using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using Tansr.Examples;
using Tansr.Sdk.Client;

namespace WpfAssistant;

internal sealed class WorkspaceWindow : Window
{
    private readonly CancellationTokenSource lifetime;
    internal WorkspaceWindow(ExampleSessionWorkspace workspace, CancellationToken sessionLifetime)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime);
        Title = "会话工作台 · 能力 / 任务 / 快照"; Width = 900; Height = 650;
        AutomationProperties.SetAutomationId(this, "SessionWorkspace");
        var panel = new DockPanel { Margin = new Thickness(12) }; Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "动作作用于当前原会话；恢复、删除、分叉及工作区切换不会自动重试。结果未知时先查询列表和历史。页码从 0 开始，每页 25 条。", TextWrapping = TextWrapping.Wrap });
        var actions = new ComboBox { ItemsSource = ExampleSessionWorkspace.Actions, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 5) };
        AutomationProperties.SetAutomationId(actions, "WorkspaceAction"); top.Children.Add(actions);
        var input = new TextBox { MinHeight = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(input, "WorkspaceArgument"); top.Children.Add(input);
        var row = new WrapPanel(); top.Children.Add(row);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetAutomationId(output, "WorkspaceResult"); panel.Children.Add(output);
        var buttons = new List<Button>();
        void Add(string name, string id, Func<Task<string>> action)
        {
            var button = new Button { Content = name, Margin = new Thickness(3), Padding = new Thickness(8) };
            AutomationProperties.SetAutomationId(button, id); row.Children.Add(button); buttons.Add(button);
            button.Click += async (_, _) =>
            {
                foreach (var control in buttons) control.IsEnabled = false;
                try { var result = await action(); if (!lifetime.IsCancellationRequested) output.Text = result; }
                catch (OperationCanceledException) { if (IsVisible) output.Text = "本机等待已取消，远端结果尚未确认；不自动重投。"; }
                catch (Exception error) { if (IsVisible) output.Text = Error(error); }
                finally { if (IsVisible) foreach (var control in buttons) control.IsEnabled = !lifetime.IsCancellationRequested; }
            };
        }
        Add("执行选中操作", "WorkspaceExecute", () => workspace.ExecuteAsync(actions.SelectedIndex, input.Text, lifetime.Token));
        Add("导出输入 ID 的快照", "WorkspaceExport", async () =>
        {
            var file = new SaveFileDialog { Filter = "快照|*.json", OverwritePrompt = true };
            if (file.ShowDialog(this) != true) return "未导出。";
            await workspace.ExportAsync(input.Text, file.FileName, lifetime.Token); return "已写入新快照文件：" + file.FileName;
        });
        Add("导入快照文件", "WorkspaceImport", async () =>
        {
            var file = new OpenFileDialog { Filter = "快照|*.json" };
            return file.ShowDialog(this) == true ? await workspace.ImportAsync(file.FileName, lifetime.Token) : "未导入。";
        });
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }
    private static string Error(Exception error) => error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name;
}
