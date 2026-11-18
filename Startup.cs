using Microsoft.Win32;

namespace MelodyDesk
{
    // Start with Windows: the HKCU Run entry that opens MelodyDesk in the notification area at sign-in.
    // Task Manager's Startup apps page keeps its own Enabled/Disabled state for each entry under StartupApproved.
    // Shared by MelodyDesk.exe and the setup.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string Name = "MelodyDesk";

        // On when the Run entry exists and Task Manager hasn't disabled it (its StartupApproved first byte is odd then).
        public static bool Enabled
        {
            get
            {
                using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
                    if (run == null || run.GetValue(Name) == null) return false;
                using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    var state = approved == null ? null : approved.GetValue(Name) as byte[];
                    return state == null || state.Length == 0 || (state[0] & 1) == 0;
                }
            }
        }

        // Also clears Task Manager's state, so turning it on here undoes a Disable there.
        public static void Set(bool on, string exe)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) run.SetValue(Name, "\"" + exe + "\" --tray");
                else run.DeleteValue(Name, false);
            }
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                if (approved != null) approved.DeleteValue(Name, false);
        }
    }
}
