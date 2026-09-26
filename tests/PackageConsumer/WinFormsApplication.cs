using System;
using System.Threading;
using System.Windows.Forms;

internal static class WinFormsApplication
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        var result = 1;
        using var form = new Form { Text = "Tansr .NET Framework package consumer", Width = 480, Height = 140 };
        form.Controls.Add(new Label { Text = "原生 WinForms NuGet 消费 😀", Dock = DockStyle.Fill, AutoSize = false });
        form.Shown += async (_, _) =>
        {
            try
            {
                if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext) throw new InvalidOperationException("winforms_context");
                result = await Program.RunAsync(args);
                if (form.InvokeRequired) throw new InvalidOperationException("winforms_dispatcher");
            }
            catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
        return result;
    }
}
