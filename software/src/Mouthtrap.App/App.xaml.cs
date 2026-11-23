using System.Globalization;
using System.Windows;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The saved language is applied before the window is built, because the window reads its text once.
        // A missing or unreadable file keeps the Windows language; the window reports the problem itself.
        if (SettingsStore.Load(SettingsStore.DefaultPath) is Ok<AppSettings>(var settings) && AppLanguages.Culture(settings.Language) is { } culture)
        {
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        new MainWindow().Show();
    }
}
