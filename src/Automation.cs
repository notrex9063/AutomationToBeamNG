// Reading the results Automation computed for an engine (dyno curves, weight, idle) from its
// sandbox database, using the SQLite library that ships with Windows (winsqlite3.dll).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AutomationToBeamNG
{
    class AutomationResults
    {
        public double PeakPowerKW, PeakPowerRPM, PeakTorqueNm, PeakTorqueRPM, MaxRPM, PeakBoostBar, WeightKg, IdleRPM;
        // the engine designer's stats panel (NaN when the database doesn't have them)
        public double Smoothness = double.NaN, Noise = double.NaN, EfficiencyPct = double.NaN, Reliability = double.NaN,
                      CoolingRequired = double.NaN, Responsiveness = double.NaN, Octane = double.NaN;
        public double[] Rpm, Torque, Boost;   // full-load curves from idle to max rpm
        public string DbFile;
        public bool HasCurves { get { return Rpm != null && Torque != null && Rpm.Length > 3 && Rpm.Length == Torque.Length; } }
    }

    static class AutomationDb
    {
        const int SQLITE_ROW = 100, OPEN_READONLY = 1, OPEN_URI = 0x40;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] text, int n, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern double sqlite3_column_double(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close(IntPtr db);

        static readonly IntPtr Transient = new IntPtr(-1);
        static readonly Dictionary<string, AutomationResults> cache = new Dictionary<string, AutomationResults>();

        // Automation keeps its databases next to the CarSaveExport folder.
        public static string UserDataDir(string automationExportPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(automationExportPath))
                {
                    var p = Directory.GetParent(automationExportPath.TrimEnd('\\'));
                    if (p != null && Directory.GetFiles(p.FullName, "Sandbox_*.db").Length > 0) return p.FullName;
                }
            }
            catch (Exception) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AutomationGame\Saved\UserData");
        }

        public static void ClearCache() { lock (cache) cache.Clear(); }

        // Results for an engine variant, newest database first; null if Automation never computed it.
        public static AutomationResults Find(string variantUid, string automationExportPath)
        {
            if (string.IsNullOrEmpty(variantUid)) return null;
            lock (cache)
            {
                AutomationResults hit;
                if (cache.TryGetValue(variantUid, out hit)) return hit;
            }
            AutomationResults res = null;
            try
            {
                string dir = UserDataDir(automationExportPath);
                if (Directory.Exists(dir))
                    foreach (var f in new DirectoryInfo(dir).GetFiles("*.db", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc))
                    {
                        if (f.Name.StartsWith("Scenario", StringComparison.OrdinalIgnoreCase)) continue;
                        res = Query(f.FullName, variantUid);
                        if (res != null) break;
                    }
            }
            catch (DllNotFoundException) { res = null; }
            catch (EntryPointNotFoundException) { res = null; }
            catch (Exception) { res = null; }
            lock (cache) cache[variantUid] = res;
            return res;
        }

        static AutomationResults Query(string file, string uid)
        {
            IntPtr db;
            var uri = Encoding.UTF8.GetBytes("file:" + Uri.EscapeUriString(file.Replace('\\', '/')) + "?immutable=1\0");
            if (sqlite3_open_v2(uri, out db, OPEN_READONLY | OPEN_URI, IntPtr.Zero) != 0) { sqlite3_close(db); return null; }
            try
            {
                var r = new AutomationResults { DbFile = file };
                bool found = false;
                Run(db, "select PeakPower, PeakPowerRPM, PeakTorque, PeakTorqueRPM, MaxRPM, PeakBoost, Weight, IdleSpeed from EngineResults where UID = ?", uid, st =>
                {
                    found = true;
                    r.PeakPowerKW = sqlite3_column_double(st, 0); r.PeakPowerRPM = sqlite3_column_double(st, 1);
                    r.PeakTorqueNm = sqlite3_column_double(st, 2); r.PeakTorqueRPM = sqlite3_column_double(st, 3);
                    r.MaxRPM = sqlite3_column_double(st, 4); r.PeakBoostBar = sqlite3_column_double(st, 5);
                    r.WeightKg = sqlite3_column_double(st, 6); r.IdleRPM = sqlite3_column_double(st, 7);
                });
                if (!found || r.PeakTorqueNm <= 0) return null;
                // separate query: older databases may lack some of these columns, which only loses the stats
                Run(db, "select Smoothness, Noise, EconEff, ReliabilityPostEngineering, CoolingRequired, Responsiveness, RON from EngineResults where UID = ?", uid, st =>
                {
                    Func<int, double> col = i => sqlite3_column_type(st, i) == 5 ? double.NaN : sqlite3_column_double(st, i);   // 5 = NULL
                    r.Smoothness = col(0); r.Noise = col(1); r.EfficiencyPct = col(2); r.Reliability = col(3);
                    r.CoolingRequired = col(4); r.Responsiveness = col(5); r.Octane = col(6);
                    // 0 means "not worked out yet" in these columns, not a real score
                    if (r.Reliability <= 0) r.Reliability = double.NaN;
                    if (r.EfficiencyPct <= 0) r.EfficiencyPct = double.NaN;
                    if (r.Smoothness <= 0) r.Smoothness = double.NaN;
                    if (r.Noise <= 0) r.Noise = double.NaN;
                });
                Run(db, "select RPMCurve, TorqueCurve, BoostCurve from EngineCurves where UID = ?", uid, st =>
                {
                    r.Rpm = DecodeCurve(Blob(st, 0));
                    r.Torque = DecodeCurve(Blob(st, 1));
                    r.Boost = DecodeCurve(Blob(st, 2));
                    if (r.Boost != null && r.Rpm != null && r.Boost.Length != r.Rpm.Length) r.Boost = null;
                });
                return r;
            }
            catch (Exception) { return null; }
            finally { sqlite3_close(db); }
        }

        static void Run(IntPtr db, string sql, string arg, Action<IntPtr> row)
        {
            IntPtr st;
            if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out st, IntPtr.Zero) != 0) return;
            try
            {
                var a = Encoding.UTF8.GetBytes(arg);
                sqlite3_bind_text(st, 1, a, a.Length, Transient);
                if (sqlite3_step(st) == SQLITE_ROW) row(st);
            }
            finally { sqlite3_finalize(st); }
        }

        static byte[] Blob(IntPtr st, int col)
        {
            int n = sqlite3_column_bytes(st, col);
            IntPtr p = sqlite3_column_blob(st, col);
            if (n <= 0 || p == IntPtr.Zero) return null;
            var b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return b;
        }

        // Curves are a serialized Lua array: 10-byte header, then 'N' + double pairs (index, value).
        public static double[] DecodeCurve(byte[] b)
        {
            if (b == null || b.Length < 10) return null;
            var vals = new List<double>();
            int p = 10;
            while (p + 9 <= b.Length && b[p] == (byte)'N') { vals.Add(BitConverter.ToDouble(b, p + 1)); p += 9; }
            if (vals.Count < 2) return null;
            var keyed = new SortedDictionary<double, double>();
            for (int i = 0; i + 1 < vals.Count; i += 2) keyed[vals[i]] = vals[i + 1];
            return keyed.Values.ToArray();
        }
    }
}
