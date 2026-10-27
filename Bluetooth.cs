using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace MelodyDesk
{
    // SOCKADDR_BTH for Winsock RFCOMM. Port 0 + serviceClassId makes Windows resolve the RFCOMM channel through SDP.
    sealed class BluetoothEndPoint : EndPoint
    {
        const AddressFamily AF_BTH = (AddressFamily)32;
        readonly ulong address;
        readonly Guid service;

        public BluetoothEndPoint(ulong address, Guid service)
        {
            this.address = address;
            this.service = service;
        }

        public override AddressFamily AddressFamily { get { return AF_BTH; } }

        public override SocketAddress Serialize()
        {
            // packed layout: family(2) btAddr(8) serviceClassId(16) port(4)
            var sa = new SocketAddress(AF_BTH, 30);
            byte[] a = BitConverter.GetBytes(address), g = service.ToByteArray();
            for (int i = 0; i < 8; i++) sa[2 + i] = a[i];
            for (int i = 0; i < 16; i++) sa[10 + i] = g[i];
            return sa;
        }

        public override EndPoint Create(SocketAddress socketAddress) { return this; }
    }

    public sealed class PairedDevice
    {
        public string Name;
        public ulong Address;
        public bool Connected;

        public string AddressText { get { return Address.ToString("X12"); } }

        public override string ToString()
        {
            return Name + (Connected ? "  (connected)" : "");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SearchParams
        {
            public int dwSize;
            public bool fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SystemTime { public ushort y, mo, dow, d, h, mi, s, ms; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DeviceInfo
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public bool fConnected, fRemembered, fAuthenticated;
            public SystemTime stLastSeen, stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);

        [DllImport("bthprops.cpl", SetLastError = true)]
        static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);

        [DllImport("bthprops.cpl")]
        static extern bool BluetoothFindDeviceClose(IntPtr find);

        // Paired (remembered) devices only; no inquiry scan, so this returns instantly.
        public static List<PairedDevice> List()
        {
            var list = new List<PairedDevice>();
            var search = new SearchParams { fReturnAuthenticated = true, fReturnRemembered = true, fReturnConnected = true };
            search.dwSize = Marshal.SizeOf(search);
            var info = new DeviceInfo();
            info.dwSize = Marshal.SizeOf(info);
            IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
            if (find == IntPtr.Zero) return list;
            try
            {
                do
                {
                    list.Add(new PairedDevice { Name = info.szName, Address = info.Address, Connected = info.fConnected });
                    info = new DeviceInfo();
                    info.dwSize = Marshal.SizeOf(info);
                } while (BluetoothFindNextDevice(find, ref info));
            }
            finally
            {
                BluetoothFindDeviceClose(find);
            }
            return list;
        }
    }

    // One RFCOMM session to the earbuds' HeyMelody service. Callbacks run on the receive thread.
    public sealed class EarbudsLink
    {
        // HeyMelody's SPP service UUIDs (model/repository/whitelist/a.f20142y), tried in order.
        static readonly Guid[] Services = { new Guid("0000079A-D102-11E1-9B23-00025B00A5A5"), new Guid("00001107-D102-11E1-9B23-00025B00A5A5") };

        Socket socket;
        byte seq;
        readonly object sendLock = new object();

        public event Action<Packet> PacketReceived;
        public event Action<byte[], bool> Traffic; // raw bytes, true = sent
        public event Action<string> Closed;

        public void Connect(ulong address)
        {
            Exception last = null;
            foreach (var service in Services)
            {
                var s = new Socket((AddressFamily)32, SocketType.Stream, (ProtocolType)3);
                try
                {
                    s.Connect(new BluetoothEndPoint(address, service));
                    socket = s;
                    seq = 0;
                    var rx = new Thread(ReceiveLoop) { IsBackground = true, Name = "rfcomm-rx" };
                    rx.Start(s);
                    return;
                }
                catch (SocketException e)
                {
                    last = e;
                    s.Close();
                }
            }
            throw last;
        }

        public bool IsOpen { get { return socket != null; } }

        public void Send(int cmd, params byte[] payload)
        {
            lock (sendLock) Write(cmd, seq++, payload);
        }

        // Answers a device-initiated request: same seq, response bit set.
        public void Reply(Packet request, params byte[] payload)
        {
            lock (sendLock) Write(request.Cmd | Cmd.Response, request.Seq, payload);
        }

        void Write(int cmd, int sequence, byte[] payload)
        {
            if (socket == null) return;
            byte[] frame = Frames.Encode(cmd, sequence, payload);
            if (Traffic != null) Traffic(frame, true);
            try { socket.Send(frame); }
            catch (SocketException e) { Close(e.Message); }
        }

        public void Close(string reason)
        {
            Socket s;
            lock (sendLock)
            {
                s = socket;
                socket = null;
            }
            if (s == null) return;
            try { s.Close(); } catch (SocketException) { }
            if (Closed != null) Closed(reason);
        }

        void ReceiveLoop(object state)
        {
            var s = (Socket)state;
            var decoder = new FrameDecoder();
            var buf = new byte[4096];
            string reason = "Earbuds closed the connection";
            try
            {
                while (true)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    if (Traffic != null)
                    {
                        var copy = new byte[n];
                        Array.Copy(buf, copy, n);
                        Traffic(copy, false);
                    }
                    foreach (var p in decoder.Feed(buf, n))
                        if (PacketReceived != null) PacketReceived(p);
                }
            }
            catch (SocketException e) { reason = e.Message; }
            catch (ObjectDisposedException) { }
            if (socket == s) Close(reason);
        }
    }
}
