using System.Windows;

namespace ClaudeVoice;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            // fix #16: don't swallow fatal exceptions — let OutOfMemory etc. crash naturally
            if (ex.Exception is OutOfMemoryException or StackOverflowException)
                return;

            MessageBox.Show($"Error:\n{ex.Exception.Message}", "ClaudeVoice",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
