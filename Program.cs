namespace Glance;

static class Program
{
    [STAThread]
    static void Main()
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Glance.Show");
        using var mutex = new Mutex(true, @"Local\Glance", out bool first);
        if (!first)
        {
            show.Set();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled", (Exception)e.ExceptionObject);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();

        TrayApp? app = null;
        Application.ThreadException += (_, e) =>
        {
            Log.Write("ui thread", e.Exception);
            app?.Notify(Strings.Crashed, ToolTipIcon.Error);
        };

        app = new TrayApp();
        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ui.Post(_ => app.ShowRunning(), null), null, -1, false);
        Application.Run(app);
    }
}
