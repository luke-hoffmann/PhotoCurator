namespace PhotoCuratorApp;

static class Program
{
    [STAThread]
    static int Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\PhotoCuratorDesktop", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PhotoCurator is already open.", "PhotoCurator", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new MainForm());
        return 0;
    }
}
