using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MelodyDesk
{
    // Notification-area icon. Left click raises Clicked; the right-click menu is rebuilt by MenuOpening every time it opens.
    sealed class Tray : IDisposable
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "MelodyDesk";
        // Two earbuds (round head + stem) on a 16x16 grid; used for the Device navigation item and Find my earbuds.
        public const string Glyph = "M4.5,1.5 A3.5,3.5 0 1 0 4.5,8.5 A3.5,3.5 0 1 0 4.5,1.5 Z M5.4,6.8 H7.4 V13.8 A1,1 0 0 1 5.4,13.8 Z "
                                  + "M11.5,1.5 A3.5,3.5 0 1 1 11.5,8.5 A3.5,3.5 0 1 1 11.5,1.5 Z M8.6,6.8 H10.6 V13.8 A1,1 0 0 1 8.6,13.8 Z";

        readonly NotifyIcon icon = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();

        public event Action Clicked;
        public event Action<ContextMenuStrip> MenuOpening;

        public Tray()
        {
            // The HeyMelody launcher icon (Assets/app.ico), at the size Windows uses for notification-area icons.
            using (var ico = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                icon.Icon = new Icon(ico, SystemInformation.SmallIconSize);
            icon.Text = "MelodyDesk";
            menu.Items.Add("MelodyDesk"); // an empty strip never raises Opening
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                if (MenuOpening != null) MenuOpening(menu);
                e.Cancel = menu.Items.Count == 0;
            };
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && Clicked != null) Clicked(); };
            icon.Visible = true;
        }

        public void SetText(string text)
        {
            icon.Text = text.Length > 63 ? text.Substring(0, 63) : text; // NotifyIcon.Text throws above 63 chars
        }

        // WinForms menus don't follow the Windows theme, so dark mode swaps in a dark renderer.
        public void SetDark(bool dark)
        {
            menu.Renderer = dark ? new DarkRenderer() : new ToolStripProfessionalRenderer();
            menu.BackColor = dark ? DarkColors.Background : SystemColors.Menu;
            menu.ForeColor = dark ? Color.FromArgb(0xE6, 0xE6, 0xE6) : SystemColors.MenuText;
        }

        sealed class DarkColors : ProfessionalColorTable
        {
            public static readonly Color Background = Color.FromArgb(0x2B, 0x2B, 0x2B);
            static readonly Color Highlight = Color.FromArgb(0x3D, 0x3D, 0x3D);
            static readonly Color Line = Color.FromArgb(0x45, 0x45, 0x45);
            public override Color ToolStripDropDownBackground { get { return Background; } }
            public override Color ImageMarginGradientBegin { get { return Background; } }
            public override Color ImageMarginGradientMiddle { get { return Background; } }
            public override Color ImageMarginGradientEnd { get { return Background; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Highlight; } }
            public override Color MenuItemSelected { get { return Highlight; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Background; } }
            public override Color CheckBackground { get { return Background; } }
            public override Color CheckSelectedBackground { get { return Highlight; } }
            public override Color CheckPressedBackground { get { return Highlight; } }
        }

        sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            // The stock check mark is a black bitmap; draw a light one instead.
            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.FromArgb(0xE6, 0xE6, 0xE6), 1.6f))
                    e.Graphics.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f), new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.3f) });
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Color.FromArgb(0xE6, 0xE6, 0xE6) : Color.FromArgb(0x8A, 0x8A, 0x8A);
                base.OnRenderItemText(e);
            }
        }

        public void Balloon(string title, string text)
        {
            icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }

        // Opt-in autostart: HKCU Run entry that launches straight into the tray.
        public static bool StartsWithWindows
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(RunValue) != null;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(RunValue, "\"" + Assembly.GetEntryAssembly().Location + "\" --tray");
                    else key.DeleteValue(RunValue, false);
                }
            }
        }
    }
}
