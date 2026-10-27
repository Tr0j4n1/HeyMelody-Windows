using System;
using System.Collections.Generic;
using System.Text;

namespace MelodyDesk
{
    // Application packet: cmd(LE16) seq(u8) len(LE16) payload. Responses echo seq and set cmd bit 0x8000.
    // Reverse-engineered from HeyMelody 116.9.0: f7.a (Packet), v7.b (TLVDataProcessor), w7.a (OPOv1Wrapper).
    public sealed class Packet
    {
        public int Cmd;
        public int Seq;
        public byte[] Data;

        public int Status { get { return Data.Length > 0 ? Data[0] : -1; } }

        public override string ToString()
        {
            return string.Format("cmd=0x{0:X4} seq={1} data={2}", Cmd, Seq, Data.Length == 0 ? "-" : BitConverter.ToString(Data));
        }
    }

    public static class Cmd
    {
        public const int GetCapability = 0x0100;
        public const int GetVersion = 0x0105;
        public const int GetBattery = 0x0106;
        public const int GetKeyFunctions = 0x0108;
        public const int GetEarStatus = 0x0109;
        public const int GetNoiseMode = 0x010C;
        public const int GetFeatures = 0x010D;
        public const int GetEqMode = 0x010F;
        public const int GetMultiConnect = 0x0112; // devices the buds know: [count, (address, length, state, flags, nameLen, name)...]
        public const int GetCustomEqs = 0x0122;
        public const int GetBassEngine = 0x0124;  // BassWave level: [status, min, max, value] (signed)
        public const int GetNotifyCapability = 0x0200;
        public const int RegisterNotify = 0x0201;
        public const int Event = 0x0204;
        public const int RegisterNotifyMulti = 0x0205;
        public const int TimeRequest = 0x0500; // device -> host, answered with [0, unix seconds LE32]
        public const int FeatureSync = 0x0503; // device -> host: [count, (featureId, on)...]
        public const int EqSync = 0x0504;      // device -> host: [eq preset]
        public const int FindEarbuds = 0x0400;
        public const int SetKeyFunction = 0x0401;  // [count, (device, button, action, function)...]
        public const int SetFeature = 0x0403;
        public const int SetNoiseMode = 0x0404;
        public const int SetEqMode = 0x0406;
        public const int SetCustomEq = 0x0418;     // [action, custom EQ body]; action 1 add, 2 select, 3 delete
        public const int SetBassEngine = 0x041B;   // [min, max, value]
        public const int Response = 0x8000;

        // Capability bit -> command ids it unlocks (R6.a in the APK). Bit n is bit (n % 8) of capability byte (n / 8).
        static readonly int[][] CapabilityBits = {
            new[] {261}, new[] {262}, new[] {263}, new[] {264, 1025, 1046}, new[] {265}, new[] {1024}, new[] {1026}, new[] {1027},
            new[] {268, 1028}, new[] {1029}, new[] {1030, 271}, new[] {1031}, new int[0], new[] {1032}, new[] {1033}, new int[0],
            new int[0], new[] {276}, new int[0], new[] {1038, 1037, 277, 278}, new[] {1039}, new[] {1040, 281}, new[] {517}, new[] {3840},
            new int[0], new[] {280, 1041}, new[] {282, 1042}, new[] {284, 1043}, new int[0], new[] {274, 1035}, new[] {286, 287, 1045}, new[] {1037},
            new int[0], new[] {289, 1047}, new[] {290, 1048}, new int[0], new[] {285, 1044}, new[] {291, 1050}, new[] {292, 1051}, new[] {293, 1052, 295, 1053, 1055},
            new[] {1057, 35, 36, 34, 294, 297}, new[] {61185}, new[] {61186}, new[] {61187, 1054}, new[] {1056}, new[] {28}, new int[0], new[] {1058, 298},
            new[] {61188}, new[] {1059, 299}, new int[0], new[] {1060}, new[] {61190}, new int[0], new int[0], new[] {1061, 302, 1062},
            new[] {303}, new[] {1063, 304}, new[] {305, 1064}, new[] {1065, 306}, new[] {20}, new[] {1069, 307}, new[] {1070}, new[] {61191},
            new[] {61192}, new[] {61193}, new[] {1073, 308},
        };

        // Commands every device answers regardless of its capability bitmap.
        static readonly int[] AlwaysSupported = {256, 257, 258, 259, 260, 267, 3840, 3844, 269, 3843, 262};

        public static HashSet<int> ParseCapabilities(byte[] data)
        {
            var set = new HashSet<int>(AlwaysSupported);
            int bits = Math.Min((data.Length - 1) * 8, CapabilityBits.Length);
            for (int i = 0; i < bits; i++)
                if ((data[1 + i / 8] & (1 << (i % 8))) != 0)
                    set.UnionWith(CapabilityBits[i]);
            return set;
        }
    }

    public static class Frames
    {
        // OPOv1 link frame: AA | varint(len) | fsn-control | 00 | packet. len counts the two control bytes plus the packet.
        // shortcut: single frame only (control 0); packets over ~650 bytes need FSN splitting like TLVDataProcessor, only OTA sends those.
        public static byte[] Encode(int cmd, int seq, byte[] payload)
        {
            var packet = new List<byte> { (byte)cmd, (byte)(cmd >> 8), (byte)seq, (byte)payload.Length, (byte)(payload.Length >> 8) };
            packet.AddRange(payload);
            var frame = new List<byte> { 0xAA };
            int len = packet.Count + 2;
            do
            {
                byte b = (byte)(len & 0x7F);
                len >>= 7;
                if (len != 0) b |= 0x80;
                frame.Add(b);
            } while (len != 0);
            frame.Add(0);
            frame.Add(0);
            frame.AddRange(packet);
            return frame.ToArray();
        }
    }

    // Reassembles packets from the RFCOMM byte stream: frames can arrive split or concatenated, and long packets span FSN frames.
    public sealed class FrameDecoder
    {
        readonly List<byte> buffer = new List<byte>();
        List<byte> sliced;

        public List<Packet> Feed(byte[] data, int count)
        {
            var packets = new List<Packet>();
            for (int i = 0; i < count; i++) buffer.Add(data[i]);
            while (buffer.Count > 0)
            {
                int start = buffer.IndexOf(0xAA);
                if (start < 0) { buffer.Clear(); break; }
                if (start > 0) buffer.RemoveRange(0, start);

                int len = 0, shift = 0, p = 1;
                bool complete = false;
                while (p < buffer.Count && p <= 3)
                {
                    byte b = buffer[p++];
                    len |= (b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0) { complete = true; break; }
                }
                if (!complete)
                {
                    if (p > 3) { buffer.RemoveAt(0); continue; } // not a real header, resync on the next 0xAA
                    break;
                }
                int total = p + len;
                if (buffer.Count < total) break;
                byte[] frame = buffer.GetRange(0, total).ToArray();
                buffer.RemoveRange(0, total);
                if (len < 2) continue;

                int control = frame[p] & 3;
                int offset = p + (control == 0 ? 2 : 3);
                var body = new byte[Math.Max(0, total - offset)];
                Array.Copy(frame, offset, body, 0, body.Length);
                if (control == 0)
                {
                    Emit(body, packets);
                    continue;
                }
                if (control == 1 || sliced == null) sliced = new List<byte>();
                sliced.AddRange(body);
                if (control == 3)
                {
                    Emit(sliced.ToArray(), packets);
                    sliced = null;
                }
            }
            return packets;
        }

        static void Emit(byte[] body, List<Packet> packets)
        {
            if (body.Length < 5) return;
            int len = Math.Min(body[3] | body[4] << 8, body.Length - 5);
            var data = new byte[len];
            Array.Copy(body, 5, data, 0, len);
            packets.Add(new Packet { Cmd = body[0] | body[1] << 8, Seq = body[2], Data = data });
        }
    }

    public sealed class Battery
    {
        public int Device; // 1 left, 2 right, 3 case
        public int Level;
        public bool Charging;
    }

    public sealed class EarStatus
    {
        public int Device;
        public bool InEar;
        public bool InCase;
    }

    public sealed class FeatureState
    {
        public int Id;
        public bool On;
    }

    public sealed class CustomEq
    {
        public int Id;          // also the value 0x810F reports while it is selected
        public string Name;
        public bool Selected;
        public byte[] Body;     // the entry after its selected flag: what 0x0418 expects after the action byte
    }

    public sealed class KnownDevice
    {
        public string Name;
        public bool Connected;
        public bool ThisDevice; // the host that asked (this PC)
    }

    public static class Parse
    {
        // [count, (device, level | 0x80 when charging)...] starting at offset (BatteryInfo).
        public static List<Battery> Batteries(byte[] d, int offset)
        {
            var list = new List<Battery>();
            if (d.Length <= offset) return list;
            int n = d[offset];
            for (int i = 0; i < n; i++)
            {
                int at = offset + 1 + i * 2;
                if (at + 1 >= d.Length) break;
                list.Add(new Battery { Device = d[at], Level = d[at + 1] & 0x7F, Charging = (d[at + 1] & 0x80) != 0 });
            }
            return list;
        }

        // [count, (device, flags)...]: flags bit0 clear = in case, bit1 set = in ear (StatusInfo).
        public static List<EarStatus> EarStatuses(byte[] d, int offset)
        {
            var list = new List<EarStatus>();
            if (d.Length <= offset) return list;
            int n = d[offset];
            for (int i = 0; i < n; i++)
            {
                int at = offset + 1 + i * 2;
                if (at + 1 >= d.Length) break;
                list.Add(new EarStatus { Device = d[at], InCase = (d[at + 1] & 1) == 0, InEar = (d[at + 1] & 2) != 0 });
            }
            return list;
        }

        // [count, (featureId, on)...]; the device zero-pads entries for ids it doesn't support.
        public static List<FeatureState> Features(byte[] d, int offset)
        {
            var list = new List<FeatureState>();
            if (d.Length <= offset) return list;
            int n = d[offset];
            for (int i = 0; i < n; i++)
            {
                int at = offset + 1 + i * 2;
                if (at + 1 >= d.Length) break;
                if (d[at] != 0) list.Add(new FeatureState { Id = d[at], On = d[at + 1] != 0 });
            }
            return list;
        }

        // 0x8105: [status, count, "device,type,version,device,type,version,..."] as UTF-8 CSV.
        public static string Versions(byte[] d)
        {
            if (d.Length < 3) return "";
            string[] f = Encoding.UTF8.GetString(d, 2, d.Length - 2).Split(',');
            var byDevice = new SortedDictionary<string, List<string>>();
            for (int i = 0; i + 2 < f.Length; i += 3)
            {
                if (!byDevice.ContainsKey(f[i])) byDevice[f[i]] = new List<string>();
                byDevice[f[i]].Add(f[i + 2]);
            }
            var parts = new List<string>();
            foreach (var kv in byDevice) parts.Add(DeviceName(int.Parse(kv.Key)) + " " + string.Join(".", kv.Value));
            return string.Join("   ", parts);
        }

        // The phone's one-line firmware: the last version of each part (left, right, case), e.g. "155.155.101".
        public static string FirmwareSummary(byte[] d)
        {
            if (d.Length < 3) return "";
            string[] f = Encoding.UTF8.GetString(d, 2, d.Length - 2).Split(',');
            var last = new SortedDictionary<int, string>();
            for (int i = 0; i + 2 < f.Length; i += 3)
            {
                int device;
                if (int.TryParse(f[i], out device)) last[device] = f[i + 2];
            }
            return string.Join(".", last.Values);
        }

        // 0x8122: [status, count, (selected, min, max, id, nameLen, name, bandCount, (freq LE16, dB)...)...]
        public static List<CustomEq> CustomEqs(byte[] d, int offset)
        {
            var list = new List<CustomEq>();
            if (d.Length <= offset) return list;
            int count = d[offset], at = offset + 1;
            for (int i = 0; i < count; i++)
            {
                if (at + 5 > d.Length) break;
                int nameLength = d[at + 4], bands = at + 5 + nameLength;
                if (bands >= d.Length) break;
                int end = bands + 1 + d[bands] * 3;
                if (end > d.Length) break;
                var body = new byte[end - at - 1];
                Array.Copy(d, at + 1, body, 0, body.Length);
                list.Add(new CustomEq { Selected = d[at] != 0, Id = d[at + 3], Name = Encoding.UTF8.GetString(d, at + 5, nameLength).TrimEnd('\0'), Body = body });
                at = end;
            }
            return list;
        }

        // 0x8112 / event 6: [count, (address(6), length, state, flags, nameLen, name)...]; state 2 = connected,
        // flags bit 0 = the host that asked (CommandUtil multi-connect parser).
        public static List<KnownDevice> KnownDevices(byte[] d, int offset)
        {
            var list = new List<KnownDevice>();
            if (d.Length <= offset) return list;
            int count = d[offset], at = offset + 1;
            for (int i = 0; i < count; i++)
            {
                if (at + 10 > d.Length) break;
                int length = d[at + 6], nameLength = d[at + 9];
                if (at + 10 + nameLength > d.Length) break;
                bool blank = true;
                for (int b = 0; b < 6; b++) if (d[at + b] != 0) blank = false;
                if (!blank)
                    list.Add(new KnownDevice { Name = Encoding.UTF8.GetString(d, at + 10, nameLength).TrimEnd('\0'), Connected = d[at + 7] == 2, ThisDevice = (d[at + 8] & 1) != 0 });
                at += 7 + length; // address + length byte + the `length` bytes that follow it
            }
            return list;
        }

        public static string DeviceName(int device)
        {
            switch (device)
            {
                case 1: return "Left";
                case 2: return "Right";
                case 3: return "Case";
                default: return "Device " + device;
            }
        }
    }
}
