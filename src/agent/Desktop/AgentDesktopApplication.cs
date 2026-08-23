using CodexControl.Agent.Configuration;

namespace CodexControl.Agent.Desktop;

internal static class AgentDesktopApplication
{
    public static int Run(bool startHidden)
    {
        ApplicationConfiguration.Initialize();
        using var singleInstance = SingleInstanceGate.Acquire();
        if (!singleInstance.IsPrimary)
        {
            singleInstance.SignalPrimary();
            return 0;
        }

        try
        {
            var paths = AgentDataPaths.FromApplicationDirectory();
            var store = new AgentSettingsStore(paths);
            var loaded = store.Load();
            using var context = new TrayApplicationContext(paths, store, loaded, startHidden);
            singleInstance.Listen(context.ActivateWindow);
            Application.Run(context);
            return 0;
        }
        catch (AgentConfigurationException exception)
        {
            MessageBox.Show(
                exception.Message,
                "Codex Control 无法启动",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Codex Control 内部错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
