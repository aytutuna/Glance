namespace ScreenCopy;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ScreenCopy", out bool first);
        if (!first)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
