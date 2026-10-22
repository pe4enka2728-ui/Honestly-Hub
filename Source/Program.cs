namespace VpnZapretSwitch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ApplicationExit += (_, _) => IncyScreenControl.Shutdown();
        Application.ApplicationExit += (_, _) => EmbeddedVpnHost.Shared.Dispose();
        using var mutex = new Mutex(true, @"Local\VpnZapretSwitch", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Программа уже открыта.", "Honestly TUN");
            return;
        }
        try { Application.Run(new MainForm()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
