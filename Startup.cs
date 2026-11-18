using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace MelodyDesk
{
    // Start with Windows: MelodyDesk.lnk in the user's Startup folder, which opens MelodyDesk in the notification area at
    // sign-in. A shortcut rather than a Run registry entry: Task Manager and Settings > Apps > Startup can leave a new Run
    // entry out (seen on Windows build 26300) but list a new Startup folder shortcut straight away.
    // Task Manager keeps its own Enabled/Disabled state for each item under StartupApproved. Shared by MelodyDesk.exe and the setup.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run"; // where 1.6 and older registered
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        const string Name = "MelodyDesk";
        static readonly string Link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Name + ".lnk");

        // On when the shortcut (or an older version's Run entry) exists and Task Manager hasn't disabled it.
        public static bool Enabled
        {
            get
            {
                if (File.Exists(Link)) return Approved("StartupFolder", Name + ".lnk");
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                    return run != null && run.GetValue(Name) != null && Approved("Run", Name);
            }
        }

        // Also removes an older version's Run entry and Task Manager's state, so turning it on here undoes a Disable there.
        public static void Set(bool on, string exe)
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Link));
                Shortcut(Link, exe, "--tray");
            }
            else File.Delete(Link);
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (run != null) run.DeleteValue(Name, false);
            Forget("Run", Name);
            Forget("StartupFolder", Name + ".lnk");
        }

        // Task Manager marks a disabled item with an odd first byte.
        static bool Approved(string list, string item)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(ApprovedKey + list))
            {
                var state = key == null ? null : key.GetValue(item) as byte[];
                return state == null || state.Length == 0 || (state[0] & 1) == 0;
            }
        }

        static void Forget(string list, string item)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(ApprovedKey + list, true))
                if (key != null) key.DeleteValue(item, false);
        }

        // A .lnk made with the shell's WScript.Shell object, late-bound so neither exe needs another reference.
        public static void Shortcut(string path, string exe, string arguments)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Put(link, "TargetPath", exe);
            Put(link, "Arguments", arguments);
            Put(link, "WorkingDirectory", Path.GetDirectoryName(exe));
            Put(link, "Description", "Earbuds control for Windows");
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }

        static void Put(object target, string property, string value)
        {
            target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, new object[] { value });
        }
    }
}
