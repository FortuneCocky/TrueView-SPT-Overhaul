using System;
using System.Windows;

namespace HardcoreDMGUI;

public partial class App : Application
{
    private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Unhandled error:\n" + e.Exception.ToString(), "HardcoreDMG Config Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
