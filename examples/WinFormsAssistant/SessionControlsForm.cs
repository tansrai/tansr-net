using System.Drawing;
using System.Windows.Forms;
using Tansr.Examples;
using Tansr.Sdk.Client;

namespace WinFormsAssistant;

internal sealed class SessionControlsForm : Form
{
    internal SessionControlsForm(ExampleSessionControls? controls)
    {
        Text = "配置 / 记忆 · 显式 preview"; Width = 900; Height = 650; Padding = new Padding(12);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        top.Controls.Add(new Label { Text = "schema 固定的 preview；model/thinking只修改本会话。记忆来源、修订、durable/consumed均显示原回执。未知结果不自动重投。", AutoSize = true, MaximumSize = new Size(830, 0) });
        if (controls == null)
        {
            top.Controls.Add(new Label { Text = "尚未启用。需宿主提供 TANSR_TERMINAL_PREVIEW=1 与 TANSR_TRUSTED_SCOPE_FILE，按 examples/Shared/session-controls.md 装配并重新连接。", AutoSize = true, MaximumSize = new Size(830, 0) });
            Controls.Add(top); return;
        }
        var actions = new ComboBox { Width = 820, DropDownStyle = ComboBoxStyle.DropDownList }; actions.Items.AddRange(ExampleSessionControls.Actions); actions.SelectedIndex = 0; top.Controls.Add(actions);
        top.Controls.Add(new Label { Text = "参数：模型=受信别名；思考预算=非负整数或null；置顶=全文；遗忘=主题；其它无参数。", AutoSize = true });
        var input = new TextBox { Multiline = true, MaxLength = int.MaxValue, Width = 820, Height = 80, ScrollBars = ScrollBars.Vertical }; top.Controls.Add(input);
        var execute = new Button { Text = "执行选中操作", AutoSize = true }; top.Controls.Add(execute);
        var output = new TextBox { ReadOnly = true, Multiline = true, MaxLength = int.MaxValue, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, Text = controls.DescribePending() };
        Controls.Add(output); Controls.Add(top);
        execute.Click += async (_, _) =>
        {
            execute.Enabled = false;
            try { output.Text = (await controls.ExecuteAsync(actions.SelectedIndex, input.Text)).GetRawText() + "\r\n\r\n" + controls.DescribePending(); }
            catch (Exception error) { if (!IsDisposed) output.Text = (error is TansrException sdk ? sdk.Code : error is InvalidOperationException ? error.Message : error.GetType().Name) + "\r\n发送前已持久保存的原操作保留；配置没有单独回执GET。"; }
            finally { if (!IsDisposed) execute.Enabled = true; }
        };
    }
}
