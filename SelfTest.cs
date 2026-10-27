using System;
using System.Collections.Generic;
using System.Linq;

namespace MelodyDesk
{
    // `MelodyDesk.exe --selftest`: protocol checks against bytes exchanged with a OnePlus Nord Buds 3 Pro.
    static class SelfTest
    {
        static int failures;

        public static int Run()
        {
            // Exact frame the buds accepted for "noise control bit 1".
            Check("encode", Hex(Frames.Encode(Cmd.SetNoiseMode, 0, new byte[] { 1, 1, 2 })) == "AA0A00000404000300010102");

            // Battery response split across two reads, with line noise in front: L 80%, R 100%, case unknown.
            byte[] battery = Frames.Encode(Cmd.GetBattery | Cmd.Response, 3, Bytes("0003015002640300"));
            var decoder = new FrameDecoder();
            var first = decoder.Feed(new byte[] { 0x00, 0x13 }.Concat(battery.Take(6)).ToArray(), 8);
            var rest = decoder.Feed(battery.Skip(6).ToArray(), battery.Length - 6);
            Check("split frame waits", first.Count == 0);
            Check("split frame decodes", rest.Count == 1 && rest[0].Cmd == 0x8106 && rest[0].Seq == 3);
            var levels = Parse.Batteries(rest[0].Data, 1);
            Check("battery", levels.Count == 3 && levels[0].Level == 80 && levels[1].Level == 100 && !levels[0].Charging);
            Check("charging bit", Parse.Batteries(Bytes("000101D0"), 1)[0].Charging && Parse.Batteries(Bytes("000101D0"), 1)[0].Level == 80);

            // Two FSN slices (control 1 then 3) carrying one packet, plus a back-to-back single frame.
            byte[] packet = Bytes("0681070400AABBCCDD");
            byte[] slice1 = new byte[] { 0xAA, 0x08, 0x01, 0x00, 0x00 }.Concat(packet.Take(5)).ToArray();
            byte[] slice2 = new byte[] { 0xAA, 0x07, 0x03, 0x00, 0x01 }.Concat(packet.Skip(5)).ToArray();
            byte[] stream = slice1.Concat(slice2).Concat(Frames.Encode(0x8109, 1, Bytes("00"))).ToArray();
            var sliced = new FrameDecoder().Feed(stream, stream.Length);
            Check("fsn reassembly", sliced.Count == 2 && Hex(sliced[0].Data) == "AABBCCDD" && sliced[1].Cmd == 0x8109);

            var caps = Cmd.ParseCapabilities(Bytes("00FF2752EEC714"));
            Check("capabilities", caps.Contains(Cmd.SetNoiseMode) && caps.Contains(Cmd.SetEqMode) && caps.Contains(Cmd.FindEarbuds)
                && caps.Contains(Cmd.SetFeature) && !caps.Contains(1059));

            var features = Parse.Features(Bytes("000B0401060009001100180000000000000000000000000000"), 1);
            Check("features", features.Count == 5 && features[0].Id == 4 && features[0].On && !features[1].On);

            var status = Parse.EarStatuses(Bytes("0003010302030304"), 1);
            Check("ear status", status.Count == 3 && status[0].InEar && !status[0].InCase);

            byte[] version = new byte[] { 0, 9 }.Concat(System.Text.Encoding.ASCII.GetBytes(
                "1,1,111,1,2,155,1,3,155,2,1,111,2,2,155,2,3,155,3,1,111,3,2,101,3,3,101")).ToArray();
            Check("versions", Parse.Versions(version) == "Left 111.155.155   Right 111.155.155   Case 111.101.101");
            Check("firmware summary", Parse.FirmwareSummary(version) == "155.155.101"); // what the phone shows

            // 0x8122 from the buds; Custom1's body is exactly what 0x0418 [02 ...] accepted to select it.
            var customs = Parse.CustomEqs(Bytes("0002" +
                "00FA060707437573746F6D31063E0000FA0000E80300A00F00401F00803E00" +
                "00FA060807437573746F6D32063E0000FA0000E80300A00F00401F00803E00"), 1);
            Check("custom eqs", customs.Count == 2 && customs[0].Name == "Custom1" && customs[0].Id == 7 && customs[1].Id == 8 && !customs[0].Selected
                && Hex(customs[0].Body) == "FA060707437573746F6D31063E0000FA0000E80300A00F00401F00803E00");

            // Two records shaped like 0x8112 from the buds (this PC, then a phone); addresses and names replaced.
            var known = Parse.KnownDevices(Bytes("0002" +
                "11223344556613020110" + "4445534B544F502D4558414D504C4500" +
                "66554433221115000012" + "53616DE2809973206950686F6E6520313500"), 1);
            Check("known devices", known.Count == 2 && known[0].Name == "DESKTOP-EXAMPLE" && known[0].Connected && known[0].ThisDevice
                && known[1].Name == "Sam’s iPhone 15" && !known[1].Connected);

            // Replays the case sequence captured from the buds: Noise cancellation chosen, one bud into the case (the buds
            // switch to Adaptive), both in (Off), both worn again (Transparency until the choice is put back).
            var now = new DateTime(2026, 1, 1);
            var sent = new List<int>();
            var memory = new NoiseMemory(sent.Add, () => now);
            Func<string, List<EarStatus>> ears = lr => Parse.EarStatuses(Bytes("000301" + lr.Substring(0, 2) + "02" + lr.Substring(2, 2) + "0304"), 1);
            memory.OnEars(ears("0303"));
            memory.OnReport(4);
            Check("noise: report while worn is the choice", memory.Chosen == 4 && sent.Count == 0);
            memory.OnEars(ears("0003"));
            memory.OnReport(5);
            Check("noise: one bud in case keeps choice", memory.Shown == 4 && memory.Reported == 5);
            memory.OnEars(ears("0000"));
            memory.OnReport(0);
            Check("noise: both in case keeps choice", memory.Shown == 4 && memory.NoneWorn && !memory.Pick(2) && memory.Chosen == 4);
            now = now.AddSeconds(30);
            memory.OnEars(ears("0303"));
            now = now.AddSeconds(1);
            memory.OnReport(2);
            now = now.AddSeconds(1);
            memory.OnReport(4);
            Check("noise: restored when worn again", memory.Chosen == 4 && sent.SequenceEqual(new[] { 4, 4 }));
            now = now.AddSeconds(10);
            memory.OnReport(0);
            Check("noise: later change on the buds is adopted", memory.Chosen == 0 && sent.Count == 2);

            // Text stays readable (WCAG AA, 4.5:1) on cards and page backgrounds in both themes.
            foreach (bool dark in new[] { false, true })
                foreach (string surface in new[] { "CardBg", "ContentBg", "WindowBg" })
                {
                    var bg = Theme.ColorOf(surface, dark);
                    double worst = new[] { "LabelPrimary", "LabelSecondary", "AccentText" }.Min(text => Theme.Contrast(Theme.ColorOf(text, dark), bg));
                    Check("contrast " + (dark ? "dark " : "light ") + surface + " " + worst.ToString("0.0") + ":1", worst >= 4.5);
                }

            Console.WriteLine(failures == 0 ? "selftest: all passed" : "selftest: " + failures + " failed");
            return failures == 0 ? 0 : 1;
        }

        static void Check(string name, bool ok)
        {
            if (!ok) failures++;
            Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        }

        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }

        static byte[] Bytes(string hex)
        {
            var r = new byte[hex.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return r;
        }
    }
}
