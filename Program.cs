namespace Glance;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Glance", out bool first);
        if (!first)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
