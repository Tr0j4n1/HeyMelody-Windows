using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

[assembly: AssemblyTitle("MelodyDesk")]
[assembly: AssemblyVersion("1.3.0.0")]

namespace MelodyDesk
{
    static class Program
    {
        // MelodyDesk.exe            open the window (or bring up the copy that is already running)
        // MelodyDesk.exe --tray     start in the notification area only (used by "Start with Windows")
        // MelodyDesk.exe --selftest protocol checks, prints results, exit code 0 when all pass
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();

            // One instance owns the RFCOMM link; a second launch just asks the first to show its window.
            bool first;
            var instance = new Mutex(true, @"Local\MelodyDesk.Instance", out first);
            var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MelodyDesk.Show");
            if (!first)
            {
                showRequest.Set();
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // Keep running on a UI bug and leave a trace of it next to the settings file.
            app.DispatcherUnhandledException += (s, e) =>
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MelodyDesk", "error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.AppendAllText(file, DateTime.Now + " " + e.Exception + Environment.NewLine);
                MessageBox.Show(e.Exception.Message, "MelodyDesk error", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            using (var styles = Assembly.GetExecutingAssembly().GetManifestResourceStream("Styles.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(styles);
            Theme.Apply(app.Resources, Theme.Resolve(Theme.Chosen));

            var main = new MainWindow(!args.Contains("--tray"));
            new Thread(() => { while (showRequest.WaitOne()) main.Ui(main.ShowMain); }) { IsBackground = true, Name = "show-request" }.Start();
            int code = app.Run();
            GC.KeepAlive(instance);
            return code;
        }
    }
}
