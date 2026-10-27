using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MelodyDesk
{
    // Light and dark palettes, applied as application resources that every style and layout uses through DynamicResource.
    // Text and control colours follow HeyMelody's COUI day/night values; window surfaces follow Windows 11.
    static class Theme
    {
        public enum Mode { System, Light, Dark }

        // key -> (light, dark)
        static readonly Dictionary<string, Tuple<string, string>> Palette = new Dictionary<string, Tuple<string, string>> {
            {"Accent",          Tuple.Create("#0A7FFB", "#0A7FFB")},   // fills: selected mode button, switch, slider
            {"AccentText",      Tuple.Create("#0067D0", "#5AA9FF")},   // accent as text: #0A7FFB is 3.9:1 on white, these clear 4.5:1 on every surface
            {"AccentSoft",      Tuple.Create("#260A7FFB", "#400A7FFB")},
            {"AccentSoftHover", Tuple.Create("#400A7FFB", "#590A7FFB")},
            {"LabelPrimary",    Tuple.Create("#E6000000", "#E6FFFFFF")},   // coui_color_label_primary
            {"LabelSecondary",  Tuple.Create("#94000000", "#8AFFFFFF")},   // coui_color_label_secondary (#8A000000) darkened to stay 4.5:1 on the grey window
            {"LabelTertiary",   Tuple.Create("#42000000", "#4DFFFFFF")},   // coui_color_label_tertiary
            {"Divider",         Tuple.Create("#14000000", "#1AFFFFFF")},
            {"WindowBg",        Tuple.Create("#F3F3F3", "#202020")},
            {"ContentBg",       Tuple.Create("#F9F9F9", "#272727")},
            {"CardBg",          Tuple.Create("#FFFFFF", "#2D2D2D")},
            {"CardBorder",      Tuple.Create("#0F000000", "#1F1F1F")},
            {"MenuBorder",      Tuple.Create("#1F000000", "#454545")},     // same edge as the tray menu (Tray.DarkColors.Line)
            {"Hover",           Tuple.Create("#0A000000", "#0DFFFFFF")},
            {"Pressed",         Tuple.Create("#14000000", "#17FFFFFF")},
            {"Selected",        Tuple.Create("#0F000000", "#12FFFFFF")},
            {"ModeBg",          Tuple.Create("#EBEBEB", "#1FFFFFFF")},     // melody_ui_color_mode_normal_unselected_button_bg
            {"ModeHover",       Tuple.Create("#E0E0E0", "#2EFFFFFF")},
            {"ModeIcon",        Tuple.Create("#D9000000", "#D9FFFFFF")},   // melody_ui_iot_mode_button_icon_normal_tint
            {"SoftFill",        Tuple.Create("#EBEBEB", "#1FFFFFFF")},
            {"TrackOff",        Tuple.Create("#26000000", "#40FFFFFF")},   // coui_color_container16
            {"ScrollThumb",     Tuple.Create("#33000000", "#4DFFFFFF")},
            {"BatteryFill",     Tuple.Create("#33000000", "#40FFFFFF")},
            {"Badge",           Tuple.Create("#6E6E6E", "#8A8A8A")},
        };

        static readonly string SettingFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MelodyDesk", "theme.txt");

        public static bool IsDark { get; private set; }

        public static Mode Chosen
        {
            get
            {
                Mode mode;
                try { if (File.Exists(SettingFile) && Enum.TryParse(File.ReadAllText(SettingFile).Trim(), true, out mode)) return mode; }
                catch (IOException) { }
                return Mode.System;
            }
            set
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingFile));
                File.WriteAllText(SettingFile, value.ToString());
            }
        }

        // Windows "Choose your app mode" (Settings > Personalisation > Colours).
        public static bool SystemIsDark()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
        }

        public static bool Resolve(Mode mode)
        {
            return mode == Mode.Dark || (mode == Mode.System && SystemIsDark());
        }

        public static void Apply(ResourceDictionary resources, bool dark)
        {
            IsDark = dark;
            foreach (var entry in Palette)
                resources[entry.Key] = Brush(dark ? entry.Value.Item2 : entry.Value.Item1);
            // WPF's built-in menus (the ⋮ menu, gesture picker, submenus) draw with these system keys.
            resources[SystemColors.MenuBrushKey] = resources["CardBg"];
            resources[SystemColors.MenuTextBrushKey] = resources["LabelPrimary"];
        }

        public static Color ColorOf(string key, bool dark)
        {
            var entry = Palette[key];
            return (Color)ColorConverter.ConvertFromString(dark ? entry.Item2 : entry.Item1);
        }

        // WCAG contrast ratio of a (possibly translucent) foreground drawn over an opaque background.
        public static double Contrast(Color foreground, Color background)
        {
            double a = foreground.A / 255.0;
            Func<byte, byte, double> blend = (f, b) => (f * a + b * (1 - a)) / 255.0;
            double lf = Luminance(blend(foreground.R, background.R), blend(foreground.G, background.G), blend(foreground.B, background.B));
            double lb = Luminance(background.R / 255.0, background.G / 255.0, background.B / 255.0);
            return (Math.Max(lf, lb) + 0.05) / (Math.Min(lf, lb) + 0.05);
        }

        static double Luminance(double r, double g, double b)
        {
            Func<double, double> lin = c => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
        }

        static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        // Dark title bar to match (Windows 10 20H1+ and 11); older builds ignore the attribute.
        public static void ApplyTitleBar(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
