// Where things live: app data, the user's saved choices, and auto-detection of the
// Automation export folder, the BeamNG.drive install and its mods folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    static class AppPaths
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutomationToBeamNG");
        public static string SettingsFile { get { return Path.Combine(Dir, "settings.txt"); } }
        public static string IndexFile { get { return Path.Combine(Dir, "vehicle_index.txt"); } }
        public static string ThumbDir { get { return Path.Combine(Dir, "thumbs"); } }

        static string LocalAppData { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }

        public static bool IsBeamNG(string p) { return !string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, @"content\vehicles")); }
        public static bool IsFolder(string p) { return !string.IsNullOrEmpty(p) && Directory.Exists(p); }

        public static string DefaultAutomation { get { return Path.Combine(LocalAppData, @"AutomationGame\Saved\UserData\CarSaveExport"); } }

        public static string FindBeamNG()
        {
            try
            {
                string ini = Path.Combine(LocalAppData, @"BeamNG\BeamNG.drive.ini");
                if (File.Exists(ini))
                    foreach (var ln in File.ReadAllLines(ini))
                    {
                        var m = Regex.Match(ln, @"^\s*installPath\s*=\s*(.+)$");
                        if (m.Success && IsBeamNG(m.Groups[1].Value.Trim())) return m.Groups[1].Value.Trim();
                    }
                string vdf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Steam\steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string c = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\BeamNG.drive");
                        if (IsBeamNG(c)) return c;
                    }
            }
            catch (Exception) { }
            return "";
        }

        public static string FindMods()
        {
            foreach (var c in new[] { Path.Combine(LocalAppData, @"BeamNG\BeamNG.drive\current"), Path.Combine(LocalAppData, @"BeamNG.drive\latest") })
                if (Directory.Exists(c)) return Path.Combine(c, "mods");
            return Path.Combine(LocalAppData, @"BeamNG\BeamNG.drive\current\mods");
        }
    }

    class Settings
    {
        public string AutomationPath = "", BeamNGPath = "", ModsPath = "", LastEngine = "";
        public string Theme = "System";             // System / Light / Dark
        public string UpdateRepo = "";              // GitHub "owner/name" to check for new versions
        public string LastUpdateCheck = "";
        public bool SimulateBoost = true, MakeConfigs = true, UseEngineModel = true, FitToCar = true, Upgrades = true;
        public bool AutomationEngine = true;       // use an export's own engine (camsoEngine) when there is one
        public HashSet<string> CheckedCars = new HashSet<string> { "covet" };
        public Dictionary<string, string> BaseOverrides = new Dictionary<string, string>();   // car model -> stock engine part
        public Dictionary<string, double[]> Dyno = new Dictionary<string, double[]>();        // engine id -> [torque Nm, power hp]
        public Dictionary<string, string> EngineSound = new Dictionary<string, string>();     // engine id -> sound option name

        public bool IsComplete { get { return AppPaths.IsFolder(AutomationPath) && AppPaths.IsBeamNG(BeamNGPath) && !string.IsNullOrEmpty(ModsPath); } }

        public static string EngineId(EngineSpec s) { return string.IsNullOrEmpty(s.VariantUID) ? Path.GetFileName(s.File) : s.VariantUID; }

        static string Esc(string s) { return (s ?? "").Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D").Replace("\r", "").Replace("\n", ""); }
        static string Unesc(string s) { return s.Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%"); }

        static Dictionary<string, string> Map(string v)
        {
            var d = new Dictionary<string, string>();
            foreach (var pair in v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = pair.IndexOf('=');
                if (i > 0) d[Unesc(pair.Substring(0, i))] = Unesc(pair.Substring(i + 1));
            }
            return d;
        }

        static string Join(IEnumerable<KeyValuePair<string, string>> d) { return string.Join(";", d.OrderBy(x => x.Key).Select(x => Esc(x.Key) + "=" + Esc(x.Value))); }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(AppPaths.SettingsFile)) return s;
                foreach (var ln in File.ReadAllLines(AppPaths.SettingsFile, Encoding.UTF8))
                {
                    int i = ln.IndexOf('=');
                    if (i <= 0) continue;
                    string k = ln.Substring(0, i), v = ln.Substring(i + 1);
                    switch (k)
                    {
                        case "AutomationPath": s.AutomationPath = v; break;
                        case "BeamNGPath": s.BeamNGPath = v; break;
                        case "ModsPath": s.ModsPath = v; break;
                        case "LastEngine": s.LastEngine = v; break;
                        case "Theme": s.Theme = v; break;
                        case "UpdateRepo": s.UpdateRepo = v; break;
                        case "LastUpdateCheck": s.LastUpdateCheck = v; break;
                        case "SimulateBoost": s.SimulateBoost = v != "0"; break;
                        case "MakeConfigs": s.MakeConfigs = v != "0"; break;
                        case "UseEngineModel": s.UseEngineModel = v != "0"; break;
                        case "FitToCar": s.FitToCar = v != "0"; break;
                        case "Upgrades": s.Upgrades = v != "0"; break;
                        case "AutomationEngine": s.AutomationEngine = v != "0"; break;
                        case "CheckedCars": s.CheckedCars = new HashSet<string>(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)); break;
                        case "BaseOverrides": s.BaseOverrides = Map(v); break;
                        case "EngineSound": s.EngineSound = Map(v); break;
                        case "Dyno":
                            foreach (var kv in Map(v))
                            {
                                var nums = kv.Value.Split(',');
                                double t, p;
                                if (nums.Length == 2 && double.TryParse(nums[0], System.Globalization.NumberStyles.Float, Num.Inv, out t)
                                    && double.TryParse(nums[1], System.Globalization.NumberStyles.Float, Num.Inv, out p))
                                    s.Dyno[kv.Key] = new[] { t, p };
                            }
                            break;
                    }
                }
            }
            catch (Exception) { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Dir);
                File.WriteAllLines(AppPaths.SettingsFile, new[]
                {
                    "AutomationPath=" + AutomationPath, "BeamNGPath=" + BeamNGPath, "ModsPath=" + ModsPath, "LastEngine=" + LastEngine,
                    "Theme=" + Theme, "UpdateRepo=" + UpdateRepo, "LastUpdateCheck=" + LastUpdateCheck,
                    "SimulateBoost=" + (SimulateBoost ? "1" : "0"), "MakeConfigs=" + (MakeConfigs ? "1" : "0"), "UseEngineModel=" + (UseEngineModel ? "1" : "0"), "FitToCar=" + (FitToCar ? "1" : "0"), "Upgrades=" + (Upgrades ? "1" : "0"),
                    "AutomationEngine=" + (AutomationEngine ? "1" : "0"),
                    "CheckedCars=" + string.Join(",", CheckedCars.OrderBy(x => x)),
                    "BaseOverrides=" + Join(BaseOverrides), "EngineSound=" + Join(EngineSound),
                    "Dyno=" + Join(Dyno.Select(kv => new KeyValuePair<string, string>(kv.Key, Num.F(kv.Value[0], "0.##") + "," + Num.F(kv.Value[1], "0.##")))),
                }, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // Fill blanks with detected folders (used for the first-run setup suggestions).
        public void FillDetected()
        {
            if (!AppPaths.IsFolder(AutomationPath) && AppPaths.IsFolder(AppPaths.DefaultAutomation)) AutomationPath = AppPaths.DefaultAutomation;
            if (!AppPaths.IsBeamNG(BeamNGPath)) BeamNGPath = AppPaths.FindBeamNG();
            if (string.IsNullOrEmpty(ModsPath)) ModsPath = AppPaths.FindMods();
        }
    }
}
