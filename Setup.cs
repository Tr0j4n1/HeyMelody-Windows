using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("MelodyDesk Setup")]

namespace MelodyDesk
{
    // MelodyDesk-Setup.exe: installs MelodyDesk for the current user, with no administrator rights. MelodyDesk.exe travels
    // inside it as a resource, and a copy of the setup stays in the install folder as the uninstaller.
    //   MelodyDesk-Setup.exe               install, update or uninstall from a small window
    //   MelodyDesk-Setup.exe --uninstall   what Settings > Apps > Uninstall runs
    static class Setup
    {
        const string Name = "MelodyDesk";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MelodyDesk";
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
        static readonly string AppExe = Path.Combine(Dir, "MelodyDesk.exe");
        static readonly string SetupExe = Path.Combine(Dir, "MelodyDesk-Setup.exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
        static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");
        static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(2);

        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application();
            using (var styles = Assembly.GetExecutingAssembly().GetManifestResourceStream("Styles.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(styles);
            Theme.Apply(app.Resources, Theme.Resolve(Theme.Chosen));

            if (args.Length > 0 && args[0] == "--uninstall")
            {
                if (!ConfirmUninstall()) return 1;
                try { Uninstall(); }
                catch (Exception ex) { MessageBox.Show("MelodyDesk could not be removed.\n" + ex.Message, "Uninstall MelodyDesk", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                MessageBox.Show("MelodyDesk was removed from this PC.", "Uninstall MelodyDesk");
                return 0;
            }
            return app.Run(BuildWindow());
        }

        static Window BuildWindow()
        {
            string installed = null;
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                if (key != null) installed = key.GetValue("DisplayVersion") as string;

            var window = new Window
            {
                Title = "MelodyDesk Setup", Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 14,
            };
            window.SetResourceReference(Control.BackgroundProperty, "WindowBg");
            window.SetResourceReference(Control.ForegroundProperty, "LabelPrimary");
            using (var png = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png"))
                window.Icon = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            window.SourceInitialized += (s, e) => Theme.ApplyTitleBar(window);

            var header = new DockPanel();
            var icon = new Image { Source = window.Icon, Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "MelodyDesk " + Version, FontSize = 20, FontWeight = FontWeights.SemiBold });
            titles.Children.Add(new TextBlock
            {
                Style = (Style)window.FindResource("RowSummary"),
                Text = installed == null ? "Earbuds control for Windows. Installs for you only; no administrator rights needed."
                     : installed == Version ? "Version " + installed + " is already installed."
                     : "Updates the installed version " + installed + ".",
            });
            header.Children.Add(titles);

            CheckBox startup, desktop;
            var card = new Border { Style = (Style)window.FindResource("Card"), Margin = new Thickness(0, 20, 0, 0) };
            var rows = new StackPanel();
            rows.Children.Add(ToggleRow(window, "Start with Windows", "Opens in the notification area when you sign in, so the earbuds connect without you opening it.",
                installed == null || Startup.Enabled, out startup));
            rows.Children.Add(new Border { Height = 1, Margin = new Thickness(16, 0, 16, 0), Background = (System.Windows.Media.Brush)window.FindResource("Divider") });
            rows.Children.Add(ToggleRow(window, "Desktop shortcut", null, File.Exists(DesktopLink), out desktop));
            card.Child = rows;

            var status = new TextBlock { Style = (Style)window.FindResource("RowSummary"), Margin = new Thickness(2, 12, 0, 0), Text = "Installs to " + Dir };

            var install = new Button { Content = installed == null ? "Install" : installed == Version ? "Reinstall" : "Update", Style = (Style)window.FindResource("AccentButton"), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Style = (Style)window.FindResource("PlainButton"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            var uninstall = new Button { Content = "Uninstall", Style = (Style)window.FindResource("PlainButton"), Visibility = installed == null ? Visibility.Collapsed : Visibility.Visible };
            var buttons = new DockPanel { Margin = new Thickness(0, 24, 0, 0), LastChildFill = false };
            DockPanel.SetDock(uninstall, Dock.Left);
            DockPanel.SetDock(cancel, Dock.Right);
            DockPanel.SetDock(install, Dock.Right);
            buttons.Children.Add(uninstall);
            buttons.Children.Add(cancel);
            buttons.Children.Add(install);

            Action<bool> busy = on => install.IsEnabled = cancel.IsEnabled = uninstall.IsEnabled = card.IsEnabled = !on;
            cancel.Click += (s, e) => window.Close();
            install.Click += async (s, e) =>
            {
                busy(true);
                status.Text = "Installing…";
                bool atStartup = startup.IsChecked == true, onDesktop = desktop.IsChecked == true;
                try { await Task.Run(() => Install(atStartup, onDesktop)); }
                catch (Exception ex)
                {
                    status.Text = "Installs to " + Dir;
                    busy(false);
                    MessageBox.Show(window, "MelodyDesk could not be installed.\n" + ex.Message, "MelodyDesk Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                Process.Start(AppExe);
                window.Close();
            };
            uninstall.Click += async (s, e) =>
            {
                if (!ConfirmUninstall()) return;
                busy(true);
                status.Text = "Removing…";
                try { await Task.Run(() => Uninstall()); }
                catch (Exception ex)
                {
                    busy(false);
                    MessageBox.Show(window, "MelodyDesk could not be removed.\n" + ex.Message, "MelodyDesk Setup", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                MessageBox.Show(window, "MelodyDesk was removed from this PC.", "MelodyDesk Setup");
                window.Close();
            };

            var page = new StackPanel { Margin = new Thickness(24) };
            page.Children.Add(header);
            page.Children.Add(card);
            page.Children.Add(status);
            page.Children.Add(buttons);
            window.Content = page;
            return window;
        }

        // A card row with a switch, like the app's own settings rows; clicking anywhere on the row flips it.
        static Button ToggleRow(Window window, string title, string summary, bool on, out CheckBox box)
        {
            var toggle = new CheckBox { IsChecked = on, Style = (Style)window.FindResource("Switch"), Margin = new Thickness(16, 0, 0, 0) };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = (Style)window.FindResource("RowTitle") });
            if (summary != null) text.Children.Add(new TextBlock { Text = summary, Style = (Style)window.FindResource("RowSummary") });
            var layout = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            layout.Children.Add(toggle);
            layout.Children.Add(text);
            var row = new Button { Style = (Style)window.FindResource("Row"), Content = layout };
            System.Windows.Automation.AutomationProperties.SetName(row, title);
            row.Click += (s, e) => toggle.IsChecked = toggle.IsChecked != true;
            box = toggle;
            return row;
        }

        static bool ConfirmUninstall()
        {
            return MessageBox.Show("Remove MelodyDesk from this PC?\n\nYour settings in %APPDATA%\\MelodyDesk are kept, in case you install it again.",
                "Uninstall MelodyDesk", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        static void Install(bool startup, bool desktopShortcut)
        {
            CloseRunningApp();
            Directory.CreateDirectory(Dir);
            using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("MelodyDesk.exe"))
            using (var file = File.Create(AppExe))
                embedded.CopyTo(file);
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (!string.Equals(self, SetupExe, StringComparison.OrdinalIgnoreCase)) File.Copy(self, SetupExe, true);

            Startup.Shortcut(StartMenuLink, AppExe, "");
            if (desktopShortcut) Startup.Shortcut(DesktopLink, AppExe, ""); else File.Delete(DesktopLink);
            Startup.Set(startup, AppExe);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", Name);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("DisplayIcon", AppExe);
                key.SetValue("InstallLocation", Dir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", "\"" + SetupExe + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)((new FileInfo(AppExe).Length + new FileInfo(SetupExe).Length) / 1024), RegistryValueKind.DWord);
            }
        }

        static void Uninstall()
        {
            CloseRunningApp();
            Startup.Set(false, AppExe);
            File.Delete(StartMenuLink);
            File.Delete(DesktopLink);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (self.StartsWith(Dir + "\\", StringComparison.OrdinalIgnoreCase))
                // A running exe can't delete itself, so a hidden cmd keeps trying (for up to a minute) until this one has exited.
                Process.Start(new ProcessStartInfo("cmd.exe", "/c for /l %i in (1,1,30) do (ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Dir + "\" 2> nul & if not exist \"" + Dir + "\" exit)")
                    { CreateNoWindow = true, UseShellExecute = false });
            else if (Directory.Exists(Dir))
                Directory.Delete(Dir, true);
        }

        // Asks a running MelodyDesk to quit, as Exit in its menu does, so its exe can be replaced or removed.
        // Versions before 1.4 don't listen for that request and are ended instead.
        static void CloseRunningApp()
        {
            EventWaitHandle exit;
            if (EventWaitHandle.TryOpenExisting(@"Local\MelodyDesk.Exit", out exit))
                using (exit) exit.Set();
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName(Name))
                using (process)
                {
                    if (process.SessionId != session || process.WaitForExit(5000)) continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
        }
    }
}
