using System.Windows;

namespace FastShare;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Relaunched by an update: "--updated <pid>" means the copy that swapped our files is
        // still shutting down. Wait for it to release the share port, then sweep away the
        // .fsold files it left behind now that nothing has them open.
        var args = e.Args;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--updated" && int.TryParse(args[i + 1], out var pid))
            {
                Updater.WaitForPrevious(pid);
                break;
            }
        }

        Updater.CleanStaleFiles();

        // Anything that escapes a handler is written to log.txt with its full stack, so a crash
        // on the far PC leaves something to go on. UI-thread faults are survivable - logged,
        // shown once, and the app carries on - rather than taking the whole window down.
        DispatcherUnhandledException += (_, args) =>
        {
            Diagnostics.Log("crash", "unhandled on the UI thread", args.Exception);
            args.Handled = true;
            ReportOnce(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diagnostics.Log("crash", $"unhandled (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diagnostics.Log("crash", "unobserved task fault", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static bool _reported;

    private static void ReportOnce(Exception ex)
    {
        if (_reported) return;
        _reported = true;
        try
        {
            MessageBox.Show(
                "Something went wrong inside FastShare. It has kept running, but if things look odd, restart it.\n\n" +
                $"{ex.GetBaseException().Message}\n\nThe details are in log.txt (Settings → Open log).",
                "FastShare hit a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }
}
