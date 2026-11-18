using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;

namespace MelodyDesk
{
    // Checks GitHub for a newer release and starts its installer, which closes this app and replaces it.
    static class Update
    {
        const string Releases = "https://github.com/Tr0j4n1/HeyMelody-Windows/releases/latest";
        public static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;
        static readonly string Agent = "MelodyDesk/" + Current.ToString(2);

        // GitHub redirects /releases/latest to /releases/tag/v<version>, so the redirect alone names the newest version.
        public static Version Latest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // GitHub needs TLS 1.2, which older .NET defaults leave off
            var request = (HttpWebRequest)WebRequest.Create(Releases);
            request.Method = "HEAD";
            request.AllowAutoRedirect = false;
            request.UserAgent = Agent;
            using (var response = (HttpWebResponse)request.GetResponse())
                return FromTagUrl(response.Headers[HttpResponseHeader.Location]);
        }

        public static Version FromTagUrl(string url)
        {
            int at = url == null ? -1 : url.LastIndexOf("/tag/v", StringComparison.Ordinal);
            Version version;
            if (at < 0 || !Version.TryParse(url.Substring(at + 6), out version)) throw new InvalidDataException("Unexpected release address: " + url);
            return version;
        }

        // A tag like v1.5 parses without build and revision numbers, so compare it as 1.5.0.0.
        public static bool IsNewer(Version latest)
        {
            return new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0), Math.Max(latest.Revision, 0)) > Current;
        }

        public static void Install()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var setup = Path.Combine(Path.GetTempPath(), "MelodyDesk-Setup.exe");
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = Agent;
                client.DownloadFile(Releases + "/download/MelodyDesk-Setup.exe", setup);
            }
            Process.Start(setup);
        }
    }
}
