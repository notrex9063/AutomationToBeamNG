// Everything on the BeamNG side: finding cars and their stock engines in the game install,
// building an engine part (plus a ready-made config) on top of a stock one, checking the result
// and writing the mod zip; also listing/removing installed engines.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AutomationToBeamNG
{
    // jbeam is relaxed JSON: comments, optional commas. We edit it as text and only need
    // bracket matching that understands strings and comments.
    static class JBeam
    {
        public static int FindClose(string t, int open)
        {
            char o = t[open], c = o == '{' ? '}' : ']';
            int depth = 0;
            for (int i = open; i < t.Length; i++)
            {
                char ch = t[i];
                if (ch == '"') { i++; while (i < t.Length && t[i] != '"') { if (t[i] == '\\') i++; i++; } }
                else if (ch == '/' && i + 1 < t.Length && t[i + 1] == '/') { while (i < t.Length && t[i] != '\n') i++; }
                else if (ch == '/' && i + 1 < t.Length && t[i + 1] == '*') { int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal); if (e < 0) return -1; i = e + 1; }
                else if (ch == o) depth++;
                else if (ch == c) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        // [start, end] of the object/array following "key": , or null.
        public static int[] FindBlock(string t, string key)
        {
            var m = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*([\\[{])").Match(t);
            if (!m.Success) return null;
            int open = m.Groups[1].Index, close = FindClose(t, open);
            if (close < 0) throw new InvalidDataException("Unbalanced brackets after \"" + key + "\"");
            return new[] { open, close };
        }

        // Removes "key": {...} (and a trailing comma) wherever it appears as a block.
        public static string RemoveBlock(string t, string key)
        {
            var re = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*([\\[{])");
            Match m;
            while ((m = re.Match(t)).Success)
            {
                int close = FindClose(t, m.Groups[1].Index);
                if (close < 0) break;
                int end = close + 1;
                var tail = Regex.Match(t.Substring(end), "^[ \\t]*,?");
                t = t.Remove(m.Index, end + tail.Length - m.Index);
            }
            return t;
        }

        static readonly Regex PartKeyRe = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\\{");

        // Top-level parts of a jbeam file: key -> the part's {...} text.
        public static List<KeyValuePair<string, string>> TopLevelParts(string t)
        {
            var parts = new List<KeyValuePair<string, string>>();
            int i = t.IndexOf('{');
            if (i < 0) return parts;
            int end = FindClose(t, i);
            if (end < 0) return parts;
            int pos = i + 1;
            while (true)
            {
                var m = PartKeyRe.Match(t, pos);
                if (!m.Success || m.Index >= end) break;
                int open = m.Index + m.Length - 1, close = FindClose(t, open);
                if (close < 0) break;
                parts.Add(new KeyValuePair<string, string>(m.Groups[1].Value, t.Substring(open, close - open + 1)));
                pos = close + 1;
            }
            return parts;
        }

        public static string ReadEntry(ZipArchiveEntry e)
        {
            using (var r = new StreamReader(e.Open())) return r.ReadToEnd();
        }

        public static byte[] ReadBytes(ZipArchiveEntry e)
        {
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    // A lenient jbeam/JSON reader like BeamNG's own: commas are optional, trailing commas and
    // comments are fine. Objects -> Dictionary, arrays -> List, numbers -> double.
    class JBeamParser
    {
        readonly string s;
        int p;

        JBeamParser(string text) { s = text; }

        public static object Parse(string text)
        {
            var r = new JBeamParser(text);
            object v = r.Value();
            r.Ws();
            if (r.p < r.s.Length) throw r.Error("unexpected text after the end");
            return v;
        }

        Exception Error(string msg)
        {
            int line = 1;
            for (int i = 0; i < p && i < s.Length; i++) if (s[i] == '\n') line++;
            return new InvalidDataException(msg + " (line " + line + ")");
        }

        void Ws()
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (char.IsWhiteSpace(c) || c == ',') { p++; continue; }
                if (c == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; continue; }
                if (c == '/' && p + 1 < s.Length && s[p + 1] == '*')
                {
                    int e = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                    if (e < 0) throw Error("unclosed comment");
                    p = e + 2; continue;
                }
                break;
            }
        }

        object Value()
        {
            Ws();
            if (p >= s.Length) throw Error("unexpected end of file");
            char c = s[p];
            if (c == '{')
            {
                p++;
                var d = new Dictionary<string, object>();
                while (true)
                {
                    Ws();
                    if (p >= s.Length) throw Error("unclosed {");
                    if (s[p] == '}') { p++; return d; }
                    if (s[p] != '"') throw Error("expected a \"key\"");
                    string k = Str();
                    Ws();
                    if (p >= s.Length || s[p] != ':') throw Error("expected ':' after \"" + k + "\"");
                    p++;
                    d[k] = Value();
                }
            }
            if (c == '[')
            {
                p++;
                var l = new List<object>();
                while (true)
                {
                    Ws();
                    if (p >= s.Length) throw Error("unclosed [");
                    if (s[p] == ']') { p++; return l; }
                    l.Add(Value());
                }
            }
            if (c == '"') return Str();
            int st = p;
            while (p < s.Length && "{}[],:\"".IndexOf(s[p]) < 0 && !char.IsWhiteSpace(s[p]) && s[p] != '/') p++;
            string tok = s.Substring(st, p - st);
            if (tok == "true") return true;
            if (tok == "false") return false;
            if (tok == "null") return null;
            double d2;
            if (double.TryParse(tok, System.Globalization.NumberStyles.Float, Num.Inv, out d2)) return d2;
            throw Error("unexpected '" + (tok.Length > 0 ? tok : c.ToString()) + "'");
        }

        string Str()
        {
            p++;
            var sb = new StringBuilder();
            while (p < s.Length && s[p] != '"')
            {
                if (s[p] == '\\' && p + 1 < s.Length) { p++; char e = s[p]; sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e); }
                else sb.Append(s[p]);
                p++;
            }
            if (p >= s.Length) throw Error("unclosed string");
            p++;
            return sb.ToString();
        }
    }

    // A stock engine part we can build on: a combustion engine with its own torque table.
    class EngineCandidate
    {
        public string PartKey, SlotType, Name, Model, Zip, File;
        public double PeakTorque;
        public int Cylinders;
        public bool Diesel, WarnBoost;   // WarnBoost: a stock sub-part would add its own turbo/supercharger

        public string DisplayName { get { return Name != null && Name.Length > 6 ? Name : PartKey; } }

        public static EngineCandidate From(string key, string text)
        {
            if (text.IndexOf("\"combustionEngine\"", StringComparison.Ordinal) < 0) return null;
            var st = Regex.Match(text, "\"slotType\"\\s*:\\s*\"([^\"]+)\"");
            if (!st.Success) return null;
            int[] b, tb;
            try
            {
                b = JBeam.FindBlock(text, "mainEngine");
                if (b == null) return null;
                tb = JBeam.FindBlock(text.Substring(b[0], b[1] - b[0] + 1), "torque");
                if (tb == null) return null;
            }
            catch (InvalidDataException) { return null; }
            string me = text.Substring(b[0], b[1] - b[0] + 1);
            double peak = 0;
            foreach (Match m in Regex.Matches(me.Substring(tb[0], tb[1] - tb[0] + 1), @"\[\s*[\d.]+\s*,\s*([\d.]+)\s*\]"))
            {
                double v;
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, Num.Inv, out v)) peak = Math.Max(peak, v);
            }
            var cyl = Regex.Match(text, "\"fundamentalFrequencyCylinderCount\"\\s*:\\s*(\\d+)");
            return new EngineCandidate
            {
                PartKey = key,
                SlotType = st.Groups[1].Value,
                Name = Regex.Match(text, "\"name\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value,
                PeakTorque = peak,
                Cylinders = cyl.Success ? int.Parse(cyl.Groups[1].Value, Num.Inv) : 0,
                Diesel = Regex.IsMatch(text, "\"requiredEnergyType\"\\s*:\\s*\"diesel\"", RegexOptions.IgnoreCase),
            };
        }

        public string Describe()
        {
            var bits = new List<string>();
            if (PeakTorque > 0) bits.Add(Num.F(PeakTorque, "0") + " Nm stock");
            if (Diesel) bits.Add("diesel");
            if (WarnBoost) bits.Add("keeps stock turbo");
            return DisplayName + (bits.Count > 0 ? "  (" + string.Join(", ", bits) + ")" : "");
        }
    }

    class VehicleInfo
    {
        public string Model, Brand, Name, Zip, DefaultPc = "";
        public List<EngineCandidate> Engines = new List<EngineCandidate>();
        public string FullName { get { return (Brand + " " + Name).Trim(); } }
        public string ThumbFile { get { return Path.Combine(AppPaths.ThumbDir, Model + ".png"); } }

        // The candidates in the car's main engine slot (the one most stock engines use).
        public List<EngineCandidate> MainSlotEngines
        {
            get { return Engines.GroupBy(e => e.SlotType).OrderByDescending(g => g.Count()).First().ToList(); }
        }
    }

    // Every car in the install that has a combustion engine: its own, or a shared one from
    // common.zip that fits one of its slots. Cached until the game files change.
    static class VehicleIndex
    {
        static readonly HashSet<string> NotCars = new HashSet<string> { "common", "engine_props", "large_roller", "simple_traffic" };

        class ZipScan
        {
            public List<EngineCandidate> Engines = new List<EngineCandidate>();
            public Dictionary<string, HashSet<string>> SharedSlotsUsed = new Dictionary<string, HashSet<string>>();
            public Dictionary<string, string[]> Info = new Dictionary<string, string[]>();   // model -> [brand, name key, default pc]
            public HashSet<string> Models = new HashSet<string>();
        }

        static ZipScan ScanZip(string zipPath, ICollection<string> sharedTypes)
        {
            var r = new ZipScan();
            var allParts = new ModelParts();
            using (var z = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in z.Entries)
                {
                    var mm = Regex.Match(e.FullName, "^vehicles/([^/]+)/(.*)$");
                    if (!mm.Success) continue;
                    string model = mm.Groups[1].Value, rest = mm.Groups[2].Value;
                    if (rest == "info.json")
                    {
                        string j = JBeam.ReadEntry(e);
                        r.Info[model] = new[]
                        {
                            Regex.Match(j, "\"Brand\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value,
                            Regex.Match(j, "\"Name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value,
                            Regex.Match(j, "\"default_pc\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value,
                        };
                        continue;
                    }
                    if (Regex.IsMatch(rest, "^default\\.(jpg|png)$", RegexOptions.IgnoreCase) && !NotCars.Contains(model)) { SaveThumb(e, model); continue; }
                    if (!e.FullName.EndsWith(".jbeam", StringComparison.OrdinalIgnoreCase)) continue;
                    string t = JBeam.ReadEntry(e);
                    r.Models.Add(model);
                    foreach (string st in sharedTypes)
                    {
                        if (t.IndexOf("\"" + st + "\"", StringComparison.Ordinal) < 0) continue;
                        HashSet<string> used;
                        if (!r.SharedSlotsUsed.TryGetValue(model, out used)) r.SharedSlotsUsed[model] = used = new HashSet<string>();
                        used.Add(st);
                    }
                    var parts = JBeam.TopLevelParts(t);
                    foreach (var p in parts) allParts.Add(p.Key, p.Value, e.FullName);
                    if (t.IndexOf("\"combustionEngine\"", StringComparison.Ordinal) < 0) continue;
                    foreach (var p in parts)
                    {
                        var c = EngineCandidate.From(p.Key, p.Value);
                        if (c == null) continue;
                        c.Model = model; c.Zip = Path.GetFileName(zipPath); c.File = e.FullName;
                        r.Engines.Add(c);
                    }
                }
            }
            // pre-check which bases would have to keep a boosted stock sub-part
            foreach (var c in r.Engines)
            {
                try
                {
                    var notes = new List<string>();
                    Converter.UpdateSubSlots(allParts.ByKey[c.PartKey], allParts, notes);
                    c.WarnBoost = notes.Any(n => n.StartsWith("WARNING", StringComparison.Ordinal));
                }
                catch (Exception) { }
            }
            return r;
        }

        static void SaveThumb(ZipArchiveEntry e, string model)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ThumbDir);
                using (var ms = new MemoryStream(JBeam.ReadBytes(e)))
                using (var src = Image.FromStream(ms))
                using (var dst = new Bitmap(192, 108))
                {
                    using (var g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        float k = Math.Max(192f / src.Width, 108f / src.Height);
                        float w = src.Width * k, h = src.Height * k;
                        g.DrawImage(src, (192 - w) / 2, (108 - h) / 2, w, h);
                    }
                    dst.Save(Path.Combine(AppPaths.ThumbDir, model + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception) { }
        }

        static Dictionary<string, string> Translations(string beamng)
        {
            var map = new Dictionary<string, string>();
            string f = Path.Combine(beamng, @"locales\translations\en-US\vehiclesGenerated.translation.json");
            if (!System.IO.File.Exists(f)) return map;
            foreach (Match m in Regex.Matches(System.IO.File.ReadAllText(f), "\"(vehiclesData\\.[^\"]+\\.Name)\"\\s*:\\s*\"([^\"]*)\""))
                map[m.Groups[1].Value] = m.Groups[2].Value;
            return map;
        }

        static string Stamp(string beamng, FileInfo[] zips)
        {
            var sb = new StringBuilder("index-v5|" + beamng);
            foreach (var f in zips) sb.Append('|').Append(f.Name).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks);
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        public static List<VehicleInfo> Load(string beamng, Action<string> progress, bool forceRescan)
        {
            var zips = new DirectoryInfo(Path.Combine(beamng, @"content\vehicles")).GetFiles("*.zip").OrderBy(f => f.Name).ToArray();
            string stamp = Stamp(beamng, zips);
            if (!forceRescan)
            {
                var cached = ReadCache(stamp);
                if (cached != null) return cached;
            }

            var tr = Translations(beamng);
            var shared = new List<EngineCandidate>();
            var common = zips.FirstOrDefault(f => f.Name.Equals("common.zip", StringComparison.OrdinalIgnoreCase));
            if (common != null)
            {
                if (progress != null) progress("Scanning shared engines...");
                shared = ScanZip(common.FullName, new string[0]).Engines;
            }
            var sharedTypes = new HashSet<string>(shared.Select(e => e.SlotType));

            var result = new List<VehicleInfo>();
            var others = zips.Where(f => f != common).ToArray();
            for (int n = 0; n < others.Length; n++)
            {
                var f = others[n];
                if (progress != null) progress("Scanning cars... " + (n + 1) + "/" + others.Length + " (" + Path.GetFileNameWithoutExtension(f.Name) + ")");
                ZipScan r;
                try { r = ScanZip(f.FullName, sharedTypes); }
                catch (Exception) { continue; }
                foreach (string model in r.Models)
                {
                    if (NotCars.Contains(model)) continue;
                    var v = new VehicleInfo { Model = model, Zip = f.Name, Brand = "", Name = model };
                    v.Engines.AddRange(r.Engines.Where(e => e.Model == model));
                    HashSet<string> used;
                    if (r.SharedSlotsUsed.TryGetValue(model, out used))
                        v.Engines.AddRange(shared.Where(e => used.Contains(e.SlotType)));
                    if (v.Engines.Count == 0) continue;
                    string[] info;
                    if (r.Info.TryGetValue(model, out info))
                    {
                        v.Brand = info[0];
                        v.DefaultPc = info[2];
                        string key = info[1], name;
                        if (tr.TryGetValue(key, out name)) v.Name = name;
                        else if (key.Length > 0 && !key.StartsWith("vehiclesData.", StringComparison.Ordinal)) v.Name = key;
                    }
                    result.Add(v);
                }
            }
            result = result.OrderBy(v => v.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            WriteCache(stamp, result);
            return result;
        }

        // ---- cache: one tab-separated line per car / engine ----
        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        static void WriteCache(string stamp, List<VehicleInfo> list)
        {
            try
            {
                var sb = new StringBuilder(stamp + "\n");
                foreach (var v in list)
                {
                    sb.Append("V\t").Append(Clean(v.Model)).Append('\t').Append(Clean(v.Brand)).Append('\t').Append(Clean(v.Name)).Append('\t')
                      .Append(Clean(v.Zip)).Append('\t').Append(Clean(v.DefaultPc)).Append('\n');
                    foreach (var e in v.Engines)
                        sb.Append("E\t").Append(Clean(e.PartKey)).Append('\t').Append(Clean(e.SlotType)).Append('\t').Append(Clean(e.Name)).Append('\t')
                          .Append(Num.F(e.PeakTorque, "0.###")).Append('\t').Append(e.Cylinders).Append('\t').Append(e.Diesel ? 1 : 0).Append('\t')
                          .Append(Clean(e.Model)).Append('\t').Append(Clean(e.Zip)).Append('\t').Append(Clean(e.File)).Append('\t').Append(e.WarnBoost ? 1 : 0).Append('\n');
                }
                Directory.CreateDirectory(AppPaths.Dir);
                System.IO.File.WriteAllText(AppPaths.IndexFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static List<VehicleInfo> ReadCache(string stamp)
        {
            try
            {
                if (!System.IO.File.Exists(AppPaths.IndexFile)) return null;
                var lines = System.IO.File.ReadAllLines(AppPaths.IndexFile, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != stamp) return null;
                var list = new List<VehicleInfo>();
                VehicleInfo cur = null;
                foreach (var ln in lines.Skip(1))
                {
                    var c = ln.Split('\t');
                    if (c[0] == "V" && c.Length >= 6) { cur = new VehicleInfo { Model = c[1], Brand = c[2], Name = c[3], Zip = c[4], DefaultPc = c[5] }; list.Add(cur); }
                    else if (c[0] == "E" && c.Length >= 11 && cur != null)
                        cur.Engines.Add(new EngineCandidate
                        {
                            PartKey = c[1], SlotType = c[2], Name = c[3], PeakTorque = double.Parse(c[4], Num.Inv),
                            Cylinders = int.Parse(c[5], Num.Inv), Diesel = c[6] == "1", Model = c[7], Zip = c[8], File = c[9], WarnBoost = c[10] == "1",
                        });
                }
                return list;
            }
            catch (Exception) { return null; }
        }
    }

    // All parts a car can see for its engine: its own folder, plus the shared engine family
    // folders in common.zip that its engines come from.
    class ModelParts
    {
        public Dictionary<string, string> ByKey = new Dictionary<string, string>();
        public Dictionary<string, List<string>> ByType = new Dictionary<string, List<string>>();
        public Dictionary<string, string> FileOf = new Dictionary<string, string>();

        public void Add(string key, string text, string file)
        {
            ByKey[key] = text;
            FileOf[key] = file;
            var st = Regex.Match(text, "\"slotType\"\\s*:\\s*\"([^\"]+)\"");
            if (!st.Success) return;
            List<string> l;
            if (!ByType.TryGetValue(st.Groups[1].Value, out l)) ByType[st.Groups[1].Value] = l = new List<string>();
            if (!l.Contains(key)) l.Add(key);
        }

        public static ModelParts Load(string beamng, VehicleInfo veh)
        {
            var sources = new List<string[]> { new[] { veh.Zip, "vehicles/" + veh.Model + "/" } };
            foreach (var e in veh.Engines)
            {
                if (e.Zip == veh.Zip) continue;
                string d = e.File.Substring(0, e.File.LastIndexOf('/') + 1);
                if (!sources.Any(s => s[0] == e.Zip && s[1] == d)) sources.Add(new[] { e.Zip, d });
            }
            var mp = new ModelParts();
            foreach (var src in sources)
            {
                using (var z = ZipFile.OpenRead(Path.Combine(beamng, @"content\vehicles", src[0])))
                {
                    foreach (var e in z.Entries)
                    {
                        if (!e.FullName.StartsWith(src[1], StringComparison.Ordinal) || !e.FullName.EndsWith(".jbeam", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var p in JBeam.TopLevelParts(JBeam.ReadEntry(e))) mp.Add(p.Key, p.Value, e.FullName);
                    }
                }
            }
            return mp;
        }
    }

    class ConvertOptions
    {
        public bool SimulateBoost = true, MakeConfigs = true;
        public Sounds.Option Sound;                                                    // null = automatic
        public ExportedEngine Model;                                                   // Automation's 3D engine model, or null for the stock look
        public bool BuildModel;                                                        // no export: build the model from Automation's own parts
        public ExportedEngine EngineFrom;                                              // export whose own engine (camsoEngine) to use, or null for ours
        public Dictionary<string, string> BaseOverrides = new Dictionary<string, string>();   // car model -> stock engine part
        public double ManualTorque, ManualPowerKW;                                    // remembered for "Update"
        public Func<string, bool> Unlock;                                              // asks a running BeamNG to let go of a zip
        public bool Upgrades = true;                                                   // tuning sliders + optional upgrade parts
    }

    class ConvertResult
    {
        public string ZipPath;
        public int Built, Failed, Configs, FilesChecked;
        public string EngineModelFrom;   // set when Automation's 3D engine model was fitted
        public string LiveMessage;       // what a running BeamNG did with it (live updates)
        public Dictionary<string, string[]> Parts = new Dictionary<string, string[]>();   // car model -> [our part, engine slot type]
        public List<string> Warnings = new List<string>();
        public List<string> Problems = new List<string>();
    }

    static class Converter
    {
        // Parts that add their own forced induction or torque changes on top of our curve.
        static readonly Regex PowerAdder = new Regex("\"turbocharger\"\\s*:\\s*\\{|\"supercharger\"\\s*:\\s*\\{|\"torqueModIntake\"|\"torqueModMult\"");
        static readonly Regex BoostRe = new Regex("\"turbocharger\"\\s*:\\s*\\{|\"supercharger\"\\s*:\\s*\\{");
        static readonly Regex Slots2Re = new Regex("^(\\s*\\[\\s*\"([^\"]*)\"\\s*,\\s*\\[([^\\]]*)\\]\\s*,\\s*\\[[^\\]]*\\]\\s*,\\s*\")([^\"]*)(\".*)$");
        static readonly Regex Slots1Re = new Regex("^(\\s*\\[\\s*\"([^\"]*)\"\\s*,\\s*\")([^\"]*)(\".*)$");
        public const string MetaFolder = "automation_to_beamng/";

        // A copy of a stock sub-part (e.g. a truck's turbo) without its own boost or torque changes,
        // so the car keeps the part's nodes, meshes and exhaust while our engine sets the power.
        static string BoostFreeCopy(string text)
        {
            foreach (var k in new[] { "turbocharger", "supercharger", "torqueModIntake", "torqueModMult", "torqueModExhaust" }) text = JBeam.RemoveBlock(text, k);
            text = Regex.Replace(text, "(?m)^[ \\t]*\"(turbocharger|supercharger)\"\\s*:\\s*\"[^\"]*\"[ \\t]*,?[ \\t]*\\r?\\n", "");
            var ib = JBeam.FindBlock(text, "information");
            if (ib != null)
            {
                string info = text.Substring(ib[0], ib[1] - ib[0] + 1);
                info = Regex.Replace(info, "(\"name\"\\s*:\\s*\")([^\"]*)\"", m => m.Groups[1].Value + m.Groups[2].Value + " (boost-free, for Automation engine)\"");
                text = text.Substring(0, ib[0]) + info + text.Substring(ib[1] + 1);
            }
            return text;
        }

        // Rework the engine's sub-slots: drop the ECU (it would impose the stock rev limiter) and
        // swap defaults that bolt on a turbo/supercharger or torque mods for a neutral sibling part,
        // or, when there is none, for a boost-free copy (added to `copies`; null = just checking).
        public static string UpdateSubSlots(string part, ModelParts parts, List<string> notes, List<KeyValuePair<string, string>> copies = null, string copySuffix = "x")
        {
            bool v2 = true;
            var b = JBeam.FindBlock(part, "slots2");
            if (b == null) { v2 = false; b = JBeam.FindBlock(part, "slots"); }
            if (b == null) return part;
            string sl = part.Substring(b[0], b[1] - b[0] + 1);
            var outLines = new List<string>();
            foreach (string ln in sl.Split('\n'))
            {
                var m = (v2 ? Slots2Re : Slots1Re).Match(ln);
                if (!m.Success) { outLines.Add(ln); continue; }
                string slot = m.Groups[2].Value;
                if (slot == "type" || slot == "name") { outLines.Add(ln); continue; }
                string pre, def, post;
                List<string> types;
                if (v2)
                {
                    types = Regex.Matches(m.Groups[3].Value, "\"([^\"]+)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
                    pre = m.Groups[1].Value; def = m.Groups[4].Value; post = m.Groups[5].Value;
                }
                else
                {
                    types = new List<string> { slot };
                    pre = m.Groups[1].Value; def = m.Groups[3].Value; post = m.Groups[4].Value;
                }
                if (slot.IndexOf("ecu", StringComparison.OrdinalIgnoreCase) >= 0) { notes.Add("removed stock ECU slot '" + slot + "'"); continue; }
                string defText;
                if (def.Length > 0 && parts.ByKey.TryGetValue(def, out defText) && PowerAdder.IsMatch(defText))
                {
                    bool boosted = BoostRe.IsMatch(defText);
                    var cands = types.Where(parts.ByType.ContainsKey).SelectMany(ty => parts.ByType[ty]).ToList();
                    string alt = cands.FirstOrDefault(k => !PowerAdder.IsMatch(parts.ByKey[k]));
                    if (alt == null && boosted) alt = cands.FirstOrDefault(k => !BoostRe.IsMatch(parts.ByKey[k]));
                    if (alt != null) { notes.Add("'" + slot + "' default " + def + " -> " + alt + " (no extra boost/torque mods)"); outLines.Add(pre + alt + post); continue; }
                    string copyKey = def + "_automation_" + copySuffix;
                    if (copies != null && !copies.Any(c => c.Key == copyKey)) copies.Add(new KeyValuePair<string, string>(copyKey, BoostFreeCopy(defText)));
                    notes.Add("'" + slot + "' stock " + def + " -> boost-free copy (keeps its parts, drops its own " + (boosted ? "turbo/supercharger" : "torque changes") + ")");
                    outLines.Add(pre + copyKey + post);
                    continue;
                }
                outLines.Add(ln);
            }
            return part.Substring(0, b[0]) + string.Join("\n", outLines) + part.Substring(b[1] + 1);
        }

        // Petrol bases are preferred (a diesel base would burn diesel and smoke), as are bases whose
        // stock sub-parts can all be swapped for ones that add no boost of their own.
        public static EngineCandidate SelectBase(VehicleInfo veh, EngineSpec s, double peakTorque, string overrideKey)
        {
            var pool = veh.MainSlotEngines;
            if (!string.IsNullOrEmpty(overrideKey))
            {
                var o = veh.Engines.FirstOrDefault(e => e.PartKey == overrideKey);
                if (o != null) return o;
            }
            if (pool.Any(e => !e.Diesel)) pool = pool.Where(e => !e.Diesel).ToList();
            EngineCandidate best = null;
            double bestScore = double.MaxValue;
            foreach (var e in pool)
            {
                double score = Math.Abs(Math.Log(Math.Max(1.0, e.PeakTorque) / Math.Max(1.0, peakTorque)));
                if (e.Cylinders > 0) score += 0.3 * Math.Abs(e.Cylinders - s.Cylinders);
                if (e.WarnBoost) score += 3;
                if (score < bestScore) { bestScore = score; best = e; }
            }
            if (best == null) throw new InvalidOperationException("no usable stock engine found");
            return best;
        }

        // Scale the engine's node weights so the block weighs what Automation says.
        static string ScaleWeight(string part, double targetKg, List<string> notes)
        {
            var b = JBeam.FindBlock(part, "nodes");
            if (b == null || targetKg <= 0) return part;
            string nodes = part.Substring(b[0], b[1] - b[0] + 1);
            double stock = 0, cur = 25;
            try
            {
                foreach (var row in (List<object>)JBeamParser.Parse(nodes))
                {
                    var mod = row as Dictionary<string, object>;
                    if (mod != null) { object w; if (mod.TryGetValue("nodeWeight", out w) && w is double) cur = (double)w; continue; }
                    var l = row as List<object>;
                    if (l == null || l.Count < 4 || !(l[1] is double)) continue;   // header row
                    double nw = cur;
                    var opt = l[l.Count - 1] as Dictionary<string, object>;
                    object ow;
                    if (opt != null && opt.TryGetValue("nodeWeight", out ow) && ow is double) nw = (double)ow;
                    stock += nw;
                }
            }
            catch (Exception) { return part; }
            if (stock <= 0) return part;
            // Only ever add weight: the stock node weights are tuned to the stiffness of the engine's
            // beams, and lighter nodes on those beams make BeamNG's physics explode ("instability").
            if (targetKg <= stock)
            {
                notes.Add("engine weight kept at stock " + Num.F(stock, "0") + " kg (Automation: " + Num.F(targetKg, "0") + " kg; lighter would make the physics unstable)");
                return part;
            }
            double k = Math.Min(targetKg / stock, 4);
            nodes = Regex.Replace(nodes, "(\"nodeWeight\"\\s*:\\s*)([\\d.]+)", m =>
                m.Groups[1].Value + Num.F(Math.Max(0.5, double.Parse(m.Groups[2].Value, Num.Inv) * k), "0.##"));
            notes.Add("engine weight " + Num.F(stock, "0") + " kg -> " + Num.F(stock * k, "0") + " kg (Automation: " + Num.F(targetKg, "0") + " kg)");
            return part.Substring(0, b[0]) + nodes + part.Substring(b[1] + 1);
        }

        // Engine block nodes of a part: their box and the node groups they belong to.
        static double[] EngineNodes(string part, HashSet<string> groups)
        {
            var b = JBeam.FindBlock(part, "nodes");
            if (b == null) return null;
            List<object> rows;
            try { rows = JBeamParser.Parse(part.Substring(b[0], b[1] - b[0] + 1)) as List<object>; } catch (Exception) { return null; }
            if (rows == null) return null;
            var box = EngineExports.EngineBox(rows);
            object curGroup = null, curEngine = null;
            foreach (var row in rows)
            {
                var mod = row as Dictionary<string, object>;
                if (mod != null) { if (mod.ContainsKey("group")) curGroup = mod["group"]; if (mod.ContainsKey("engineGroup")) curEngine = mod["engineGroup"]; continue; }
                var l = row as List<object>;
                if (l == null || l.Count < 4 || !(l[1] is double)) continue;
                object g = curGroup, eg = curEngine;
                var opt = l.OfType<Dictionary<string, object>>().FirstOrDefault();
                if (opt != null) { if (opt.ContainsKey("group")) g = opt["group"]; if (opt.ContainsKey("engineGroup")) eg = opt["engineGroup"]; }
                if (!EngineExports.IsBlockGroup(eg)) continue;
                if (g is string && ((string)g).Length > 0) groups.Add((string)g);
                if (g is List<object>) foreach (var s in ((List<object>)g).OfType<string>()) if (s.Length > 0) groups.Add(s);
            }
            return box;
        }

        // Swap the stock engine meshes for Automation's model, placed in the engine block's box.
        static string FitEngineModel(string part, EngineMeshFiles mesh, List<string> notes)
        {
            var groups = new HashSet<string>();
            var box = EngineNodes(part, groups);
            if (box == null || groups.Count == 0) { notes.Add("engine model skipped: couldn't find the engine block nodes"); return part; }
            double dx = box[3] - box[0], dy = box[4] - box[1];
            bool alongY = dy > dx;   // the car's crankshaft direction (lengthwise when the box is longer along the car)
            double yaw = mesh.CrankAlongY == alongY ? 0 : 90;
            double cx = (box[0] + box[3]) / 2, cy = (box[1] + box[4]) / 2, cz = (box[2] + box[5]) / 2;

            var fb = JBeam.FindBlock(part, "flexbodies");
            var removed = new List<string>();
            var rowRe = new Regex("^\\s*\\[\\s*\"([^\"]+)\"\\s*,\\s*\\[([^\\]]*)\\]");
            string row = "        [\"" + mesh.MeshName + "\", [" + string.Join(", ", groups.Select(g => "\"" + g + "\"")) + "], [], {\"pos\":{\"x\":" + Num.F(cx, "0.0###")
                + ", \"y\":" + Num.F(cy, "0.0###") + ", \"z\":" + Num.F(cz, "0.0###") + "}, \"rot\":{\"x\":0, \"y\":0, \"z\":" + Num.F(yaw, "0") + "}, \"scale\":{\"x\":1, \"y\":1, \"z\":1}}],\r\n";
            if (fb == null)
            {
                var mk = Regex.Match(part, "\"nodes\"\\s*:");
                int lineStart = part.LastIndexOf('\n', mk.Index) + 1;
                part = part.Insert(lineStart, "    \"flexbodies\": [\r\n        [\"mesh\", \"[group]:\", \"nonFlexMaterials\"],\r\n" + row + "    ],\r\n");
            }
            else
            {
                string block = part.Substring(fb[0], fb[1] - fb[0] + 1);
                var lines = block.Split('\n').Where(ln =>
                {
                    var m = rowRe.Match(ln);
                    if (!m.Success || m.Groups[1].Value == "mesh") return true;
                    var gs = Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToList();
                    if (gs.Count > 0 && gs.All(groups.Contains)) { removed.Add(m.Groups[1].Value); return false; }
                    return true;
                }).ToList();
                block = string.Join("\n", lines);
                int close = block.LastIndexOf(']');
                block = block.Substring(0, close) + row + "    " + block.Substring(close);
                part = part.Substring(0, fb[0]) + block + part.Substring(fb[1] + 1);
            }
            notes.Add("engine model: Automation's own, " + Num.F(mesh.Size[0], "0.00") + " x " + Num.F(mesh.Size[1], "0.00") + " x " + Num.F(mesh.Size[2], "0.00")
                + " m" + (yaw != 0 ? ", turned 90° to fit the engine bay" : "") + (removed.Count > 0 ? "; hid stock " + string.Join(", ", removed) : ""));
            double bayLong = Math.Max(dx, dy), meshLong = Math.Max(mesh.Size[0], mesh.Size[1]);
            if (meshLong > bayLong + 0.6 || mesh.Size[2] > (box[5] - box[2]) + 0.5)
                notes.Add("WARNING: Automation's engine model is much bigger than this car's engine (" + Num.F(meshLong, "0.0") + " m vs " + Num.F(bayLong, "0.0") + " m long) and may poke through the body");
            return part;
        }

        public static string BuildPart(ModelParts parts, string baseKey, EngineData data, ForcedInduction fi, Sounds.Option sound, string partKey,
                                       TorqueMods mods, List<string> notes, EngineMeshFiles mesh = null, bool upgrades = false, CamsoEngine camso = null)
        {
            var s = data.Spec;
            var peaks = data.Peaks;
            string part = parts.ByKey[baseKey];
            if (camso != null) fi = null;     // Automation's own turbo/supercharger comes with its engine
            var up = upgrades ? Upgrades.Build(data, fi, partKey, part, parts, camso) : null;

            // stock forced induction on the base engine itself would stack on top of ours
            part = JBeam.RemoveBlock(part, "turbocharger");
            part = JBeam.RemoveBlock(part, "supercharger");

            // --- mainEngine: drop the stock curve and the values we replace, insert ours
            var b = JBeam.FindBlock(part, "mainEngine");
            string me = part.Substring(b[0], b[1] - b[0] + 1);
            var tb = JBeam.FindBlock(me, "torque");
            var tm = Regex.Match(me.Substring(tb[1] + 1), "^[ \\t]*,?");
            int tStart = me.LastIndexOf("\"torque\"", tb[0], StringComparison.Ordinal);
            me = me.Remove(tStart, tb[1] + 1 + tm.Length - tStart);
            var real = data.Real;
            // maxPhysicalRPM: where over-rev damage starts; the stock engine's (e.g. the Moonhawk's 5500) would wreck a
            // higher-revving engine, so it goes and the engine works it out from our maxRPM (x1.05)
            const string keys = "idleRPM|maxRPM|maxPhysicalRPM|hasRevLimiter|revLimiterRPM|revLimiterType|revLimiterCutTime|revLimiterRPMDrop|revLimiterMaxRPMDrop|inertia|friction|dynamicFriction|engineBrakeTorque|maxTorqueRating|maxOverTorqueDamage|turbocharger|supercharger";
            me = Regex.Replace(me, "(?m)^[ \\t]*\"(" + keys + ")\"\\s*:[^\\r\\n]*\\r?\\n", "");
            if (real != null && !double.IsNaN(real.Smoothness)) me = Regex.Replace(me, "(?m)^[ \\t]*\"idleRPMRoughness\"\\s*:[^\\r\\n]*\\r?\\n", "");
            if (real != null && !double.IsNaN(real.Reliability))
                me = Regex.Replace(me, "(?m)^[ \\t]*\"(headGasketDamageThreshold|pistonRingDamageThreshold|connectingRodDamageThreshold)\"\\s*:[^\\r\\n]*\\r?\\n", "");
            foreach (var k in new[] { "torqueModIntake", "torqueModExhaust", "torqueModMult" }) me = JBeam.RemoveBlock(me, k);

            var fr = Friction.For(data);
            var beamCurve = fr.TorqueTable(data.Curve, fi, mods);
            double d = s.DisplacementL;
            double crankMul = Regex.IsMatch(s.Crank, "Light", RegexOptions.IgnoreCase) ? 0.85 : Regex.IsMatch(s.Crank, "Heavy", RegexOptions.IgnoreCase) ? 1.15 : 1.0;
            double inertia = Math.Max(0.03, (0.015 + 0.035 * d) * crankMul);   // very low inertia makes the drivetrain jittery
            var ins = new StringBuilder();
            if (camso != null)
            {
                me = Regex.Replace(me, "(?m)^[ \\t]*\"(burnEfficiency|frictionTorque)\"\\s*:\\s*\\[", "\"_unused_$1\":[");
                // the stock engine's own lines for everything Automation's engine sets (later duplicates would win)
                me = Regex.Replace(me, "(?m)^[ \\t]*\"(" + string.Join("|", camso.Values.Keys.Select(Regex.Escape)) + ")\"\\s*:[^\\r\\n\\[{]*\\r?\\n", "");
                ins.Append("\r\n        // ---- Automation's own engine (camsoEngine) from the export \"").Append(camso.Source.Replace("\"", "'")).Append("\" ----\r\n");
                ins.Append(camso.MainEngineText(null));
                EngineCharacter.AppendDurability(ins, real);
                ins.Append("        // ---- stock values kept below ----\r\n");
                me = me.Insert(1, ins.ToString());
                part = part.Substring(0, b[0]) + me + part.Substring(b[1] + 1);
                part = Regex.Replace(part, "\\[\\s*\"combustionEngine\"\\s*,\\s*\"mainEngine\"", "[\"camsoEngine\", \"mainEngine\"");
                if (camso.ChargerKey != null)
                {
                    var mk = Regex.Match(part, "\"mainEngine\"\\s*:");
                    int lineStart = part.LastIndexOf('\n', mk.Index) + 1;
                    string vars = camso.VariableRows.Count == 0 ? "" : "    \"variables\": [\r\n        [\"name\", \"type\", \"unit\", \"category\", \"default\", \"min\", \"max\", \"title\", \"description\"],\r\n        "
                        + string.Join(",\r\n        ", camso.VariableRows) + ",\r\n    ],\r\n";
                    if (vars.Length > 0 && JBeam.FindBlock(part, "variables") != null)
                    {
                        var vb = JBeam.FindBlock(part, "variables");
                        part = part.Insert(vb[1], "    " + string.Join(",\r\n        ", camso.VariableRows) + ",\r\n    ");
                        vars = "";
                    }
                    part = part.Insert(lineStart, vars + "    \"" + camso.ChargerKey + "\": " + camso.ChargerBlock + ",\r\n");
                }
                notes.Add("engine: Automation's own (camsoEngine, throttle map, inertia " + camso.Values["inertia"] + (camso.ChargerKey != null ? ", Automation's " + camso.ChargerKey : "") + ")");
                // always: the radiator (and an automatic's converter) need sizing even with nothing else to do
                part = AddController(part, CamsoControllerRow(mods, up != null, camso, ConverterCoef(parts.ByKey[baseKey], data)));
                goto subParts;
            }
            ins.Append("\r\n        // ---- generated from Automation: ").Append(s.Name)
               .Append(data.FromAutomation ? " (Automation's own dyno curve)" : " (estimated curve)")
               .Append(fi != null ? ", boost simulated by BeamNG" : "").Append(" ----\r\n");
            ins.Append("        \"torque\":[\r\n            [\"rpm\", \"torque\"],\r\n");
            foreach (var p in beamCurve) ins.Append("            [").Append(Num.F(p[0], "0")).Append(", ").Append(Num.F(p[1], "0.00")).Append("],\r\n");
            ins.Append("        ],\r\n");
            Action<string, string> kv = (k, v) => ins.Append("        \"").Append(k).Append("\":").Append(v).Append(",\r\n");
            kv("idleRPM", Num.F(data.IdleRpm, "0"));
            kv("maxRPM", Num.F(data.RpmLimit + 500, "0"));
            kv("hasRevLimiter", "true");
            kv("revLimiterRPM", Num.F(data.RpmLimit, "0"));
            kv("revLimiterType", "\"timeBased\"");
            kv("revLimiterCutTime", "0.1");
            kv("inertia", Num.F(inertia, "0.0000"));
            kv("friction", Num.F(fr.Static, "0.000"));
            kv("dynamicFriction", Num.F(fr.Dynamic, "0.000000"));
            kv("engineBrakeTorque", Num.F(fr.EngineBrake, "0.0"));
            kv("maxTorqueRating", Num.F(peaks.TorqueNm * 1.5, "0"));
            kv("maxOverTorqueDamage", Num.F(peaks.TorqueNm * 2.0, "0"));
            if (fi != null) kv(fi.IsTurbo ? "turbocharger" : "supercharger", fi.IsTurbo ? "\"turbocharger\"" : "\"supercharger\"");
            if (real != null && !double.IsNaN(real.Smoothness)) kv("idleRPMRoughness", Num.F(EngineCharacter.IdleRoughness(real.Smoothness), "0.0"));
            EngineCharacter.AppendDurability(ins, real);
            ins.Append("        // ---- stock values kept below ----\r\n");
            // fuel use: the stock engine's burn efficiency scaled to Automation's Efficiency
            if (real != null && !double.IsNaN(real.EfficiencyPct) && real.EfficiencyPct > 0)
            {
                var be = JBeam.FindBlock(me, "burnEfficiency");
                if (be != null && me[be[0]] == '[')
                {
                    double ek = EngineCharacter.EfficiencyFactor(real.EfficiencyPct);
                    string tbl = Regex.Replace(me.Substring(be[0], be[1] - be[0] + 1), "\\[\\s*([0-9.]+)\\s*,\\s*([0-9.]+)\\s*\\]",
                        m => "[" + m.Groups[1].Value + ", " + Num.F(Math.Min(0.6, double.Parse(m.Groups[2].Value, Num.Inv) * ek), "0.###") + "]");
                    me = me.Substring(0, be[0]) + tbl + me.Substring(be[1] + 1);
                }
            }
            me = me.Insert(1, ins.ToString());
            part = part.Substring(0, b[0]) + me + part.Substring(b[1] + 1);

            // --- our turbo/supercharger, as a part-level section next to mainEngine
            if (fi != null)
            {
                var mk = Regex.Match(part, "\"mainEngine\"\\s*:");
                int lineStart = part.LastIndexOf('\n', mk.Index) + 1;
                part = part.Insert(lineStart, fi.JBeamSection(up != null ? Upgrades.Boost : null));
            }

            // --- controller that resizes the intake/exhaust parts' torque changes to this engine
            // and applies the ECU's Power slider
            bool scaled = mods != null && mods.Scaled;
            double conv = ConverterCoef(parts.ByKey[baseKey], data);
            double fly = FlywheelCoef(parts.ByKey[baseKey], inertia);
            if (scaled || up != null || Math.Abs(conv - 1) > 1e-3 || Math.Abs(fly - 1) > 1e-3)
            {
                string row = "[\"" + TorqueScaleController.FileName + "\", {\"engineName\":\"mainEngine\""
                    + (up != null ? ", \"powerTrim\":\"" + Upgrades.Power + "\"" : "")
                    + (Math.Abs(fly - 1) > 1e-3 ? ", \"flywheelCoef\":" + Num.F(fly, "0.####") : "")
                    + (Math.Abs(conv - 1) > 1e-3 ? ", \"converterCoef\":" + Num.F(conv, "0.####") : "")
                    + (scaled ? ", \"rpmRatio\":" + Num.F(mods.RpmRatio, "0.0000")
                        + ", \"scaleCurve\":[" + string.Join(", ", mods.ScaleCurve.Select(p => "[" + Num.F(p[0], "0") + ", " + Num.F(p[1], "0.0000") + "]")) + "]" : "")
                    + "}]";
                part = AddController(part, row);
            }

            // --- sub-slots, weight
            subParts:
            var copies = new List<KeyValuePair<string, string>>();
            part = UpdateSubSlots(part, parts, notes, copies, partKey);
            if (up != null)
            {
                part = Upgrades.AddSlots(part, up.Slots);
                copies.AddRange(up.Parts);
            }
            if (data.Real != null && data.Real.WeightKg > 0) part = ScaleWeight(part, data.Real.WeightKg, notes);
            if (mesh != null) part = FitEngineModel(part, mesh, notes);

            // --- information, sound, shift points
            var ib = JBeam.FindBlock(part, "information");
            if (ib != null)
            {
                string info = "{\r\n        \"authors\":\"Automation export (AutomationToBeamNG)\",\r\n        \"name\":\"" + PartLabel(s).Replace("\"", "'").Replace("\\", "/")
                    + "\",\r\n        \"value\":8000,\r\n    }";
                part = part.Substring(0, ib[0]) + info + part.Substring(ib[1] + 1);
            }
            var snd = sound ?? Sounds.Auto(s, data.RpmLimit);
            int n = 0;
            part = Regex.Replace(part, "\"sampleName\"\\s*:\\s*\"[^\"]*\"", m => "\"sampleName\": \"" + (n++ == 0 ? snd.Engine : snd.Exhaust) + "\"");
            part = Regex.Replace(part, "\"fundamentalFrequencyCylinderCount\"\\s*:\\s*\\d+", "\"fundamentalFrequencyCylinderCount\":" + s.Cylinders);
            part = Regex.Replace(part, "\"highShiftUpRPM\"\\s*:\\s*[\\d.]+", "\"highShiftUpRPM\":" + Num.F(data.RpmLimit - 300, "0"));
            string loud = "";
            if (data.Real != null && !double.IsNaN(data.Real.Noise))
            {
                // Automation's Noise: louder engines get a louder exhaust
                double db = EngineCharacter.NoiseGainDb(data.Real.Noise);
                var sx = JBeam.FindBlock(part, "soundConfigExhaust");
                if (sx != null && Math.Abs(db) >= 0.25)
                {
                    string blk = part.Substring(sx[0], sx[1] - sx[0] + 1);
                    var g = Regex.Match(blk, "\"mainGain\"\\s*:\\s*(-?[0-9.]+)");
                    blk = g.Success ? blk.Substring(0, g.Groups[1].Index) + Num.F(double.Parse(g.Groups[1].Value, Num.Inv) + db, "0.0#") + blk.Substring(g.Groups[1].Index + g.Groups[1].Length)
                                    : blk.Insert(1, "\r\n        \"mainGain\":" + Num.F(db, "0.0#") + ",");
                    part = part.Substring(0, sx[0]) + blk + part.Substring(sx[1] + 1);
                    loud = ", exhaust " + (db > 0 ? "+" : "") + Num.F(db, "0.0") + " dB (Automation noise " + Num.F(data.Real.Noise, "0") + ")";
                }
            }
            notes.Add("sound: " + snd.Name + loud);
            if (data.Real != null && !double.IsNaN(data.Real.Smoothness))
                notes.Add("character: smoothness " + Num.F(data.Real.Smoothness, "0") + " -> idle roughness " + Num.F(camso != null && camso.Values.ContainsKey("idleRPMRoughness") ? double.Parse(camso.Values["idleRPMRoughness"], Num.Inv) : EngineCharacter.IdleRoughness(data.Real.Smoothness), "0")
                    + (double.IsNaN(data.Real.Reliability) ? "" : ", reliability " + Num.F(data.Real.Reliability, "0") + " -> durability x" + Num.F(EngineCharacter.DurabilityFactor(data.Real.Reliability), "0.00"))
                    + (double.IsNaN(data.Real.EfficiencyPct) ? "" : ", efficiency " + Num.F(data.Real.EfficiencyPct, "0.0") + "%" + (camso != null ? " (Automation's own fuel map)" : " -> fuel burn x" + Num.F(1 / EngineCharacter.EfficiencyFactor(data.Real.EfficiencyPct), "0.00"))));

            var sb = new StringBuilder();
            sb.Append("// Generated by AutomationToBeamNG from an Automation .engine export.\r\n// Base: ").Append(parts.FileOf[baseKey]).Append(" / ").Append(baseKey)
              .Append("\r\n{\r\n\"").Append(partKey).Append("\": ").Append(part).Append(",\r\n");
            foreach (var c in copies) sb.Append("\"").Append(c.Key).Append("\": ").Append(c.Value).Append(",\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        // Automatic gearboxes: torque converter pull scaled to this engine (see TorqueScaleController):
        // our torque / stock torque, over (our rev limit / stock rev limit)^2, so it stalls at the same
        // point of this engine's rev range as it did for the stock engine.
        static double ConverterCoef(string stockPartText, EngineData data)
        {
            try
            {
                var sp = JBeamParser.Parse(stockPartText) as Dictionary<string, object>;
                var sme = sp != null && sp.ContainsKey("mainEngine") ? sp["mainEngine"] as Dictionary<string, object> : null;
                var rows = sme != null && sme.ContainsKey("torque") ? BeamNGSim.Rows(sme["torque"]) : null;
                if (rows == null || rows.Count < 2) return 1;
                double stockPeak = rows.Max(p => p[1]);
                if (stockPeak <= 0) return 1;
                double rr = data.RpmLimit / Math.Max(1, StockLimit(sp, data.RpmLimit));
                return Num.Clamp(data.Peaks.TorqueNm / stockPeak / (rr * rr), 0.02, 4);
            }
            catch (Exception) { return 1; }
        }

        // Converted engines: the car's flywheel (clutch additionalEngineInertia) in proportion to our
        // engine's inertia vs the stock engine's.
        static double FlywheelCoef(string stockPartText, double ourInertia)
        {
            try
            {
                var sp = JBeamParser.Parse(stockPartText) as Dictionary<string, object>;
                var sme = sp != null && sp.ContainsKey("mainEngine") ? sp["mainEngine"] as Dictionary<string, object> : null;
                object o;
                if (sme == null || !sme.TryGetValue("inertia", out o) || !(o is double) || (double)o <= 0) return 1;
                return Num.Clamp(ourInertia / (double)o, 0.2, 3);
            }
            catch (Exception) { return 1; }
        }

        static string AddController(string part, string row)
        {
            var cb = JBeam.FindBlock(part, "controller");
            if (cb != null && part[cb[0]] == '[')
            {
                string body = part.Substring(cb[0] + 1, cb[1] - cb[0] - 1).TrimEnd();
                string sep = body.EndsWith(",") || body.Length == 0 ? "" : ",";
                return part.Substring(0, cb[0] + 1) + body + sep + "\r\n        " + row + ",\r\n    " + part.Substring(cb[1]);
            }
            var mk = Regex.Match(part, "\"mainEngine\"\\s*:");
            int lineStart = part.LastIndexOf('\n', mk.Index) + 1;
            return part.Insert(lineStart, "    \"controller\": [\r\n        [\"fileName\"],\r\n        " + row + ",\r\n    ],\r\n");
        }

        // Controller settings for Automation's own engine: the default parts' change (baseAdd, baseMult)
        // is already in its map, so only differences from them are applied.
        static string CamsoControllerRow(TorqueMods mods, bool power, CamsoEngine camso, double converterCoef)
        {
            Func<double, string> f4 = x => Num.F(x, "0.####");
            // Automation's engine inertia already includes the flywheel, so the car's goes
            var sb = new StringBuilder("[\"" + TorqueScaleController.FileName + "\", {\"engineName\":\"mainEngine\", \"camso\":true, \"flywheelCoef\":0");
            if (Math.Abs(converterCoef - 1) > 1e-3) sb.Append(", \"converterCoef\":").Append(f4(converterCoef));
            if (camso.RadiatorCoef > 0) sb.Append(", \"radiatorCoef\":").Append(Num.F(camso.RadiatorCoef, "0.#"));
            if (camso.FanAirSpeed > 0) sb.Append(", \"radiatorFanAirSpeed\":").Append(f4(camso.FanAirSpeed));
            if (power) sb.Append(", \"powerTrim\":\"").Append(Upgrades.Power).Append("\"");
            if (mods != null && mods.Scaled)
            {
                sb.Append(", \"rpmRatio\":").Append(Num.F(mods.RpmRatio, "0.0000"));
                sb.Append(", \"scaleCurve\":[").Append(string.Join(", ", mods.ScaleCurve.Select(p => "[" + Num.F(p[0], "0") + ", " + f4(p[1]) + "]"))).Append("]");
                sb.Append(", \"baseAdd\":[").Append(string.Join(", ", camso.Gross.Select(p => "[" + Num.F(p[0], "0") + ", " + f4(mods.Add(p[0])) + "]"))).Append("]");
            }
            if (mods != null && mods.MultCurve != null)
                sb.Append(", \"baseMult\":[").Append(string.Join(", ", camso.Gross.Select(p => "[" + Num.F(p[0], "0") + ", " + f4(mods.Mult(p[0])) + "]"))).Append("]");
            return sb.Append("}]").ToString();
        }

        public static string PartLabel(EngineSpec s)
        {
            return s.Name + " " + s.SizeText.Replace(" ", "") + " " + s.LayoutShort + " (Automation)";
        }

        public static string SafeName(EngineSpec s)
        {
            string n = Regex.Replace(s.Name, "[^A-Za-z0-9]+", "_").Trim('_').ToLowerInvariant();
            return n.Length > 0 ? n : "engine";
        }

        // The car's default config: which part sits in each slot, plus its info file and picture.
        class DefaultConfig
        {
            public Dictionary<string, object> Pc, Parts;
            public string InfoText, MainPart;
            public byte[] Thumb;

            public static DefaultConfig Load(string beamng, VehicleInfo veh)
            {
                if (string.IsNullOrEmpty(veh.DefaultPc)) return null;
                string dir = "vehicles/" + veh.Model + "/";
                var c = new DefaultConfig();
                using (var z = ZipFile.OpenRead(Path.Combine(beamng, @"content\vehicles", veh.Zip)))
                {
                    var pc = z.GetEntry(dir + veh.DefaultPc + ".pc");
                    if (pc == null) return null;
                    c.Pc = (Dictionary<string, object>)JBeamParser.Parse(JBeam.ReadEntry(pc));
                    var info = z.GetEntry(dir + "info_" + veh.DefaultPc + ".json");
                    if (info != null) c.InfoText = JBeam.ReadEntry(info);
                    var img = z.GetEntry(dir + veh.DefaultPc + ".jpg") ?? z.GetEntry(dir + veh.DefaultPc + ".png") ?? z.GetEntry(dir + "default.jpg");
                    if (img != null) c.Thumb = JBeam.ReadBytes(img);
                }
                // format 2+: {"format":2, "parts":{slot:part}}; the older format is the slot map itself
                c.Parts = c.Pc.ContainsKey("parts") ? c.Pc["parts"] as Dictionary<string, object> : c.Pc.ContainsKey("format") ? null : c.Pc;
                object mp;
                c.MainPart = c.Pc.TryGetValue("mainPartName", out mp) && mp is string ? (string)mp : veh.Model;
                return c.Parts == null ? null : c;
            }
        }

        static List<object[]> SlotRows(Dictionary<string, object> part)
        {
            // [slot name, allowed types, default part]
            var list = new List<object[]>();
            object so;
            bool v2 = part.TryGetValue("slots2", out so);
            if (!v2 && !part.TryGetValue("slots", out so)) return list;
            foreach (var row in ((so as List<object>) ?? new List<object>()).OfType<List<object>>().Skip(1))
            {
                if (row.Count < (v2 ? 4 : 2) || !(row[0] is string)) continue;
                var types = v2 && row[1] is List<object> ? ((List<object>)row[1]).OfType<string>().ToList() : new List<string> { (string)row[0] };
                list.Add(new object[] { row[0], types, row[v2 ? 3 : 1] as string ?? "" });
            }
            return list;
        }

        // Walks the parts that will actually be fitted (the default config, with our engine and its
        // sub-parts in place) and collects their torque/friction modifiers on the engine.
        static TorqueMods FittedMods(ModelParts parts, VehicleInfo veh, DefaultConfig cfg, string engineSlotType, string jb, string partKey)
        {
            var mods = new TorqueMods();
            var overlay = (Dictionary<string, object>)JBeamParser.Parse(jb);   // our engine + boost-free copies
            var cache = new Dictionary<string, Dictionary<string, object>>();
            Func<string, Dictionary<string, object>> get = key =>
            {
                Dictionary<string, object> p;
                if (cache.TryGetValue(key, out p)) return p;
                object o;
                string text;
                if (overlay.TryGetValue(key, out o)) p = o as Dictionary<string, object>;
                else if (parts.ByKey.TryGetValue(key, out text)) { try { p = JBeamParser.Parse(text) as Dictionary<string, object>; } catch (Exception) { p = null; } }
                cache[key] = p;
                return p;
            };
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(cfg != null ? cfg.MainPart : veh.Model);
            while (stack.Count > 0)
            {
                string key = stack.Pop();
                if (!seen.Add(key)) continue;
                var p = get(key);
                if (p == null) continue;
                object meObj;
                if (key != partKey && p.TryGetValue("mainEngine", out meObj) && meObj is Dictionary<string, object>)
                {
                    var me = (Dictionary<string, object>)meObj;
                    object v;
                    if (me.TryGetValue("torqueModIntake", out v)) { mods.Intake = BeamNGSim.Rows(v); mods.Sources.Add(key + " (intake torque)"); }
                    if (me.TryGetValue("torqueModExhaust", out v)) { mods.Exhaust = BeamNGSim.Rows(v); mods.Sources.Add(key + " (exhaust torque)"); }
                    if (me.TryGetValue("torqueModMult", out v)) { mods.MultCurve = BeamNGSim.Rows(v); mods.Sources.Add(key + " (torque multiplier)"); }
                    if (me.TryGetValue("$*friction", out v) && v is double) { mods.FrictionMul *= (double)v; mods.Sources.Add(key + " (friction)"); }
                    if (me.TryGetValue("$*dynamicFriction", out v) && v is double) { mods.DynamicMul *= (double)v; mods.Sources.Add(key + " (friction)"); }
                    if (me.TryGetValue("$+friction", out v) && v is double) { mods.FrictionAdd += (double)v; mods.Sources.Add(key + " (friction)"); }
                    if (me.TryGetValue("$+dynamicFriction", out v) && v is double) { mods.DynamicAdd += (double)v; mods.Sources.Add(key + " (friction)"); }
                }
                bool ours = overlay.ContainsKey(key);
                foreach (var row in SlotRows(p))
                {
                    string name = (string)row[0], chosen = (string)row[2];
                    var types = (List<string>)row[1];
                    if (name == engineSlotType || types.Contains(engineSlotType)) chosen = partKey;
                    else if (!ours && cfg != null && cfg.Parts.ContainsKey(name)) chosen = cfg.Parts[name] as string ?? "";
                    if (!string.IsNullOrEmpty(chosen) && chosen != "none") stack.Push(chosen);
                }
            }
            return mods;
        }

        // ---- ready-made vehicle config: the car's default config with our engine fitted ----
        static void AddConfig(DefaultConfig cfg, VehicleInfo veh, EngineData data, string partText, string partKey, string safe,
                              List<KeyValuePair<string, byte[]>> files, List<string> notes)
        {
            if (cfg == null) { notes.Add("no usable default config to copy, skipped the ready-made config"); return; }
            string dir = "vehicles/" + veh.Model + "/";
            string infoText = cfg.InfoText;
            byte[] thumb = cfg.Thumb;
            var js = new JavaScriptSerializer();
            var pcObj = cfg.Pc;
            var partsObj = cfg.Parts;
            var engineKeys = new HashSet<string>(veh.Engines.Select(e => e.PartKey));
            string slotKey = partsObj.Keys.FirstOrDefault(k => partsObj[k] is string && engineKeys.Contains((string)partsObj[k]))
                             ?? veh.MainSlotEngines[0].SlotType;
            partsObj[slotKey] = partKey;

            // our engine's sub-slot defaults win over whatever the old engine's config said
            var root = (Dictionary<string, object>)JBeamParser.Parse(partText.Substring(partText.IndexOf('{')));
            var part = (Dictionary<string, object>)root[partKey];
            object slotsObj;
            if (part.TryGetValue("slots2", out slotsObj) || part.TryGetValue("slots", out slotsObj))
            {
                bool v2 = part.ContainsKey("slots2");
                foreach (var row in ((List<object>)slotsObj).OfType<List<object>>().Skip(1))
                {
                    if (row.Count < (v2 ? 4 : 2) || !(row[0] is string)) continue;
                    var def = row[v2 ? 3 : 1] as string;
                    if (def != null) partsObj[(string)row[0]] = def;
                }
            }
            string cfgName = "automation_" + safe;
            files.Add(new KeyValuePair<string, byte[]>(dir + cfgName + ".pc", Encoding.UTF8.GetBytes(js.Serialize(pcObj))));

            var pk = data.Peaks;
            Dictionary<string, object> info2;
            try { info2 = infoText != null ? (Dictionary<string, object>)JBeamParser.Parse(infoText) : new Dictionary<string, object>(); }
            catch (Exception) { info2 = new Dictionary<string, object>(); }
            foreach (var k in new[] { "0-100 km/h", "0-100 mph", "0-60 mph", "100-0 km/h", "60-0 mph", "60-100 mph", "Braking G", "Drag Times", "Performance Data",
                                      "Top Speed", "Off-Road Score", "Weight/Power", "Population", "vehicleSelectorSubGroup", "isAuxiliary" })
                info2.Remove(k);
            info2["Configuration"] = data.Spec.Name + " (Automation)";
            info2["Description"] = "Stock " + veh.FullName + " with the " + data.Spec.Name + " engine from Automation: " + data.Spec.LayoutShort + " " + data.Spec.SizeText
                + ", " + Num.F(pk.PowerHP, "0") + " hp, " + Num.F(pk.TorqueNm, "0") + " Nm.";
            info2["Config Type"] = "Custom";
            info2["Power"] = Math.Round(pk.PowerHP);
            info2["Torque"] = Math.Round(pk.TorqueNm);
            info2["PowerPeakRPM"] = Num.F(pk.PowerRPM, "0");
            info2["TorquePeakRPM"] = Num.F(pk.TorqueRPM, "0");
            info2["Induction Type"] = data.IsBoosted ? (data.Spec.IsTurbo ? "Turbo" : "Supercharger") : "NA";
            info2["Propulsion"] = "ICE";
            files.Add(new KeyValuePair<string, byte[]>(dir + "info_" + cfgName + ".json", Encoding.UTF8.GetBytes(js.Serialize(info2))));
            if (thumb != null) files.Add(new KeyValuePair<string, byte[]>(dir + cfgName + ".jpg", thumb));
        }

        // The extracted model: valid COLLADA with geometry, every material defined, every texture present.
        static List<string> CheckMesh(EngineMeshFiles mesh)
        {
            var problems = new List<string>();
            var dae = mesh.Files.FirstOrDefault(f => f.Key.EndsWith(".dae", StringComparison.OrdinalIgnoreCase));
            var mats = mesh.Files.FirstOrDefault(f => f.Key.EndsWith(".materials.json", StringComparison.OrdinalIgnoreCase));
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(Encoding.UTF8.GetString(dae.Value));
                if (doc.GetElementsByTagName("triangles").Count == 0) problems.Add("the model has no triangles");
            }
            catch (Exception ex) { problems.Add("the model file is broken (" + ex.Message + ")"); }
            try
            {
                var m = JBeamParser.Parse(Encoding.UTF8.GetString(mats.Value)) as Dictionary<string, object>;
                var missing = mesh.Materials.Where(x => m == null || !m.ContainsKey(x)).ToList();
                if (missing.Count > 0) problems.Add(missing.Count + " materials missing");
                var have = new HashSet<string>(mesh.Files.Select(f => f.Key.Substring(f.Key.LastIndexOf('/') + 1)), StringComparer.OrdinalIgnoreCase);
                var refs = Regex.Matches(Encoding.UTF8.GetString(mats.Value), "\"([^\"/\\\\]+\\.(dds|png|jpg|jpeg|tga))\"", RegexOptions.IgnoreCase).Cast<Match>().Select(x => x.Groups[1].Value).Distinct();
                int lost = refs.Count(r => !have.Contains(r));
                if (lost > 0) problems.Add(lost + " textures missing from the export");
            }
            catch (Exception ex) { problems.Add("materials file is broken (" + ex.Message + ")"); }
            return problems;
        }

        static double StockLimit(Dictionary<string, object> stockPart, double fallback)
        {
            var me = stockPart != null && stockPart.ContainsKey("mainEngine") ? stockPart["mainEngine"] as Dictionary<string, object> : null;
            object o;
            if (me != null && me.TryGetValue("revLimiterRPM", out o) && o is double) return (double)o;
            if (me != null && me.TryGetValue("maxRPM", out o) && o is double) return (double)o;
            return fallback;
        }

        // Intake/exhaust parts add Nm sized for the stock engine. Our controller turns that into the same
        // share of our torque (before boost, which multiplies it, and before friction) at the same point
        // of the rev range, so removing an exhaust changes power like it does on the stock engine.
        // ours: our engine's torque the mods are a share of (default: our pre-boost table; Automation's
        // own engine: its full-throttle map row)
        static void ScaleMods(TorqueMods mods, Dictionary<string, object> stockPart, EngineData data, ForcedInduction fi, List<double[]> ours = null)
        {
            if (mods.Intake == null && mods.Exhaust == null) return;
            var sme = stockPart != null && stockPart.ContainsKey("mainEngine") ? stockPart["mainEngine"] as Dictionary<string, object> : null;
            var stockTable = sme != null && sme.ContainsKey("torque") ? BeamNGSim.Rows(sme["torque"]) : new List<double[]>();
            if (stockTable.Count < 2) return;
            double stockPeak = stockTable.Max(p => p[1]);
            mods.RpmRatio = Math.Max(0.1, Math.Min(10, StockLimit(stockPart, data.RpmLimit) / Math.Max(1, data.RpmLimit)));
            if (ours == null) ours = Friction.For(data).TorqueTable(data.Curve, fi, null);
            mods.ScaleCurve = ours.Select(p => new[] { p[0],
                Math.Max(0.02, Math.Min(20, p[1] / Math.Max(0.15 * stockPeak, BeamNGSim.Lerp(stockTable, p[0] * mods.RpmRatio)))) }).ToList();
            if (mods.ScaleCurve[0][0] > 0) mods.ScaleCurve.Insert(0, new[] { 0.0, mods.ScaleCurve[0][1] });
        }

        // ---- checks: read everything back the way BeamNG would ----
        static List<string> Validate(VehicleInfo veh, string partKey, List<KeyValuePair<string, byte[]>> files, bool hasFi, EngineData data, TorqueMods mods,
                                     Dictionary<string, object> stockPart, List<string> notes, CamsoEngine camso = null)
        {
            var problems = new List<string>();
            foreach (var f in files)
            {
                string name = f.Key.Substring(f.Key.LastIndexOf('/') + 1);
                if (name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    string lua = Encoding.UTF8.GetString(f.Value);
                    if (name.StartsWith("camso", StringComparison.OrdinalIgnoreCase)) { if (!lua.TrimEnd().EndsWith("return M")) problems.Add(name + ": Automation's engine script is incomplete"); }
                    else if (!lua.Contains("M.init = init") || !lua.TrimEnd().EndsWith("return M")) problems.Add(name + ": controller script is incomplete");
                    continue;
                }
                object root;
                try { root = JBeamParser.Parse(Encoding.UTF8.GetString(f.Value)); }
                catch (Exception ex) { problems.Add(name + ": can't be read - " + ex.Message); continue; }
                var obj = root as Dictionary<string, object>;
                if (obj == null) { problems.Add(name + ": not an object"); continue; }
                if (name.EndsWith(".jbeam", StringComparison.OrdinalIgnoreCase))
                {
                    object po;
                    var part = obj.TryGetValue(partKey, out po) ? po as Dictionary<string, object> : null;
                    if (part == null) { problems.Add(name + ": engine part missing"); continue; }
                    if (!(part.ContainsKey("slotType") && part["slotType"] is string)) problems.Add(name + ": no slot type");

                    // tuning sliders: check the engine the way it comes with every slider at its default
                    object ecuObj;
                    Dictionary<string, double> vars = null;
                    if (obj.TryGetValue(partKey + "_ecu_stock", out ecuObj) && ecuObj is Dictionary<string, object>)
                    {
                        vars = JBeamVars.Defaults((Dictionary<string, object>)ecuObj);
                        try
                        {
                            JBeamVars.Resolve(part, vars);
                            var ecuMe = ((Dictionary<string, object>)ecuObj).ContainsKey("mainEngine") ? ((Dictionary<string, object>)ecuObj)["mainEngine"] : null;
                            JBeamVars.Resolve(ecuMe, vars);
                            var em = ecuMe as Dictionary<string, object>;
                            if (em == null || !(em["revLimiterRPM"] is double) || Math.Abs((double)em["revLimiterRPM"] - data.RpmLimit) > 1)
                                problems.Add(name + ": the default ECU doesn't use Automation's rev limit");
                        }
                        catch (Exception ex) { problems.Add(name + ": a tuning slider can't be worked out (" + ex.Message + ")"); }
                    }
                    var upgradeChecks = new List<string>();
                    foreach (var kv in obj.Where(x => x.Key != partKey && x.Key.StartsWith(partKey + "_", StringComparison.Ordinal)))
                    {
                        var up = kv.Value as Dictionary<string, object>;
                        if (up == null || !(up.ContainsKey("slotType") && up["slotType"] is string)) { problems.Add(name + ": upgrade part " + kv.Key + " has no slot type"); continue; }
                        if (!kv.Key.Contains("_turbokit_")) continue;
                        // what each turbo kit does to the stock-tuned engine
                        try
                        {
                            var kitVars = new Dictionary<string, double>(vars ?? new Dictionary<string, double>());
                            foreach (var dv in JBeamVars.Defaults(up)) kitVars[dv.Key] = dv.Value;
                            var kitSec = up["turbocharger"];
                            JBeamVars.Resolve(kitSec, kitVars);
                            var merged = new Dictionary<string, object>(part);
                            var mergedMe = new Dictionary<string, object>((Dictionary<string, object>)part["mainEngine"]);
                            mergedMe["turbocharger"] = "turbocharger";
                            merged["mainEngine"] = mergedMe;
                            merged["turbocharger"] = kitSec;
                            var kp = EngineModel.GetPeaks(BeamNGSim.Curve(merged, mods), data.RpmLimit);
                            upgradeChecks.Add(kv.Key.Substring(kv.Key.LastIndexOf('_') + 1) + ": " + Num.F(kp.PowerHP, "#,0") + " hp");
                        }
                        catch (Exception ex) { problems.Add(name + ": turbo kit " + kv.Key + " can't be checked (" + ex.Message + ")"); }
                    }
                    if (upgradeChecks.Count > 0) notes.Add("turbo kits (stage 1/2/3): " + string.Join(", ", upgradeChecks));
                    var me = part.ContainsKey("mainEngine") ? part["mainEngine"] as Dictionary<string, object> : null;
                    if (me == null) { problems.Add(name + ": no mainEngine"); continue; }
                    // over-rev damage starts at maxPhysicalRPM (default maxRPM x 1.05): never below the rev limiter
                    {
                        object o;
                        double mx = me.TryGetValue("maxRPM", out o) && o is double ? (double)o : 0;
                        double phys = me.TryGetValue("maxPhysicalRPM", out o) && o is double ? (double)o : mx * 1.05;
                        if (phys > 0 && phys < data.RpmLimit * 1.02)
                            problems.Add(name + ": over-rev damage would start at " + Num.F(phys, "0") + " rpm, below the " + Num.F(data.RpmLimit, "0") + " rpm rev limit");
                    }
                    if (camso != null)
                    {
                        // Automation's map: its full-throttle row minus friction is Automation's dyno curve
                        string text = Encoding.UTF8.GetString(f.Value);
                        var tb2 = JBeam.FindBlock(text, "torque");
                        var fb2 = JBeam.FindBlock(text, "frictionTorque");
                        var pw = part.ContainsKey("powertrain") ? part["powertrain"] as List<object> : null;
                        if (pw == null || !pw.OfType<List<object>>().Any(r => r.Count > 1 && (r[0] as string) == "camsoEngine" && (r[1] as string) == "mainEngine"))
                            problems.Add(name + ": the engine isn't set to Automation's camsoEngine");
                        if (tb2 == null || fb2 == null) { problems.Add(name + ": Automation's torque map is missing"); continue; }
                        var net = CamsoEngine.NetCurve(text.Substring(tb2[0], tb2[1] - tb2[0] + 1), text.Substring(fb2[0], fb2[1] - fb2[0] + 1));
                        if (net.Count < 6) { problems.Add(name + ": Automation's torque map is broken"); continue; }
                        var shownC = EngineModel.GetPeaks(net, data.RpmLimit);
                        notes.Add("BeamNG will show " + Num.F(shownC.PowerHP, "#,0") + " hp / " + Num.F(shownC.TorqueNm, "#,0.#") + " Nm (Automation's own engine at full throttle)");
                        if (camso.ChargerKey != null && !part.ContainsKey(camso.ChargerKey)) problems.Add(name + ": Automation's " + camso.ChargerKey + " section is missing");
                        // part swaps: the controller's change with the exhaust taken off, vs the stock engine's
                        if (mods != null && mods.Scaled && mods.Exhaust != null)
                        {
                            var bare = EngineModel.GetPeaks(camso.NetWithParts(mods.WithoutExhaust(), mods, 1), data.RpmLimit);
                            double change = bare.PowerKW / Math.Max(0.1, shownC.PowerKW) - 1;
                            double stockChange = double.NaN;
                            try
                            {
                                var sm = mods.Unscaled();
                                double rl = StockLimit(stockPart, data.RpmLimit);
                                var sOn = EngineModel.GetPeaks(BeamNGSim.Curve(stockPart, sm), rl);
                                var sOff = EngineModel.GetPeaks(BeamNGSim.Curve(stockPart, sm.WithoutExhaust()), rl);
                                stockChange = sOff.PowerKW / Math.Max(0.1, sOn.PowerKW) - 1;
                            }
                            catch (Exception) { stockChange = double.NaN; }
                            notes.Add("with the exhaust removed: " + Num.F(bare.PowerHP, "#,0") + " hp (" + (change >= 0 ? "+" : "") + Num.F(change * 100, "0") + "%"
                                + (double.IsNaN(stockChange) ? "" : "; stock engine " + (stockChange >= 0 ? "+" : "") + Num.F(stockChange * 100, "0") + "%") + ")");
                            double allowed = double.IsNaN(stockChange) ? 0.6 : Math.Abs(stockChange) + 0.25;
                            if (Math.Abs(change) > allowed) problems.Add(name + ": removing the exhaust would change power by " + Num.F(change * 100, "0") + "%");
                        }
                        if (((mods != null && (mods.Scaled || mods.MultCurve != null)) || vars != null))
                        {
                            var ctl = part.ContainsKey("controller") ? part["controller"] as List<object> : null;
                            if (ctl == null || !ctl.OfType<List<object>>().Any(r => r.Count > 0 && (r[0] as string) == TorqueScaleController.FileName))
                                problems.Add(name + ": torque-scale controller isn't listed in the part");
                        }
                        if (!(me.ContainsKey("revLimiterRPM") && me["revLimiterRPM"] is double)) problems.Add(name + ": no rev limiter");
                        continue;
                    }
                    var tq = me.ContainsKey("torque") ? me["torque"] as List<object> : null;
                    var rows = tq == null ? new List<List<object>>() : tq.OfType<List<object>>().Skip(1).ToList();
                    if (rows.Count < 6 || rows.Any(r => r.Count < 2 || !(r[0] is double) || !(r[1] is double))) problems.Add(name + ": torque table is broken");
                    else for (int i = 1; i < rows.Count; i++) if ((double)rows[i][0] <= (double)rows[i - 1][0]) { problems.Add(name + ": torque table rpm not increasing"); break; }
                    if (!(me.ContainsKey("revLimiterRPM") && me["revLimiterRPM"] is double)) problems.Add(name + ": no rev limiter");
                    if (hasFi && !(part.ContainsKey("turbocharger") || part.ContainsKey("supercharger"))) problems.Add(name + ": boost section missing");

                    // what BeamNG's torque graph will show, recomputed from the file alone
                    try
                    {
                        var shown = EngineModel.GetPeaks(BeamNGSim.Curve(part, mods), data.RpmLimit);
                        var want = data.Peaks;
                        double dt = Math.Abs(shown.TorqueNm - want.TorqueNm) / Math.Max(0.1, want.TorqueNm);
                        double dp = Math.Abs(shown.PowerKW - want.PowerKW) / Math.Max(0.1, want.PowerKW);
                        string got = Num.F(shown.PowerHP, "#,0") + " hp / " + Num.F(shown.TorqueNm, "#,0.#") + " Nm";
                        if (dt > 0.03 || dp > 0.03)
                            problems.Add(name + ": BeamNG would show " + got + " instead of " + Num.F(want.PowerHP, "#,0") + " hp / " + Num.F(want.TorqueNm, "#,0.#") + " Nm");
                        else notes.Add("BeamNG will show " + got + " (matches within " + Num.F(Math.Max(dt, dp) * 100, "0.0") + "%)");

                        // with the exhaust taken off, power should move by the share it does on the stock engine, not explode
                        if (mods != null && mods.Exhaust != null)
                        {
                            var bare = EngineModel.GetPeaks(BeamNGSim.Curve(part, mods.WithoutExhaust()), data.RpmLimit);
                            double change = bare.PowerKW / Math.Max(0.1, shown.PowerKW) - 1;
                            // the same check on the stock engine, as BeamNG ships it
                            double stockChange = double.NaN;
                            try
                            {
                                if (stockPart != null && stockPart.ContainsKey("mainEngine"))
                                {
                                    var sm = mods.Unscaled();
                                    double rl = StockLimit(stockPart, data.RpmLimit);
                                    var sOn = EngineModel.GetPeaks(BeamNGSim.Curve(stockPart, sm), rl);
                                    var sOff = EngineModel.GetPeaks(BeamNGSim.Curve(stockPart, sm.WithoutExhaust()), rl);
                                    stockChange = sOff.PowerKW / Math.Max(0.1, sOn.PowerKW) - 1;
                                }
                            }
                            catch (Exception) { stockChange = double.NaN; }
                            notes.Add("with the exhaust removed: " + Num.F(bare.PowerHP, "#,0") + " hp / " + Num.F(bare.TorqueNm, "#,0.#") + " Nm (" + (change >= 0 ? "+" : "") + Num.F(change * 100, "0") + "%"
                                + (double.IsNaN(stockChange) ? "" : "; stock engine " + (stockChange >= 0 ? "+" : "") + Num.F(stockChange * 100, "0") + "%") + ")");
                            double allowed = double.IsNaN(stockChange) ? 0.6 : Math.Abs(stockChange) + 0.25;
                            if (Math.Abs(change) > allowed) problems.Add(name + ": removing the exhaust would change power by " + Num.F(change * 100, "0") + "%"
                                + (double.IsNaN(stockChange) ? "" : " (stock engine: " + Num.F(stockChange * 100, "0") + "%)"));
                        }
                        if ((mods != null && mods.Scaled) || vars != null)
                        {
                            var ctl = part.ContainsKey("controller") ? part["controller"] as List<object> : null;
                            if (ctl == null || !ctl.OfType<List<object>>().Any(r => r.Count > 0 && (r[0] as string) == TorqueScaleController.FileName))
                                problems.Add(name + ": torque-scale controller isn't listed in the part");
                        }
                    }
                    catch (Exception ex) { problems.Add(name + ": couldn't recompute the power curve (" + ex.Message + ")"); }
                }
                else if (name.EndsWith(".pc", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = obj.ContainsKey("parts") ? obj["parts"] as Dictionary<string, object> : obj;
                    if (parts == null || !parts.Values.OfType<string>().Contains(partKey)) problems.Add(name + ": config doesn't fit the engine");
                }
            }
            return problems;
        }

        // One mod zip with an engine part (and config) for every selected car, written into mods.
        public static ConvertResult Convert(EngineData data, IList<VehicleInfo> vehicles, string beamng, string mods, ConvertOptions opt,
                                            Action<string> log, Action<int, int, string> progress)
        {
            var s = data.Spec;
            var peaks = data.Peaks;
            var res = new ConvertResult();
            string safe = SafeName(s);
            var fi = opt.SimulateBoost ? ForcedInduction.Design(data) : null;
            var all = new List<KeyValuePair<string, byte[]>>();
            var bases = new List<string>();

            log("Engine: " + s.Name + " - " + s.LayoutShort + " " + s.SizeText + ", " + Num.F(peaks.PowerHP, "0") + " hp, "
                + Num.F(peaks.TorqueNm, "0") + " Nm, rev limit " + Num.F(data.RpmLimit, "0") + " rpm ("
                + (data.FromAutomation ? "Automation's results" : "estimated") + ")");
            // with Automation's own engine the boost comes from its export (logged when that loads)
            if (fi != null && opt.EngineFrom == null) log("Boost: " + (fi.IsTurbo ? "turbocharger" : fi.ScType + " supercharger") + " simulated by BeamNG, up to " + Num.F(fi.MaxPsi / ForcedInduction.PsiPerBar, "0.00") + " bar");

            // Automation's 3D engine model, cut out of the user's export once and shared by every car
            EngineMeshFiles mesh = null;
            if (opt.Model != null)
            {
                if (progress != null) progress(0, vehicles.Count, "Automation's engine model");
                try
                {
                    mesh = EngineExports.Extract(opt.Model, safe);
                    var probs = CheckMesh(mesh);
                    if (probs.Count > 0) throw new InvalidDataException(string.Join("; ", probs));
                    all.AddRange(mesh.Files);
                    res.EngineModelFrom = opt.Model.CarName;
                    res.FilesChecked += mesh.Files.Count(f => !Regex.IsMatch(f.Key, "\\.(dds|png|jpg)$", RegexOptions.IgnoreCase));
                    log("3D model: from export \"" + opt.Model.Describe + "\": " + mesh.Triangles.ToString("#,0", Num.Inv) + " triangles, "
                        + mesh.Materials.Count + " materials, " + (mesh.Files.Count - 2) + " textures" + (mesh.DroppedPieces > 0 ? "; left out " + mesh.DroppedPieces + " driveline pieces" : ""));
                }
                catch (Exception ex)
                {
                    mesh = null;
                    log("3D model: couldn't use the export (" + ex.Message + "), keeping the stock engine models");
                    res.Warnings.Add("Automation's engine model couldn't be used (" + ex.Message + "); the cars keep their stock engine look");
                }
            }
            else if (opt.BuildModel)
            {
                if (progress != null) progress(0, vehicles.Count, "Building the engine model from Automation's parts");
                try
                {
                    var notes = new List<string>();
                    mesh = EngineBuilder.ToMeshFiles(s, safe, notes);
                    var probs = CheckMesh(mesh);
                    if (probs.Count > 0) throw new InvalidDataException(string.Join("; ", probs));
                    all.AddRange(mesh.Files);
                    res.EngineModelFrom = "Automation's parts";
                    res.FilesChecked += mesh.Files.Count(f => !Regex.IsMatch(f.Key, "\\.(dds|png|jpg)$", RegexOptions.IgnoreCase));
                    log("3D model: built from Automation's parts: " + mesh.Triangles.ToString("#,0", Num.Inv) + " triangles, " + mesh.Materials.Count + " materials"
                        + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : ""));
                }
                catch (Exception ex)
                {
                    mesh = null;
                    log("3D model: couldn't build it from Automation's parts (" + ex.Message + "), keeping the stock engine models");
                    res.Warnings.Add("The engine model couldn't be built from Automation's parts (" + ex.Message + "); the cars keep their stock engine look");
                }
            }
            // Automation's own engine from an export of it: same throttle response, inertia and turbo as Automation
            CamsoEngine camso = null;
            if (opt.EngineFrom != null)
            {
                try
                {
                    camso = CamsoEngine.Load(opt.EngineFrom);
                    var cp = EngineModel.GetPeaks(camso.FullThrottleNet, data.RpmLimit);
                    log("Engine: Automation's own (from export \"" + opt.EngineFrom.Describe + "\"): " + Num.F(cp.PowerHP, "0") + " hp / " + Num.F(cp.TorqueNm, "0.#") + " Nm, inertia "
                        + camso.Values["inertia"] + (camso.ChargerKey != null ? ", Automation's " + camso.ChargerKey : ""));
                    double dt = Math.Abs(cp.TorqueNm - peaks.TorqueNm) / Math.Max(0.1, peaks.TorqueNm);
                    if (dt > 0.05) res.Warnings.Add("the export \"" + opt.EngineFrom.Describe + "\" makes " + Num.F(cp.TorqueNm, "0") + " Nm, not " + Num.F(peaks.TorqueNm, "0")
                        + " Nm: the engine may have changed since it was exported; export the car again to update it");
                    if (camso.License != null) all.Add(new KeyValuePair<string, byte[]>("automation_to_beamng/" + safe + "_automation_license.txt", camso.License));
                }
                catch (Exception ex)
                {
                    camso = null;
                    log("Engine: couldn't use the export's own engine (" + ex.Message + "), using the converted one");
                    res.Warnings.Add("Automation's own engine couldn't be taken from the export (" + ex.Message + "); the converted engine is used instead");
                }
            }
            for (int i = 0; i < vehicles.Count; i++)
            {
                var veh = vehicles[i];
                if (progress != null) progress(i, vehicles.Count, veh.FullName);
                try
                {
                    var parts = ModelParts.Load(beamng, veh);
                    string over;
                    opt.BaseOverrides.TryGetValue(veh.Model, out over);
                    var bas = Converter.SelectBase(veh, s, peaks.TorqueNm, over);
                    if (!parts.ByKey.ContainsKey(bas.PartKey)) throw new InvalidOperationException("stock engine " + bas.PartKey + " not found");
                    var notes = new List<string>();
                    string partKey = veh.Model + "_engine_automation_" + safe;
                    DefaultConfig cfg = null;
                    try { cfg = DefaultConfig.Load(beamng, veh); } catch (Exception) { cfg = null; }
                    // first pass gives our part tree; the fitted parts' torque modifiers are then
                    // cancelled out in the second pass so BeamNG ends up at Automation's curve
                    string draft = BuildPart(parts, bas.PartKey, data, fi, opt.Sound, partKey, null, new List<string>(), null, opt.Upgrades, camso);
                    var tmods = FittedMods(parts, veh, cfg, bas.SlotType, draft, partKey);
                    Dictionary<string, object> stock = null;
                    try { stock = JBeamParser.Parse(parts.ByKey[bas.PartKey]) as Dictionary<string, object>; } catch (Exception) { stock = null; }
                    // Automation's own engine: its map already is Automation's engine with the default parts, so
                    // the controller only applies part changes relative to them (sized to this engine)
                    ScaleMods(tmods, stock, data, fi, camso != null ? camso.Gross : null);
                    string jb = BuildPart(parts, bas.PartKey, data, fi, opt.Sound, partKey, tmods, notes, mesh, opt.Upgrades, camso);
                    if (tmods.Any) notes.Add("compensated torque changes from the car's other parts: " + string.Join(", ", tmods.Sources.Distinct()));
                    if (tmods.Scaled) notes.Add("intake/exhaust torque changes scaled to this engine: x" + Num.F(tmods.ScaleCurve.Min(p => p[1]), "0.00")
                                               + "-" + Num.F(tmods.ScaleCurve.Max(p => p[1]), "0.00") + ", rev range x" + Num.F(1 / tmods.RpmRatio, "0.00"));
                    var files = new List<KeyValuePair<string, byte[]>>
                    {
                        new KeyValuePair<string, byte[]>("vehicles/" + veh.Model + "/automation_" + safe + ".jbeam", new UTF8Encoding(false).GetBytes(jb)),
                    };
                    if (camso != null)
                        foreach (var l in camso.Lua) files.Add(new KeyValuePair<string, byte[]>("vehicles/" + veh.Model + "/lua/powertrain/" + l.Key, l.Value));
                    if (tmods.Scaled || tmods.MultCurve != null || opt.Upgrades)
                        files.Add(new KeyValuePair<string, byte[]>("vehicles/" + veh.Model + "/lua/controller/" + TorqueScaleController.FileName + ".lua",
                                                                   new UTF8Encoding(false).GetBytes(TorqueScaleController.Lua.Replace("\r\n", "\n"))));
                    if (opt.MakeConfigs)
                    {
                        try { AddConfig(cfg, veh, data, jb, partKey, safe, files, notes); }
                        catch (Exception ex) { notes.Add("ready-made config skipped (" + ex.Message + ")"); }
                    }
                    var problems = Validate(veh, partKey, files, fi != null && camso == null, data, tmods, stock, notes, camso);
                    res.FilesChecked += files.Count(f => !f.Key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
                    if (problems.Count > 0)
                    {
                        res.Failed++;
                        foreach (var p in problems) { log("  ! " + veh.FullName + ": " + p); res.Problems.Add(veh.FullName + ": " + p); }
                        continue;
                    }
                    all.AddRange(files);
                    if (files.Any(f => f.Key.EndsWith(".pc", StringComparison.Ordinal))) res.Configs++;
                    bases.Add(veh.Model + "=" + bas.PartKey);
                    res.Parts[veh.Model] = new[] { partKey, bas.SlotType };
                    log("  + " + veh.FullName + ": built on stock '" + bas.DisplayName + "'");
                    if (bas.Diesel) notes.Add("WARNING: this car only has diesel engines, so it will run on diesel fuel");
                    if (fi != null && notes.Any(x => x.StartsWith("WARNING: '", StringComparison.Ordinal)))
                        notes.Add("WARNING: the stock turbo/supercharger part replaces the simulated one");
                    foreach (var note in notes)
                    {
                        log("      " + note);
                        if (note.StartsWith("WARNING: ", StringComparison.Ordinal)) res.Warnings.Add(veh.FullName + ": " + note.Substring(9));
                    }
                    res.Built++;
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    log("  ! " + veh.FullName + ": failed - " + ex.Message);
                    res.Problems.Add(veh.FullName + ": could not be built (" + ex.Message + ")");
                }
            }
            if (progress != null) progress(vehicles.Count, vehicles.Count, "Writing mod");
            if (res.Built == 0) throw new InvalidOperationException("No engine parts could be built." + (res.Problems.Count > 0 ? "\n\n" + string.Join("\n", res.Problems) : ""));

            // remember what was built, so "Update" can rebuild it later
            var meta = new StringBuilder();
            Action<string, string> m = (k, v) => meta.Append(k).Append('=').Append((v ?? "").Replace("\r", " ").Replace("\n", " ")).Append("\r\n");
            m("AppVersion", typeof(Converter).Assembly.GetName().Version.ToString());
            m("AppBuild", BeamNGProcess.AppBuild());
            m("GameVersion", BeamNGProcess.GameVersion(beamng));
            m("Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Num.Inv));
            m("EngineName", s.Name);
            m("EngineFile", s.File);
            m("VariantUID", s.VariantUID);
            m("Source", data.FromAutomation ? "automation" : "estimate");
            m("PowerHP", Num.F(peaks.PowerHP, "0"));
            m("TorqueNm", Num.F(peaks.TorqueNm, "0"));
            m("Cars", string.Join(",", vehicles.Where(v => bases.Any(b => b.StartsWith(v.Model + "=", StringComparison.Ordinal))).Select(v => v.Model)));
            m("Bases", string.Join(";", bases));
            m("Overrides", string.Join(";", opt.BaseOverrides.Select(kv => kv.Key + "=" + kv.Value)));
            m("SimulateBoost", opt.SimulateBoost ? "1" : "0");
            m("MakeConfigs", opt.MakeConfigs ? "1" : "0");
            m("Upgrades", opt.Upgrades ? "1" : "0");
            m("EngineModel", mesh != null ? "1" : "0");
            m("AutomationEngine", camso != null ? "1" : "0");
            m("EngineModelFrom", mesh == null ? "" : opt.Model != null ? opt.Model.Describe : "built");
            m("Sound", opt.Sound != null ? opt.Sound.Name : "");
            m("ManualTorque", Num.F(opt.ManualTorque, "0.##"));
            m("ManualPowerKW", Num.F(opt.ManualPowerKW, "0.##"));
            all.Add(new KeyValuePair<string, byte[]>(MetaFolder + safe + ".txt", Encoding.UTF8.GetBytes(meta.ToString())));

            Directory.CreateDirectory(mods);
            string zipPath = Path.Combine(mods, "automation_" + safe + ".zip");
            string tmp = zipPath + ".tmp";
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var e in all)
                    using (var w = z.CreateEntry(e.Key).Open()) w.Write(e.Value, 0, e.Value.Length);
            }
            // a running BeamNG keeps the old zip locked; the live link can unmount it for a moment
            bool unlocked = false;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                    System.IO.File.Move(tmp, zipPath);
                    break;
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                    if (!unlocked && opt.Unlock != null)
                    {
                        log("BeamNG has the old version loaded, asking it to let go…");
                        unlocked = opt.Unlock(zipPath);
                    }
                    if (unlocked && attempt < 20) { System.Threading.Thread.Sleep(150); continue; }
                    try { System.IO.File.Delete(tmp); } catch (Exception) { }
                    throw new ModLockedException(zipPath);
                }
            }
            log("Installed: " + zipPath + " (" + res.FilesChecked + " files checked)");
            res.ZipPath = zipPath;
            return res;
        }
    }

    class ModLockedException : IOException
    {
        public readonly string ZipPath;

        public ModLockedException(string path)
            : base("BeamNG.drive is using " + Path.GetFileName(path) + ", so it can't be replaced. Close BeamNG.drive and try again.") { ZipPath = path; }
    }

    // An engine mod this app installed earlier.
    class InstalledMod
    {
        public string ZipPath, EngineName = "", EngineFile = "";
        public List<string> Cars = new List<string>();
        public DateTime Date;
        public long Size;
        public Dictionary<string, string> Meta = new Dictionary<string, string>();

        public string Get(string k) { string v; return Meta.TryGetValue(k, out v) ? v : ""; }

        public static List<InstalledMod> List(string mods)
        {
            var list = new List<InstalledMod>();
            if (string.IsNullOrEmpty(mods) || !Directory.Exists(mods)) return list;
            foreach (var f in new DirectoryInfo(mods).GetFiles("automation_*.zip").OrderByDescending(f => f.LastWriteTime))
            {
                var m = new InstalledMod { ZipPath = f.FullName, Date = f.LastWriteTime, Size = f.Length };
                try
                {
                    using (var z = ZipFile.OpenRead(f.FullName))
                    {
                        foreach (var e in z.Entries)
                        {
                            if (e.FullName.StartsWith(Converter.MetaFolder, StringComparison.Ordinal))
                                foreach (var ln in JBeam.ReadEntry(e).Split('\n'))
                                {
                                    int i = ln.IndexOf('=');
                                    if (i > 0) m.Meta[ln.Substring(0, i)] = ln.Substring(i + 1).TrimEnd('\r');
                                }
                            var mm = Regex.Match(e.FullName, "^vehicles/([^/]+)/[^/]+\\.jbeam$");
                            if (mm.Success && !m.Cars.Contains(mm.Groups[1].Value)) m.Cars.Add(mm.Groups[1].Value);
                        }
                    }
                }
                catch (Exception) { }
                m.EngineName = m.Get("EngineName");
                if (m.EngineName.Length == 0) m.EngineName = Path.GetFileNameWithoutExtension(f.Name).Substring("automation_".Length).Replace('_', ' ');
                m.EngineFile = m.Get("EngineFile");
                list.Add(m);
            }
            return list;
        }
    }

    static class BeamNGProcess
    {
        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("BeamNG.drive.x64").Length > 0 || Process.GetProcessesByName("BeamNG.drive").Length > 0; }
            catch (Exception) { return false; }
        }

        // e.g. "0.40.4.0.296339"; changes with every game update
        public static string GameVersion(string beamng)
        {
            try
            {
                string exe = Path.Combine(beamng ?? "", "BeamNG.drive.exe");
                return File.Exists(exe) ? (FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "") : "";
            }
            catch (Exception) { return ""; }
        }

        // when this converter was built; changes with every new version of the app
        public static string AppBuild()
        {
            try
            {
                string exe = typeof(Converter).Assembly.Location;
                return string.IsNullOrEmpty(exe) ? "" : File.GetLastWriteTimeUtc(exe).ToString("yyyyMMddHHmmss", Num.Inv);
            }
            catch (Exception) { return ""; }
        }

        // Why an installed engine should be rebuilt, or null when it's current.
        public static string Outdated(InstalledMod m, string beamng)
        {
            string game = GameVersion(beamng), app = AppBuild();
            if (game.Length > 0 && m.Get("GameVersion") != game) return m.Get("GameVersion").Length > 0 ? "BeamNG was updated" : "built for an older BeamNG";
            if (app.Length > 0 && m.Get("AppBuild") != app) return "built with an older converter";
            return null;
        }
    }
}
