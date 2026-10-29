// Small extras: Start menu / Desktop shortcuts and checking GitHub for a newer version.
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AutomationToBeamNG
{
    static class Shortcuts
    {
        public const string Name = "Automation to BeamNG";

        public static string StartMenuPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
        public static string DesktopPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }

        // Creates a .lnk to this exe through the Windows Script Host shell object.
        public static void Create(string lnkPath)
        {
            string exe = Application.ExecutablePath;
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t), lnk = null;
            try
            {
                lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { exe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(exe) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Convert Automation engines for BeamNG.drive" });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { exe + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                if (lnk != null) Marshal.ReleaseComObject(lnk);
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    static class Updates
    {
        public static Version Current { get { return typeof(Updates).Assembly.GetName().Version; } }

        public static bool IsValidRepo(string repo) { return !string.IsNullOrEmpty(repo) && Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"); }

        // Latest GitHub release of owner/name. Returns false (with a message) when it can't tell.
        public static bool TryGetLatest(string repo, out Version latest, out string pageUrl, out string message)
        {
            latest = null; pageUrl = null; message = null;
            if (!IsValidRepo(repo)) { message = "No update source set. Add the GitHub repository (owner/name) in Folders & settings."; return false; }
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + repo + "/releases/latest");
                req.UserAgent = "AutomationToBeamNG";
                req.Timeout = 8000;
                string body;
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream())) body = r.ReadToEnd();
                var tag = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)");
                pageUrl = Regex.Match(body, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+)\"").Groups[1].Value;
                if (!tag.Success || !Version.TryParse(tag.Groups[1].Value.Trim('.'), out latest)) { message = "The latest release has no version number."; return false; }
                return true;
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                message = hr != null && hr.StatusCode == HttpStatusCode.NotFound ? "No releases found for " + repo + "." : "Couldn't reach GitHub: " + ex.Message;
                return false;
            }
            catch (Exception ex) { message = "Update check failed: " + ex.Message; return false; }
        }
    }
}
