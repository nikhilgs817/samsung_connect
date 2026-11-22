using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

[assembly: AssemblyTitle("SamsungCONNECT")]
[assembly: AssemblyDescription("Connect your laptop to a TV and launch streaming apps from a big-screen home")]
[assembly: AssemblyVersion("3.0.0.0")]
[assembly: AssemblyFileVersion("3.0.0.0")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SamsungCONNECT
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool first;
            using (Mutex instance = new Mutex(true, "Local\\SamsungCONNECTSingleInstance", out first))
            using (EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SamsungCONNECTShow"))
            {
                if (!first) { showSignal.Set(); return 0; } // Already running: bring that window forward.
                System.Windows.Forms.Application.EnableVisualStyles();
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Controller controller = null;
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    e.Handled = true;
                    ActivityLog.Write("Crash: " + e.Exception);
                    if (controller != null) controller.RecoverBeforeExit();
                    MessageBox.Show("SamsungCONNECT could not continue. Your laptop screen has been restored; if not, press Win+P > PC screen only.\n\n" + e.Exception.Message,
                        "SamsungCONNECT", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown(1);
                };
                try
                {
                    bool hidden = Has(args, "--tray");
                    bool auto = !Has(args, "--no-connect") && (Has(args, "--connect") || Settings.AutoConnect);
                    controller = new Controller(hidden, auto);
                    Thread listener = new Thread(delegate()
                    {
                        while (showSignal.WaitOne())
                            app.Dispatcher.BeginInvoke((Action)delegate { controller.Window.BringHome(); });
                    }) { IsBackground = true };
                    listener.Start();
                    return app.Run();
                }
                catch (Exception ex)
                {
                    ActivityLog.Write("Startup failure: " + ex);
                    MessageBox.Show("SamsungCONNECT could not continue. Use Win+P > PC screen only if needed.\n\n" + ex.Message,
                        "SamsungCONNECT", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally
                {
                    if (controller != null) controller.RecoverBeforeExit();
                    instance.ReleaseMutex();
                }
            }
        }
        static bool Has(string[] args, string value)
        {
            foreach (string arg in args) if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
