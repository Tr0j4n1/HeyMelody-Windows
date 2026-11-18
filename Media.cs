using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace MelodyDesk
{
    // Turns a play/pause tap the buds got backwards into the play that was meant.
    // The buds choose play or pause by whether the PC is sending them sound. While another app plays short sounds,
    // a tap meant to resume sends "pause" to a player that is already paused, and nothing happens until the sounds stop.
    // The buds report every tap to MelodyDesk, so in exactly that case MelodyDesk resumes the player itself.
    sealed class TapFix
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        readonly object gate = new object();
        Timer timer;
        GlobalSystemMediaTransportControlsSessionManager manager;
        IAudioMeterInformation meter;
        long meterMade;
        // What Listen last saw, in Clock milliseconds.
        GlobalSystemMediaTransportControlsSession session;
        string player;
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status;
        long statusSince, soundSeen = -60000;
        public event Action<string> Log;

        public void Start()
        {
            timer = new Timer(_ => Listen(), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(0, Timeout.Infinite); // only once timer is set, since Listen re-arms it
        }

        // Every 100 ms: which player Windows' media keys control and whether it plays, and whether the PC sends sound.
        // Polled rather than evented because the compiler that ships with Windows can't bind WinRT events.
        void Listen()
        {
            long now = Clock.ElapsedMilliseconds;
            try
            {
                if (manager == null) manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                var current = manager.GetCurrentSession();
                string id = current == null ? null : current.SourceAppUserModelId;
                var playback = current == null ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed : current.GetPlaybackInfo().PlaybackStatus;
                lock (gate)
                {
                    if (id != player || playback != status) statusSince = now;
                    session = current;
                    player = id;
                    status = playback;
                }
            }
            catch (Exception e)
            {
                if (manager == null) { Report("Media controls unavailable: " + e.Message); return; } // stops listening
            }
            try
            {
                if (meter == null || now - meterMade > 30000) { meter = Audio.DefaultMeter(); meterMade = now; } // follows device changes
                float peak;
                if (meter.GetPeakValue(out peak) != 0) meter = null;
                else if (peak > 0.0001f) lock (gate) soundSeen = now;
            }
            catch { meter = null; } // no playback device right now
            timer.Change(100, Timeout.Infinite);
        }

        // sentPlay is what the buds told the player for this tap.
        public void OnTap(bool sentPlay)
        {
            GlobalSystemMediaTransportControlsSession target;
            string targetPlayer;
            long since;
            lock (gate)
            {
                long now = Clock.ElapsedMilliseconds;
                // The report arrives about 0.5 s before the player reacts, so a pause meant for a playing player finds it
                // still playing, and a player already paused (allowing 0.3 s for polling) means the pause will do nothing.
                // Without sound from the PC the tap was for the other device the buds are connected to, so the player here
                // stays as it is.
                bool backwards = !sentPlay && session != null && status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
                    && now - statusSince > 300 && now - soundSeen < 3000;
                if (!backwards) return;
                target = session;
                targetPlayer = player;
                since = statusSince;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // The buds' own pause reaches the player about 0.5 s after their report (0.46-0.52 s measured); resuming
                // before it lands gets undone. Skip if the player changed meanwhile, e.g. a second tap already resumed it.
                Thread.Sleep(800);
                lock (gate)
                    if (player != targetPlayer || statusSince != since) return;
                Report("Play/pause tap sent pause to a paused player; resuming it");
                try { Wait(target.TryPlayAsync()); }
                catch (Exception e) { Report("Resume failed: " + e.Message); }
            });
        }

        // Blocks a pool thread until a WinRT call finishes (no await: see Listen).
        static T Wait<T>(IAsyncOperation<T> operation)
        {
            while (operation.Status == AsyncStatus.Started) Thread.Sleep(5);
            if (operation.Status != AsyncStatus.Completed) throw new InvalidOperationException("media controls call " + operation.Status);
            return operation.GetResults();
        }

        void Report(string line)
        {
            var log = Log;
            if (log != null) log(line);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator { int NotImpl1(); [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device); }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result); }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation { [PreserveSig] int GetPeakValue(out float peak); }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    static class Audio
    {
        // Peak meter of the default playback device (render, multimedia role), the level Windows mixes for it.
        public static IAudioMeterInformation DefaultMeter()
        {
            IMMDevice device;
            Marshal.ThrowExceptionForHR(((IMMDeviceEnumerator)new MMDeviceEnumerator()).GetDefaultAudioEndpoint(0, 1, out device));
            var iid = typeof(IAudioMeterInformation).GUID;
            object meter;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out meter));
            return (IAudioMeterInformation)meter;
        }
    }
}
