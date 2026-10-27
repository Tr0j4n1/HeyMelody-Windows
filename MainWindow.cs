using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Path = System.Windows.Shapes.Path;

namespace MelodyDesk
{
    // What the HeyMelody phone app shows for one model. Unknown models show everything the buds report.
    sealed class ModelProfile
    {
        public string NameContains;
        public int[] NoiseBits;                 // noise buttons, in display order
        public Tuple<int, string>[] EqPresets;  // EQ index and name; null lists indexes 0-6 as "Preset N"
        public int DefaultEq = -1;
        public int[] Features;                  // feature ids the phone shows; null shows all the buds report
    }

    // Drives MainWindow.xaml (embedded and loaded at runtime: csc has no XAML compiler), the tray icon and its flyout.
    sealed class MainWindow
    {
        // Noise-control bits on a OnePlus Nord Buds 3 Pro, identified by ear (bits 1 and 3 are accepted but alias to 4 and 2).
        // Bit 5 is what the buds return for the "intelligent" query (0x010C [04 01]).
        static readonly Dictionary<int, Tuple<string, string>> NoiseModes = new Dictionary<int, Tuple<string, string>> {
            {4, Tuple.Create("Noise cancellation", Icons.NoiseCancel)},
            {5, Tuple.Create("Adaptive", Icons.Adaptive)},
            {0, Tuple.Create("Off", Icons.NoiseOff)},
            {2, Tuple.Create("Transparency", Icons.Transparency)},
        };

        const int FeatureAutoPlay = 4, FeatureGameMode = 6, FeaturePersonalNoise = 12, FeatureDual = 17, FeatureHiRes = 24, FeatureBassWave = 29;
        static readonly int[] PlacedFeatures = { FeatureAutoPlay, FeatureGameMode, FeaturePersonalNoise, FeatureDual, FeatureBassWave };

        // Nord Buds 3 Pro as the HeyMelody phone app (2.30.0) shows it; EQ indexes read back from the buds while picking each on the phone.
        static readonly ModelProfile[] Profiles = {
            new ModelProfile {
                NameContains = "Nord Buds 3 Pro",
                NoiseBits = new[] { 4, 0, 2 },
                EqPresets = new[] { Tuple.Create(3, "Balanced"), Tuple.Create(4, "Serenade"), Tuple.Create(5, "Bass") },
                DefaultEq = 3,
                Features = new[] { FeaturePersonalNoise, FeatureDual, FeatureBassWave, FeatureGameMode, FeatureAutoPlay },
            },
        };
        static readonly ModelProfile Generic = new ModelProfile { NoiseBits = new[] { 4, 5, 0, 2 } };

        // Feature-switch ids from PollCommandManager.k(), labelled with HeyMelody's English strings.
        static readonly Dictionary<int, string> FeatureNames = new Dictionary<int, string> {
            {FeatureAutoPlay, "Auto play/pause"}, {FeatureGameMode, "Game mode"}, {9, "Vocal enhancement"}, {FeaturePersonalNoise, "Personalised noise cancellation"},
            {13, "Camera control"}, {15, "Zen mode"}, {FeatureDual, "Dual connection"}, {20, "Voice wake-up"}, {21, "Smart call"},
            {22, "Lost reminder"}, {23, "Long battery mode"}, {FeatureHiRes, "Hi-Res mode"}, {25, "Voice commands"}, {27, "Spatial Audio"},
            {28, "Auto volume"}, {FeatureBassWave, "BassWave™"}, {33, "Game EQ"}, {48, "Adaptive volume"}, {49, "Adaptive ear fit"},
            {50, "Speech perception"}, {52, "Meeting assistant"}, {53, "Long-press volume"}, {55, "Swift Pair"}, {56, "Hearing optimisation"},
            {57, "Incoming call voice control"}, {58, "Sleep detection"}, {59, "Head motion"},
        };

        // Gesture codes as the phone labels them: actions in the phone's order, then the functions it offers.
        static readonly Tuple<int, string>[] GestureActions = {
            Tuple.Create(1, "Single tap"), Tuple.Create(2, "Double-tap"), Tuple.Create(3, "Triple-tap"),
            Tuple.Create(6, "Long touch and hold"), Tuple.Create(4, "Touch and hold"),
        };
        static readonly Dictionary<int, string> GestureFunctions = new Dictionary<int, string> {
            {0, "None"}, {1, "Play/Pause"}, {5, "Previous"}, {6, "Next"}, {8, "Noise cancellation / Transparency"}, {17, "Game mode"},
        };
        static readonly int[] TapFunctions = { 1, 5, 6, 17, 0 };
        static readonly int[] HoldFunctions = { 8, 0 };
        const int TouchAndHold = 4;

        const int EqPresets = 7; // shortcut: generic models list indexes 0-6 (what a Nord Buds 3 Pro accepts); grows if a device reports more
        const string WearHint = "Wear your earbuds to use Noise control"; // melody_ui_detail_main_need_wear_earphone2

        static readonly string SettingsFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MelodyDesk", "last-device.txt");

        readonly Window window;
        readonly TextBlock deviceName, subTitle;
        readonly StackPanel heroStatus, cards, subContent, controlsContent, aboutContent;
        readonly WrapPanel heroBattery;
        readonly TextBlock navFooter;
        readonly FrameworkElement subPage;
        readonly FrameworkElement[] tabPages;
        readonly Brush accent, labelPrimary, labelSecondary, labelTertiary, divider;
        readonly BitmapSource earbudsImage, appIcon;

        readonly Window flyout;
        readonly TextBlock flyoutName, flyoutHint;
        readonly WrapPanel flyoutStatus;
        readonly Border flyoutNoise;
        DateTime flyoutHiddenAt;

        readonly Tray tray = new Tray();
        readonly EarbudsLink link = new EarbudsLink();
        readonly NoiseMemory noise;
        readonly DispatcherTimer batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        readonly DispatcherTimer reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        readonly DispatcherTimer bassTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        readonly StringBuilder log = new StringBuilder();
        TextBox logBox;
        bool exiting, trayHintShown, userDisconnected;

        List<PairedDevice> paired = new List<PairedDevice>();
        PairedDevice device;
        bool connected, connecting, finding;
        HashSet<int> caps = new HashSet<int>();
        readonly Dictionary<int, Battery> batteries = new Dictionary<int, Battery>();
        readonly SortedDictionary<int, bool> features = new SortedDictionary<int, bool>();
        int eqIndex = -1;
        int bassMin = -10, bassMax = 10, bassValue = int.MinValue;
        List<CustomEq> customEqs = new List<CustomEq>();
        List<KnownDevice> knownDevices = new List<KnownDevice>();
        string firmware;
        byte[] gestures;
        int gestureSide = 1;

        // Controls that are updated in place (rebuilding them on every packet made switches twitch).
        string cardLayout, subLayout, controlsLayout;
        Func<string> subLayoutKey;
        Action subBuild;
        readonly Dictionary<int, RadioButton> noiseButtons = new Dictionary<int, RadioButton>();
        readonly Dictionary<int, RadioButton> flyoutNoiseButtons = new Dictionary<int, RadioButton>();
        readonly Dictionary<int, CheckBox> switches = new Dictionary<int, CheckBox>();
        readonly Dictionary<int, Grid> eqMarks = new Dictionary<int, Grid>();
        TextBlock eqSummary, firmwareText, noiseHint, bassText;
        Slider bassSlider;
        Path findGlyph;
        bool updating;

        public MainWindow(bool show)
        {
            noise = new NoiseMemory(bit =>
            {
                Log("Setting noise mode " + NoiseLabel(bit));
                link.Send(Cmd.SetNoiseMode, 1, 1, (byte)(1 << bit));
            }, () => DateTime.UtcNow);

            using (var xaml = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                window = (Window)XamlReader.Load(xaml);
            deviceName = (TextBlock)window.FindName("DeviceName");
            subTitle = (TextBlock)window.FindName("SubTitle");
            heroStatus = (StackPanel)window.FindName("HeroStatus");
            heroBattery = (WrapPanel)window.FindName("HeroBattery");
            navFooter = (TextBlock)window.FindName("NavFooter");
            cards = (StackPanel)window.FindName("Cards");
            subContent = (StackPanel)window.FindName("SubContent");
            controlsContent = (StackPanel)window.FindName("ControlsContent");
            aboutContent = (StackPanel)window.FindName("AboutContent");
            subPage = (FrameworkElement)window.FindName("SubPage");
            tabPages = new[] { (FrameworkElement)window.FindName("MainPage"), (FrameworkElement)window.FindName("ControlsPage"), (FrameworkElement)window.FindName("AboutPage") };
            accent = (Brush)window.FindResource("Accent");
            labelPrimary = (Brush)window.FindResource("LabelPrimary");
            labelSecondary = (Brush)window.FindResource("LabelSecondary");
            labelTertiary = (Brush)window.FindResource("LabelTertiary");
            divider = (Brush)window.FindResource("Divider");

            using (var png = Assembly.GetExecutingAssembly().GetManifestResourceStream("earbuds.png"))
                earbudsImage = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            ((Image)window.FindName("Product")).Source = earbudsImage;
            using (var png = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png"))
                appIcon = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            window.Icon = appIcon;
            ((Image)window.FindName("AppIcon")).Source = appIcon;
            ((TextBlock)window.FindName("SubCrumb")).MouseLeftButtonUp += (s, e) => CloseSubPage();
            BuildNav((StackPanel)window.FindName("NavItems"));
            BuildAbout();

            flyoutName = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            flyoutStatus = new WrapPanel { Margin = new Thickness(-4, 6, 0, 0), MinHeight = 22 };
            flyoutNoise = new Border { Margin = new Thickness(-8, 14, -8, 0) };
            flyoutHint = Summary(null);
            flyoutHint.TextAlignment = TextAlignment.Center;
            flyoutHint.Margin = new Thickness(0, 8, 0, 0);
            flyout = BuildFlyout();

            ((Button)window.FindName("MenuButton")).Click += (s, e) => ShowMenu((Button)s);
            ((Button)window.FindName("BackButton")).Click += (s, e) => CloseSubPage();
            batteryTimer.Tick += (s, e) => link.Send(Cmd.GetBattery);
            reconnectTimer.Tick += (s, e) => AutoReconnect();
            bassTimer.Tick += (s, e) =>
            {
                bassTimer.Stop();
                link.Send(Cmd.SetBassEngine, (byte)bassMin, (byte)bassMax, (byte)bassValue);
            };
            link.PacketReceived += p => Ui(() => OnPacket(p));
            link.Traffic += (bytes, sent) => Ui(() => Log((sent ? "TX " : "RX ") + BitConverter.ToString(bytes)));
            link.Closed += reason => Ui(() => OnClosed(reason));
            window.Closing += (s, e) =>
            {
                if (exiting) return;
                e.Cancel = true; // closing the window keeps MelodyDesk in the notification area
                window.Hide();
                if (!trayHintShown) tray.Balloon("MelodyDesk is still running", "Click the earbuds icon in the notification area to open it, or right-click it to exit.");
                trayHintShown = true;
            };
            tray.Clicked += ToggleFlyout;
            tray.MenuOpening += BuildTrayMenu;

            LoadDevices();
            Render();
            if (show) window.Show();
            reconnectTimer.Start();
            window.Dispatcher.BeginInvoke(new Action(() => { if (device != null && File.Exists(SettingsFile)) Connect(true); }));
        }

        public void Ui(Action action)
        {
            if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(action);
        }

        public void ShowMain()
        {
            flyout.Hide();
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        void Exit()
        {
            exiting = true;
            link.Close("Exiting");
            tray.Dispose();
            Application.Current.Shutdown();
        }

        ModelProfile Profile
        {
            get
            {
                if (device == null) return Generic;
                return Profiles.FirstOrDefault(p => device.Name.IndexOf(p.NameContains, StringComparison.OrdinalIgnoreCase) >= 0) ?? Generic;
            }
        }

        bool Visible(int feature)
        {
            var shown = Profile.Features;
            return features.ContainsKey(feature) && (shown == null || shown.Contains(feature));
        }

        // ---------- devices and connection ----------

        void LoadDevices()
        {
            string last = File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile).Trim() : "";
            paired = PairedDevice.List();
            device = paired.FirstOrDefault(d => d.AddressText == last)
                ?? paired.FirstOrDefault(d => d.Connected && IsMelodyBrand(d.Name))
                ?? paired.FirstOrDefault(d => IsMelodyBrand(d.Name))
                ?? (device != null ? paired.FirstOrDefault(d => d.Address == device.Address) : null);
        }

        static bool IsMelodyBrand(string name)
        {
            return new[] { "OnePlus", "OPPO", "Enco", "realme" }.Any(b => name.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // quiet: automatic attempt (start-up, reconnect timer), so failures only go to the log.
        void Connect(bool quiet)
        {
            if (device == null || connected || connecting) return;
            if (!quiet) userDisconnected = false;
            var target = device;
            connecting = true;
            Render();
            new Thread(() =>
            {
                string error = null;
                try { link.Connect(target.Address); }
                catch (Exception e) { error = e.Message; }
                Ui(() => OnConnectResult(target, error, quiet));
            }) { IsBackground = true }.Start();
        }

        void OnConnectResult(PairedDevice target, string error, bool quiet)
        {
            connecting = false;
            if (error != null)
            {
                Log("Connect failed: " + error);
                Render();
                if (!quiet) MessageBox.Show(window, "Could not connect to " + target.Name + ".\n" + error, "MelodyDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile));
            File.WriteAllText(SettingsFile, target.AddressText);
            connected = true;
            batteryTimer.Start();
            link.Send(Cmd.GetCapability);
            Render();
        }

        void OnClosed(string reason)
        {
            connected = connecting = finding = false;
            batteryTimer.Stop();
            noise.Disconnected(); // the chosen mode is kept so it can be restored after the reconnect
            Log("Closed: " + reason);
            Render();
        }

        // The buds drop the link when they go into a closed case; pick it up again once Windows sees them connected.
        void AutoReconnect()
        {
            if (connected || connecting || device == null || userDisconnected) return;
            var now = PairedDevice.List().FirstOrDefault(d => d.Address == device.Address);
            if (now != null && now.Connected) Connect(true);
        }

        void Disconnect()
        {
            userDisconnected = true;
            link.Close("Disconnected");
        }

        void SwitchTo(PairedDevice target)
        {
            if (device != null && device.Address == target.Address) { Connect(false); return; }
            link.Close("Switching device");
            device = target;
            caps = new HashSet<int>();
            batteries.Clear();
            features.Clear();
            noise.Forget();
            eqIndex = -1;
            bassValue = int.MinValue;
            customEqs = new List<CustomEq>();
            knownDevices = new List<KnownDevice>();
            firmware = null;
            gestures = null;
            CloseSubPage();
            Connect(false);
        }

        void ShowMenu(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            var devices = new MenuItem { Header = "Earbuds" };
            foreach (var d in paired)
            {
                var target = d;
                var item = new MenuItem { Header = d.Name, IsCheckable = true, IsChecked = device != null && d.Address == device.Address };
                item.Click += (s, e) => SwitchTo(target);
                devices.Items.Add(item);
            }
            if (paired.Count == 0) devices.Items.Add(new MenuItem { Header = "No paired Bluetooth devices", IsEnabled = false });
            menu.Items.Add(devices);
            var refresh = new MenuItem { Header = "Refresh device list" };
            refresh.Click += (s, e) => { LoadDevices(); Render(); };
            menu.Items.Add(refresh);
            menu.Items.Add(new Separator());
            var connection = new MenuItem { Header = connected ? "Disconnect" : "Connect", IsEnabled = device != null && !connecting };
            connection.Click += (s, e) => { if (connected) Disconnect(); else Connect(false); };
            menu.Items.Add(connection);
            var traffic = new MenuItem { Header = "Traffic log" };
            traffic.Click += (s, e) => ShowLog();
            menu.Items.Add(traffic);
            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "Exit MelodyDesk" };
            exit.Click += (s, e) => Exit();
            menu.Items.Add(exit);
            menu.IsOpen = true;
        }

        // ---------- protocol ----------

        // Same start-up order as HeyMelody: capabilities, state queries, then event registration. Ear status goes before
        // the noise mode so the first noise report already knows whether the buds are worn.
        void QueryAll()
        {
            link.Send(Cmd.GetVersion);
            link.Send(Cmd.GetBattery);
            link.Send(Cmd.GetEarStatus);
            if (caps.Contains(Cmd.GetNoiseMode)) link.Send(Cmd.GetNoiseMode, 1, 1);
            if (caps.Contains(Cmd.GetEqMode)) link.Send(Cmd.GetEqMode);
            if (caps.Contains(Cmd.GetCustomEqs)) link.Send(Cmd.GetCustomEqs);
            if (caps.Contains(Cmd.GetBassEngine)) link.Send(Cmd.GetBassEngine);
            if (caps.Contains(Cmd.GetKeyFunctions)) link.Send(Cmd.GetKeyFunctions);
            if (caps.Contains(Cmd.GetMultiConnect)) link.Send(Cmd.GetMultiConnect);
            QueryFeatures(FeatureNames.Keys);
            link.Send(Cmd.GetNotifyCapability);
        }

        // Nord Buds 3 Pro close the link ~2 s after a query listing more than ~20 ids, so ask in batches of 10.
        void QueryFeatures(IEnumerable<int> ids)
        {
            var all = ids.ToList();
            for (int i = 0; i < all.Count; i += 10)
            {
                var batch = all.Skip(i).Take(10).Select(id => (byte)id).ToList();
                batch.Insert(0, (byte)batch.Count);
                link.Send(Cmd.GetFeatures, batch.ToArray());
            }
        }

        void OnPacket(Packet p)
        {
            byte[] d = p.Data;
            bool ok = p.Status == 0;
            switch (p.Cmd)
            {
                case Cmd.GetCapability | Cmd.Response:
                    if (ok && d.Length > 1) caps = Cmd.ParseCapabilities(d);
                    QueryAll();
                    break;
                case Cmd.GetNotifyCapability | Cmd.Response:
                    if (!ok || d.Length < 2) return;
                    var events = d.Skip(2).Take(d[1]).ToArray();
                    if (caps.Contains(Cmd.RegisterNotifyMulti))
                        link.Send(Cmd.RegisterNotifyMulti, new[] { (byte)events.Length }.Concat(events).ToArray());
                    else
                        foreach (byte ev in events) link.Send(Cmd.RegisterNotify, ev);
                    return;
                case Cmd.Event:
                    if (d.Length > 0) OnEvent(d);
                    break;
                case Cmd.TimeRequest:
                    link.Reply(p, new byte[] { 0 }.Concat(BitConverter.GetBytes((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())).ToArray());
                    return;
                case Cmd.FeatureSync:
                    foreach (var f in Parse.Features(d, 0)) features[f.Id] = f.On;
                    break;
                case Cmd.EqSync:
                    if (d.Length > 0) eqIndex = d[0];
                    break;
                case Cmd.GetBattery | Cmd.Response:
                    if (ok) SetBatteries(Parse.Batteries(d, 1));
                    break;
                case Cmd.GetEarStatus | Cmd.Response:
                    if (ok) noise.OnEars(Parse.EarStatuses(d, 1));
                    break;
                case Cmd.GetNoiseMode | Cmd.Response:
                    if (ok && d.Length >= 4 && d[1] == 1 && d[2] == 1) noise.OnReport(LowestBit(d[3]));
                    break;
                case Cmd.GetEqMode | Cmd.Response:
                    if (ok && d.Length >= 2) eqIndex = d[1];
                    break;
                case Cmd.GetCustomEqs | Cmd.Response:
                    if (ok) customEqs = Parse.CustomEqs(d, 1);
                    break;
                case Cmd.GetBassEngine | Cmd.Response:
                    if (ok && d.Length >= 4) { bassMin = (sbyte)d[1]; bassMax = (sbyte)d[2]; bassValue = (sbyte)d[3]; }
                    break;
                case Cmd.GetMultiConnect | Cmd.Response:
                    if (ok) knownDevices = Parse.KnownDevices(d, 1);
                    break;
                case Cmd.GetFeatures | Cmd.Response:
                    if (!ok) return;
                    foreach (var f in Parse.Features(d, 1)) features[f.Id] = f.On;
                    break;
                case Cmd.GetVersion | Cmd.Response:
                    if (ok) firmware = Parse.FirmwareSummary(d);
                    break;
                case Cmd.GetKeyFunctions | Cmd.Response:
                    if (ok) gestures = d;
                    break;
                case Cmd.SetNoiseMode | Cmd.Response:
                    link.Send(Cmd.GetNoiseMode, 1, 1); // the device may substitute an alias bit
                    return;
                case Cmd.SetEqMode | Cmd.Response:
                case Cmd.SetCustomEq | Cmd.Response:
                    link.Send(Cmd.GetEqMode);
                    link.Send(Cmd.GetCustomEqs);
                    return;
                case Cmd.SetBassEngine | Cmd.Response:
                    link.Send(Cmd.GetBassEngine);
                    return;
                case Cmd.SetKeyFunction | Cmd.Response:
                    if (!ok) Log("Earbuds rejected the gesture (status " + p.Status + ")");
                    link.Send(Cmd.GetKeyFunctions);
                    return;
                case Cmd.SetFeature | Cmd.Response:
                    if (!ok) Log("Earbuds rejected the setting (status " + p.Status + ")");
                    QueryFeatures(features.Keys);
                    return;
                case Cmd.FindEarbuds | Cmd.Response:
                    if (!ok) { finding = false; Log("Find earbuds failed (status " + p.Status + ")"); }
                    break;
                default:
                    return;
            }
            Render();
        }

        // 0x0204 payload: [event type, event data...] (NotificationCommandManager.b).
        void OnEvent(byte[] d)
        {
            switch (d[0])
            {
                case 1:
                    SetBatteries(Parse.Batteries(d, 1));
                    break;
                case 2:
                    noise.OnEars(Parse.EarStatuses(d, 1));
                    break;
                case 3:
                    if (d.Length >= 4 && d[1] == 1 && d[2] == 1) noise.OnReport(LowestBit(d[3]));
                    break;
                case 5:
                    if (d.Length >= 2 && features.ContainsKey(FeatureGameMode)) features[FeatureGameMode] = d[1] != 0;
                    break;
                case 6:
                    knownDevices = Parse.KnownDevices(d, 1);
                    break;
            }
        }

        void SetBatteries(List<Battery> list)
        {
            foreach (var b in list) batteries[b.Device] = b;
        }

        static int LowestBit(int mask)
        {
            for (int i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) return i;
            return -1;
        }

        // ---------- noise mode ----------

        void PickNoise(int bit)
        {
            // Refused with no bud worn (as in HeyMelody); deferred so a radio button isn't re-checked inside its own Checked event.
            if (!noise.Pick(bit)) Ui(UpdateCards);
            else UpdateCards();
        }

        static string NoiseLabel(int bit)
        {
            Tuple<string, string> mode;
            return NoiseModes.TryGetValue(bit, out mode) ? mode.Item1 : "Mode " + bit;
        }

        string NoiseHintText()
        {
            if (!connected) return null;
            if (noise.NoneWorn) return WearHint;
            if (!noise.BothWorn && noise.Chosen >= 0 && noise.Reported >= 0 && noise.Reported != noise.Chosen)
                return "With one earbud out, the earbuds switched to " + NoiseLabel(noise.Reported) + ". " + NoiseLabel(noise.Chosen) + " comes back when you wear both.";
            return null;
        }

        // A bit the buds report that this model's buttons don't cover gets its own button.
        int ExtraNoiseBit()
        {
            int shown = noise.Shown;
            return shown >= 0 && !Profile.NoiseBits.Contains(shown) ? shown : -1;
        }

        string EqName(int index)
        {
            var profile = Profile;
            if (profile.EqPresets != null)
            {
                var preset = profile.EqPresets.FirstOrDefault(p => p.Item1 == index);
                if (preset != null) return preset.Item2 + (index == profile.DefaultEq ? " (Default)" : "");
            }
            var custom = customEqs.FirstOrDefault(c => c.Id == index);
            if (custom != null) return custom.Name;
            return index >= 0 ? "Preset " + index : null;
        }

        // ---------- rendering ----------

        void Render()
        {
            string name = device != null ? device.Name : "Earbuds";
            deviceName.Text = name;
            flyoutName.Text = name;
            window.Title = name + " — MelodyDesk";
            RenderHero();
            RenderStatus(flyoutStatus);
            cards.IsEnabled = flyoutNoise.IsEnabled = connected;
            cards.Opacity = flyoutNoise.Opacity = connected ? 1 : 0.45;
            tray.SetText(name + "\n" + (connected ? BatteryText() : connecting ? "Connecting…" : "Not connected"));

            string layout = CardLayout();
            if (layout != cardLayout)
            {
                cardLayout = layout;
                BuildCards();
                subLayout = null; // BuildCards dropped the open sub page's switches from the registry
            }
            if (subBuild != null)
            {
                string key = subLayoutKey();
                if (key != subLayout) { subLayout = key; subBuild(); }
                subContent.IsEnabled = connected;
            }
            string gestureKey = connected + ":" + gestureSide + ":" + (gestures != null ? BitConverter.ToString(gestures) : "");
            if (gestureKey != controlsLayout) { controlsLayout = gestureKey; BuildControls(); }
            UpdateCards();
        }

        string BatteryText()
        {
            var parts = new List<string>();
            foreach (var pair in new[] { Tuple.Create(1, "L"), Tuple.Create(2, "R"), Tuple.Create(3, "Case") })
            {
                Battery b;
                if (batteries.TryGetValue(pair.Item1, out b) && (b.Level > 0 || b.Charging))
                    parts.Add(pair.Item2 + " " + b.Level + "%" + (b.Charging ? "+" : ""));
            }
            return parts.Count > 0 ? string.Join("  ", parts) : "Connected";
        }

        string CardLayout()
        {
            if (caps.Count == 0) return "";
            var visible = features.Keys.Where(Visible);
            return string.Join(",", Profile.NoiseBits.Length, caps.Contains(Cmd.SetNoiseMode), caps.Contains(Cmd.SetEqMode), caps.Contains(Cmd.FindEarbuds),
                firmware != null, ExtraNoiseBit(), string.Join(" ", visible));
        }

        void UpdateCards()
        {
            updating = true;
            int shown = noise.Shown;
            foreach (var kv in noiseButtons) kv.Value.IsChecked = kv.Key == shown;
            foreach (var kv in flyoutNoiseButtons) kv.Value.IsChecked = kv.Key == shown;
            string hint = NoiseHintText();
            SetSummary(noiseHint, hint);
            SetSummary(flyoutHint, hint);
            foreach (var kv in switches)
            {
                bool on;
                features.TryGetValue(kv.Key, out on);
                kv.Value.IsChecked = on;
                AutomationProperties.SetItemStatus((UIElement)kv.Value.Tag, on ? "On" : "Off");
            }
            SetSummary(eqSummary, EqName(eqIndex));
            if (firmwareText != null) firmwareText.Text = firmware ?? "";
            foreach (var kv in eqMarks) SetMark(kv.Value, kv.Key == eqIndex);
            if (bassSlider != null && bassValue != int.MinValue)
            {
                bassSlider.Minimum = bassMin;
                bassSlider.Maximum = bassMax;
                if (!bassTimer.IsEnabled) bassSlider.Value = bassValue;
                bassSlider.IsEnabled = features.ContainsKey(FeatureBassWave) && features[FeatureBassWave];
                bassText.Text = ((int)bassSlider.Value).ToString();
            }
            if (findGlyph != null) findGlyph.Data = Geometry.Parse(finding ? "M0,0 H10 V10 H0 Z" : "M0,0 L10,6 L0,12 Z");
            updating = false;
        }

        // Main page sections in the phone app's order.
        void BuildCards()
        {
            cards.Children.Clear();
            noiseButtons.Clear();
            flyoutNoiseButtons.Clear();
            switches.Clear();
            eqSummary = firmwareText = noiseHint = null;
            flyoutNoise.Child = null;
            if (caps.Count == 0)
            {
                cards.Children.Add(new TextBlock { Style = (Style)window.FindResource("RowSummary"), Margin = new Thickness(4, 20, 0, 0),
                    Text = "Take the earbuds out of the case so Windows connects to them. MelodyDesk then connects on its own, or click Connect." });
                return;
            }

            if (caps.Contains(Cmd.SetNoiseMode))
            {
                AddSection(cards, "Noise control", NoiseCard());
                flyoutNoise.Child = NoiseGrid("flyoutNoise", flyoutNoiseButtons);
            }
            if (Visible(FeatureDual))
                AddSection(cards, "Device management", Card(new[] { Row("Dual connection", Summary("Connect 2 devices at the same time and switch between them easily."), Chevron(), OpenDualConnection) }));
            if (caps.Contains(Cmd.SetEqMode))
            {
                eqSummary = Summary(null);
                eqSummary.Foreground = accent;
                AddSection(cards, "Audio effects", Card(new[] { Row("Sound Master EQ", eqSummary, Chevron(), OpenEqualizer) }));
            }
            if (Visible(FeatureGameMode))
                AddSection(cards, "Gaming features", Card(new[] { SwitchRow(FeatureGameMode, "Game mode", null) }));

            var other = new List<UIElement>();
            if (caps.Contains(Cmd.FindEarbuds)) other.Add(Row("Find my earbuds", null, Chevron(), OpenFind));
            foreach (int id in features.Keys.Where(f => Visible(f) && !PlacedFeatures.Contains(f)))
            {
                string label;
                other.Add(SwitchRow(id, FeatureNames.TryGetValue(id, out label) ? label : "Feature " + id, null));
            }
            if (other.Count > 0) AddSection(cards, "Other features", Card(other));
            if (Visible(FeatureAutoPlay))
                AddSection(cards, "Earbud settings", Card(new[] { Row("More settings", null, Chevron(), OpenMoreSettings) }));
            if (firmware != null)
            {
                firmwareText = new TextBlock { Foreground = labelSecondary, FontSize = 14 };
                AddSection(cards, "About Earbuds", Card(new[] { Row("Firmware update", null, firmwareText, null) }));
            }
        }

        void AddSection(StackPanel panel, string title, UIElement card)
        {
            panel.Children.Add(new TextBlock { Text = title, Style = (Style)window.FindResource("Category") });
            panel.Children.Add(card);
        }

        TextBlock Summary(string text)
        {
            var summary = new TextBlock { Style = (Style)window.FindResource("RowSummary"), Visibility = Visibility.Collapsed };
            SetSummary(summary, text);
            return summary;
        }

        static void SetSummary(TextBlock summary, string text)
        {
            if (summary == null) return;
            summary.Text = text ?? "";
            summary.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Header card next to the product image: connection status (with Connect when offline), then a battery per part.
        void RenderHero()
        {
            heroStatus.Children.Clear();
            heroBattery.Children.Clear();
            var dot = connected ? new SolidColorBrush(Color.FromRgb(0x00, 0xBD, 0x13)) : (Brush)labelTertiary;
            heroStatus.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, Margin = new Thickness(1, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            heroStatus.Children.Add(new TextBlock { Text = connected ? "Connected" : connecting ? "Connecting…" : device == null ? "No earbuds paired" : "Not connected", Foreground = labelSecondary, VerticalAlignment = VerticalAlignment.Center });
            if (!connected && !connecting && device != null)
            {
                var connect = new Button { Content = "Connect", Style = (Style)window.FindResource("PillButton"), Margin = new Thickness(14, 0, 0, 0) };
                connect.Click += (s, e) => Connect(false);
                heroStatus.Children.Add(connect);
            }
            navFooter.Text = connected ? (device != null ? device.Name + "\n" : "") + BatteryText() : "";
            if (!connected) return;
            foreach (int id in new[] { 1, 2, 3 })
            {
                Battery b;
                if (!batteries.TryGetValue(id, out b) || (b.Level == 0 && !b.Charging)) continue;
                var item = BatteryItem(id, b);
                ((FrameworkElement)item).Margin = new Thickness(0, 0, 28, 0);
                heroBattery.Children.Add(item);
            }
        }

        UIElement BatteryItem(int id, Battery b)
        {
            var body = new Grid { Width = 40, Height = 20 };
            var fill = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(0, 36 * Math.Min(100, b.Level) / 100.0), Background = b.Charging ? new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0xBD, 0x13)) : (Brush)new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)) };
            body.Children.Add(fill);
            body.Children.Add(new Border { CornerRadius = new CornerRadius(4), BorderBrush = labelSecondary, BorderThickness = new Thickness(1.2) });
            body.Children.Add(new TextBlock { Text = b.Level.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var battery = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            battery.Children.Add(body);
            battery.Children.Add(new Border { Width = 2, Height = 8, Background = labelSecondary, CornerRadius = new CornerRadius(0, 1, 1, 0), VerticalAlignment = VerticalAlignment.Center });
            if (b.Charging) battery.Children.Add(Glyph(Icons.Charging, 7, 12, new SolidColorBrush(Color.FromRgb(0x00, 0xBD, 0x13)), new Thickness(4, 0, 0, 0)));

            var badge = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)) });
            if (id == 3) badge.Children.Add(Glyph(Icons.Case, 10, 9, Brushes.White, new Thickness(0)));
            else badge.Children.Add(new TextBlock { Text = id == 1 ? "L" : "R", Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(battery);
            stack.Children.Add(badge);
            AutomationProperties.SetName(stack, Parse.DeviceName(id) + " battery " + b.Level + " percent" + (b.Charging ? ", charging" : ""));
            return stack;
        }

        // Compact battery strip for the tray flyout: case, left, right with the APK's glyphs.
        void RenderStatus(WrapPanel panel)
        {
            panel.Children.Clear();
            if (!connected)
            {
                panel.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = labelTertiary, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(new TextBlock { Text = connecting ? "Connecting…" : device == null ? "No earbuds paired" : "Not connected", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                if (device != null && !connecting)
                {
                    var connect = new Button { Content = "Connect", Style = (Style)window.FindResource("PillButton"), Margin = new Thickness(10, 0, 0, 0) };
                    connect.Click += (s, e) => Connect(false);
                    panel.Children.Add(connect);
                }
                return;
            }
            foreach (int id in new[] { 3, 1, 2 })
            {
                Battery b;
                if (!batteries.TryGetValue(id, out b) || (b.Level == 0 && !b.Charging)) continue;
                var icon = id == 3 ? Icons.Case : id == 1 ? Icons.Left : Icons.Right;
                panel.Children.Add(Glyph(icon, id == 3 ? 14 : 12, 12, labelPrimary, new Thickness(4, 0, 3, 0)));
                panel.Children.Add(new TextBlock { Text = b.Level + "%", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                if (b.Charging) panel.Children.Add(Glyph(Icons.Charging, 7, 12, new SolidColorBrush(Color.FromRgb(0x00, 0xBD, 0x13)), new Thickness(3, 0, 0, 0)));
                panel.Children.Add(new Border { Width = 10 });
            }
        }

        UIElement NoiseCard()
        {
            var stack = new StackPanel { Margin = new Thickness(0, 16, 0, 4) };
            var grid = NoiseGrid("noise", noiseButtons);
            grid.Margin = new Thickness(12, 0, 12, 12);
            grid.HorizontalAlignment = HorizontalAlignment.Left; // fixed-width buttons on a wide window instead of spreading out
            foreach (FrameworkElement button in grid.Children) button.Width = 120;
            stack.Children.Add(grid);
            noiseHint = Summary(null);
            noiseHint.Margin = new Thickness(18, 0, 18, 12);
            stack.Children.Add(noiseHint);
            if (Visible(FeaturePersonalNoise))
            {
                stack.Children.Add(new Border { Height = 1, Background = divider, Margin = new Thickness(16, 0, 16, 0) });
                stack.Children.Add(SwitchRow(FeaturePersonalNoise, "Personalised noise cancellation",
                    "Noise cancellation effects are personalised to your auditory canal structure and earbud fit to give you clearer sound."));
            }
            return new Border { Style = (Style)window.FindResource("Card"), Child = stack, Padding = new Thickness(0) };
        }

        UniformGrid NoiseGrid(string group, Dictionary<int, RadioButton> into)
        {
            var grid = new UniformGrid { Rows = 1 };
            var bits = Profile.NoiseBits.ToList();
            int extra = ExtraNoiseBit();
            if (extra >= 0) bits.Add(extra);
            foreach (int bit in bits)
            {
                int b = bit;
                Tuple<string, string> mode;
                if (!NoiseModes.TryGetValue(b, out mode)) mode = Tuple.Create("Mode " + b, Icons.NoiseCancel);
                var button = new RadioButton { Content = mode.Item1, Tag = Geometry.Parse(mode.Item2), GroupName = group, Style = (Style)window.FindResource("ModeButton") };
                button.Checked += (s, e) =>
                {
                    if (updating || noise.Shown == b) return; // UpdateCards reflecting the current state
                    PickNoise(b);
                };
                into[b] = button;
                grid.Children.Add(button);
            }
            return grid;
        }

        Border Card(IList<UIElement> rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = divider, Margin = new Thickness(16, 0, 16, 0) });
                stack.Children.Add(rows[i]);
            }
            return new Border { Style = (Style)window.FindResource("Card"), Child = stack };
        }

        // A card row: title with optional content under it (summary, value), optional trailing and leading elements.
        UIElement Row(string title, UIElement summary, UIElement trailing, Action click, UIElement leading = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (leading != null)
            {
                var lead = new Border { Child = leading, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                grid.Children.Add(lead);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = (Style)window.FindResource("RowTitle") });
            if (summary != null) text.Children.Add(summary);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (trailing != null)
            {
                var host = new Border { Child = trailing, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(host, 2);
                grid.Children.Add(host);
            }
            if (click == null) return new Border { Child = grid, Padding = new Thickness(16, 8, 16, 8), MinHeight = 56 };
            var row = new Button { Content = grid, Style = (Style)window.FindResource("Row") };
            AutomationProperties.SetName(row, title);
            row.Click += (s, e) => click();
            return row;
        }

        // The switch's Tag holds its row so UpdateCards can publish the on/off state to screen readers.
        UIElement SwitchRow(int id, string title, string summary)
        {
            bool on;
            features.TryGetValue(id, out on);
            var toggle = new CheckBox { Style = (Style)window.FindResource("Switch"), IsChecked = on };
            var row = Row(title, Summary(summary), toggle, () =>
            {
                bool next = !features.ContainsKey(id) || !features[id];
                if (id == FeatureHiRes && MessageBox.Show(window, next
                        ? "The transmission of high-resolution audio may reduce your listening time.\n\nTurning on this mode will disconnect your earbuds and reconnect them."
                        : "Turning off this mode will disconnect your earbuds and reconnect them.",
                        next ? "Turn on Hi-Res mode?" : "Turn off Hi-Res mode?", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                features[id] = next;
                UpdateCards();
                link.Send(Cmd.SetFeature, (byte)id, (byte)(next ? 1 : 0));
            });
            toggle.Tag = row;
            switches[id] = toggle;
            return row;
        }

        UIElement Chevron()
        {
            return new Path { Data = Geometry.Parse("M0,0 L5,5 L0,10"), Stroke = labelTertiary, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 0, 4, 0) };
        }

        static Path Glyph(string data, double width, double height, Brush fill, Thickness margin)
        {
            return new Path { Data = Geometry.Parse(data), Width = width, Height = height, Stretch = Stretch.Uniform, Fill = fill, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        }

        // Radio indicator on the EQ page: grey ring, or an accent disc with a white centre when selected.
        Grid Mark()
        {
            var mark = new Grid { Width = 22, Height = 22 };
            mark.Children.Add(new Ellipse { StrokeThickness = 1.5 });
            mark.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Brushes.White });
            SetMark(mark, false);
            return mark;
        }

        void SetMark(Grid mark, bool selected)
        {
            var ring = (Ellipse)mark.Children[0];
            ring.Stroke = selected ? accent : labelTertiary;
            ring.Fill = selected ? accent : Brushes.Transparent;
            mark.Children[1].Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- navigation ----------

        // Left navigation pane: Device, Earbud controls, About. Picking one also leaves any open sub page.
        void BuildNav(StackPanel nav)
        {
            var items = new[] { Tuple.Create("Device", (string)null), Tuple.Create("Earbud controls", ""), Tuple.Create("About", "") };
            for (int i = 0; i < items.Length; i++)
            {
                int index = i;
                FrameworkElement icon;
                if (items[i].Item2 == null)
                    icon = new Path { Data = Geometry.Parse(Tray.Glyph), Width = 16, Height = 16, Stretch = Stretch.Uniform, Fill = labelPrimary };
                else
                    icon = new TextBlock { Text = items[i].Item2, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 };
                icon.VerticalAlignment = VerticalAlignment.Center;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(icon);
                content.Children.Add(new TextBlock { Text = items[i].Item1, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                var item = new RadioButton { Content = content, GroupName = "nav", Style = (Style)window.FindResource("NavItem"), IsChecked = i == 0 };
                AutomationProperties.SetName(item, items[i].Item1);
                item.Checked += (s, e) =>
                {
                    CloseSubPage();
                    for (int p = 0; p < tabPages.Length; p++) tabPages[p].Visibility = p == index ? Visibility.Visible : Visibility.Collapsed;
                };
                nav.Children.Add(item);
            }
        }

        // Earbud controls tab: Left/Right picker, then the gestures for that side under the phone's labels.
        void BuildControls()
        {
            controlsContent.Children.Clear();
            controlsContent.Children.Add(new TextBlock { Text = "Earbud controls", Style = (Style)window.FindResource("PageTitle") });

            // Earbud picture beside the Left/Right picker, one card wide.
            var picker = new Grid { Margin = new Thickness(20, 14, 20, 14) };
            picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            picker.ColumnDefinitions.Add(new ColumnDefinition());
            picker.Children.Add(new Image { Source = earbudsImage, Width = 110, Height = 110 });
            var choose = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            choose.Children.Add(new TextBlock { Text = "Choose an earbud", Style = (Style)window.FindResource("RowTitle") });
            choose.Children.Add(new TextBlock { Text = "Pick a gesture below to change what it does.", Style = (Style)window.FindResource("RowSummary") });
            var sides = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 12, 0, 0) };
            foreach (int side in new[] { 1, 2 })
            {
                int s2 = side;
                var pill = new RadioButton { Content = Parse.DeviceName(side), GroupName = "side", IsChecked = side == gestureSide, Style = (Style)window.FindResource("SidePill") };
                pill.Checked += (s, e) => { gestureSide = s2; Render(); };
                sides.Children.Add(pill);
            }
            choose.Children.Add(sides);
            Grid.SetColumn(choose, 1);
            picker.Children.Add(choose);
            controlsContent.Children.Add(new Border { Style = (Style)window.FindResource("Card"), Child = picker, Margin = new Thickness(0, 16, 0, 0) });

            if (gestures == null)
            {
                controlsContent.Children.Add(new TextBlock { Text = connected ? "Reading earbud controls…" : "Connect your earbuds to see their controls.", Style = (Style)window.FindResource("RowSummary"), Margin = new Thickness(2, 16, 0, 0) });
                return;
            }

            string sideName = gestureSide == 1 ? "left" : "right";
            var current = new Dictionary<int, Tuple<int, int>>(); // action -> (button, function)
            for (int i = 0; i < (gestures.Length > 1 ? gestures[1] : 0); i++)
            {
                int at = 2 + i * 4;
                if (at + 3 >= gestures.Length) break;
                if (gestures[at] == gestureSide) current[gestures[at + 2]] = Tuple.Create((int)gestures[at + 1], (int)gestures[at + 3]);
            }
            var rows = new List<UIElement>();
            foreach (var action in GestureActions)
            {
                Tuple<int, int> entry;
                if (!current.TryGetValue(action.Item1, out entry)) continue;
                bool hold = action.Item1 == TouchAndHold;
                string title = hold ? action.Item2 : action.Item2 + " " + sideName + " earbud";
                var value = Summary(FunctionLabel(entry.Item2));
                value.Foreground = accent;
                var below = new StackPanel();
                if (hold) below.Children.Add(new TextBlock { Text = "Touch and hold the stem of an earbud for 1 s to switch between noise control modes.", Style = (Style)window.FindResource("RowSummary") });
                below.Children.Add(value);
                int act = action.Item1, button = entry.Item1, function = entry.Item2, side = gestureSide;
                var upDown = new Path { Data = Geometry.Parse("M0,4 L4,0 L8,4 M0,9 L4,13 L8,9"), Stroke = labelTertiary, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
                UIElement row = null;
                row = Row(title, below, upDown, () => PickGesture((FrameworkElement)row, side, button, act, function, hold ? HoldFunctions : TapFunctions));
                AutomationProperties.SetName(row, title + ": " + FunctionLabel(function));
                rows.Add(row);
            }
            controlsContent.Children.Add(new TextBlock { Text = "When not on a call", Style = (Style)window.FindResource("Category") });
            controlsContent.Children.Add(Card(rows));

            var calls = Card(new[] {
                Row("Double-tap " + sideName + " earbud", Summary("Answer/End calls"), null, null),
                Row("Long touch and hold " + sideName + " earbud", Summary("Decline/End call"), null, null),
            });
            controlsContent.Children.Add(new TextBlock { Text = "When on a call", Style = (Style)window.FindResource("Category") });
            controlsContent.Children.Add(calls);
            controlsContent.IsEnabled = connected;
        }

        static string FunctionLabel(int function)
        {
            string label;
            return GestureFunctions.TryGetValue(function, out label) ? label : "Function " + function;
        }

        void PickGesture(FrameworkElement anchor, int side, int button, int action, int current, int[] options)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, HorizontalOffset = 16 };
            foreach (int option in options)
            {
                int f = option;
                var item = new MenuItem { Header = FunctionLabel(f), IsCheckable = true, IsChecked = f == current };
                item.Click += (s, e) =>
                {
                    if (f == current) return;
                    Log("Setting gesture " + side + "/" + action + " to " + FunctionLabel(f));
                    link.Send(Cmd.SetKeyFunction, 1, (byte)side, (byte)button, (byte)action, (byte)f);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        void BuildAbout()
        {
            aboutContent.Children.Clear();
            aboutContent.Children.Add(new TextBlock { Text = "About", Style = (Style)window.FindResource("PageTitle") });
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 18, 20, 18) };
            header.Children.Add(new Image { Source = appIcon, Width = 56, Height = 56 });
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            name.Children.Add(new TextBlock { Text = "MelodyDesk", FontSize = 20, FontWeight = FontWeights.SemiBold });
            name.Children.Add(new TextBlock { Text = "Earbuds control for Windows", Foreground = labelSecondary });
            header.Children.Add(name);
            aboutContent.Children.Add(new Border { Style = (Style)window.FindResource("Card"), Child = header, Margin = new Thickness(0, 16, 0, 0) });
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            AddSection(aboutContent, "About MelodyDesk", Card(new[] {
                Row("App version", null, new TextBlock { Text = version.Major + "." + version.Minor, Foreground = labelSecondary }, null),
                Row("Protocol", Summary("Reverse-engineered from HeyMelody 116.9.0 for Android"), null, null),
                Row("Supported devices", Summary("OPPO, OnePlus and realme earbuds that use the HeyMelody app"), null, null),
            }));
        }

        // ---------- sub pages ----------

        void OpenSubPage(string title, Func<string> layoutKey, Action build)
        {
            subTitle.Text = title;
            subLayoutKey = layoutKey;
            subBuild = build;
            subLayout = layoutKey();
            build();
            subContent.IsEnabled = connected;
            UpdateCards();
            subPage.Visibility = Visibility.Visible;
        }

        void CloseSubPage()
        {
            subBuild = null;
            subLayoutKey = null;
            eqMarks.Clear();
            bassSlider = null;
            bassText = null;
            findGlyph = null;
            subPage.Visibility = Visibility.Collapsed;
        }

        // Sound Master EQ: the model's presets, BassWave switch and level, then the custom EQs stored on the buds.
        void OpenEqualizer()
        {
            OpenSubPage("Sound Master EQ",
                () => string.Join(",", Profile.EqPresets != null, Visible(FeatureBassWave), bassValue != int.MinValue, eqIndex >= EqPresets,
                    string.Join(" ", customEqs.Select(c => c.Id + "=" + c.Name))),
                () =>
                {
                    subContent.Children.Clear();
                    eqMarks.Clear();
                    var profile = Profile;
                    var presets = profile.EqPresets ?? Enumerable.Range(0, Math.Max(EqPresets, eqIndex < 0 || customEqs.Any(c => c.Id == eqIndex) ? 0 : eqIndex + 1)).Select(i => Tuple.Create(i, "Preset " + i)).ToArray();
                    var rows = new List<UIElement>();
                    foreach (var preset in presets)
                    {
                        int index = preset.Item1;
                        var mark = Mark();
                        eqMarks[index] = mark;
                        rows.Add(Row(preset.Item2 + (index == profile.DefaultEq ? " (Default)" : ""), null, mark, () =>
                        {
                            eqIndex = index;
                            link.Send(Cmd.SetEqMode, (byte)index);
                            UpdateCards();
                        }));
                    }
                    AddSection(subContent, "Recommended", Card(rows));

                    if (Visible(FeatureBassWave) && caps.Contains(Cmd.SetBassEngine) && bassValue != int.MinValue)
                    {
                        var bass = new StackPanel();
                        bass.Children.Add(SwitchRow(FeatureBassWave, "BassWave™", "Dynamically boost bass to deliver deep bass with clarity and fidelity."));
                        bass.Children.Add(new Border { Height = 1, Background = divider, Margin = new Thickness(16, 0, 16, 0) });
                        var level = new StackPanel { Margin = new Thickness(16, 8, 16, 12) };
                        bassText = new TextBlock { FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 6) };
                        bassSlider = new Slider { Style = (Style)window.FindResource("LevelSlider"), Minimum = bassMin, Maximum = bassMax, Value = bassValue };
                        AutomationProperties.SetName(bassSlider, "BassWave level");
                        bassSlider.ValueChanged += (s, e) =>
                        {
                            if (updating || bassText == null) return;
                            bassValue = (int)bassSlider.Value;
                            bassText.Text = bassValue.ToString();
                            bassTimer.Stop();
                            bassTimer.Start(); // send once the slider settles
                        };
                        var ends = new Grid { Margin = new Thickness(2, 6, 2, 0) };
                        ends.Children.Add(new TextBlock { Text = "Weak", Foreground = labelSecondary, FontSize = 12 });
                        ends.Children.Add(new TextBlock { Text = "Strong", Foreground = labelSecondary, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
                        level.Children.Add(bassText);
                        level.Children.Add(bassSlider);
                        level.Children.Add(ends);
                        bass.Children.Add(level);
                        subContent.Children.Add(new Border { Style = (Style)window.FindResource("Card"), Child = bass, Margin = new Thickness(0, 16, 0, 0) });
                    }

                    if (customEqs.Count > 0)
                    {
                        var customRows = new List<UIElement>();
                        foreach (var custom in customEqs)
                        {
                            var eq = custom;
                            var mark = Mark();
                            eqMarks[eq.Id] = mark;
                            customRows.Add(Row(eq.Name, null, mark, () =>
                            {
                                eqIndex = eq.Id;
                                link.Send(Cmd.SetCustomEq, new byte[] { 2 }.Concat(eq.Body).ToArray()); // action 2 selects (CustomEqFragment)
                                UpdateCards();
                            }));
                        }
                        AddSection(subContent, "Custom", Card(customRows));
                    }
                });
        }

        // Dual connection: the switch, then the devices the buds remember (0x8112).
        void OpenDualConnection()
        {
            if (caps.Contains(Cmd.GetMultiConnect)) link.Send(Cmd.GetMultiConnect);
            OpenSubPage("Dual connection",
                () => string.Join("|", knownDevices.Select(k => k.Name + k.Connected + k.ThisDevice)),
                () =>
                {
                    subContent.Children.Clear();
                    subContent.Children.Add(new Border { Style = (Style)window.FindResource("Card"), Margin = new Thickness(0, 16, 0, 0),
                        Child = SwitchRow(FeatureDual, "Dual connection", "Connect 2 devices at the same time and switch between them easily.") });
                    if (knownDevices.Count == 0) return;
                    var rows = knownDevices.Select(k => Row(k.Name, Summary(k.ThisDevice ? (k.Connected ? "Connected · This PC" : "This PC") : k.Connected ? "Connected" : null), null, null)).ToList();
                    AddSection(subContent, "Devices", Card(rows));
                });
        }

        void OpenFind()
        {
            OpenSubPage("Find my earbuds", () => device != null ? device.Name : "", () =>
            {
                subContent.Children.Clear();
                findGlyph = new Path { Width = 12, Height = 14, Stretch = Stretch.Uniform, Fill = accent };
                var leading = Glyph(Tray.Glyph, 20, 20, labelPrimary, new Thickness(0));
                AddSection(subContent, "Bluetooth earbuds", Card(new[] { Row(device != null ? device.Name : "Earbuds", null, findGlyph, ToggleFind, leading) }));
            });
        }

        void OpenMoreSettings()
        {
            OpenSubPage("More settings", () => "", () =>
            {
                subContent.Children.Clear();
                subContent.Children.Add(new Border { Style = (Style)window.FindResource("Card"), Margin = new Thickness(0, 16, 0, 0),
                    Child = SwitchRow(FeatureAutoPlay, "Auto play/pause", "Remove either earbud to pause audio playback and insert it again to resume.") });
            });
        }

        void ToggleFind()
        {
            if (!finding && MessageBox.Show(window, "The earbuds will play a loud sound.\nTake both earbuds out of your ears first.", "Find my earbuds",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            finding = !finding;
            link.Send(Cmd.FindEarbuds, (byte)(finding ? 1 : 0));
            UpdateCards();
        }

        // ---------- tray flyout and menu ----------

        // Small card above the notification area: name, battery strip, noise buttons. Closes when it loses focus.
        Window BuildFlyout()
        {
            var open = new Button { Content = "Open MelodyDesk", Style = (Style)window.FindResource("PillButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            open.Click += (s, e) => ShowMain();
            var stack = new StackPanel();
            stack.Children.Add(flyoutName);
            stack.Children.Add(flyoutStatus);
            stack.Children.Add(flyoutNoise);
            stack.Children.Add(flyoutHint);
            stack.Children.Add(open);
            var card = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Width = 344,
                Margin = new Thickness(12), Child = stack, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.25 },
            };
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                Topmost = true, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, UseLayoutRounding = true,
                FontFamily = window.FontFamily, FontSize = 14, Foreground = labelPrimary, Content = card, Title = "MelodyDesk",
            };
            w.Deactivated += (s, e) => { w.Hide(); flyoutHiddenAt = DateTime.UtcNow; };
            return w;
        }

        void ToggleFlyout()
        {
            if (flyout.IsVisible) { flyout.Hide(); return; }
            if ((DateTime.UtcNow - flyoutHiddenAt).TotalMilliseconds < 400) return; // this click is what just closed it
            Render();
            flyout.Left = -10000; // measure off-screen first, then park it in the work-area corner next to the tray
            flyout.Show();
            flyout.UpdateLayout();
            var area = SystemParameters.WorkArea;
            flyout.Left = area.Right - flyout.ActualWidth;
            flyout.Top = area.Bottom - flyout.ActualHeight;
            flyout.Activate();
        }

        void BuildTrayMenu(Forms.ContextMenuStrip menu)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem((device != null ? device.Name : "MelodyDesk") + "   " + (connected ? BatteryText() : "Not connected")) { Enabled = false });
            if (connected && caps.Contains(Cmd.SetNoiseMode))
            {
                menu.Items.Add(new Forms.ToolStripSeparator());
                foreach (int bit in Profile.NoiseBits)
                {
                    int b = bit;
                    var item = new Forms.ToolStripMenuItem(NoiseLabel(b)) { Checked = noise.Shown == b, Enabled = !noise.NoneWorn };
                    item.Click += (s, e) => PickNoise(b);
                    menu.Items.Add(item);
                }
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            var open = new Forms.ToolStripMenuItem("Open MelodyDesk");
            open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            open.Click += (s, e) => ShowMain();
            menu.Items.Add(open);
            if (device != null)
            {
                var connection = new Forms.ToolStripMenuItem(connected ? "Disconnect" : "Connect") { Enabled = !connecting };
                connection.Click += (s, e) => { if (connected) Disconnect(); else Connect(false); };
                menu.Items.Add(connection);
            }
            var autostart = new Forms.ToolStripMenuItem("Start with Windows") { Checked = Tray.StartsWithWindows };
            autostart.Click += (s, e) =>
            {
                try { Tray.StartsWithWindows = !Tray.StartsWithWindows; }
                catch (Exception ex) { MessageBox.Show("Could not change the start-up setting.\n" + ex.Message, "MelodyDesk"); }
            };
            menu.Items.Add(autostart);
            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = new Forms.ToolStripMenuItem("Exit");
            exit.Click += (s, e) => Exit();
            menu.Items.Add(exit);
        }

        // ---------- traffic log ----------

        void Log(string line)
        {
            string entry = DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine;
            if (log.Length > 200000) log.Clear();
            log.Append(entry);
            if (logBox != null) { logBox.AppendText(entry); logBox.ScrollToEnd(); }
        }

        void ShowLog()
        {
            if (logBox != null) { Window.GetWindow(logBox).Activate(); return; }
            logBox = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = log.ToString(), BorderThickness = new Thickness(0), Padding = new Thickness(8) };
            var w = new Window { Title = "Traffic log — MelodyDesk", Width = 640, Height = 420, Content = logBox };
            w.Closed += (s, e) => logBox = null;
            w.Show();
            logBox.ScrollToEnd();
        }
    }
}
