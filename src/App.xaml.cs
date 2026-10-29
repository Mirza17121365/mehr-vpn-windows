using System.Windows;
using MehrVpn.Services;

namespace MehrVpn;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            Logger.Write("crash", e.Exception.ToString());
            MessageBox.Show("خطای غیرمنتظره:\n" + e.Exception.Message + "\n\nجزئیات در صفحه‌ی لاگ و فایل app.log است.",
                "Mehr VPN", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Logger.Write("crash", e.ExceptionObject?.ToString() ?? "unknown");
    }
}
