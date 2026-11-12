namespace PSTrainerApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, "PSTrainer.SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
