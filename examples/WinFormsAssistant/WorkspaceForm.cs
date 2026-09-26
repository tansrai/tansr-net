using System.Drawing;
using System.Windows.Forms;
using Tansr.Examples;
using Tansr.Sdk.Client;

namespace WinFormsAssistant;

internal sealed class WorkspaceForm : Form
{
    private readonly CancellationTokenSource lifetime;
    internal WorkspaceForm(ExampleSessionWorkspace workspace, CancellationToken sessionLifetime)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime);
        Text = "会话工作台 · 能力 / 任务 / 快照"; Name = "SessionWorkspace"; Width = 900; Height = 650; Padding = new Padding(12);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        top.Controls.Add(new Label { Text = "动作作用于当前原会话；恢复、删除、分叉及工作区切换不会自动重试。结果未知时先查询列表和历史。页码从 0 开始，每页 25 条。", AutoSize = true, MaximumSize = new Size(830, 0) });
        var actions = new ComboBox { Name = "WorkspaceAction", Width = 820, DropDownStyle = ComboBoxStyle.DropDownList };
        actions.Items.AddRange(ExampleSessionWorkspace.Actions); actions.SelectedIndex = 0; top.Controls.Add(actions);
        var input = new TextBox { Name = "WorkspaceArgument", Multiline = true, MaxLength = int.MaxValue, Width = 820, Height = 60, ScrollBars = ScrollBars.Vertical }; top.Controls.Add(input);
        var row = new FlowLayoutPanel { AutoSize = true }; top.Controls.Add(row);
        var output = new TextBox { Name = "WorkspaceResult", ReadOnly = true, Multiline = true, MaxLength = int.MaxValue, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both };
        Controls.Add(output); Controls.Add(top);
        var buttons = new List<Button>();
        void Add(string text, string name, Func<Task<string>> action)
        {
            var button = new Button { Text = text, Name = name, AutoSize = true }; row.Controls.Add(button); buttons.Add(button);
            button.Click += async (_, _) =>
            {
                foreach (var control in buttons) control.Enabled = false;
                try { var result = await action(); if (!IsDisposed && !lifetime.IsCancellationRequested) output.Text = result.Replace("\n", "\r\n"); }
                catch (OperationCanceledException) { if (!IsDisposed) output.Text = "本机等待已取消，远端结果尚未确认；不自动重投。"; }
                catch (Exception error) { if (!IsDisposed) output.Text = Error(error); }
                finally { if (!IsDisposed) foreach (var control in buttons) control.Enabled = !lifetime.IsCancellationRequested; }
            };
        }
        Add("执行选中操作", "WorkspaceExecute", () => workspace.ExecuteAsync(actions.SelectedIndex, input.Text, lifetime.Token));
        Add("导出输入 ID 的快照", "WorkspaceExport", async () =>
        {
            using var file = new SaveFileDialog { Filter = "快照|*.json", OverwritePrompt = true };
            if (file.ShowDialog(this) != DialogResult.OK) return "未导出。";
            await workspace.ExportAsync(input.Text, file.FileName, lifetime.Token); return "已写入新快照文件：" + file.FileName;
        });
        Add("导入快照文件", "WorkspaceImport", async () =>
        {
            using var file = new OpenFileDialog { Filter = "快照|*.json" };
            return file.ShowDialog(this) == DialogResult.OK ? await workspace.ImportAsync(file.FileName, lifetime.Token) : "未导入。";
        });
        FormClosed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }
    private static string Error(Exception error) => error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name;
}
