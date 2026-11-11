namespace Glance;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Glance", out bool first);
        if (!first)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled", (Exception)e.ExceptionObject);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();

        TrayApp? app = null;
        Application.ThreadException += (_, e) =>
        {
            Log.Write("ui thread", e.Exception);
            app?.Notify("Something went wrong. Details are in glance.log.", ToolTipIcon.Error);
        };

        app = new TrayApp();
        Application.Run(app);
    }
}
