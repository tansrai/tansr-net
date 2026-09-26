using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tansr.Sdk.Views;

internal sealed class WpfApplication : Application
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new WpfApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var output = new TextBlock { Text = "Tansr 独立 NuGet 消费", Margin = new Thickness(20) };
        var window = new Window { Title = "Tansr package consumer", Content = output, Width = 440, Height = 140 };
        window.ContentRendered += async (_, _) =>
        {
            try
            {
                if (SynchronizationContext.Current is not DispatcherSynchronizationContext) throw new InvalidOperationException("wpf_context");
                using var view = new SessionView();
                view.AppendUserMessage("自包含 WPF 消费 😀");
                output.Text = "WPF、SDK 与本地 SQLite 已装入";
                var result = await Program.RunAsync(args);
                if (!window.Dispatcher.CheckAccess()) throw new InvalidOperationException("wpf_dispatcher");
                application.Shutdown(result);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                application.Shutdown(1);
            }
        };
        window.Show();
        return application.Run();
    }
}
