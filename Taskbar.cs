using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MelodyDesk
{
    // The battery levels as text on the taskbar, just left of the notification area. The label is a child window of the
    // taskbar (as TrafficMonitor does it), so it hides with the taskbar for full-screen apps and auto-hide. It runs on its
    // own thread: a child window inside Explorer's taskbar shares input with it, and a busy main window must not stall it.
    sealed class TaskbarLabel
    {
        static readonly string SettingFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MelodyDesk", "taskbar.txt");
        static bool enabled = ReadEnabled();

        readonly Dispatcher dispatcher;
        HwndSource source;
        IntPtr taskbar;
        TextBlock label;
        Path glyph;
        Border shade;
        string text;
        bool hovering, pressed, dark;

        public event Action Clicked; // raised on the label's thread

        public TaskbarLabel()
        {
            var ready = new ManualResetEvent(false);
            Dispatcher d = null;
            var thread = new Thread(() => { d = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true, Name = "Taskbar label" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.WaitOne();
            dispatcher = d;
            dispatcher.BeginInvoke(new Action(() =>
            {
                // Keeps up with the tray's width (icons, clock), Explorer restarts and the taskbar theme.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s, e) => Place();
                timer.Start();
            }));
        }

        // On unless turned off in the tray menu.
        public static bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingFile));
                System.IO.File.WriteAllText(SettingFile, value ? "On" : "Off");
            }
        }

        static bool ReadEnabled()
        {
            try { return !System.IO.File.Exists(SettingFile) || System.IO.File.ReadAllText(SettingFile).Trim() != "Off"; }
            catch (System.IO.IOException) { return true; }
        }

        // null hides the label.
        public void SetText(string value)
        {
            dispatcher.BeginInvoke(new Action(() => { text = value; Place(); }));
        }

        public void Close()
        {
            dispatcher.BeginInvoke(new Action(() => { if (source != null) source.Dispose(); dispatcher.InvokeShutdown(); }));
        }

        void Place()
        {
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            if (source != null && (source.IsDisposed || bar != taskbar)) // Explorer restarted and took the old label with it
            {
                if (!source.IsDisposed) source.Dispose();
                source = null;
            }
            if (text == null || bar == IntPtr.Zero)
            {
                if (source != null) ShowWindow(source.Handle, 0);
                return;
            }
            if (source == null) Create(bar);

            label.Text = text;
            dark = Theme.TaskbarIsDark();
            label.Foreground = glyph.Fill = dark ? Brushes.White : Brushes.Black;
            Shade();
            RECT barRect, tray;
            GetWindowRect(bar, out barRect);
            if (!GetWindowRect(FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null), out tray)) tray.Left = barRect.Right;
            var toDevice = source.CompositionTarget.TransformToDevice;
            var root = (FrameworkElement)source.RootVisual;
            root.Height = (barRect.Bottom - barRect.Top) / toDevice.M22; // the root doesn't stretch to the child window on its own
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            int width = (int)Math.Ceiling(root.DesiredSize.Width * toDevice.M11);
            SetWindowPos(source.Handle, IntPtr.Zero, tray.Left - barRect.Left - width, 0, width, barRect.Bottom - barRect.Top, 0x0010 | 0x0040); // HWND_TOP, SWP_NOACTIVATE | SWP_SHOWWINDOW
        }

        void Create(IntPtr bar)
        {
            taskbar = bar;
            glyph = new Path { Data = Geometry.Parse(Tray.Glyph), Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            label = new TextBlock { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            // The rounded hover shade of the taskbar's own buttons: 4 px clear above and below, 4 px corners.
            shade = new Border { Margin = new Thickness(0, 4, 2, 4), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Hidden };
            // Almost rather than fully transparent: a layered window lets clicks through its fully transparent pixels.
            var root = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Children = { shade, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 12, 0), Children = { glyph, label } } },
            };
            source = new HwndSource(new HwndSourceParameters("MelodyDesk battery")
            {
                ParentWindow = bar,
                WindowStyle = 0x40000000 | 0x04000000, // WS_CHILD | WS_CLIPSIBLINGS
                ExtendedWindowStyle = 0x00000004,      // WS_EX_NOPARENTNOTIFY: the taskbar takes focus when told a child was clicked
                UsesPerPixelTransparency = true,       // a layered child window; needs the Windows 10 entry in app.manifest
                Width = 1,
                Height = 1,
            });
            source.RootVisual = root;
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == 0x0021) // WM_MOUSEACTIVATE: don't activate the taskbar, or it takes focus back from the panel the click opens
                {
                    handled = true;
                    return (IntPtr)3; // MA_NOACTIVATE
                }
                switch (msg)
                {
                    case 0x0200: // WM_MOUSEMOVE
                        if (!hovering)
                        {
                            var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = 0x2, hwndTrack = hwnd }; // TME_LEAVE
                            TrackMouseEvent(ref track);
                        }
                        hovering = true;
                        break;
                    case 0x02A3: hovering = pressed = false; break; // WM_MOUSELEAVE
                    case 0x0201: pressed = true; break;             // WM_LBUTTONDOWN
                    case 0x0202:                                    // WM_LBUTTONUP
                        pressed = false;
                        if (Clicked != null) Clicked();
                        break;
                    default: return IntPtr.Zero;
                }
                Shade();
                return IntPtr.Zero;
            });
        }

        // Windows 11's subtle fills: SubtleFillColorSecondary on hover, Tertiary while pressed.
        void Shade()
        {
            shade.Visibility = hovering || pressed ? Visibility.Visible : Visibility.Hidden;
            shade.Background = new SolidColorBrush(dark ? Color.FromArgb(pressed ? (byte)0x0A : (byte)0x0F, 255, 255, 255) : Color.FromArgb(pressed ? (byte)0x06 : (byte)0x09, 0, 0, 0));
            shade.BorderBrush = dark ? new SolidColorBrush(Color.FromArgb(0x17, 255, 255, 255)) : shade.Background; // the slightly lighter top edge
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string className, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    }
}
