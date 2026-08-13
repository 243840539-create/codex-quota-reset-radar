using WindexBar.Core.Config;
using WindexBar.Core.Refresh;

namespace QuotaResetRadar.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var settings = new SettingsStore(new WindexBarConfigStore());
        using var usageStore = new UsageStore(settings);
        Application.Run(new RadarForm(usageStore));
    }
}
